using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiUsageWidget.Core;
using AiUsageWidget.Providers;

namespace AiUsageWidget
{
    // Headless checks (spec section 10). RunAll points AI_USAGE_WIDGET_DATA at a fresh temp
    // folder and makes no network calls: every check below is pure or local-only, and the
    // Codex provider is pointed at a temp auth folder so the real %USERPROFILE%\.codex is
    // never read.
    public static class SelfTest
    {
        public static int RunAll(string outPath)
        {
            Environment.SetEnvironmentVariable("AI_USAGE_WIDGET_DATA", Path.Combine(
                Path.GetTempPath(), "ai-usage-widget-selftest-" + Guid.NewGuid().ToString("N")));

            var lines = new List<string>();
            Check(lines, "codex_fixture_mapping", CodexFixtureMapping);
            Check(lines, "window_kind_by_length", WindowKindByLength);
            Check(lines, "codex_no_windows_is_error", NoWindowsIsError);
            Check(lines, "colour_thresholds", ColourThresholds);
            Check(lines, "cache_round_trip", CacheRoundTrip);
            Check(lines, "stale_and_backoff_transitions", StaleAndBackoff);
            Check(lines, "codex_missing_auth", CodexMissingAuth);
            Check(lines, "settings_defaults_and_clamping", SettingsDefaultsAndClamping);
            Check(lines, "version_comparison", VersionComparison);
            Check(lines, "hash_mismatch_rejection", HashMismatchRejection);
            Check(lines, "exe_swap_and_rollback", ExeSwapAndRollback);
            Check(lines, "token_store_round_trip", TokenStoreRoundTrip);
            Check(lines, "claude_fixture_mapping", ClaudeFixtureMapping);
            Check(lines, "claude_plan_names", ClaudePlanNames);
            Check(lines, "claude_auth_states", ClaudeAuthStates);
            Check(lines, "zai_fixture_mapping", ZaiFixtureMapping);
            Check(lines, "zai_auth_and_missing", ZaiAuthAndMissing);
            Check(lines, "muse_empty_allowance", MuseEmptyAllowance);
            Check(lines, "muse_synthetic_mapping", MuseSyntheticMapping);
            Check(lines, "muse_cli_states", MuseCliStates);
            Check(lines, "opencode_go_not_configured", OpenCodeGoNotConfigured);

            string[] all = lines.ToArray();
            if (!string.IsNullOrEmpty(outPath)) File.WriteAllLines(outPath, all, new UTF8Encoding(false)); // no BOM: the log must start with PASS
            foreach (string line in all) Console.WriteLine(line);
            foreach (string line in all)
                if (!line.StartsWith("PASS ", StringComparison.Ordinal)) return 1;
            return 0;
        }

        static void Check(List<string> lines, string name, Action body)
        {
            try
            {
                body();
                lines.Add("PASS " + name);
            }
            catch (Exception ex)
            {
                lines.Add("FAIL " + name + ": " + ex.Message);
            }
        }

        static void That(bool condition, string what)
        {
            if (!condition) throw new Exception(what);
        }

        // Walks up from bin\ until tests\fixtures is found, so the check works no matter
        // which directory the exe is started from.
        static string FixturePath(string name)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                string candidate = Path.Combine(dir, "tests", "fixtures", name);
                if (File.Exists(candidate)) return candidate;
                string trimmed = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string parent = Path.GetDirectoryName(trimmed);
                if (string.IsNullOrEmpty(parent) || parent == trimmed) break;
                dir = parent;
            }
            throw new Exception("fixture not found: " + name);
        }

        // --- section 10 checks ----------------------------------------------------------

        static void CodexFixtureMapping()
        {
            string json = File.ReadAllText(FixturePath("codex-usage.json"));
            UsageSnapshot s = CodexProvider.Parse(json);
            That(s.Status == ProviderStatus.Ok, "status should be Ok, got " + s.Status);
            That(s.PlanName == "Pro Lite", "plan name should be Pro Lite, got '" + s.PlanName + "'");
            That(s.Windows.Count == 1, "exactly one window expected, got " + s.Windows.Count);
            That(s.Windows[0].Kind == WindowKind.Weekly, "the window should be weekly");
            That(s.Windows[0].UsedPercent == 9, "used percent should be 9");
            DateTime expectedReset = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddSeconds(1795002963);
            That(s.Windows[0].ResetsAtUtc.HasValue && s.Windows[0].ResetsAtUtc.Value == expectedReset,
                "reset time");
            That(!HasWindow(s, WindowKind.FiveHour), "no five-hour window on this plan");
        }

        static bool HasWindow(UsageSnapshot s, WindowKind kind)
        {
            foreach (UsageWindow w in s.Windows)
                if (w.Kind == kind) return true;
            return false;
        }

        static void WindowKindByLength()
        {
            That(CodexProvider.KindForSeconds(18000) == WindowKind.FiveHour, "18000 s -> five-hour");
            That(CodexProvider.KindForSeconds(604800) == WindowKind.Weekly, "604800 s -> weekly");
            That(CodexProvider.KindForSeconds(2592000) == WindowKind.Monthly, "2592000 s -> monthly");
            That(CodexProvider.KindForSeconds(5184000) == WindowKind.Monthly, "60 d -> monthly");
            That(!CodexProvider.KindForSeconds(3600).HasValue, "unknown length is ignored");
            That(!CodexProvider.KindForSeconds(null).HasValue, "missing length is ignored");
        }

        static void NoWindowsIsError()
        {
            string json = "{\"plan_type\":\"pro\",\"rate_limit\":{\"primary_window\":null,\"secondary_window\":null}}";
            UsageSnapshot s = CodexProvider.Parse(json);
            That(s.Status == ProviderStatus.Error, "both windows null -> Error");
            That(s.Message == "unexpected response", "message should be 'unexpected response'");
        }

        static void ColourThresholds()
        {
            Color green = Color.FromArgb(0x3F, 0xB9, 0x50);
            Color yellow = Color.FromArgb(0xD2, 0x99, 0x22);
            Color red = Color.FromArgb(0xF8, 0x51, 0x49);
            That(Painter.ColorFor(49.9).ToArgb() == green.ToArgb(), "49.9 must be green");
            That(Painter.ColorFor(50).ToArgb() == yellow.ToArgb(), "50 must be yellow");
            That(Painter.ColorFor(70).ToArgb() == yellow.ToArgb(), "70 must be yellow");
            That(Painter.ColorFor(70.1).ToArgb() == red.ToArgb(), "70.1 must be red");
        }

        static void CacheRoundTrip()
        {
            string path = Path.Combine(AppPaths.DataDir, "cache-roundtrip.json");
            var full = new UsageSnapshot();
            full.ProviderId = "codex";
            full.DisplayName = "Codex";
            full.PlanName = "Pro Lite";
            full.FetchedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            full.Status = ProviderStatus.Ok;
            full.Message = "";
            var w = new UsageWindow();
            w.Kind = WindowKind.Weekly;
            w.UsedPercent = 41.5;
            w.Used = 123.25;
            w.Limit = 1000;
            w.ResetsAtUtc = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            full.Windows.Add(w);
            var minimal = new UsageSnapshot();
            minimal.ProviderId = "claude-code";
            minimal.DisplayName = "Claude Code";
            minimal.Status = ProviderStatus.NotConfigured;
            minimal.Message = "not configured";

            var snapshots = new List<UsageSnapshot>();
            snapshots.Add(full);
            snapshots.Add(minimal);
            SnapshotCache.Save(path, snapshots);
            List<UsageSnapshot> back = SnapshotCache.Load(path);

            That(back.Count == 2, "two snapshots round-trip");
            UsageSnapshot a = back[0];
            That(a.ProviderId == "codex" && a.DisplayName == "Codex" && a.PlanName == "Pro Lite",
                "identity fields survive");
            That(a.Status == ProviderStatus.Ok, "status survives");
            That(a.FetchedAtUtc == full.FetchedAtUtc, "fetched time survives");
            That(a.Windows.Count == 1, "one window survives");
            That(a.Windows[0].Kind == WindowKind.Weekly && a.Windows[0].UsedPercent == 41.5,
                "window kind and percent survive");
            That(a.Windows[0].Used.HasValue && a.Windows[0].Used.Value == 123.25, "used survives");
            That(a.Windows[0].Limit.HasValue && a.Windows[0].Limit.Value == 1000, "limit survives");
            That(a.Windows[0].ResetsAtUtc.HasValue && a.Windows[0].ResetsAtUtc.Value == w.ResetsAtUtc.Value,
                "reset time survives");
            UsageSnapshot b = back[1];
            That(b.ProviderId == "claude-code" && b.Status == ProviderStatus.NotConfigured
                && b.Windows.Count == 0, "minimal snapshot survives");
        }

        sealed class FakeClock
        {
            public DateTime Now;
            public DateTime UtcNow() { return Now; }
        }

        sealed class FakeProvider : IUsageProvider
        {
            public string Id { get { return "fake"; } }
            public string DisplayName { get { return "Fake"; } }
            public int Calls;
            public ProviderStatus Status;
            public string Message;

            public Task<UsageSnapshot> FetchAsync(CancellationToken ct)
            {
                Calls++;
                var s = new UsageSnapshot();
                s.ProviderId = Id;
                s.DisplayName = DisplayName;
                s.Status = Status;
                s.Message = Message;
                if (Status == ProviderStatus.Ok)
                {
                    var w = new UsageWindow();
                    w.Kind = WindowKind.Weekly;
                    w.UsedPercent = 20;
                    s.Windows.Add(w);
                }
                return Task.FromResult(s);
            }
        }

        static void StaleAndBackoff()
        {
            DateTime t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            string cachePath = Path.Combine(AppPaths.DataDir, "cache-stale.json");
            FakeClock clock = new FakeClock();
            clock.Now = t0;
            FakeProvider provider = new FakeProvider();
            provider.Status = ProviderStatus.Ok;

            RefreshService svc = new RefreshService(
                new IUsageProvider[] { provider }, clock.UtcNow, cachePath);
            svc.RunOnceAsync(false).GetAwaiter().GetResult();

            That(svc.Snapshots.Count == 1 && svc.Snapshots[0].Status == ProviderStatus.Ok,
                "first fetch is Ok");
            That(svc.Snapshots[0].FetchedAtUtc == t0, "FetchedAtUtc comes from the injected clock");
            That(File.Exists(cachePath), "cache written on success");

            provider.Status = ProviderStatus.Error;
            provider.Message = "boom";
            clock.Now = t0.AddMinutes(1);
            svc.RunOnceAsync(false).GetAwaiter().GetResult();

            That(svc.Snapshots[0].Status == ProviderStatus.Stale, "error keeps last good as Stale");
            That(svc.Snapshots[0].FetchedAtUtc == t0, "stale keeps the original fetch time");
            That(svc.Snapshots[0].Windows.Count == 1, "stale keeps its windows");
            That(svc.IsSkipped("fake", clock.Now), "backoff starts after an error");
            That(!svc.IsSkipped("fake", t0.AddMinutes(11)), "first backoff lasts 10 min");
            That(provider.Calls == 2, "two fetches so far");

            clock.Now = t0.AddMinutes(5);   // inside backoff
            svc.RunOnceAsync(false).GetAwaiter().GetResult();
            That(provider.Calls == 2, "scheduled refresh skipped during backoff");

            clock.Now = t0.AddMinutes(6);   // manual refresh bypasses backoff (and the 30 s guard)
            svc.RunOnceAsync(true).GetAwaiter().GetResult();
            That(provider.Calls == 3, "manual refresh bypasses backoff");
            That(svc.Snapshots[0].Status == ProviderStatus.Stale, "still stale after a failing manual refresh");
            That(svc.IsSkipped("fake", t0.AddMinutes(16)), "second failure backs off 20 min");
            That(!svc.IsSkipped("fake", t0.AddMinutes(26)), "second backoff ends after 20 min");

            clock.Now = t0.AddMinutes(27);
            svc.RunOnceAsync(false).GetAwaiter().GetResult();
            That(provider.Calls == 4, "refetch once backoff ends");
            That(svc.IsSkipped("fake", t0.AddMinutes(56)), "third failure backs off 30 min");
            That(!svc.IsSkipped("fake", t0.AddMinutes(57)), "third backoff ends after 30 min");

            clock.Now = t0.AddMinutes(58);
            svc.RunOnceAsync(false).GetAwaiter().GetResult();
            That(provider.Calls == 5, "refetch again");
            That(svc.IsSkipped("fake", t0.AddMinutes(87)), "backoff is capped at 30 min");
            That(!svc.IsSkipped("fake", t0.AddMinutes(88)), "cap window ends after 30 min");

            provider.Status = ProviderStatus.Ok;
            clock.Now = t0.AddMinutes(88);   // past the cap, and > 30 s since the last run start
            svc.RunOnceAsync(false).GetAwaiter().GetResult();
            That(provider.Calls == 6, "success refetch");
            That(svc.Snapshots[0].Status == ProviderStatus.Ok, "recovers to Ok");
            That(svc.Snapshots[0].FetchedAtUtc == t0.AddMinutes(88), "re-stamped with the new time");
            That(!svc.IsSkipped("fake", clock.Now.AddSeconds(1)), "success resets backoff");

            // Error with no previous good snapshot: show the Error row, never cache it.
            FakeClock clock2 = new FakeClock();
            clock2.Now = t0;
            FakeProvider provider2 = new FakeProvider();
            provider2.Status = ProviderStatus.Error;
            provider2.Message = "down";
            string cachePath2 = Path.Combine(AppPaths.DataDir, "cache-stale2.json");
            RefreshService svc2 = new RefreshService(
                new IUsageProvider[] { provider2 }, clock2.UtcNow, cachePath2);
            svc2.RunOnceAsync(false).GetAwaiter().GetResult();
            That(svc2.Snapshots[0].Status == ProviderStatus.Error, "Error shown without previous good data");
            That(svc2.Snapshots[0].Message == "down", "Error keeps its message");
            That(!File.Exists(cachePath2), "Error is not persisted");

            provider2.Status = ProviderStatus.AuthRequired;
            provider2.Message = "sign in needed: run fake login";
            clock2.Now = t0.AddMinutes(11);   // past the first backoff window (10 min)
            svc2.RunOnceAsync(false).GetAwaiter().GetResult();
            That(svc2.Snapshots[0].Status == ProviderStatus.AuthRequired, "AuthRequired replaces the snapshot");
            That(File.Exists(cachePath2), "AuthRequired is persisted");
            That(!svc2.IsSkipped("fake", clock2.Now.AddSeconds(1)), "AuthRequired does not back off");
        }

        static void CodexMissingAuth()
        {
            string dir = Path.Combine(AppPaths.DataDir, "codex-home");
            Directory.CreateDirectory(dir);
            string authPath = Path.Combine(dir, "auth.json");
            CodexProvider p = new CodexProvider(authPath);

            UsageSnapshot s = p.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.NotConfigured, "missing auth file -> NotConfigured");

            File.WriteAllText(authPath, "{\"OPENAI_API_KEY\":null}");
            s = p.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.NotConfigured, "no tokens object -> NotConfigured");

            File.WriteAllText(authPath, "{\"tokens\":{\"account_id\":\"a\"}}");
            s = p.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.NotConfigured, "no access_token -> NotConfigured");
        }

        static void SettingsDefaultsAndClamping()
        {
            string path = Path.Combine(AppPaths.DataDir, "settings-test.json");
            Settings s = Settings.Load(path);   // missing file
            That(s.RefreshMinutes == 5 && s.Layout == "detailed" && s.AlwaysOnTop, "defaults");
            That(s.Bounds.Width == 340 && s.Bounds.Height == 560, "default bounds");
            That(s.Providers.Count == 1 && s.Providers[0].Id == "codex" && s.Providers[0].Enabled,
                "default providers");
            That(s.Update.Mode == "auto" && s.Update.Repo == "SalahSaad93/ai-usage-widget" && s.Update.RequiresToken,
                "update defaults");

            File.WriteAllText(path, "{\"refreshMinutes\":0,\"custom\":\"keep\"," +
                "\"update\":{\"mode\":\"bogus\",\"repo\":\"owner/repo\",\"extra\":1}}");
            s = Settings.Load(path);
            That(s.RefreshMinutes == 1, "refreshMinutes clamped low");
            That(s.Update.Repo == "owner/repo", "repo read");
            That(s.Update.Mode == "auto", "unknown mode falls back to auto");

            s.RefreshMinutes = 500;
            s.Save(path);
            string saved = File.ReadAllText(path);
            That(saved.Contains("\"refreshMinutes\":120"), "refreshMinutes clamped high on save");
            That(saved.Contains("\"custom\""), "unknown keys preserved on save");
            That(saved.Contains("\"extra\""), "unknown update keys preserved on save");

            s = Settings.Load(path);
            That(s.RefreshMinutes == 120, "clamped value round-trips");

            File.WriteAllText(path, "{ this is not json");
            s = Settings.Load(path);
            That(s.RefreshMinutes == 5, "malformed file falls back to defaults");
        }

        static void VersionComparison()
        {
            That(Updater.IsNewerVersion("v0.2.0", new Version("0.1.0")), "newer tag accepted");
            That(!Updater.IsNewerVersion("0.1.0", new Version("0.1.0")), "equal version is not newer");
            That(!Updater.IsNewerVersion("v0.0.9", new Version("0.1.0")), "older version is not newer");
            That(Updater.IsNewerVersion("v0.10.0", new Version("0.9.9")), "numeric comparison, not lexical");
            That(!Updater.IsNewerVersion("garbage", new Version("0.1.0")), "garbage tag rejected");
            That(!Updater.IsNewerVersion("v0.2.0", null), "null running version rejected");
            That(Updater.NormalizeTag("  V1.2.3 ") == "1.2.3", "tag normalisation");
        }

        static void HashMismatchRejection()
        {
            byte[] data = Encoding.UTF8.GetBytes("ai usage widget");
            string hex;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                hex = sb.ToString();
            }
            That(Updater.VerifySha256(data, hex), "matching hash accepted");
            That(Updater.VerifySha256(data, hex.ToUpperInvariant()), "hash comparison is case-insensitive");
            That(!Updater.VerifySha256(data, new string('0', 64)), "wrong hash rejected");
            That(!Updater.VerifySha256(data, ""), "empty hash rejected");
            That(!Updater.VerifySha256(null, hex), "null data rejected");
        }

        static void ExeSwapAndRollback()
        {
            string dir = Path.Combine(AppPaths.DataDir, "swap");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            string running = Path.Combine(dir, "AiUsageWidget.exe");
            File.WriteAllText(running, "OLD");
            string downloadDir = Path.Combine(dir, "update");
            Directory.CreateDirectory(downloadDir);
            string fresh = Path.Combine(downloadDir, "AiUsageWidget.exe");
            File.WriteAllText(fresh, "NEW");

            bool ok = Updater.TrySwap(running, fresh, delegate(string startedPath)
            {
                throw new IOException("simulated launch failure");
            });
            That(!ok, "failed launch -> swap reports false");
            That(File.ReadAllText(running) == "OLD", "running exe restored after failed launch");
            That(!File.Exists(Path.Combine(dir, "AiUsageWidget.old.exe")), "no .old left behind");

            ok = Updater.TrySwap(running, fresh, delegate(string startedPath)
            {
                File.WriteAllText(Path.Combine(dir, "marker.txt"), startedPath);
            });
            That(ok, "swap succeeds when the new exe launches");
            That(File.ReadAllText(running) == "NEW", "new exe takes the running path");
            That(File.ReadAllText(Path.Combine(dir, "AiUsageWidget.old.exe")) == "OLD",
                "old exe renamed to .old");
            That(File.ReadAllText(Path.Combine(dir, "marker.txt")) == running,
                "launcher receives the new exe path");
            That(!File.Exists(fresh), "downloaded file is consumed by the swap");
        }

        static void TokenStoreRoundTrip()
        {
            That(TokenStore.Load() == null, "no token at start");
            TokenStore.Save("github_pat_selftest");
            That(TokenStore.Load() == "github_pat_selftest", "token round-trips through DPAPI");
            TokenStore.Save("second");
            That(TokenStore.Load() == "second", "save overwrites");
            TokenStore.Clear();
            That(TokenStore.Load() == null, "cleared");
        }

        static DateTime EpochMs(double ms)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
        }

        static DateTime EpochBase(int y, int mo, int d, int h, int mi)
        {
            return new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc);
        }

        static UsageWindow WindowOf(UsageSnapshot s, WindowKind kind)
        {
            foreach (UsageWindow w in s.Windows)
                if (w.Kind == kind) return w;
            return null;
        }

        static void Near(DateTime? actual, DateTime expected, string what)
        {
            That(actual.HasValue, what + " present");
            That(Math.Abs((actual.Value - expected).TotalMilliseconds) < 1,
                what + " value");
            That(actual.Value.Kind == DateTimeKind.Utc, what + " is UTC");
        }

        static void ClaudeFixtureMapping()
        {
            string json = File.ReadAllText(FixturePath("claude-usage.json"));
            UsageSnapshot s = ClaudeCodeProvider.Parse(json, "Max 5x");
            That(s.Status == ProviderStatus.Ok, "status should be Ok");
            That(s.PlanName == "Max 5x", "plan name passes through, got '" + s.PlanName + "'");
            That(s.Windows.Count == 2, "two windows expected, got " + s.Windows.Count);
            UsageWindow five = WindowOf(s, WindowKind.FiveHour);
            That(five != null && five.UsedPercent == 17, "five-hour utilization 17");
            Near(five.ResetsAtUtc, EpochBase(2026, 11, 11, 21, 40).AddMilliseconds(908.682),
                "five-hour reset");
            UsageWindow week = WindowOf(s, WindowKind.Weekly);
            That(week != null && week.UsedPercent == 54, "weekly utilization 54");
            Near(week.ResetsAtUtc, EpochBase(2026, 11, 16, 19, 0).AddMilliseconds(908.707),
                "weekly reset");
            That(WindowOf(s, WindowKind.Monthly) == null, "no monthly window");
        }

        static void ClaudePlanNames()
        {
            string tpl = "{\"claudeAiOauth\":{\"subscriptionType\":\"SUB\"," +
                "\"rateLimitTier\":\"TIER\"}}";
            That(ClaudeCodeProvider.PlanNameOf(tpl.Replace("SUB", "max").Replace("TIER", "claude_max_5x")) == "Max 5x",
                "max 5x tier");
            That(ClaudeCodeProvider.PlanNameOf(tpl.Replace("SUB", "max").Replace("TIER", "claude_max_20x")) == "Max 20x",
                "max 20x tier");
            That(ClaudeCodeProvider.PlanNameOf(tpl.Replace("SUB", "max").Replace("TIER", "claude_max")) == "Max",
                "max without tier");
            That(ClaudeCodeProvider.PlanNameOf(tpl.Replace("SUB", "pro").Replace("TIER", "")) == "Pro", "pro plan");
            That(ClaudeCodeProvider.PlanNameOf(tpl.Replace("SUB", "team").Replace("TIER", "")) == "Team",
                "raw value capitalized");
        }

        static void ClaudeAuthStates()
        {
            string dir = Path.Combine(AppPaths.DataDir, "claude-home");
            Directory.CreateDirectory(dir);
            string cred = Path.Combine(dir, "credentials.json");
            ClaudeCodeProvider p = new ClaudeCodeProvider(cred);

            UsageSnapshot s = p.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.NotConfigured, "missing file -> NotConfigured");

            File.WriteAllText(cred,
                "{\"claudeAiOauth\":{\"accessToken\":\"tok\",\"expiresAt\":1000}}");
            s = p.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.AuthRequired,
                "expired login -> AuthRequired with no network call");
            That(s.Message == "sign in needed: open Claude Code", "expired login message");
        }

        static void ZaiFixtureMapping()
        {
            string json = File.ReadAllText(FixturePath("zai-quota.json"));
            UsageSnapshot s = ZaiProvider.Parse(json);
            That(s.Status == ProviderStatus.Ok, "status should be Ok");
            That(s.PlanName == "Coding Lite", "plan should be Coding Lite, got '" + s.PlanName + "'");
            That(s.Windows.Count == 3, "three windows expected, got " + s.Windows.Count);
            UsageWindow five = WindowOf(s, WindowKind.FiveHour);
            That(five != null && five.UsedPercent == 8, "five-hour 8 percent");
            Near(five.ResetsAtUtc, EpochMs(1794433677039), "five-hour reset");
            UsageWindow week = WindowOf(s, WindowKind.Weekly);
            That(week != null && week.UsedPercent == 92, "weekly 92 percent");
            Near(week.ResetsAtUtc, EpochMs(1794598684997), "weekly reset");
            UsageWindow month = WindowOf(s, WindowKind.Monthly);
            That(month != null && month.UsedPercent == 7, "monthly 7 percent");
            That(month.Used.HasValue && month.Used.Value == 7, "monthly used 7");
            That(month.Limit.HasValue && month.Limit.Value == 100, "monthly limit 100");
            Near(month.ResetsAtUtc, EpochMs(1794484611982), "monthly reset");
        }

        static void ZaiAuthAndMissing()
        {
            UsageSnapshot s = ZaiProvider.Parse(
                "{\"success\":false,\"code\":401,\"msg\":\"unauthorized\"}");
            That(s.Status == ProviderStatus.AuthRequired, "200 body with code 401 -> AuthRequired");
            That(s.Message == "sign in needed: check Z.ai key", "auth message");

            string dir = Path.Combine(AppPaths.DataDir, "zai-home");
            string missing = Path.Combine(dir, "auth.json");
            ZaiProvider p = new ZaiProvider(missing);
            s = p.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.NotConfigured, "missing key file -> NotConfigured");
        }

        static void MuseEmptyAllowance()
        {
            string json = File.ReadAllText(FixturePath("muse-usage-empty.json"));
            UsageSnapshot s = MuseProvider.Parse(json, 0, "");
            That(s.Status == ProviderStatus.Ok, "empty allowance is Ok, not an error");
            That(s.Windows.Count == 0, "no windows");
            That(s.Message == "no usage yet in this window", "spec message");

            // The painter draws an Ok row with no windows as its message, like NotConfigured.
            var one = new List<UsageSnapshot>();
            one.Add(s);
            List<RowVm> rows = Painter.BuildRows(one, DateTime.UtcNow, null);
            That(rows.Count == 1 && rows[0].Msg == "no usage yet in this window",
                "BuildRows exposes the message for an Ok row with no windows");
        }

        static void MuseSyntheticMapping()
        {
            string json = File.ReadAllText(FixturePath("muse-usage-synthetic.json"));
            UsageSnapshot s = MuseProvider.Parse(json, 0, "");
            That(s.Status == ProviderStatus.Ok, "status should be Ok");
            That(s.PlanName == "Pro", "tier capitalized to 'Pro', got '" + s.PlanName + "'");
            That(s.Windows.Count == 2, "two windows expected, got " + s.Windows.Count);
            UsageWindow five = WindowOf(s, WindowKind.FiveHour);
            That(five != null && five.UsedPercent == 41, "five-hour 41 percent");
            Near(five.ResetsAtUtc, EpochMs(1794433677000), "five-hour reset");
            UsageWindow week = WindowOf(s, WindowKind.Weekly);
            That(week != null && week.UsedPercent == 63, "weekly 63 percent");
            Near(week.ResetsAtUtc, EpochMs(1794598684000), "weekly reset");
        }

        static MuseProvider MuseReturning(int exitCode, string stdout, string stderr)
        {
            return new MuseProvider(delegate(CancellationToken ct)
            {
                var r = new MuseCliResult();
                r.ExitCode = exitCode;
                r.StdOut = stdout;
                r.StdErr = stderr;
                return Task.FromResult(r);
            });
        }

        static void MuseCliStates()
        {
            UsageSnapshot s = MuseReturning(9009, null,
                "'muse' is not recognized as an internal or external command")
                .FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.NotConfigured, "CLI missing -> NotConfigured");

            s = MuseReturning(1, null, "run muse login first")
                .FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.AuthRequired, "login exit -> AuthRequired");
            That(s.Message == "sign in needed: run muse login", "login message");

            s = MuseReturning(3, null, "kaboom")
                .FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.Error, "other failure -> Error");
        }

        static void OpenCodeGoNotConfigured()
        {
            UsageSnapshot s = new OpenCodeGoProvider()
                .FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.NotConfigured, "opencode-go -> NotConfigured");
            That(s.Message == "no usage API from vendor", "spec message");
        }

    }
}
