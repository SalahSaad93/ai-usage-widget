using System;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace AiUsageWidget.Core
{
    // Owns the snapshot set: parallel fetch with a per-provider timeout, per-provider backoff,
    // stale handling, and cache.json persistence. The clock is injected so the self-test can
    // step time deterministically, and isOnline so an offline machine can be told apart from
    // a failing provider.
    public sealed class RefreshService
    {
        public const int TimeoutSeconds = 15;
        static readonly int[] BackoffMinutes = { 10, 20, 30 };

        IUsageProvider[] providers;   // replaced at the start of every refresh by the form
        readonly Func<DateTime> clockUtc;
        readonly string cachePath;
        readonly Func<bool> isOnline;

        // Serialises the per-provider applies when no synchronisation context exists (the
        // self-test), where the continuations otherwise land on concurrent worker threads.
        readonly object applyLock = new object();

        readonly Dictionary<string, DateTime> skipUntil = new Dictionary<string, DateTime>();
        readonly Dictionary<string, int> backoffStep = new Dictionary<string, int>();

        public RefreshService(IUsageProvider[] providers, Func<DateTime> clockUtc, string cachePath,
            Func<bool> isOnline = null)
        {
            this.providers = providers;
            this.clockUtc = clockUtc;
            this.cachePath = cachePath;
            this.isOnline = isOnline ?? NetworkInterface.GetIsNetworkAvailable;
        }

        // Detection may add or remove providers between runs (spec section 13): the form
        // recomputes the shown set at the start of every refresh and hands it over here.
        public void SetProviders(IUsageProvider[] shown)
        {
            providers = shown;
        }

        public List<UsageSnapshot> Snapshots { get; private set; }
        public bool Busy { get; private set; }
        public DateTime LastRunStartUtc { get; private set; }

        // UI hook: called after the snapshot set changed.
        public Action Changed;

        // Startup: paint the last-good data before the first refresh.
        public void LoadCache()
        {
            Snapshots = SnapshotCache.Load(cachePath);
            FireChanged();
        }

        // True while the provider is in backoff. Scheduled refreshes skip it; manual bypass.
        public bool IsSkipped(string providerId, DateTime nowUtc)
        {
            DateTime until;
            return skipUntil.TryGetValue(providerId, out until) && nowUtc < until;
        }

        public async Task RunOnceAsync(bool manual)
        {
            if (Busy) return;
            DateTime now = clockUtc();
            if (manual && (now - LastRunStartUtc).TotalSeconds < 30) return;
            if (Snapshots == null) Snapshots = new List<UsageSnapshot>();
            Busy = true;
            LastRunStartUtc = now;
            FireChanged();
            try
            {
                // Each provider's result is applied the moment it completes, so one slow
                // provider never delays the rows of the fast ones. The apply continuation goes
                // back to the calling synchronisation context (the UI thread); RunOnceAsync
                // only finishes once every apply has run.
                SynchronizationContext context = SynchronizationContext.Current;
                TaskScheduler scheduler = context != null
                    ? TaskScheduler.FromCurrentSynchronizationContext()
                    : TaskScheduler.Default;
                var applied = new List<Task>();
                foreach (IUsageProvider p in providers)
                    if (manual || !IsSkipped(p.Id, now))
                        applied.Add(FetchOne(p).ContinueWith(OnFetched, CancellationToken.None,
                            TaskContinuationOptions.None, scheduler));
                await Task.WhenAll(applied.ToArray());
            }
            catch (Exception)
            {
                // a refresh must never take the widget down; state stays as it was
            }
            finally
            {
                Busy = false;
                FireChanged();
            }
        }

        void OnFetched(Task<FetchResult> fetch)
        {
            if (fetch.Status != TaskStatus.RanToCompletion) return;
            lock (applyLock)
            {
                Apply(fetch.Result, clockUtc());
                FireChanged();
            }
        }

        sealed class FetchResult
        {
            public IUsageProvider Provider;
            public UsageSnapshot Snapshot;   // null on timeout
            public bool TimedOut;
        }

        static async Task<FetchResult> FetchOne(IUsageProvider p)
        {
            var result = new FetchResult();
            result.Provider = p;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds)))
            {
                try
                {
                    result.Snapshot = await p.FetchAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    result.TimedOut = true;
                }
                catch (Exception ex)
                {
                    string msg = ex.Message;
                    if (string.IsNullOrEmpty(msg)) msg = "unknown error";
                    if (msg.Length > 60) msg = msg.Substring(0, 60);
                    result.Snapshot = Fail(p, "error: " + msg);
                }
            }
            return result;
        }

        void Apply(FetchResult r, DateTime nowUtc)
        {
            if (r.TimedOut || r.Snapshot == null)
            {
                ApplyFailure(r.Provider, Fail(r.Provider, r.TimedOut ? "timeout" : "error"), nowUtc);
                return;
            }
            UsageSnapshot s = r.Snapshot;
            if (string.IsNullOrEmpty(s.ProviderId)) s.ProviderId = r.Provider.Id;
            if (string.IsNullOrEmpty(s.DisplayName)) s.DisplayName = r.Provider.DisplayName;
            if (s.Status == ProviderStatus.Ok || s.Status == ProviderStatus.NotConfigured)
            {
                // current facts, not failures: replace the cached snapshot, no backoff
                ResetBackoff(r.Provider.Id);
                Replace(s, nowUtc);
                Persist();
                return;
            }
            if (s.Status == ProviderStatus.AuthRequired)
            {
                ApplyAuthRequired(r.Provider, s, nowUtc);
                return;
            }
            // Error: back off; keep showing last-good data when there is any.
            ApplyFailure(r.Provider, s, nowUtc);
        }

        // An expired login must not wipe last-known usage: with windows to show, the previous
        // snapshot stays (as Stale) and only the message is swapped for the sign-in hint; the
        // painter renders it as "stale 42m · <message>". With no previous windows, behave as
        // today: replace, no backoff.
        void ApplyAuthRequired(IUsageProvider p, UsageSnapshot s, DateTime nowUtc)
        {
            UsageSnapshot prev = Find(p.Id);
            if (prev != null && prev.Windows != null && prev.Windows.Count > 0 &&
                (prev.Status == ProviderStatus.Ok || prev.Status == ProviderStatus.Stale))
            {
                prev.Status = ProviderStatus.Stale;   // FetchedAtUtc stays: the tag shows its age
                prev.Message = s.Message;
                Persist();
                return;
            }
            ResetBackoff(p.Id);
            Replace(s, nowUtc);
            Persist();
        }

        // Error or timeout: back off, then keep last-good data when there is any. Offline the
        // failure is this machine's connection rather than the provider, so no backoff; the
        // message "offline" replaces the provider's own text.
        void ApplyFailure(IUsageProvider p, UsageSnapshot error, DateTime nowUtc)
        {
            if (!isOnline())
            {
                KeepLastGoodOr(p, Fail(p, "offline"), nowUtc);
                return;
            }
            Escalate(p.Id, nowUtc);
            KeepLastGoodOr(p, error, nowUtc);
        }

        void KeepLastGoodOr(IUsageProvider p, UsageSnapshot error, DateTime nowUtc)
        {
            UsageSnapshot prev = Find(p.Id);
            if (prev != null && prev.Status != ProviderStatus.Error)
            {
                prev.Status = ProviderStatus.Stale;   // fetched time stays: the stale tag shows its age
                prev.Message = "";   // drop any earlier sign-in note: this failure is not that
                Persist();
                return;
            }
            // No good data yet: show the failure, but Error rows are never written to cache.json.
            Replace(error, nowUtc);
        }

        void Replace(UsageSnapshot s, DateTime nowUtc)
        {
            s.FetchedAtUtc = nowUtc;
            for (int i = 0; i < Snapshots.Count; i++)
            {
                if (Snapshots[i].ProviderId == s.ProviderId)
                {
                    Snapshots[i] = s;
                    return;
                }
            }
            Snapshots.Add(s);
        }

        void Escalate(string providerId, DateTime nowUtc)
        {
            int step;
            backoffStep.TryGetValue(providerId, out step);
            step = step + 1;
            if (step > BackoffMinutes.Length) step = BackoffMinutes.Length;
            backoffStep[providerId] = step;
            skipUntil[providerId] = nowUtc.AddMinutes(BackoffMinutes[step - 1]);
        }

        void ResetBackoff(string providerId)
        {
            backoffStep.Remove(providerId);
            skipUntil.Remove(providerId);
        }

        UsageSnapshot Find(string providerId)
        {
            if (Snapshots == null) return null;
            foreach (UsageSnapshot s in Snapshots)
                if (s.ProviderId == providerId) return s;
            return null;
        }

        // cache.json keeps last-good snapshots only.
        void Persist()
        {
            var good = new List<UsageSnapshot>();
            foreach (UsageSnapshot s in Snapshots)
                if (s.Status != ProviderStatus.Error) good.Add(s);
            try
            {
                SnapshotCache.Save(cachePath, good);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        static UsageSnapshot Fail(IUsageProvider p, string message)
        {
            var s = new UsageSnapshot();
            s.ProviderId = p.Id;
            s.DisplayName = p.DisplayName;
            s.Status = ProviderStatus.Error;
            s.Message = message;
            return s;
        }

        void FireChanged()
        {
            var changed = Changed;
            if (changed != null) changed();
        }
    }
}
