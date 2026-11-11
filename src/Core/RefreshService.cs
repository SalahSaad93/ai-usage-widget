using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AiUsageWidget.Core
{
    // Owns the snapshot set: parallel fetch with a per-provider timeout, per-provider backoff,
    // stale handling, and cache.json persistence. The clock is injected so the self-test can
    // step time deterministically.
    public sealed class RefreshService
    {
        public const int TimeoutSeconds = 15;
        static readonly int[] BackoffMinutes = { 10, 20, 30 };

        readonly IUsageProvider[] providers;
        readonly Func<DateTime> clockUtc;
        readonly string cachePath;

        readonly Dictionary<string, DateTime> skipUntil = new Dictionary<string, DateTime>();
        readonly Dictionary<string, int> backoffStep = new Dictionary<string, int>();

        public RefreshService(IUsageProvider[] providers, Func<DateTime> clockUtc, string cachePath)
        {
            this.providers = providers;
            this.clockUtc = clockUtc;
            this.cachePath = cachePath;
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
                var fetches = new List<Task<FetchResult>>();
                foreach (IUsageProvider p in providers)
                    if (manual || !IsSkipped(p.Id, now))
                        fetches.Add(FetchOne(p));
                FetchResult[] results = await Task.WhenAll(fetches.ToArray());
                DateTime stamped = clockUtc();
                foreach (FetchResult r in results) Apply(r, stamped);
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
                Escalate(r.Provider.Id, nowUtc);
                KeepLastGoodOr(r.Provider, Fail(r.Provider, r.TimedOut ? "timeout" : "error"), nowUtc);
                return;
            }
            UsageSnapshot s = r.Snapshot;
            if (string.IsNullOrEmpty(s.ProviderId)) s.ProviderId = r.Provider.Id;
            if (string.IsNullOrEmpty(s.DisplayName)) s.DisplayName = r.Provider.DisplayName;
            if (s.Status == ProviderStatus.Ok || s.Status == ProviderStatus.AuthRequired
                || s.Status == ProviderStatus.NotConfigured)
            {
                // current facts, not failures: replace the cached snapshot, no backoff
                ResetBackoff(r.Provider.Id);
                Replace(s, nowUtc);
                Persist();
                return;
            }
            // Error: back off; keep showing last-good data when there is any.
            Escalate(r.Provider.Id, nowUtc);
            KeepLastGoodOr(r.Provider, s, nowUtc);
        }

        void KeepLastGoodOr(IUsageProvider p, UsageSnapshot error, DateTime nowUtc)
        {
            UsageSnapshot prev = Find(p.Id);
            if (prev != null && prev.Status != ProviderStatus.Error)
            {
                prev.Status = ProviderStatus.Stale;   // fetched time stays: the stale tag shows its age
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
