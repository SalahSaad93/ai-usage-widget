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
            Check(lines, "provider_default_order", ProviderDefaultOrder);
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
            Check(lines, "row_order_follows_providers", RowOrderFollowsProviders);
            Check(lines, "compact_width_fits_names", CompactWidthFitsNames);
            Check(lines, "default_credential_paths", DefaultCredentialPaths);
            Check(lines, "detection_helper", DetectionHelper);
            Check(lines, "providers_detected", ProvidersDetected);
            Check(lines, "shown_rule", ShownRule);
            Check(lines, "known_tools_suggestions", KnownToolsSuggestions);
            Check(lines, "settings_new_fields", SettingsNewFields);
            Check(lines, "antigravity_synthetic_mapping", AntigravitySyntheticMapping);
            Check(lines, "antigravity_tolerant_shapes", AntigravityTolerantShapes);
            Check(lines, "antigravity_cli_states", AntigravityCliStates);
            Check(lines, "antigravity_detection_and_suggestion", AntigravityDetectionAndSuggestion);
            Check(lines, "stale_row_shows_message", StaleRowShowsMessage);
            Check(lines, "toggle_provider_rule", ToggleProviderRule);
            Check(lines, "log_writes_and_rotates", LogWritesAndRotates);
            Check(lines, "settings_dedupes_id_lists", SettingsDedupesIdLists);
            Check(lines, "serialize_indented_round_trip", SerializeIndentedRoundTrip);
            Check(lines, "install_reverifies_hash", InstallReverifiesHash);
            Check(lines, "errors_describe", ErrorsDescribe);
            Check(lines, "offline_does_not_back_off", OfflineDoesNotBackOff);
            Check(lines, "auth_required_keeps_last_good", AuthRequiredKeepsLastGood);
            Check(lines, "usage_window_extensions", UsageWindowExtensions);
            Check(lines, "claude_extra_windows", ClaudeExtraWindows);
            Check(lines, "codex_review_windows", CodexReviewWindows);
            Check(lines, "painter_extended_rows_and_columns", PainterExtendedRowsAndColumns);
            Check(lines, "update_signature_verifies", UpdateSignatureVerifies);
            Check(lines, "unsigned_build_disables_updates", UnsignedBuildDisablesUpdates);
            Check(lines, "provider_info_catalogue", ProviderInfoCatalogue);
            Check(lines, "consent_rows_plan", ConsentRowsPlan);
            Check(lines, "settings_form_values_round_trip", SettingsFormValuesRoundTrip);
            Check(lines, "history_record_dedup", HistoryRecordDedup);
            Check(lines, "history_prune", HistoryPrune);
            Check(lines, "history_round_trip_and_malformed", HistoryRoundTripAndMalformed);
            Check(lines, "history_file_size", HistoryFileSize);
            Check(lines, "pace_percent_basics", PacePercentBasics);
            Check(lines, "forecast_projection", ForecastProjection);
            Check(lines, "notifier_thresholds_and_reset", NotifierThresholdsAndReset);
            Check(lines, "openrouter_fixture_mapping", OpenRouterFixtureMapping);
            Check(lines, "openrouter_no_limit_and_resets", OpenRouterNoLimitAndResets);
            Check(lines, "openrouter_key_sources", OpenRouterKeySources);
            Check(lines, "cursor_fixture_mapping", CursorFixtureMapping);
            Check(lines, "cursor_token_and_auth_states", CursorTokenAndAuthStates);
            Check(lines, "cursor_sqlite_wrapper", CursorSqliteWrapper);
            Check(lines, "copilot_fixture_mapping", CopilotFixtureMapping);
            Check(lines, "copilot_token_sources", CopilotTokenSources);
            Check(lines, "new_providers_registry_and_help", NewProvidersRegistryAndHelp);

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
            w.Key = "7d"; w.Label = "week · Opus"; w.Primary = false; w.DurationSeconds = 604800; w.Unit = "requests";
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
            That(a.Windows[0].Key == w.Key && a.Windows[0].Label == w.Label && !a.Windows[0].Primary && a.Windows[0].DurationSeconds == 604800 && a.Windows[0].Unit == "requests", "new metadata survives");
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

            public bool IsDetected() { return true; }

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
                new IUsageProvider[] { provider }, clock.UtcNow, cachePath,
                delegate() { return true; });   // pinned online: the check owns the network state
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
                new IUsageProvider[] { provider2 }, clock2.UtcNow, cachePath2,
                delegate() { return true; });
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
            That(s.Providers.Count == 0, "default providers list is empty -> registry order");
            That(s.Update.Mode == "notify" && s.Update.Repo == "SalahSaad93/ai-usage-widget" && !s.Update.RequiresToken,
                "update defaults");

            File.WriteAllText(path, "{\"refreshMinutes\":0,\"custom\":\"keep\"," +
                "\"update\":{\"mode\":\"bogus\",\"repo\":\"owner/repo\",\"extra\":1}}");
            s = Settings.Load(path);
            That(s.RefreshMinutes == 1, "refreshMinutes clamped low");
            That(s.Update.Repo == "owner/repo", "repo read");
            That(s.Update.Mode == "notify", "unknown mode falls back to notify");

            // An existing file that names a mode keeps it (only the default changed);
            // a separate file keeps the unknown keys below intact for the save assertions.
            string modePath = Path.Combine(AppPaths.DataDir, "settings-mode-kept.json");
            File.WriteAllText(modePath, "{\"update\":{\"mode\":\"auto\"}}");
            Settings modeKept = Settings.Load(modePath);
            That(modeKept.Update.Mode == "auto", "existing mode kept");

            s.RefreshMinutes = 500;
            s.Save(path);
            string saved = File.ReadAllText(path);
            That(saved.Contains("\"refreshMinutes\": 120"), "refreshMinutes clamped high on save");
            That(saved.Contains("\"custom\""), "unknown keys preserved on save");
            That(saved.Contains("\"extra\""), "unknown update keys preserved on save");

            s = Settings.Load(path);
            That(s.RefreshMinutes == 120, "clamped value round-trips");

            File.WriteAllText(path, "{ this is not json");
            s = Settings.Load(path);
            That(s.RefreshMinutes == 5, "malformed file falls back to defaults");
        }

        static void ProviderDefaultOrder()
        {
            IUsageProvider[] registry = ProviderRegistry.All;
            string path = Path.Combine(AppPaths.DataDir, "settings-order.json");

            // No settings at all: every provider, enabled, in registry order — except the
            // experimental ones (antigravity, cursor, github-copilot), which are never shown
            // automatically.
            Settings s = Settings.Load(path);
            List<IUsageProvider> merged = s.MergeProviders(registry);
            That(merged.Count == registry.Length - ProviderRegistry.ExperimentalIds.Length,
                "empty settings -> every non-experimental provider, got " + merged.Count);
            for (int i = 0; i < merged.Count; i++)
                That(Array.IndexOf(ProviderRegistry.ExperimentalIds, merged[i].Id) < 0,
                    "experimental provider never auto-shown");
            That(merged[0].Id == "claude-code", "default first provider is claude-code");
            That(merged[1].Id == "codex" && merged[2].Id == "zai" &&
                merged[3].Id == "openrouter" && merged[4].Id == "opencode-go" &&
                merged[5].Id == "muse",
                "registry order kept around the skipped experimental");

            // A single [codex] entry is a real user choice now (the pre-0.2.2 migration
            // hack is gone): codex first, the rest appended in registry order.
            File.WriteAllText(path, "{\"providers\":[{\"id\":\"codex\",\"enabled\":true}]}");
            s = Settings.Load(path);
            merged = s.MergeProviders(registry);
            That(merged.Count == registry.Length - ProviderRegistry.ExperimentalIds.Length,
                "explicit [codex] keeps every non-experimental provider");
            That(merged[0].Id == "codex", "explicit [codex] is honoured, got " + merged[0].Id);

            // An explicit list controls order and enablement; unnamed ids append enabled in
            // registry order (the experimental ones still only by explicit entry).
            File.WriteAllText(path, "{\"providers\":[{\"id\":\"zai\",\"enabled\":true}," +
                "{\"id\":\"codex\",\"enabled\":false}]}");
            s = Settings.Load(path);
            merged = s.MergeProviders(registry);
            That(merged.Count == registry.Length - ProviderRegistry.ExperimentalIds.Length - 1,
                "disabled codex dropped, got " + merged.Count);
            That(merged[0].Id == "zai", "explicit list puts zai first");
            That(merged[1].Id == "claude-code" && merged[2].Id == "openrouter" &&
                merged[3].Id == "opencode-go" && merged[4].Id == "muse",
                "unnamed providers append in registry order");

            // Saving untouched defaults must not write the merged list back: a later load
            // still yields registry order, and the file carries no merged provider ids.
            string freshPath = Path.Combine(AppPaths.DataDir, "settings-order-fresh.json");
            s = Settings.Load(freshPath);   // missing -> empty defaults
            s.Save(freshPath);
            string saved = File.ReadAllText(freshPath);
            That(!saved.Contains("\"claude-code\"") && !saved.Contains("\"muse\""),
                "save writes no merged provider ids, got " + saved);
            s = Settings.Load(freshPath);
            merged = s.MergeProviders(registry);
            That(merged.Count == registry.Length - ProviderRegistry.ExperimentalIds.Length &&
                merged[0].Id == "claude-code",
                "saved empty list keeps registry order");
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
            string missingConfig = Path.Combine(dir, "zcode-config.json");
            // With the key file gone, the fallback sources must also be inert: no real
            // ZCode config is read and no network call is made.
            string oldZai = Environment.GetEnvironmentVariable("ZAI_API_KEY");
            Environment.SetEnvironmentVariable("ZAI_API_KEY", null);
            try
            {
                ZaiProvider p = new ZaiProvider(missing, missingConfig);
                s = p.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
                That(s.Status == ProviderStatus.NotConfigured, "no key source -> NotConfigured");
            }
            finally
            {
                Environment.SetEnvironmentVariable("ZAI_API_KEY", oldZai);
            }
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
            var visibleIds = new List<string>();
            visibleIds.Add("muse");
            List<RowVm> rows = Painter.BuildRows(one, DateTime.UtcNow, visibleIds);
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

        static UsageSnapshot SnapFor(string id, string name)
        {
            var s = new UsageSnapshot();
            s.ProviderId = id;
            s.DisplayName = name;
            s.Status = ProviderStatus.Ok;
            return s;
        }

        static void RowOrderFollowsProviders()
        {
            var snapshots = new List<UsageSnapshot>();
            snapshots.Add(SnapFor("codex", "Codex"));
            snapshots.Add(SnapFor("zai", "Z.ai GLM"));
            snapshots.Add(SnapFor("claude-code", "Claude Code"));
            snapshots.Add(SnapFor("muse", "Muse"));   // not active in this session

            var orderedIds = new List<string>();
            orderedIds.Add("claude-code");
            orderedIds.Add("codex");
            orderedIds.Add("zai");
            List<RowVm> rows = Painter.BuildRows(snapshots, DateTime.UtcNow, orderedIds);
            That(rows.Count == 3, "inactive provider's snapshot is not drawn");
            That(rows[0].Name == "Claude Code" && rows[1].Name == "Codex" && rows[2].Name == "Z.ai GLM",
                "rows follow the active provider order, got " + rows[0].Name + ", " + rows[1].Name +
                ", " + rows[2].Name);
        }

        static void CompactWidthFitsNames()
        {
            using (Painter painter = new Painter(1f))
            {
                var rows = new List<RowVm>();
                rows.Add(new RowVm { Name = "Claude Code", Plan = "Max 20x Team Plan" });
                rows.Add(new RowVm { Name = "Codex" });
                rows[0].Windows.Add(new WindowVm { Kind = WindowKind.FiveHour, Primary = true });
                rows[0].Windows.Add(new WindowVm { Kind = WindowKind.Weekly, Primary = true });
                rows[0].Windows.Add(new WindowVm { Kind = WindowKind.Monthly, Primary = true });
                rows[1].Windows.Add(new WindowVm { Kind = WindowKind.FiveHour, Primary = true });
                rows[1].Windows.Add(new WindowVm { Kind = WindowKind.Weekly, Primary = true });
                rows[1].Windows.Add(new WindowVm { Kind = WindowKind.Monthly, Primary = true });
                float wide = painter.CompactWidthFor(rows);
                That(wide > painter.S(420),
                    "long name + plan needs more than the 420 minimum, got " + wide);

                float none = painter.CompactWidthFor(new List<RowVm>());
                That(none == painter.S(420), "no rows -> the 420 minimum, got " + none);

                var shortRows = new List<RowVm>();
                shortRows.Add(new RowVm { Name = "A" });
                That(painter.CompactWidthFor(shortRows) == painter.S(420),
                    "short rows never grow the window past the minimum");
            }
        }

        static void DefaultCredentialPaths()
        {
            string profile = Path.Combine(Path.GetTempPath(), "profile-fixture");
            string claude = ClaudeCodeProvider.DefaultCredentialsPath(profile);
            That(claude.EndsWith(Path.Combine(".claude", ".credentials.json"), StringComparison.Ordinal),
                "claude path ends with \\.claude\\.credentials.json, got " + claude);

            string codex = CodexProvider.DefaultAuthPath(profile, null);
            That(codex.EndsWith(Path.Combine(".codex", "auth.json"), StringComparison.Ordinal),
                "codex path ends with \\.codex\\auth.json, got " + codex);
            string codexHome = Path.Combine(profile, "custom-home");
            That(CodexProvider.DefaultAuthPath(profile, codexHome) == Path.Combine(codexHome, "auth.json"),
                "CODEX_HOME override wins");

            string zai = ZaiProvider.DefaultAuthPath(profile);
            That(zai.EndsWith(Path.Combine(".local", "share", "opencode", "auth.json"), StringComparison.Ordinal),
                "zai path ends with \\.local\\share\\opencode\\auth.json, got " + zai);
        }

        static void DetectionHelper()
        {
            string root = Path.Combine(AppPaths.DataDir, "detect");
            Directory.CreateDirectory(root);
            string dir = Path.Combine(root, "tools");
            Directory.CreateDirectory(dir);
            string folder = Path.Combine(root, ".some-tool");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(dir, "muse.exe"), "x");
            File.WriteAllText(Path.Combine(dir, "muse.cmd"), "x");
            File.WriteAllText(Path.Combine(dir, "muse.bat"), "x");

            That(Detection.FolderExists(folder), "folder probe");
            That(!Detection.FolderExists(Path.Combine(root, "missing")), "missing folder probe");
            That(!Detection.FolderExists(null), "null folder probe");
            That(Detection.FileExists(Path.Combine(dir, "muse.exe")), "file probe");
            That(!Detection.FileExists(Path.Combine(root, "missing.txt")), "missing file probe");
            That(!Detection.FileExists(null), "null file probe");
            That(Detection.ProfileFolderExists(root, ".some-tool"), "profile folder probe");
            That(!Detection.ProfileFolderExists(root, ".nope"), "profile folder miss");
            That(Detection.ReadTextIfExists(Path.Combine(dir, "muse.exe")) == "x", "read probe");
            That(Detection.ReadTextIfExists(null) == null, "null read probe");

            string pathValue = dir + ";" + root;
            That(Detection.OnPath(pathValue, "muse.exe"), ".exe on PATH");
            That(Detection.OnPath(pathValue, "muse.cmd"), ".cmd on PATH");
            That(Detection.OnPath(pathValue, "muse.bat"), ".bat on PATH");
            That(!Detection.OnPath(pathValue, "absent.exe"), "absent leaf on PATH");
            That(!Detection.OnPath(null, "muse.exe"), "null PATH");
            That(!Detection.OnPath("", "muse.exe"), "empty PATH");
            That(!Detection.OnPath(";; ;", "muse.exe"), "blank PATH segments");
        }

        static void ProvidersDetected()
        {
            string root = Path.Combine(AppPaths.DataDir, "detect-providers");
            Directory.CreateDirectory(root);

            // Claude Code: the credentials file, present or missing.
            string claudeCreds = Path.Combine(root, "claude-credentials.json");
            That(!new ClaudeCodeProvider(claudeCreds).IsDetected(), "claude: missing credentials");
            File.WriteAllText(claudeCreds, "{}");
            That(new ClaudeCodeProvider(claudeCreds).IsDetected(), "claude: credentials file");

            // Codex: the auth file, then the CODEX_HOME source.
            string codexAuth = Path.Combine(root, "codex-auth.json");
            That(!new CodexProvider(codexAuth).IsDetected(), "codex: missing auth");
            File.WriteAllText(codexAuth, "{}");
            That(new CodexProvider(codexAuth).IsDetected(), "codex: auth file");
            string codexHome = Path.Combine(root, "codex-home");
            Directory.CreateDirectory(codexHome);
            File.WriteAllText(Path.Combine(codexHome, "auth.json"), "{}");
            string oldHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
            try
            {
                That(new CodexProvider().IsDetected(), "codex: CODEX_HOME honoured");
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODEX_HOME", oldHome);
            }

            // Z.ai: the three key sources, each isolated, then none.
            string zaiAuth = Path.Combine(root, "zai-auth.json");
            string zaiConfig = Path.Combine(root, "zcode-config.json");
            string oldZai = Environment.GetEnvironmentVariable("ZAI_API_KEY");
            Environment.SetEnvironmentVariable("ZAI_API_KEY", null);
            try
            {
                ZaiProvider zai = new ZaiProvider(zaiAuth, zaiConfig);
                That(!zai.IsDetected(), "zai: no source");
                File.WriteAllText(zaiAuth, "{\"zai-coding-plan\":{\"key\":\"k\"}}");
                That(zai.IsDetected(), "zai: auth.json key");
                File.WriteAllText(zaiAuth, "{\"zai-coding-plan\":{\"key\":\"\"}}");
                Environment.SetEnvironmentVariable("ZAI_API_KEY", "selftest-placeholder");
                That(zai.IsDetected(), "zai: env var after empty auth key");
                Environment.SetEnvironmentVariable("ZAI_API_KEY", null);
                File.WriteAllText(zaiConfig, "{\"provider\":{\"zai\":{\"options\":{\"apiKey\":\"k\"}}}}");
                That(zai.IsDetected(), "zai: ZCode config key");
                File.WriteAllText(zaiConfig, "{\"provider\":{\"zai\":{\"options\":{\"apiKey\":\"\"}}}}");
                That(!zai.IsDetected(), "zai: empty keys are no source");
            }
            finally
            {
                Environment.SetEnvironmentVariable("ZAI_API_KEY", oldZai);
            }

            // OpenCode Go: an "opencode-go" entry in opencode's auth.json.
            string ogAuth = Path.Combine(root, "og-auth.json");
            That(!new OpenCodeGoProvider(ogAuth).IsDetected(), "opencode-go: missing auth");
            File.WriteAllText(ogAuth, "{\"zai-coding-plan\":{\"key\":\"k\"}}");
            That(!new OpenCodeGoProvider(ogAuth).IsDetected(), "opencode-go: no entry");
            File.WriteAllText(ogAuth, "{\"opencode-go\":{}}");
            That(new OpenCodeGoProvider(ogAuth).IsDetected(), "opencode-go: entry present");

            // Muse: on PATH, each extension form, then none.
            string museDir = Path.Combine(root, "muse-bin");
            Directory.CreateDirectory(museDir);
            string fakePath = museDir + ";C:\\nowhere";
            That(!new MuseProvider(null, fakePath).IsDetected(), "muse: not on PATH");
            File.WriteAllText(Path.Combine(museDir, "muse.exe"), "x");
            That(new MuseProvider(null, fakePath).IsDetected(), "muse: muse.exe on PATH");
            File.Delete(Path.Combine(museDir, "muse.exe"));
            File.WriteAllText(Path.Combine(museDir, "muse.cmd"), "x");
            That(new MuseProvider(null, fakePath).IsDetected(), "muse: muse.cmd on PATH");
            File.Delete(Path.Combine(museDir, "muse.cmd"));
            File.WriteAllText(Path.Combine(museDir, "muse.bat"), "x");
            That(new MuseProvider(null, fakePath).IsDetected(), "muse: muse.bat on PATH");
            File.Delete(Path.Combine(museDir, "muse.bat"));
            That(!new MuseProvider(null, "").IsDetected(), "muse: empty PATH");
        }

        sealed class FakeProv : IUsageProvider
        {
            readonly string id;
            public FakeProv(string id) { this.id = id; }
            public string Id { get { return id; } }
            public string DisplayName { get { return id; } }
            public bool IsDetected() { return false; }
            public Task<UsageSnapshot> FetchAsync(CancellationToken ct)
            {
                throw new InvalidOperationException("not fetched in this check");
            }
        }

        static void ShownRule()
        {
            var registry = new IUsageProvider[]
            {
                new FakeProv("claude-code"), new FakeProv("codex"), new FakeProv("zai")
            };
            var entries = new List<ProviderSetting>();
            entries.Add(new ProviderSetting { Id = "codex", Enabled = true });
            entries.Add(new ProviderSetting { Id = "zai", Enabled = false });
            Func<string, bool> claudeDetected = delegate(string id) { return id == "claude-code"; };

            // listed enabled -> shown; listed disabled -> hidden; unlisted detected -> shown.
            List<IUsageProvider> shown = Settings.SelectShown(registry, entries, true, claudeDetected);
            That(shown.Count == 2, "two shown, got " + shown.Count);
            That(shown[0].Id == "codex" && shown[1].Id == "claude-code",
                "listed first, then registry order");

            // unlisted + undetected + autoDetect -> hidden.
            Func<string, bool> none = delegate(string id) { return false; };
            shown = Settings.SelectShown(registry, entries, true, none);
            That(shown.Count == 1 && shown[0].Id == "codex", "undetected unlisted hidden");

            // autoDetect off: unlisted show without any detection signal (the pre-detection
            // behaviour); the listed-disabled zai stays hidden either way.
            shown = Settings.SelectShown(registry, entries, false, none);
            That(shown.Count == 2 && shown[0].Id == "codex" && shown[1].Id == "claude-code",
                "autoDetect off shows unlisted without detection");

            // listed disabled stays hidden even when autoDetect is off.
            var onlyDisabled = new List<ProviderSetting>();
            onlyDisabled.Add(new ProviderSetting { Id = "codex", Enabled = false });
            shown = Settings.SelectShown(registry, onlyDisabled, false, none);
            That(shown.Count == 2 && shown[0].Id == "claude-code" && shown[1].Id == "zai",
                "listed disabled always hidden");

            // empty entries: registry order filtered by detection.
            shown = Settings.SelectShown(registry, new List<ProviderSetting>(), true, claudeDetected);
            That(shown.Count == 1 && shown[0].Id == "claude-code",
                "empty list -> detected only, registry order");

            // Experimental providers (ProviderRegistry.ExperimentalIds) never show when
            // unlisted, whatever autoDetect and detection say; an explicit enabled entry
            // still shows one.
            var experimentalRegistry = new IUsageProvider[]
            {
                new FakeProv("antigravity"), new FakeProv("codex")
            };
            Func<string, bool> everythingDetected = delegate(string id) { return true; };

            shown = Settings.SelectShown(experimentalRegistry, new List<ProviderSetting>(),
                true, everythingDetected);
            That(shown.Count == 1 && shown[0].Id == "codex",
                "unlisted experimental hidden even when detected, got " + shown.Count);

            shown = Settings.SelectShown(experimentalRegistry, new List<ProviderSetting>(),
                false, everythingDetected);
            That(shown.Count == 1 && shown[0].Id == "codex",
                "unlisted experimental hidden with autoDetect off too");

            var experimentalListed = new List<ProviderSetting>();
            experimentalListed.Add(new ProviderSetting { Id = "antigravity", Enabled = true });
            shown = Settings.SelectShown(experimentalRegistry, experimentalListed, true,
                everythingDetected);
            That(shown.Count == 2 && shown[0].Id == "antigravity" && shown[1].Id == "codex",
                "listed enabled still shows the experimental provider");

            var experimentalDisabled = new List<ProviderSetting>();
            experimentalDisabled.Add(new ProviderSetting { Id = "antigravity", Enabled = false });
            shown = Settings.SelectShown(experimentalRegistry, experimentalDisabled, true,
                everythingDetected);
            That(shown.Count == 1 && shown[0].Id == "codex",
                "listed disabled experimental hidden");
        }

        static List<string> DetectedToolIds(string profile, string local, string pathValue)
        {
            var ids = new List<string>();
            foreach (KnownTool tool in KnownTools.All())
                if (KnownTools.IsDetected(tool, profile, local, pathValue)) ids.Add(tool.Id);
            return ids;
        }

        static void KnownToolsSuggestions()
        {
            string root = Path.Combine(AppPaths.DataDir, "known-tools");
            Directory.CreateDirectory(root);
            string profile = Path.Combine(root, "profile");
            Directory.CreateDirectory(profile);
            string local = Path.Combine(root, "local");
            string binDir = Path.Combine(root, "bin");
            Directory.CreateDirectory(binDir);
            string pathValue = binDir + ";C:\\nowhere";

            That(KnownTools.All().Count == 5, "five known tools");
            That(DetectedToolIds(profile, local, pathValue).Count == 0, "nothing detected yet");

            Directory.CreateDirectory(Path.Combine(profile, ".cursor"));
            List<string> ids = DetectedToolIds(profile, local, pathValue);
            That(ids.Contains("cursor") && ids.Count == 1, "cursor via profile folder");

            Directory.CreateDirectory(Path.Combine(local, "github-copilot"));
            ids = DetectedToolIds(profile, local, pathValue);
            That(ids.Contains("github-copilot") && ids.Count == 2, "copilot via local app data");

            // A bare .gemini folder is created by Antigravity too, so it is not a Gemini CLI
            // signal (spec section 13, updated row); only the creds file or PATH counts.
            Directory.CreateDirectory(Path.Combine(profile, ".gemini", "antigravity"));
            ids = DetectedToolIds(profile, local, pathValue);
            That(!ids.Contains("gemini-cli") && ids.Count == 2, "bare .gemini folder is no signal");

            File.WriteAllText(Path.Combine(profile, ".gemini", "oauth_creds.json"), "{}");
            ids = DetectedToolIds(profile, local, pathValue);
            That(ids.Contains("gemini-cli") && ids.Count == 3, "gemini via oauth_creds.json");
            File.Delete(Path.Combine(profile, ".gemini", "oauth_creds.json"));

            File.WriteAllText(Path.Combine(binDir, "gemini.cmd"), "x");
            ids = DetectedToolIds(profile, local, pathValue);
            That(ids.Contains("gemini-cli") && ids.Count == 3, "gemini via PATH");

            Directory.CreateDirectory(Path.Combine(profile, ".grok"));
            ids = DetectedToolIds(profile, local, pathValue);
            That(ids.Contains("grok-build") && ids.Count == 4, "grok via profile folder");

            File.WriteAllText(Path.Combine(binDir, "agy.exe"), "x");
            ids = DetectedToolIds(profile, local, pathValue);
            That(ids.Contains("antigravity") && ids.Count == 5, "antigravity via PATH");

            // Dismissed tools are not suggestions.
            var dismissed = new List<string>();
            dismissed.Add("cursor");
            List<string> suggestions = KnownTools.SuggestedIds(ids, dismissed);
            That(suggestions.Count == 4 && !suggestions.Contains("cursor"), "dismissed filtered");

            // The request URL: exact and encoded; an empty repo disables it.
            string url = KnownTools.IssueUrl("owner/repo", "GitHub Copilot");
            That(url == "https://github.com/owner/repo/issues/new?title=Add+provider:+GitHub+Copilot",
                "request URL, got " + url);
            That(KnownTools.IssueUrl(null, "Cursor") == null, "null repo disables");
            That(KnownTools.IssueUrl("", "Cursor") == null, "empty repo disables");
        }

        static void SettingsNewFields()
        {
            string path = Path.Combine(AppPaths.DataDir, "settings-new-fields.json");
            Settings s = Settings.Load(path);   // missing file
            That(s.AutoDetect, "autoDetect defaults true");
            That(s.DismissedSuggestions.Count == 0 && s.AnnouncedSuggestions.Count == 0
                && s.KnownProviders.Count == 0, "suggestion lists default empty");
            That(!s.HasFile, "missing file -> HasFile false");

            File.WriteAllText(path, "{\"autoDetect\":false," +
                "\"dismissedSuggestions\":[\"cursor\",\"gemini-cli\"]," +
                "\"announcedSuggestions\":[\"grok-build\"]," +
                "\"knownProviders\":[\"codex\",\"zai\"],\"custom\":7}");
            s = Settings.Load(path);
            That(!s.AutoDetect, "autoDetect read");
            That(s.DismissedSuggestions.Count == 2 && s.DismissedSuggestions[0] == "cursor",
                "dismissedSuggestions read");
            That(s.AnnouncedSuggestions.Count == 1 && s.AnnouncedSuggestions[0] == "grok-build",
                "announcedSuggestions read");
            That(s.KnownProviders.Count == 2 && s.KnownProviders[1] == "zai", "knownProviders read");
            That(s.HasFile, "present file -> HasFile true");

            s.AutoDetect = true;
            s.DismissedSuggestions.Add("antigravity");
            s.Save(path);
            string saved = File.ReadAllText(path);
            That(saved.Contains("\"autoDetect\": true"), "autoDetect saved");
            That(saved.Contains("antigravity"), "dismissedSuggestions saved");
            That(saved.Contains("\"custom\""), "unknown keys preserved");
            s = Settings.Load(path);
            That(s.AutoDetect && s.DismissedSuggestions.Count == 3 && s.KnownProviders.Count == 2,
                "new fields round-trip");
        }

        static void AntigravitySyntheticMapping()
        {
            string json = File.ReadAllText(FixturePath("antigravity-usage-synthetic.json"));
            UsageSnapshot s = AntigravityProvider.Parse(json, 0, "",
                new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc));
            That(s.Status == ProviderStatus.Ok, "status should be Ok");
            That(s.PlanName == "Pro", "tier capitalized to 'Pro', got '" + s.PlanName + "'");
            That(s.Windows.Count == 2, "two windows expected, got " + s.Windows.Count);

            // Per kind the bucket closest to its limit wins: 75 % (claude-gpt) over 38 %
            // (gemini) for the 5-hour window, 55 % over 20 % for the weekly window; the
            // null-fraction bucket is ignored (spec section 14).
            UsageWindow five = WindowOf(s, WindowKind.FiveHour);
            That(five != null && five.UsedPercent == 75, "five-hour 75 percent");
            Near(five.ResetsAtUtc, new DateTime(2026, 11, 11, 22, 30, 0, DateTimeKind.Utc),
                "claude 5h reset wins");
            UsageWindow week = WindowOf(s, WindowKind.Weekly);
            That(week != null && week.UsedPercent == 55, "weekly 55 percent");
            Near(week.ResetsAtUtc, new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc),
                "claude weekly reset wins");
        }

        static void AntigravityTolerantShapes()
        {
            // camelCase keys, buckets nested under groups[].buckets[] with the kind only in
            // displayName, and reset_in_seconds without reset_time (spec section 14).
            string json = "{\"plan\":\"max\",\"groups\":[{\"buckets\":[" +
                "{\"displayName\":\"5-hour limit\",\"remainingFraction\":0.5," +
                "\"reset_in_seconds\":600}," +
                "{\"displayName\":\"Weekly limit\",\"remainingFraction\":0.2," +
                "\"resetTime\":\"2026-07-01T00:00:00Z\"}]}]}";
            var now = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);
            UsageSnapshot s = AntigravityProvider.Parse(json, 0, "", now);
            That(s.Status == ProviderStatus.Ok, "status should be Ok");
            That(s.PlanName == "Max", "plan capitalized to 'Max', got '" + s.PlanName + "'");

            UsageWindow five = WindowOf(s, WindowKind.FiveHour);
            That(five != null && five.UsedPercent == 50, "camelCase fraction mapped");
            Near(five.ResetsAtUtc, now.AddSeconds(600), "reset_in_seconds relative to now");

            UsageWindow week = WindowOf(s, WindowKind.Weekly);
            That(week != null && week.UsedPercent == 80, "displayName kind mapped");
            Near(week.ResetsAtUtc, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                "camelCase resetTime mapped");
        }

        static AntigravityProvider AgyReturning(int exitCode, string stdout, string stderr)
        {
            return new AntigravityProvider(delegate(CancellationToken ct)
            {
                var r = new CliResult();
                r.ExitCode = exitCode;
                r.StdOut = stdout;
                r.StdErr = stderr;
                return Task.FromResult(r);
            }, null);
        }

        static void AntigravityCliStates()
        {
            UsageSnapshot s = AgyReturning(9009, null,
                "'agy' is not recognized as an internal or external command")
                .FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.NotConfigured, "CLI missing -> NotConfigured");

            s = AgyReturning(1, null, "please sign in first")
                .FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.AuthRequired, "explicit phrase on failure -> AuthRequired");
            That(s.Message == "sign in needed: run agy login", "login message");

            // A non-zero exit with unrelated stderr: Error with the first stderr line,
            // not auth (the old rule turned any failure into "sign in needed").
            s = AgyReturning(3, null, "boom\r\nstack trace")
                .FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.Error, "unrelated failure -> Error");
            That(s.Message == "boom", "first non-empty stderr line, got '" + s.Message + "'");

            // "authenticated": true in valid buckets with exit 0: Ok despite the word
            // ("auth" alone is never a signal).
            s = AgyReturning(0, "{\"authenticated\":true,\"usage\":{\"five_hour\":" +
                "{\"remaining_fraction\":0.5}}}", "")
                .FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.Ok, "'authenticated': true with buckets -> Ok, got " + s.Status);
            That(s.Windows.Count == 1, "the bucket still maps to a window");

            // An "author" field with no buckets is not auth either: unexpected response.
            s = AgyReturning(0, "{\"author\":\"someone\"}", "")
                .FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.Error && s.Message == "unexpected response",
                "no buckets on exit 0 -> unexpected response, got '" + s.Message + "'");

            // An explicit phrase on success with no buckets: still an auth problem.
            s = AgyReturning(0, "please log in at https://portal.example", "")
                .FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.AuthRequired, "explicit phrase on success -> AuthRequired");

            s = new AntigravityProvider(delegate(CancellationToken ct)
            {
                var r = new CliResult();
                r.ExitCode = -1;
                r.TimedOut = true;
                r.StdErr = "timeout after 15 s";
                return Task.FromResult(r);
            }, null).FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.Error, "timeout -> Error");

            s = AgyReturning(0, "{}", "")
                .FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.Error && s.Message == "unexpected response",
                "valid JSON with no buckets -> unexpected response");

            s = AgyReturning(0, "not json", "")
                .FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.Error, "non-JSON -> Error");
        }

        static void AntigravityDetectionAndSuggestion()
        {
            string root = Path.Combine(AppPaths.DataDir, "antigravity-detect");
            Directory.CreateDirectory(root);
            string profile = Path.Combine(root, "profile");
            Directory.CreateDirectory(profile);
            string binDir = Path.Combine(root, "bin");
            Directory.CreateDirectory(binDir);
            string pathValue = binDir + ";C:\\nowhere";

            AntigravityProvider provider = new AntigravityProvider(null, pathValue);
            KnownTool tool = null;
            foreach (KnownTool t in KnownTools.All())
                if (t.Id == "antigravity") tool = t;

            // Only the .antigravity desktop folder: the provider is NOT detected (no agy CLI
            // on PATH, spec section 14) and the tool suggestion stays present.
            Directory.CreateDirectory(Path.Combine(profile, ".antigravity"));
            That(!provider.IsDetected(), "desktop app alone -> provider not detected");
            That(KnownTools.IsDetected(tool, profile, null, pathValue),
                "desktop app alone -> suggestion present");

            // agy.cmd on the fake PATH: provider detected, suggestion suppressed.
            File.WriteAllText(Path.Combine(binDir, "agy.cmd"), "x");
            That(provider.IsDetected(), "agy.cmd on PATH -> provider detected");
            var detected = new List<string>();
            detected.Add("antigravity");
            var covered = new List<string>();
            covered.Add("antigravity");
            List<string> suggestions = KnownTools.SuggestedIds(detected, null, covered);
            That(!suggestions.Contains("antigravity"), "provider covers the suggestion");
            That(KnownTools.SuggestedIds(detected, null, null).Contains("antigravity"),
                "without coverage the suggestion stays");

            That(KnownTools.HelpText("antigravity") == "Install the agy CLI and sign in to see usage",
                "tray help line");
            That(KnownTools.HelpText("gemini-cli") == null, "no help line for other tools");
        }

        // A Stale row's tag gains the snapshot's own message: "stale 42m · sign in needed:
        // open Claude Code"; without a message the plain tag stays.
        static void StaleRowShowsMessage()
        {
            DateTime now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
            var snapshots = new List<UsageSnapshot>();

            UsageSnapshot withMessage = SnapFor("claude-code", "Claude Code");
            withMessage.Status = ProviderStatus.Stale;
            withMessage.Message = "sign in needed: open Claude Code";
            withMessage.FetchedAtUtc = now.AddMinutes(-42);
            snapshots.Add(withMessage);

            UsageSnapshot silent = SnapFor("codex", "Codex");
            silent.Status = ProviderStatus.Stale;
            silent.FetchedAtUtc = now.AddMinutes(-42);
            snapshots.Add(silent);

            var ids = new List<string>();
            ids.Add("claude-code");
            ids.Add("codex");
            List<RowVm> rows = Painter.BuildRows(snapshots, now, ids);
            That(rows.Count == 2, "two stale rows, got " + rows.Count);
            That(rows[0].Msg == "stale 42m · sign in needed: open Claude Code",
                "stale row appends the snapshot message, got '" + rows[0].Msg + "'");
            That(rows[1].Msg == "stale 42m",
                "stale row without a message keeps the plain tag, got '" + rows[1].Msg + "'");
        }

        // The provider toggle rule: the new state is the opposite of whether the provider is
        // shown right now, so one click hides an auto-detected provider.
        static void ToggleProviderRule()
        {
            var entries = new List<ProviderSetting>();

            WidgetForm.ApplyProviderToggle(entries, "codex", true);
            That(entries.Count == 1 && entries[0].Id == "codex" && !entries[0].Enabled,
                "unlisted and shown -> listed disabled");

            WidgetForm.ApplyProviderToggle(entries, "muse", false);
            That(entries.Count == 2 && entries[1].Id == "muse" && entries[1].Enabled,
                "unlisted and hidden -> listed enabled");

            WidgetForm.ApplyProviderToggle(entries, "codex", true);
            That(entries.Count == 2 && !entries[0].Enabled, "listed shown flips to disabled");

            WidgetForm.ApplyProviderToggle(entries, "codex", false);
            That(entries.Count == 2 && entries[0].Enabled, "listed hidden flips to enabled");

            WidgetForm.ApplyProviderToggle(entries, "codex", false);
            That(entries.Count == 2 && entries[0].Enabled, "repeated toggles add no duplicate entries");
        }

        static void LogWritesAndRotates()
        {
            string logPath = AppPaths.LogPath;
            if (File.Exists(logPath)) File.Delete(logPath);
            string oldPath = Path.Combine(Path.GetDirectoryName(logPath), "log.old.txt");
            if (File.Exists(oldPath)) File.Delete(oldPath);

            Log.Info("selftest start");
            Log.Error("selftest failure", new Exception("kaboom"));
            string[] lines = File.ReadAllLines(logPath);
            That(lines.Length == 2, "two entries written, got " + lines.Length);
            That(lines[0].EndsWith("INFO selftest start"), "info line, got " + lines[0]);
            That(lines[1].Contains("ERROR selftest failure") && lines[1].Contains("kaboom"),
                "error line carries the message and exception, got " + lines[1]);
            DateTime stamp;
            That(DateTime.TryParseExact(lines[0].Substring(0, 20), "yyyy-MM-ddTHH:mm:ss'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out stamp),
                "ISO-8601 UTC timestamp prefix, got " + lines[0]);

            // Push the file past 256 KB: the next write rotates it into log.old.txt.
            Log.Info(new string('a', 300 * 1024));
            Log.Info("after rotation");
            That(File.Exists(oldPath), "oversized log renamed to log.old.txt");
            That(File.ReadAllText(oldPath).Contains("selftest start"), "rotated entries kept");
            lines = File.ReadAllLines(logPath);
            That(lines.Length == 1 && lines[0].EndsWith("INFO after rotation"),
                "fresh log.txt holds only post-rotation entries");

            // A second rotation replaces the previous log.old.txt.
            Log.Info(new string('b', 300 * 1024));
            Log.Info("after second rotation");
            string oldText = File.ReadAllText(oldPath);
            That(oldText.Contains("after rotation") && oldText.Contains(new string('b', 1024)),
                "second rotation replaces the previous log.old.txt");
            // "after second rotation" does not contain "after rotation" as a substring, so
            // these two lines prove log.txt holds only post-second-rotation content.
            That(!File.ReadAllText(logPath).Contains("after rotation")
                && File.ReadAllText(logPath).Contains("after second rotation"),
                "rotated content left log.txt");
        }

        static void SettingsDedupesIdLists()
        {
            string path = Path.Combine(AppPaths.DataDir, "settings-dedupe.json");
            File.WriteAllText(path,
                "{\"knownProviders\":[\"codex\",\"zai\",\"codex\"]," +
                "\"dismissedSuggestions\":[\"cursor\",\"cursor\",\"gemini-cli\"]," +
                "\"announcedSuggestions\":[\"grok-build\",\"grok-build\"]}");
            Settings s = Settings.Load(path);
            That(s.KnownProviders.Count == 2 && s.KnownProviders[0] == "codex"
                && s.KnownProviders[1] == "zai",
                "knownProviders deduplicated in order, got " + string.Join(",", s.KnownProviders.ToArray()));
            That(s.DismissedSuggestions.Count == 2 && s.DismissedSuggestions[0] == "cursor"
                && s.DismissedSuggestions[1] == "gemini-cli", "dismissedSuggestions deduplicated");
            That(s.AnnouncedSuggestions.Count == 1 && s.AnnouncedSuggestions[0] == "grok-build",
                "announcedSuggestions deduplicated");
        }

        static void SerializeIndentedRoundTrip()
        {
            var inner = new Dictionary<string, object>();
            inner["n"] = 41.5;
            inner["on"] = true;
            inner["empty"] = new Dictionary<string, object>();
            inner["list"] = new List<object>();
            var root = new Dictionary<string, object>();
            root["tricky"] = "quote \" brace { } bracket [ ] comma , colon : backslash \\ end\\\"";
            root["plain"] = "value";
            root["nested"] = inner;

            string indented = Json.SerializeIndented(root);
            var back = Json.ParseObject(indented);
            That(back != null, "indented output parses back");
            That(Json.GetStr(back, "tricky") == (string)root["tricky"],
                "tricky string survives, got '" + Json.GetStr(back, "tricky") + "'");
            That(Json.GetStr(back, "plain") == "value", "plain string survives");
            Dictionary<string, object> nestedBack = Json.GetObject(back, "nested");
            That(nestedBack != null, "nested object survives");
            double? n = Json.GetDouble(nestedBack, "n");
            That(n.HasValue && n.Value == 41.5, "nested number survives");
            That(Json.GetBool(nestedBack, "on", false), "nested bool survives");
            List<object> list = Json.GetArray(nestedBack, "list");
            That(list != null && list.Count == 0, "empty list survives");
            That(Json.GetObject(nestedBack, "empty") != null, "empty object survives");

            // The settings save path (indented) still round-trips unknown keys.
            string path = Path.Combine(AppPaths.DataDir, "settings-indented.json");
            File.WriteAllText(path, "{\"custom\":\"keep \\\"quoted\\\" {braced}\"}");
            Settings s = Settings.Load(path);
            s.Save(path);
            s = Settings.Load(path);
            That(Json.GetStr(Json.ParseObject(File.ReadAllText(path)), "custom")
                == "keep \"quoted\" {braced}", "unknown key with tricky string survives a save");
        }

        static string Sha256Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        static void InstallReverifiesHash()
        {
            string dir = Path.Combine(AppPaths.DataDir, "install");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            string running = Path.Combine(dir, "AiUsageWidget.exe");
            File.WriteAllText(running, "OLD");
            Directory.CreateDirectory(AppPaths.UpdateDir);
            string fresh = Path.Combine(AppPaths.UpdateDir, "AiUsageWidget.exe");

            var cfg = new UpdateSettings();
            cfg.Mode = "notify";
            Updater updater = new Updater(cfg, running, new Version("0.3.2"));
            string sha = Sha256Hex(Encoding.UTF8.GetBytes("NEW"));

            // The verified file is tampered with before the user clicks install: refuse,
            // delete it, clear the pending state and report "update failed".
            File.WriteAllText(fresh, "NEW");
            updater.MarkPending("9.9.9", sha);
            File.WriteAllText(fresh, "TAMPERED");
            string launched = null;
            bool ok = updater.InstallPendingFile(fresh, delegate(string p) { launched = p; });
            That(!ok, "tampered download is not installed");
            That(!File.Exists(fresh), "tampered download deleted");
            That(updater.StatusText == "update failed",
                "status is update failed, got '" + updater.StatusText + "'");
            That(!updater.PendingInstall && updater.PendingVersion == null, "pending state cleared");
            That(launched == null && File.ReadAllText(running) == "OLD", "running exe untouched");

            // A download still matching the remembered hash installs normally.
            File.WriteAllText(fresh, "NEW");
            updater.MarkPending("9.9.9", sha);
            ok = updater.InstallPendingFile(fresh, delegate(string p) { launched = p; });
            That(ok, "matching download installs");
            That(launched == running, "new exe launched at the running path");
            That(File.ReadAllText(running) == "NEW", "new exe takes the running path");
            That(updater.StatusText == "" && !updater.PendingInstall && updater.PendingVersion == null,
                "successful install clears the pending state");

            // A missing file fails safe too.
            updater.MarkPending("9.9.9", sha);
            launched = null;
            ok = updater.InstallPendingFile(Path.Combine(dir, "missing.exe"),
                delegate(string p) { launched = p; });
            That(!ok && updater.StatusText == "update failed", "missing download -> update failed");
            That(launched == null, "nothing launched for a missing download");
        }

        // --- v0.3.2 checks ---------------------------------------------------------------

        static void ErrorsDescribe()
        {
            That(Errors.Describe(new System.Net.Http.HttpRequestException(
                "An error occurred while sending the request",
                new System.Net.WebException("The remote name could not be resolved",
                    System.Net.WebExceptionStatus.NameResolutionFailure)))
                == "can't reach server", "name resolution reads as can't reach server");

            That(Errors.Describe(new System.Net.Http.HttpRequestException(
                "An error occurred while sending the request",
                new System.Net.WebException("could not connect",
                    System.Net.WebExceptionStatus.ConnectFailure)))
                == "can't reach server", "connect failure reads as can't reach server");

            That(Errors.Describe(new System.Net.Http.HttpRequestException("send failed",
                new System.Net.Sockets.SocketException(10061)))
                == "can't reach server", "connection refused reads as can't reach server");

            That(Errors.Describe(new System.Net.Http.HttpRequestException("send failed",
                new System.Security.Authentication.AuthenticationException("TLS handshake failed")))
                == "secure connection failed", "TLS exception reads as secure connection failed");

            That(Errors.Describe(new System.Net.Http.HttpRequestException("send failed",
                new System.Net.WebException("chain broken",
                    System.Net.WebExceptionStatus.SecureChannelFailure)))
                == "secure connection failed", "SecureChannelFailure reads the same");

            // Anything else: the innermost message, cut to 60 characters.
            string longMessage = new string('x', 100);
            That(Errors.Describe(new System.Net.Http.HttpRequestException(
                "An error occurred while sending the request",
                new InvalidOperationException(longMessage))) == longMessage.Substring(0, 60),
                "innermost message cut to 60");

            That(Errors.Describe(new System.Net.Http.HttpRequestException(
                "An error occurred while sending the request"))
                == "An error occurred while sending the request",
                "no inner -> the exception's own message");
        }

        static void OfflineDoesNotBackOff()
        {
            DateTime t0 = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
            string cachePath = Path.Combine(AppPaths.DataDir, "cache-offline.json");
            FakeClock clock = new FakeClock();
            clock.Now = t0;
            FakeProvider provider = new FakeProvider();
            provider.Status = ProviderStatus.Ok;
            bool networkUp = false;
            Func<bool> isOnline = delegate() { return networkUp; };

            RefreshService svc = new RefreshService(
                new IUsageProvider[] { provider }, clock.UtcNow, cachePath, isOnline);
            svc.RunOnceAsync(false).GetAwaiter().GetResult();
            That(svc.Snapshots[0].Status == ProviderStatus.Ok, "first fetch is Ok");

            provider.Status = ProviderStatus.Error;
            provider.Message = "boom";
            clock.Now = t0.AddMinutes(1);
            svc.RunOnceAsync(false).GetAwaiter().GetResult();
            That(svc.Snapshots[0].Status == ProviderStatus.Stale,
                "offline error keeps last good as Stale");
            That(svc.Snapshots[0].FetchedAtUtc == t0, "stale keeps the original fetch time");
            That(!svc.IsSkipped("fake", clock.Now.AddSeconds(1)), "offline does not back off");

            clock.Now = t0.AddMinutes(2);
            svc.RunOnceAsync(false).GetAwaiter().GetResult();
            That(provider.Calls == 3, "scheduled refresh still runs while offline");

            // The network returns: the stale row is replaced by a fresh Ok.
            networkUp = true;
            provider.Status = ProviderStatus.Ok;
            provider.Message = null;
            clock.Now = t0.AddMinutes(3);
            svc.RunOnceAsync(false).GetAwaiter().GetResult();
            That(provider.Calls == 4, "fetch after reconnect");
            That(svc.Snapshots[0].Status == ProviderStatus.Ok, "recovers to Ok when online");

            // With no last-good data, offline shows an Error row saying "offline", no backoff.
            FakeProvider provider2 = new FakeProvider();
            provider2.Status = ProviderStatus.Error;
            provider2.Message = "boom";
            string cachePath2 = Path.Combine(AppPaths.DataDir, "cache-offline2.json");
            RefreshService svc2 = new RefreshService(
                new IUsageProvider[] { provider2 }, clock.UtcNow, cachePath2, isOnline);
            networkUp = false;
            clock.Now = t0.AddMinutes(4);
            svc2.RunOnceAsync(false).GetAwaiter().GetResult();
            That(svc2.Snapshots[0].Status == ProviderStatus.Error, "no last good -> Error row");
            That(svc2.Snapshots[0].Message == "offline", "message is 'offline', got '"
                + svc2.Snapshots[0].Message + "'");
            That(!svc2.IsSkipped("fake", clock.Now.AddSeconds(1)),
                "no backoff offline without last good either");
            That(!File.Exists(cachePath2), "Error still not persisted");

            networkUp = true;
            provider2.Status = ProviderStatus.Ok;
            provider2.Message = null;
            clock.Now = t0.AddMinutes(5);
            svc2.RunOnceAsync(false).GetAwaiter().GetResult();
            That(svc2.Snapshots[0].Status == ProviderStatus.Ok, "reconnect replaces the offline row");
        }

        static void AuthRequiredKeepsLastGood()
        {
            DateTime t0 = new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc);
            string cachePath = Path.Combine(AppPaths.DataDir, "cache-authkeep.json");
            FakeClock clock = new FakeClock();
            clock.Now = t0;
            FakeProvider provider = new FakeProvider();
            provider.Status = ProviderStatus.Ok;

            RefreshService svc = new RefreshService(
                new IUsageProvider[] { provider }, clock.UtcNow, cachePath);
            svc.RunOnceAsync(false).GetAwaiter().GetResult();
            That(svc.Snapshots[0].Status == ProviderStatus.Ok, "first fetch is Ok with windows");

            // The login expires: the row must keep its last-known percentages.
            provider.Status = ProviderStatus.AuthRequired;
            provider.Message = "sign in needed: open Claude Code";
            clock.Now = t0.AddMinutes(42);
            svc.RunOnceAsync(false).GetAwaiter().GetResult();
            That(svc.Snapshots[0].Status == ProviderStatus.Stale,
                "auth keeps last good as Stale, got " + svc.Snapshots[0].Status);
            That(svc.Snapshots[0].Message == "sign in needed: open Claude Code",
                "auth message carried over, got '" + svc.Snapshots[0].Message + "'");
            That(svc.Snapshots[0].FetchedAtUtc == t0, "FetchedAtUtc kept so the tag shows its age");
            That(svc.Snapshots[0].Windows.Count == 1, "windows kept");
            That(!svc.IsSkipped("fake", clock.Now.AddSeconds(1)), "auth does not back off");
            That(File.Exists(cachePath), "persisted");

            // A later Ok replaces the row and clears the message.
            provider.Status = ProviderStatus.Ok;
            provider.Message = null;
            clock.Now = t0.AddMinutes(50);
            svc.RunOnceAsync(false).GetAwaiter().GetResult();
            That(svc.Snapshots[0].Status == ProviderStatus.Ok, "recovers to Ok");
            That(string.IsNullOrEmpty(svc.Snapshots[0].Message), "a later Ok clears the message");
            That(svc.Snapshots[0].FetchedAtUtc == t0.AddMinutes(50), "re-stamped with the new time");

            // Without previous windows, behave as today: AuthRequired replaces the snapshot.
            FakeClock clock2 = new FakeClock();
            clock2.Now = t0;
            FakeProvider fresh = new FakeProvider();
            fresh.Status = ProviderStatus.AuthRequired;
            fresh.Message = "sign in needed";
            string cachePath2 = Path.Combine(AppPaths.DataDir, "cache-authkeep2.json");
            RefreshService svc2 = new RefreshService(
                new IUsageProvider[] { fresh }, clock2.UtcNow, cachePath2);
            svc2.RunOnceAsync(false).GetAwaiter().GetResult();
            That(svc2.Snapshots[0].Status == ProviderStatus.AuthRequired,
                "no previous windows -> AuthRequired replaces, as today");
            That(File.Exists(cachePath2), "AuthRequired is persisted as today");
        }

        static void UsageWindowExtensions()
        {
            UsageWindow w = new UsageWindow();
            That(w.Primary && w.Key == null && w.Label == null && !w.DurationSeconds.HasValue && w.Unit == null, "model defaults");
            That(UsageWindow.KindForDuration(18000) == WindowKind.FiveHour, "5h");
            That(UsageWindow.KindForDuration(86400) == WindowKind.Daily, "day");
            That(UsageWindow.KindForDuration(604800) == WindowKind.Weekly, "week");
            That(UsageWindow.KindForDuration(2419200) == WindowKind.Monthly, "28d");
            That(UsageWindow.KindForDuration(3600) == WindowKind.Other, "other");
            string path = Path.Combine(AppPaths.DataDir, "cache-old-format.json");
            File.WriteAllText(path, "{\"snapshots\":[{\"providerId\":\"codex\",\"windows\":[{\"kind\":\"Weekly\",\"usedPercent\":10}]}]}");
            UsageWindow old = SnapshotCache.Load(path)[0].Windows[0];
            That(old.Primary && old.Key == null && old.Label == null && !old.DurationSeconds.HasValue && old.Unit == null, "old cache defaults");
        }

        static void ClaudeExtraWindows()
        {
            string json = "{\"five_hour\":null,\"seven_day\":null,\"seven_day_opus\":{\"utilization\":25},\"seven_day_sonnet\":{\"utilization\":35},\"extra_usage\":{\"is_enabled\":true,\"utilization\":12,\"used_credits\":7,\"monthly_limit\":100}}";
            UsageSnapshot s = ClaudeCodeProvider.Parse(json, "");
            That(s.Windows.Count == 3 && !s.Windows[0].Primary, "supplemental blocks parsed");
            That(s.Windows[0].Label == "week · Opus" && s.Windows[1].Label == "week · Sonnet", "weekly labels");
            UsageWindow extra = s.Windows[2]; That(extra.Kind == WindowKind.Other && extra.Unit == "credits" && extra.Used == 7 && extra.Limit == 100 && !extra.ResetsAtUtc.HasValue, "extra credits");
        }

        static void CodexReviewWindows()
        {
            string json = "{\"rate_limit\":{\"primary_window\":{\"used_percent\":10,\"limit_window_seconds\":604800}},\"code_review_rate_limit\":{\"primary_window\":{\"used_percent\":20,\"limit_window_seconds\":18000},\"secondary_window\":{\"used_percent\":30,\"limit_window_seconds\":604800}}}";
            UsageSnapshot s = CodexProvider.Parse(json);
            That(s.Windows.Count == 3 && !s.Windows[1].Primary && s.Windows[1].Label == "code review · 5 hours" && s.Windows[2].Label == "code review · week", "review windows mapped");
        }

        static void PainterExtendedRowsAndColumns()
        {
            var sn = SnapFor("codex", "Codex");
            UsageWindow e = new UsageWindow { Kind = WindowKind.Weekly, Label = "week · Opus", Primary = false, Used = 7, Limit = 100, UsedPercent = 7 };
            UsageWindow p = new UsageWindow { Kind = WindowKind.Weekly, UsedPercent = 1 };
            sn.Windows.Add(e); sn.Windows.Add(p);
            List<RowVm> rows = Painter.BuildRows(new List<UsageSnapshot> { sn }, DateTime.UtcNow, new List<string> { "codex" });
            That(rows[0].Windows[0].Primary && rows[0].Windows[1].Detail == "7 / 100", "primary ordering and detail");
            using (Painter painter = new Painter(1f))
            {
                var three = new List<RowVm> { new RowVm() };
                three[0].Windows.Add(new WindowVm { Kind = WindowKind.FiveHour, Primary = true }); three[0].Windows.Add(new WindowVm { Kind = WindowKind.Weekly, Primary = true }); three[0].Windows.Add(new WindowVm { Kind = WindowKind.Monthly, Primary = true });
                That(painter.CompactWidthFor(three) == painter.S(420), "today columns minimum");
                var weekly = new List<RowVm> { new RowVm() }; weekly[0].Windows.Add(new WindowVm { Kind = WindowKind.Weekly, Primary = true });
                That(painter.CompactWidthFor(weekly) == painter.S(420), "weekly column");
                var daily = new List<RowVm> { new RowVm() }; daily[0].Windows.Add(new WindowVm { Kind = WindowKind.Daily, Primary = true });
                That(painter.CompactWidthFor(daily) == painter.S(420), "daily column");
            }
        }

        static void UpdateSignatureVerifies()
        {
            byte[] manifest = Encoding.UTF8.GetBytes("{\"version\":\"0.4.0\"}");
            byte[] signature;
            string publicKey;
            using (var rsa = new RSACryptoServiceProvider(3072))
            {
                publicKey = rsa.ToXmlString(false);
                signature = rsa.SignData(manifest, CryptoConfig.MapNameToOID("SHA256"));
            }
            string signatureBase64 = Convert.ToBase64String(signature);
            That(Updater.VerifySignature(manifest, signatureBase64, publicKey), "valid signature");
            That(!Updater.VerifySignature(Encoding.UTF8.GetBytes("tampered"), signatureBase64, publicKey), "tampered manifest");
            That(!Updater.VerifySignature(manifest, Convert.ToBase64String(new byte[] { 1, 2, 3 }), publicKey), "tampered signature");
            using (var wrong = new RSACryptoServiceProvider(3072))
                That(!Updater.VerifySignature(manifest, signatureBase64, wrong.ToXmlString(false)), "wrong key");
            That(!Updater.VerifySignature(manifest, signatureBase64, ""), "empty key");
            That(!Updater.VerifySignature(manifest, "not base64!", publicKey), "garbage Base64");
        }

        static void UnsignedBuildDisablesUpdates()
        {
            var cfg = new UpdateSettings();
            cfg.Mode = "auto";
            var updater = new Updater(cfg, "unused.exe", new Version(0, 1), "");
            updater.CheckAsync().GetAwaiter().GetResult();
            That(updater.StatusText == "updates disabled (unsigned build)", "unsigned status");
        }

        // --- v0.4.0 checks ---------------------------------------------------------------

        static void ProviderInfoCatalogue()
        {
            // Every registered provider id must have a real catalogue entry with text.
            foreach (IUsageProvider p in ProviderRegistry.All)
            {
                ProviderInfoEntry found = null;
                foreach (ProviderInfoEntry e in ProviderInfo.Entries)
                    if (e.Id == p.Id) found = e;
                That(found != null, "registered provider " + p.Id + " has a catalogue entry");
                That(!string.IsNullOrEmpty(found.Reads) && !string.IsNullOrEmpty(found.Contacts),
                    "catalogue text non-empty for " + p.Id);
            }

            // The full documented catalogue, including ids not registered yet.
            string[] ids = { "claude-code", "codex", "zai", "cursor", "github-copilot",
                "openrouter", "antigravity", "opencode-go", "muse" };
            foreach (string id in ids)
                That(ProviderInfo.For(id) != null, "catalogue covers " + id);

            // Official only for OpenRouter, Muse and Antigravity.
            That(ProviderInfo.For("openrouter").Official && ProviderInfo.For("muse").Official
                && ProviderInfo.For("antigravity").Official,
                "official only for openrouter, muse, antigravity");
            That(!ProviderInfo.For("claude-code").Official && !ProviderInfo.For("codex").Official
                && !ProviderInfo.For("zai").Official && !ProviderInfo.For("cursor").Official
                && !ProviderInfo.For("github-copilot").Official
                && !ProviderInfo.For("opencode-go").Official, "unofficial providers marked");

            // Unknown and null ids fall back to non-empty generic text.
            ProviderInfoEntry generic = ProviderInfo.For("no-such-provider");
            That(generic != null && !string.IsNullOrEmpty(generic.Reads)
                && !string.IsNullOrEmpty(generic.Contacts) && !generic.Official,
                "unknown id falls back to generic text");
            That(ProviderInfo.For(null) != null, "null id falls back too");
        }

        static void ConsentRowsPlan()
        {
            var providers = new List<IUsageProvider>();
            providers.Add(new FakeProv("claude-code"));
            providers.Add(new FakeProv("codex"));
            providers.Add(new FakeProv("antigravity"));
            providers.Add(new FakeProv("zai"));
            providers.Add(new FakeProv("muse"));

            // Claude detected and the experimental agy CLI detected: both come first, but
            // only plain detected providers tick.
            Func<string, bool> detectAgy = delegate(string id)
            {
                return id == "claude-code" || id == "antigravity";
            };
            List<ConsentForm.ConsentRow> rows = ConsentForm.PlanRows(providers, detectAgy);
            That(rows.Count == 5, "every provider planned");
            That(rows[0].Provider.Id == "claude-code" && rows[0].Checked && rows[0].Mark == null,
                "detected first and ticked");
            That(rows[1].Provider.Id == "antigravity" && !rows[1].Checked
                && rows[1].Mark == ConsentForm.ExperimentalMark,
                "detected experimental unticked and marked");
            That(rows[2].Provider.Id == "codex" && !rows[2].Checked
                && rows[2].Mark == ConsentForm.NotFoundMark, "undetected unticked and marked");
            That(rows[3].Provider.Id == "zai" && !rows[3].Checked, "undetected keeps given order");
            That(rows[4].Provider.Id == "muse" && !rows[4].Checked, "undetected muse last");

            // Undetected experimental: the experimental mark wins over "not found".
            Func<string, bool> detectClaude = delegate(string id) { return id == "claude-code"; };
            rows = ConsentForm.PlanRows(providers, detectClaude);
            That(rows[1].Provider.Id == "codex" && !rows[1].Checked
                && rows[1].Mark == ConsentForm.NotFoundMark, "undetected marked not found");
            That(rows[2].Provider.Id == "antigravity" && !rows[2].Checked
                && rows[2].Mark == ConsentForm.ExperimentalMark,
                "experimental mark wins over not found");

            // No detector or no providers: nothing ticked, no crash. Everything is
            // undetected now, so the given order is kept throughout.
            rows = ConsentForm.PlanRows(providers, null);
            That(rows.Count == 5 && !rows[0].Checked
                && rows[2].Mark == ConsentForm.ExperimentalMark,
                "no detector -> nothing ticked, experimental still marked");
            That(ConsentForm.PlanRows(null, detectClaude).Count == 0, "null providers -> empty plan");
            That(ConsentForm.PlanRows(new List<IUsageProvider>(), detectClaude).Count == 0,
                "no providers -> empty plan");

            // A throwing detector must not break the plan (treated as not detected).
            Func<string, bool> exploding = delegate(string id)
            {
                throw new InvalidOperationException("boom");
            };
            rows = ConsentForm.PlanRows(providers, exploding);
            That(rows.Count == 5 && !rows[0].Checked, "throwing detector reads as not detected");
        }

        static void SettingsFormValuesRoundTrip()
        {
            string path = Path.Combine(AppPaths.DataDir, "settings-form-values.json");
            File.WriteAllText(path, "{\"refreshMinutes\":150,\"layout\":\"compact\"," +
                "\"alwaysOnTop\":false,\"theme\":\"light\",\"opacity\":55,\"warnPercent\":30," +
                "\"criticalPercent\":31,\"autoDetect\":false," +
                "\"providers\":[{\"id\":\"zai\",\"enabled\":false},{\"id\":\"muse\",\"enabled\":true}]," +
                "\"notifications\":{\"enabled\":false,\"thresholds\":[90,10],\"onReset\":true}," +
                "\"update\":{\"mode\":\"off\"}}");
            Settings s = Settings.Load(path);

            SettingsForm.WorkingValues v = SettingsForm.ReadFrom(s);
            That(v.RefreshMinutes == 120 && v.Layout == "compact" && !v.AlwaysOnTop,
                "general values read and clamped");
            That(v.Theme == "light" && v.Opacity == 55 && v.WarnPercent == 30
                && v.CriticalPercent == 31, "appearance values read");
            That(!v.AutoDetect, "autoDetect read");
            That(v.Providers.Count == 2 && v.Providers[0].Id == "zai" && !v.Providers[0].Enabled
                && v.Providers[1].Id == "muse" && v.Providers[1].Enabled, "provider list read");
            That(!v.NotifyEnabled && v.NotifyLow == 10 && v.NotifyHigh == 90 && v.NotifyOnReset,
                "notification values read, thresholds sorted");
            That(v.UpdateMode == "off", "update mode read");

            // ApplyTo into a fresh Settings, read back: every scalar and the provider list
            // must survive, without shared list references either way.
            Settings back = new Settings();
            SettingsForm.ApplyTo(v, back);
            SettingsForm.WorkingValues v2 = SettingsForm.ReadFrom(back);
            That(v2.RefreshMinutes == v.RefreshMinutes && v2.Layout == v.Layout
                && v2.AlwaysOnTop == v.AlwaysOnTop && v2.Theme == v.Theme
                && v2.Opacity == v.Opacity && v2.WarnPercent == v.WarnPercent
                && v2.CriticalPercent == v.CriticalPercent && v2.AutoDetect == v.AutoDetect
                && v2.NotifyEnabled == v.NotifyEnabled && v2.NotifyLow == v.NotifyLow
                && v2.NotifyHigh == v.NotifyHigh && v2.NotifyOnReset == v.NotifyOnReset
                && v2.UpdateMode == v.UpdateMode, "every scalar round-trips");
            That(v2.Providers.Count == 2 && v2.Providers[0].Id == "zai"
                && !v2.Providers[0].Enabled && v2.Providers[1].Id == "muse"
                && v2.Providers[1].Enabled, "provider list round-trips");
            back.Providers[0].Enabled = true;
            That(!v.Providers[0].Enabled, "apply copies the provider list instead of sharing it");

            // Threshold validation on apply: critical strictly above warn, both clamped.
            var bad = new SettingsForm.WorkingValues();
            bad.WarnPercent = 95;
            bad.CriticalPercent = 20;
            SettingsForm.ApplyTo(bad, back);
            That(back.WarnPercent == 95 && back.CriticalPercent == 96,
                "critical pushed above warn");
            bad.WarnPercent = 200;
            bad.CriticalPercent = 300;
            SettingsForm.ApplyTo(bad, back);
            That(back.WarnPercent == 98 && back.CriticalPercent == 99, "percent ranges clamped");

            // Notification thresholds: 0 disables one, order is normalised, duplicates go.
            var n = new SettingsForm.WorkingValues();
            n.NotifyLow = 0;
            n.NotifyHigh = 95;
            SettingsForm.ApplyTo(n, back);
            That(back.Notifications.Thresholds.Count == 1 && back.Notifications.Thresholds[0] == 95,
                "0 disables the first threshold");
            n.NotifyLow = 80;
            n.NotifyHigh = 0;
            SettingsForm.ApplyTo(n, back);
            That(back.Notifications.Thresholds.Count == 1 && back.Notifications.Thresholds[0] == 80,
                "0 disables the second threshold");
            n.NotifyLow = 95;
            n.NotifyHigh = 70;
            SettingsForm.ApplyTo(n, back);
            That(back.Notifications.Thresholds.Count == 2 && back.Notifications.Thresholds[0] == 70
                && back.Notifications.Thresholds[1] == 95, "thresholds sorted on apply");
            n.NotifyLow = 90;
            n.NotifyHigh = 90;
            SettingsForm.ApplyTo(n, back);
            That(back.Notifications.Thresholds.Count == 1, "duplicate thresholds collapse");

            // Merged provider ticks: stored entries keep their order, unlisted follow in
            // the given order, experimental defaults to off.
            var registry = new List<IUsageProvider>();
            registry.Add(new FakeProv("claude-code"));
            registry.Add(new FakeProv("codex"));
            registry.Add(new FakeProv("zai"));
            registry.Add(new FakeProv("antigravity"));
            registry.Add(new FakeProv("muse"));
            List<ProviderSetting> ticks = SettingsForm.MergedProviderTicks(s, registry);
            That(ticks.Count == 5, "every provider merged once");
            That(ticks[0].Id == "zai" && !ticks[0].Enabled, "listed disabled kept first");
            That(ticks[1].Id == "muse" && ticks[1].Enabled, "listed enabled kept");
            That(ticks[2].Id == "claude-code" && ticks[2].Enabled,
                "unlisted non-experimental ticks on");
            That(ticks[3].Id == "codex" && ticks[3].Enabled, "unlisted codex ticks on");
            That(ticks[4].Id == "antigravity" && !ticks[4].Enabled,
                "unlisted experimental stays off");

            List<ProviderSetting> fresh = SettingsForm.MergedProviderTicks(null, registry);
            That(fresh.Count == 5 && fresh[0].Enabled && !fresh[3].Enabled,
                "null settings -> defaults with experimental off");
            That(SettingsForm.MergedProviderTicks(s, new List<IUsageProvider>()).Count == 0,
                "no providers -> no rows");
        }

        // --- v0.4.0 history, forecast and notifier checks --------------------------------

        static UsageWindow TestWindow(WindowKind kind, string key, double percent,
            int? durationSeconds, DateTime? resetsAtUtc)
        {
            var w = new UsageWindow();
            w.Kind = kind;
            w.Key = key;
            w.UsedPercent = percent;
            w.DurationSeconds = durationSeconds;
            w.ResetsAtUtc = resetsAtUtc;
            return w;
        }

        static HistorySample Sample(DateTime tUtc, double percent, DateTime? resetsAtUtc)
        {
            var s = new HistorySample();
            s.TUtc = tUtc;
            s.UsedPercent = percent;
            s.ResetsAtUtc = resetsAtUtc;
            return s;
        }

        static void HistoryRecordDedup()
        {
            string path = Path.Combine(AppPaths.DataDir, "history-dedup.json");
            DateTime t0 = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
            DateTime clockTime = t0;
            History history = new History(path, delegate() { return clockTime; });

            var snapshots = new List<UsageSnapshot>();
            UsageSnapshot ok = SnapFor("codex", "Codex");
            ok.Windows.Add(TestWindow(WindowKind.FiveHour, "5h", 40, 18000, t0.AddHours(3)));
            ok.Windows.Add(TestWindow(WindowKind.Weekly, "week", 10, 604800, t0.AddDays(4)));
            snapshots.Add(ok);
            UsageSnapshot errored = SnapFor("zai", "Z.ai");
            errored.Status = ProviderStatus.Error;
            errored.Windows.Add(TestWindow(WindowKind.FiveHour, "5h", 77, 18000, t0.AddHours(3)));
            snapshots.Add(errored);

            history.Record(snapshots, t0);
            history.Record(snapshots, t0.AddMinutes(5));   // nothing changed: both dropped
            That(history.Samples("codex", "5h").Count == 1,
                "an identical sample within 30 minutes is deduplicated");
            That(history.Samples("codex", "week").Count == 1, "the second window deduplicated too");
            That(history.Samples("zai", "5h").Count == 0, "non-Ok snapshots are never recorded");

            ok.Windows[0].UsedPercent = 55;
            history.Record(snapshots, t0.AddMinutes(5));   // percent differs: kept
            That(history.Samples("codex", "5h").Count == 2, "a different percent appends");

            history.Record(snapshots, t0.AddMinutes(10));   // identical again: dropped
            That(history.Samples("codex", "5h").Count == 2, "an identical sample still deduplicates");

            ok.Windows[1].ResetsAtUtc = ok.Windows[1].ResetsAtUtc.Value.AddHours(1);
            history.Record(snapshots, t0.AddMinutes(12));   // reset moved on the week window
            That(history.Samples("codex", "week").Count == 2, "a changed reset time appends");

            history.Record(snapshots, t0.AddMinutes(45));   // 40 min since the last 5h sample
            That(history.Samples("codex", "5h").Count == 3, "a last sample older than 30 minutes appends");

            List<HistorySample> five = history.Samples("codex", "5h");
            That(five[0].TUtc == t0 && five[1].TUtc == t0.AddMinutes(5)
                && five[2].TUtc == t0.AddMinutes(45), "samples come back oldest first");
            That(five[0].UsedPercent == 40 && five[1].UsedPercent == 55 && five[2].UsedPercent == 55,
                "sample percents survive");
            That(five[0].ResetsAtUtc.HasValue && five[0].ResetsAtUtc.Value == t0.AddHours(3),
                "sample reset time survives");

            // Windows without a key are skipped: no series is created for them.
            UsageSnapshot noKey = SnapFor("claude-code", "Claude Code");
            noKey.Windows.Add(TestWindow(WindowKind.FiveHour, null, 10, 18000, t0.AddHours(2)));
            history.Record(new List<UsageSnapshot> { noKey }, t0.AddMinutes(46));
            That(history.Samples("claude-code", "5h").Count == 0, "windows without a key are skipped");
            That(history.Samples("nope", "nope").Count == 0, "an unknown series is empty");
        }

        static void HistoryPrune()
        {
            string path = Path.Combine(AppPaths.DataDir, "history-prune.json");
            DateTime t0 = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            History history = new History(path, delegate() { return t0; });

            var snapshots = new List<UsageSnapshot>();
            UsageSnapshot ok = SnapFor("codex", "Codex");
            ok.Windows.Add(TestWindow(WindowKind.FiveHour, "5h", 40, 18000, t0.AddHours(3)));
            snapshots.Add(ok);
            history.Record(snapshots, t0.AddDays(-10));
            history.Record(snapshots, t0.AddDays(-2));   // same percent, but far apart: kept
            history.Record(snapshots, t0);
            That(history.Samples("codex", "5h").Count == 3, "three samples recorded");

            history.Prune(t0, 7);   // keep 7 days
            List<HistorySample> left = history.Samples("codex", "5h");
            That(left.Count == 2 && left[0].TUtc == t0.AddDays(-2),
                "samples older than keepDays are dropped");

            history.Prune(t0, 1);
            That(history.Samples("codex", "5h").Count == 1, "prune keeps only the recent sample");

            // A series that lost everything disappears.
            UsageSnapshot gone = SnapFor("muse", "Muse");
            gone.Windows.Add(TestWindow(WindowKind.Daily, "day", 5, 86400, t0.AddDays(-2)));
            history.Record(new List<UsageSnapshot> { gone }, t0.AddDays(-5));
            That(history.Samples("muse", "day").Count == 1, "the old series is present");
            history.Prune(t0, 1);
            That(history.Samples("muse", "day").Count == 0, "emptied series are removed");
        }

        static void HistoryRoundTripAndMalformed()
        {
            string path = Path.Combine(AppPaths.DataDir, "history-roundtrip.json");
            DateTime t0 = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            History history = new History(path, delegate() { return t0; });
            var ok = SnapFor("codex", "Codex");
            ok.Windows.Add(TestWindow(WindowKind.FiveHour, "5h", 41.5, 18000, t0.AddHours(3)));
            ok.Windows.Add(TestWindow(WindowKind.Weekly, "week", 12.34, 604800, null));
            history.Record(new List<UsageSnapshot> { ok }, t0);
            ok.Windows[0].UsedPercent = 87.65;
            history.Record(new List<UsageSnapshot> { ok }, t0.AddMinutes(40));
            history.Save();

            string raw = File.ReadAllText(path);
            That(raw.Contains("[["), "samples are compact numeric arrays, got " + raw);
            That(!raw.Contains("usedPercent"), "no per-sample field names");

            History loaded = new History(path, delegate() { return t0; });
            loaded.Load();
            List<HistorySample> five = loaded.Samples("codex", "5h");
            That(five.Count == 2, "both samples survive, got " + five.Count);
            That(five[0].TUtc == t0 && five[0].UsedPercent == 41.5, "time and percent survive");
            That(five[0].ResetsAtUtc.HasValue && five[0].ResetsAtUtc.Value == t0.AddHours(3),
                "the reset time survives");
            That(five[1].UsedPercent == 87.65, "the second percent survives");
            List<HistorySample> week = loaded.Samples("codex", "week");
            That(week.Count == 2 && week[0].UsedPercent == 12.34 && !week[0].ResetsAtUtc.HasValue,
                "a null reset survives, got " + week.Count + " samples");
            That(!week[1].ResetsAtUtc.HasValue, "the second null reset survives");

            // A malformed file loads as empty.
            File.WriteAllText(path, "{ not json at all");
            History broken = new History(path, delegate() { return t0; });
            broken.Load();
            That(broken.Samples("codex", "5h").Count == 0, "a malformed file loads as empty");

            // A missing file loads as empty.
            History missing = new History(Path.Combine(AppPaths.DataDir, "history-missing.json"),
                delegate() { return t0; });
            missing.Load();
            That(missing.Samples("codex", "5h").Count == 0, "a missing file loads as empty");

            // I/O problems never throw: saving onto a directory path is swallowed.
            string blocked = Path.Combine(AppPaths.DataDir, "history-blocked");
            Directory.CreateDirectory(blocked);
            History blockedHistory = new History(blocked, delegate() { return t0; });
            blockedHistory.Record(new List<UsageSnapshot> { ok }, t0);
            blockedHistory.Save();   // the replace fails and is logged, not thrown
            blockedHistory.Load();
        }

        static void HistoryFileSize()
        {
            string path = Path.Combine(AppPaths.DataDir, "history-size.json");
            DateTime t0 = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            History history = new History(path, delegate() { return t0; });

            // 10 windows over 30 days at the 30-minute dedup floor, the percent changing a
            // little each time so nothing deduplicates away.
            var snapshots = new List<UsageSnapshot>();
            UsageSnapshot ok = SnapFor("codex", "Codex");
            for (int i = 0; i < 10; i++)
                ok.Windows.Add(TestWindow(WindowKind.FiveHour, "w" + i, 0, 18000, t0.AddHours(5)));
            snapshots.Add(ok);

            DateTime at = t0;
            for (int i = 0; i < 1440; i++)
            {
                for (int j = 0; j < 10; j++)
                    ok.Windows[j].UsedPercent = (i + j) % 101;
                history.Record(snapshots, at);
                at = at.AddMinutes(30);
            }
            history.Save();
            long bytes = new FileInfo(path).Length;
            That(bytes < 512 * 1024,
                "30 days of samples for 10 windows stays well under 1 MB, got " + bytes + " bytes");
        }

        static void PacePercentBasics()
        {
            DateTime t0 = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);   // window start
            UsageWindow w = TestWindow(WindowKind.FiveHour, "5h", 0, 18000, t0.AddSeconds(18000));

            That(Forecast.PacePercent(w, t0) == 0, "the pace at the window start is 0");
            double? half = Forecast.PacePercent(w, t0.AddSeconds(9000));
            That(half.HasValue && Math.Abs(half.Value - 50) < 1e-9, "the pace mid-window is 50");
            That(Forecast.PacePercent(w, t0.AddSeconds(18000)) == 100, "the pace at the reset is 100");
            That(Forecast.PacePercent(w, t0.AddSeconds(36000)) == 100, "past the reset it clamps to 100");
            That(Forecast.PacePercent(w, t0.AddSeconds(-60)) == 0, "before the start it clamps to 0");

            UsageWindow noDuration = TestWindow(WindowKind.Weekly, "week", 0, null, t0.AddSeconds(600));
            That(!Forecast.PacePercent(noDuration, t0).HasValue, "an unknown duration gives null");
            UsageWindow noReset = TestWindow(WindowKind.Weekly, "week", 0, 604800, null);
            That(!Forecast.PacePercent(noReset, t0).HasValue, "an unknown reset gives null");
        }

        static void ForecastProjection()
        {
            DateTime t0 = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);   // window start
            DateTime now = t0.AddHours(1);                                        // 1 h into a 5 h window
            DateTime reset = t0.AddHours(5);
            UsageWindow w = TestWindow(WindowKind.FiveHour, "5h", 40, 18000, reset);

            // A steady burn of 30 %/h: 10, 25, 40 at the start, +30 m and +60 m. Samples
            // from the previous window generation (reset at t0) sit in the middle and
            // must be ignored even though they are within the last 90 minutes.
            var steady = new List<HistorySample>();
            steady.Add(Sample(t0.AddMinutes(-60), 80, t0));
            steady.Add(Sample(t0.AddMinutes(-30), 95, t0));
            steady.Add(Sample(t0, 10, reset));
            steady.Add(Sample(t0.AddMinutes(30), 25, reset));
            steady.Add(Sample(now, 40, reset));

            ForecastResult r = Forecast.Project(w, steady, now);
            That(r.RatePerHour.HasValue && Math.Abs(r.RatePerHour.Value - 30) < 1e-9,
                "the steady slope is 30 %/h, got " + r.RatePerHour);
            Near(r.HitsLimitAtUtc, now.AddHours(2), "hit time (100-40)/30 h after now");
            That(r.BeforeReset, "the limit is hit before the reset");
            That(r.Text == "limit in 2h", "the text is 'limit in 2h', got '" + r.Text + "'");

            // Fewer than three recent samples: fall back to the even pace (20 % at 1 h of 5 h).
            var sparse = new List<HistorySample>();
            sparse.Add(Sample(t0, 10, reset));
            sparse.Add(Sample(now.AddMinutes(-5), 12, reset));
            r = Forecast.Project(w, sparse, now);
            That(r.RatePerHour.HasValue && Math.Abs(r.RatePerHour.Value - 20) < 1e-9,
                "sparse history falls back to the pace, got " + r.RatePerHour);
            That(r.Text == "limit in 3h", "the pace fallback text, got '" + r.Text + "'");

            // Three samples but spanning under 10 minutes: still the pace.
            var tight = new List<HistorySample>();
            tight.Add(Sample(now.AddMinutes(-2), 39, reset));
            tight.Add(Sample(now.AddMinutes(-1), 40, reset));
            tight.Add(Sample(now, 41, reset));
            r = Forecast.Project(w, tight, now);
            That(r.RatePerHour.HasValue && Math.Abs(r.RatePerHour.Value - 20) < 1e-9,
                "a span under 10 minutes still uses the pace, got " + r.RatePerHour);

            // No current-window samples at all: the pace alone still answers.
            r = Forecast.Project(w, new List<HistorySample>(), now);
            That(r.RatePerHour.HasValue && Math.Abs(r.RatePerHour.Value - 20) < 1e-9,
                "an empty history still falls back to the pace, got " + r.RatePerHour);

            // Unknown window shape: nothing at all.
            UsageWindow opaque = TestWindow(WindowKind.Other, "odd", 50, null, null);
            r = Forecast.Project(opaque, steady, now);
            That(!r.RatePerHour.HasValue && !r.HitsLimitAtUtc.HasValue && !r.BeforeReset
                && r.Text == "", "an unknown window shape projects nothing");

            // The limit would only be hit well after the reset: no announcement.
            UsageWindow safe = TestWindow(WindowKind.FiveHour, "5h", 10, 18000, now.AddHours(1));
            r = Forecast.Project(safe, new List<HistorySample>(), now);
            That(r.RatePerHour.HasValue && Math.Abs(r.RatePerHour.Value - 20) < 1e-6,
                "four hours in, the even rate is 20 %/h, got " + r.RatePerHour);
            That(r.HitsLimitAtUtc.HasValue && !r.BeforeReset && r.Text == "",
                "a hit after the reset is not announced");

            // Already at the limit: no hit time at all.
            UsageWindow full = TestWindow(WindowKind.FiveHour, "5h", 100, 18000, now.AddHours(1));
            r = Forecast.Project(full, new List<HistorySample>(), now);
            That(!r.HitsLimitAtUtc.HasValue && !r.BeforeReset && r.Text == "",
                "a full window has no hit time");
        }

        static void NotifierThresholdsAndReset()
        {
            DateTime t0 = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            NotificationSettings cfg = new NotificationSettings();   // enabled, {80, 95}, no reset

            // First sight at 90 %: nothing, even though both thresholds are below it.
            Notifier n = new Notifier();
            var snaps = new List<UsageSnapshot>();
            UsageSnapshot ok = SnapFor("codex", "Codex");
            UsageWindow week = TestWindow(WindowKind.Weekly, "week", 90, 604800, t0.AddHours(3));
            week.Label = "week";
            ok.Windows.Add(week);
            snaps.Add(ok);
            That(n.Evaluate(snaps, cfg, t0).Count == 0, "first sight never notifies");

            // Crossing 95 alone announces once, with the reset suffix.
            week.UsedPercent = 96;
            List<string> msgs = n.Evaluate(snaps, cfg, t0.AddMinutes(5));
            That(msgs.Count == 1, "one crossing, one message, got " + msgs.Count);
            That(msgs[0] == "Codex week: 96% used, resets in 2h 55m",
                "the message text, got '" + msgs[0] + "'");

            // Staying above fires nothing.
            That(n.Evaluate(snaps, cfg, t0.AddMinutes(10)).Count == 0,
                "staying above the threshold does not re-fire");

            // A drop announces nothing (crossings are upward), then climbing past both
            // thresholds at once still announces once.
            week.UsedPercent = 30;
            That(n.Evaluate(snaps, cfg, t0.AddMinutes(15)).Count == 0, "a drop is quiet");
            week.UsedPercent = 97;
            msgs = n.Evaluate(snaps, cfg, t0.AddMinutes(20));
            That(msgs.Count == 1 && msgs[0] == "Codex week: 97% used, resets in 2h 40m",
                "a double crossing emits one message, got " + msgs.Count + ": '"
                + (msgs.Count > 0 ? msgs[0] : "") + "'");

            // A window without a label gets the default label of its kind.
            UsageSnapshot anon = SnapFor("zai", "Z.ai GLM");
            anon.Windows.Add(TestWindow(WindowKind.FiveHour, "5h", 10, 18000, t0.AddHours(2)));
            var anonSnaps = new List<UsageSnapshot>();
            anonSnaps.Add(anon);
            That(n.Evaluate(anonSnaps, cfg, t0.AddMinutes(25)).Count == 0, "first sight at 10 is quiet");
            anon.Windows[0].UsedPercent = 82;
            msgs = n.Evaluate(anonSnaps, cfg, t0.AddMinutes(30));
            That(msgs.Count == 1 && msgs[0].StartsWith("Z.ai GLM 5 hours: 82% used", StringComparison.Ordinal),
                "the default kind label is used, got '" + (msgs.Count > 0 ? msgs[0] : "") + "'");

            // Disabled: nothing comes out, but the remembered state still advances.
            var snaps3 = new List<UsageSnapshot>();
            UsageSnapshot muted = SnapFor("muse", "Muse");
            UsageWindow day = TestWindow(WindowKind.Daily, "day", 85, 86400, t0.AddHours(6));
            muted.Windows.Add(day);
            snaps3.Add(muted);
            NotificationSettings off = new NotificationSettings();
            off.Enabled = false;
            Notifier mutedN = new Notifier();
            That(mutedN.Evaluate(snaps3, off, t0).Count == 0, "disabled -> nothing");
            day.UsedPercent = 30;
            That(mutedN.Evaluate(snaps3, off, t0.AddMinutes(5)).Count == 0, "disabled -> still nothing");
            day.UsedPercent = 82;
            msgs = mutedN.Evaluate(snaps3, cfg, t0.AddMinutes(10));
            That(msgs.Count == 1 && msgs[0] == "Muse day: 82% used, resets in 5h 50m",
                "state advanced while disabled, got '" + (msgs.Count > 0 ? msgs[0] : "") + "'");

            // OnReset: a forward reset with a big drop announces, and the thresholds re-arm.
            NotificationSettings cfgReset = new NotificationSettings();
            cfgReset.OnReset = true;
            Notifier resetN = new Notifier();
            var snaps4 = new List<UsageSnapshot>();
            UsageSnapshot codex = SnapFor("codex", "Codex");
            UsageWindow five = TestWindow(WindowKind.FiveHour, "5h", 95, 18000, t0.AddHours(5));
            codex.Windows.Add(five);
            snaps4.Add(codex);
            That(resetN.Evaluate(snaps4, cfgReset, t0).Count == 0, "reset flow: first sight quiet");
            five.UsedPercent = 80;
            That(resetN.Evaluate(snaps4, cfgReset, t0.AddMinutes(5)).Count == 0,
                "a drop without a new window is quiet");
            five.UsedPercent = 25;
            five.ResetsAtUtc = t0.AddHours(9);
            msgs = resetN.Evaluate(snaps4, cfgReset, t0.AddMinutes(10));
            That(msgs.Count == 1 && msgs[0] == "Codex 5 hours: reset",
                "the reset message, got '" + (msgs.Count > 0 ? msgs[0] : "") + "'");
            five.UsedPercent = 83;
            msgs = resetN.Evaluate(snaps4, cfgReset, t0.AddMinutes(30));
            That(msgs.Count == 1 && msgs[0].Contains("83% used"), "thresholds re-arm after a reset");
            five.UsedPercent = 80;
            five.ResetsAtUtc = t0.AddHours(13);
            That(resetN.Evaluate(snaps4, cfgReset, t0.AddMinutes(35)).Count == 0,
                "a 3-point drop is not a reset");
        }

        // --- OpenRouter -----------------------------------------------------------------

        static void OpenRouterFixtureMapping()
        {
            string json = File.ReadAllText(FixturePath("openrouter-key.json"));
            DateTime now = new DateTime(2026, 11, 14, 15, 30, 0, DateTimeKind.Utc);
            UsageSnapshot s = OpenRouterProvider.Parse(json, now);
            That(s.Status == ProviderStatus.Ok, "status should be Ok, got " + s.Status);
            That(s.PlanName == "", "a paid key has no plan name, got '" + s.PlanName + "'");
            That(s.Windows.Count == 1, "exactly one window expected, got " + s.Windows.Count);
            UsageWindow w = s.Windows[0];
            That(w.Primary && w.Key == "limit" && w.Label == "key limit" && w.Unit == "USD",
                "window identity");
            That(w.Kind == WindowKind.Monthly && w.DurationSeconds.HasValue &&
                w.DurationSeconds.Value == 2592000, "monthly from limit_reset");
            That(w.Used.HasValue && w.Used.Value == 32.5 && w.Limit.HasValue &&
                w.Limit.Value == 100, "used = limit - limit_remaining");
            That(w.UsedPercent == 32.5, "percent from used and limit");
            That(w.ResetsAtUtc.HasValue &&
                w.ResetsAtUtc.Value == new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
                "monthly reset is the first of the next month");
        }

        static void OpenRouterNoLimitAndResets()
        {
            DateTime now = new DateTime(2026, 11, 14, 15, 30, 0, DateTimeKind.Utc);

            string json = "{\"data\":{\"label\":\"k\",\"limit\":null,\"limit_remaining\":null," +
                "\"usage\":20.5,\"usage_monthly\":12.3,\"is_free_tier\":true}}";
            UsageSnapshot s = OpenRouterProvider.Parse(json, now);
            That(s.Status == ProviderStatus.Ok, "no limit is still Ok");
            That(s.Windows.Count == 0, "no windows without a limit");
            That(s.PlanName == "Free", "free tier plan name");
            That(s.Message == "no limit set: $12.30 used this month",
                "monthly usage message, got '" + s.Message + "'");

            // The response carries no reset wall clock, so the reset time is derived from
            // the cadence and the injected now (2026-11-14 is a Saturday).
            That(OpenRouterProvider.NextResetUtc(now, "daily") ==
                new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc),
                "daily -> next UTC midnight");
            That(OpenRouterProvider.NextResetUtc(
                new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc), "daily") ==
                new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc),
                "daily at midnight -> next midnight");
            That(OpenRouterProvider.NextResetUtc(now, "weekly") ==
                new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc),
                "weekly -> next Monday 00:00 UTC");
            That(OpenRouterProvider.NextResetUtc(
                new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc), "weekly") ==
                new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc),
                "Monday 00:00 -> a full week later");
            That(OpenRouterProvider.NextResetUtc(
                new DateTime(2026, 11, 30, 23, 0, 0, DateTimeKind.Utc), "monthly") ==
                new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
                "monthly -> first of next month");
            That(OpenRouterProvider.NextResetUtc(
                new DateTime(2026, 12, 15, 0, 0, 0, DateTimeKind.Utc), "monthly") ==
                new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), "year rollover");
            That(!OpenRouterProvider.NextResetUtc(now, null).HasValue, "no cadence -> no reset");
            That(!OpenRouterProvider.NextResetUtc(now, "never").HasValue,
                "unknown cadence -> no reset");

            // An unknown cadence still yields the window: Other, no duration, no reset.
            string other = "{\"data\":{\"limit\":50,\"limit_remaining\":25," +
                "\"limit_reset\":\"never\",\"is_free_tier\":false}}";
            UsageSnapshot o = OpenRouterProvider.Parse(other, now);
            That(o.Status == ProviderStatus.Ok && o.Windows.Count == 1,
                "unknown cadence keeps the window");
            That(o.Windows[0].Kind == WindowKind.Other && !o.Windows[0].DurationSeconds.HasValue &&
                !o.Windows[0].ResetsAtUtc.HasValue, "unknown cadence -> Other, undated");

            UsageSnapshot bad = OpenRouterProvider.Parse("{\"error\":\"nope\"}", now);
            That(bad.Status == ProviderStatus.Error && bad.Message == "unexpected response",
                "missing data block -> Error");
        }

        static void OpenRouterKeySources()
        {
            string oldKey = Environment.GetEnvironmentVariable(OpenRouterProvider.EnvVarName);
            var provider = new OpenRouterProvider();
            try
            {
                Environment.SetEnvironmentVariable(OpenRouterProvider.EnvVarName, null);
                SecretStore.Clear(OpenRouterProvider.SecretName);
                That(!provider.IsDetected(), "no env key, no stored secret");
                UsageSnapshot s = provider.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
                That(s.Status == ProviderStatus.NotConfigured, "no key -> NotConfigured, no network");
                That(s.Windows.Count == 0, "no windows on NotConfigured");

                SecretStore.Save(OpenRouterProvider.SecretName, "selftest-placeholder");
                That(provider.IsDetected(), "the stored secret counts as a key source");
                SecretStore.Clear(OpenRouterProvider.SecretName);

                Environment.SetEnvironmentVariable(OpenRouterProvider.EnvVarName, "selftest-placeholder");
                That(provider.IsDetected(), "the environment variable counts as a key source");
            }
            finally
            {
                Environment.SetEnvironmentVariable(OpenRouterProvider.EnvVarName, oldKey);
                SecretStore.Clear(OpenRouterProvider.SecretName);
            }
        }

        // --- Cursor ---------------------------------------------------------------------

        static void CursorFixtureMapping()
        {
            string json = File.ReadAllText(FixturePath("cursor-usage-synthetic.json"));
            UsageSnapshot s = CursorProvider.Parse(json, "pro");
            That(s.Status == ProviderStatus.Ok, "status should be Ok, got " + s.Status);
            That(s.PlanName == "Pro", "plan from the membership hint, got '" + s.PlanName + "'");
            That(s.Windows.Count == 1, "exactly one window expected, got " + s.Windows.Count);
            UsageWindow w = s.Windows[0];
            That(w.Primary && w.Key == "cycle" && w.Label == "billing cycle", "window identity");
            That(w.Kind == WindowKind.Monthly, "the billing cycle window is monthly");
            That(w.UsedPercent == 42.5, "percent from planUsage.totalPercentUsed");
            That(w.Used.HasValue && w.Used.Value == 1200 && w.Limit.HasValue &&
                w.Limit.Value == 2000 && w.Unit == "cents", "absolute spend in cents");
            That(w.DurationSeconds.HasValue && w.DurationSeconds.Value == 2592000,
                "duration from the cycle dates (November)");
            That(w.ResetsAtUtc.HasValue &&
                w.ResetsAtUtc.Value == new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
                "reset at billingCycleEnd");

            // Tolerant shapes: epoch milliseconds as numbers, percent at the root, no spend.
            string numbers = "{\"billingCycleStart\":1793577600000,\"billingCycleEnd\":1794787200000," +
                "\"totalPercentUsed\":7}";
            UsageSnapshot n = CursorProvider.Parse(numbers, "free");
            That(n.Status == ProviderStatus.Ok && n.Windows.Count == 1, "epoch ms numbers parse");
            That(n.Windows[0].UsedPercent == 7 && !n.Windows[0].Used.HasValue &&
                !n.Windows[0].Limit.HasValue && n.Windows[0].Unit == null,
                "root percent, no absolute fields");
            That(n.Windows[0].DurationSeconds.HasValue && n.Windows[0].DurationSeconds.Value == 1209600,
                "duration from epoch dates (fortnight)");
            That(n.Windows[0].ResetsAtUtc.HasValue && n.Windows[0].ResetsAtUtc.Value ==
                new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc), "epoch ms end date");
            That(n.PlanName == "Free", "membership capitalised");

            // Epoch milliseconds quoted as strings, percent under planUsage.
            string quoted = "{\"billingCycleStart\":\"1793577600000\"," +
                "\"billingCycleEnd\":\"1794787200000\",\"planUsage\":{\"totalPercentUsed\":55}}";
            UsageSnapshot q = CursorProvider.Parse(quoted, null);
            That(q.Windows.Count == 1 && q.Windows[0].UsedPercent == 55, "quoted epoch ms parse");
            That(q.Windows[0].DurationSeconds.HasValue && q.Windows[0].DurationSeconds.Value == 1209600,
                "duration from quoted epoch dates");
            That(q.PlanName == "", "missing membership -> no plan name");

            // No percent anywhere -> Error even with dates present.
            UsageSnapshot noPercent = CursorProvider.Parse(
                "{\"billingCycleStart\":\"2026-11-01T00:00:00Z\",\"planUsage\":{\"limit\":100}}",
                "pro");
            That(noPercent.Status == ProviderStatus.Error &&
                noPercent.Message == "unexpected response", "no percent -> Error");

            UsageSnapshot garbage = CursorProvider.Parse("nope", "pro");
            That(garbage.Status == ProviderStatus.Error, "garbage body -> Error");
        }

        static string TestJwt(double expEpochSeconds)
        {
            string payload = "{\"exp\":" + expEpochSeconds.ToString(CultureInfo.InvariantCulture) + "}";
            return "eyJhbGciOiJQUzI1NiIsInR5cCI6IkpXVCJ9." +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";
        }

        static void CursorTokenAndAuthStates()
        {
            DateTime now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
            That(!CursorProvider.IsTokenExpired(TestJwt(4102444800), now), "exp 2100 -> live");
            That(CursorProvider.IsTokenExpired(TestJwt(1000), now), "exp 1970 -> expired");
            That(CursorProvider.IsTokenExpired(TestJwt(1794657600), now), "exp now -> expired");
            That(!CursorProvider.IsTokenExpired(null, now), "null token -> not expired");
            That(!CursorProvider.IsTokenExpired("garbage", now), "no payload -> not expired");
            That(!CursorProvider.IsTokenExpired("a.b.c", now), "payload not base64 -> not expired");
            That(!CursorProvider.IsTokenExpired("x.####.y", now), "bad base64url -> not expired");
            That(!CursorProvider.IsTokenExpired(
                "h." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"sub\":\"u\"}"))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".s", now),
                "payload without exp -> not expired");

            string root = Path.Combine(AppPaths.DataDir, "cursor");
            Directory.CreateDirectory(root);

            // No database file: NotConfigured, and the value reader is never called.
            string missing = Path.Combine(root, "missing.vscdb");
            var notThere = new CursorProvider(missing, delegate(string key)
            {
                throw new InvalidOperationException("reader must not run without a database");
            });
            UsageSnapshot s = notThere.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.NotConfigured, "missing db -> NotConfigured");

            // A database file without a token: NotConfigured, no network. The injected
            // reader bypasses sqlite, so a text file stands in for the database.
            string db = Path.Combine(root, "state.vscdb");
            File.WriteAllText(db, "not a real database");
            var noToken = new CursorProvider(db, delegate(string key) { return null; });
            s = noToken.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.NotConfigured, "no token -> NotConfigured, no network");

            // An expired token: AuthRequired straight from the database, no network.
            var stale = new CursorProvider(db, delegate(string key)
            {
                return key == CursorProvider.TokenKey ? TestJwt(1000) : "pro";
            });
            s = stale.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.AuthRequired, "expired token -> AuthRequired");
            That(s.Message == "sign in needed: open Cursor",
                "expiry message, got '" + s.Message + "'");
        }

        static void CursorSqliteWrapper()
        {
            // Degenerate inputs return null without touching the dll.
            That(Sqlite.ReadValue(null, "ItemTable", "k") == null, "null path");
            That(Sqlite.ReadValue("", "ItemTable", "k") == null, "empty path");
            That(Sqlite.ReadValue(Path.Combine(AppPaths.DataDir, "missing.vscdb"), "ItemTable",
                "k") == null, "missing database");

            // winsqlite3.dll ships with Windows 10/11; anywhere it is absent the nulls above
            // come from the DllNotFound catch and the real-database half is skipped. The
            // wrapper is read-only, so the fixture database ships as a binary fixture rather
            // than being created here.
            if (!Detection.FileExists(Path.Combine(Environment.SystemDirectory, "winsqlite3.dll")))
                return;

            string db = FixturePath("state.vscdb");
            That(Sqlite.ReadValue(db, "ItemTable", "cursorAuth/accessToken") == "header.payload.sig",
                "text value read");
            That(Sqlite.ReadValue(db, "ItemTable", "cursorAuth/stripeMembershipType") == "pro",
                "second text value read");
            That(Sqlite.ReadValue(db, "ItemTable", "cursorAuth/blobToken") == "stored-as-blob",
                "utf-8 blob value read");
            That(Sqlite.ReadValue(db, "ItemTable", "no-such-key") == null, "missing row -> null");
            That(Sqlite.ReadValue(db, "NoSuchTable", "k") == null, "missing table -> null");
            That(Sqlite.ReadValue(db, "ItemTable WHERE 1=1 --", "k") == null,
                "junk table -> null");
            string notDb = Path.Combine(AppPaths.DataDir, "not-a-db.vscdb");
            File.WriteAllText(notDb, "not a database");
            That(Sqlite.ReadValue(notDb, "ItemTable", "k") == null, "non-database file -> null");
        }

        // --- GitHub Copilot -------------------------------------------------------------

        static void CopilotFixtureMapping()
        {
            string json = File.ReadAllText(FixturePath("copilot-user-synthetic.json"));
            UsageSnapshot s = CopilotProvider.Parse(json);
            That(s.Status == ProviderStatus.Ok, "status should be Ok, got " + s.Status);
            That(s.PlanName == "Business", "plan from copilot_plan, got '" + s.PlanName + "'");
            That(s.Windows.Count == 2, "two quota blocks expected, got " + s.Windows.Count);

            UsageWindow premium = s.Windows[0];
            That(premium.Primary && premium.Key == "premium_interactions" &&
                premium.Label == "premium requests", "premium window identity");
            That(premium.Kind == WindowKind.Monthly, "quota windows are monthly");
            That(premium.UsedPercent == 25, "percent = 100 - percent_remaining");
            That(premium.Used.HasValue && premium.Used.Value == 75 && premium.Limit.HasValue &&
                premium.Limit.Value == 300 && premium.Unit == "requests", "absolute requests");
            That(premium.ResetsAtUtc.HasValue &&
                premium.ResetsAtUtc.Value == new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
                "reset from quota_reset_date");

            UsageWindow chat = s.Windows[1];
            That(!chat.Primary && chat.Key == "chat" && chat.Label == "chat",
                "chat window identity");
            That(chat.UsedPercent == 0, "chat percent");

            // An unlimited block is skipped entirely; the other block keeps its absolute
            // fields only when both exist.
            string unlimited = "{\"copilot_plan\":\"pro\",\"quota_reset_date\":\"2027-01-01T08:30:00Z\"," +
                "\"quota_snapshots\":{\"premium_interactions\":{\"unlimited\":true}," +
                "\"chat\":{\"percent_remaining\":90}}}";
            UsageSnapshot u = CopilotProvider.Parse(unlimited);
            That(u.Status == ProviderStatus.Ok && u.Windows.Count == 1, "unlimited block skipped");
            That(u.Windows[0].Key == "chat" && u.Windows[0].UsedPercent == 10,
                "remaining percent mapped");
            That(!u.Windows[0].Used.HasValue && !u.Windows[0].Limit.HasValue,
                "no absolute fields when absent");
            That(u.Windows[0].ResetsAtUtc.HasValue && u.Windows[0].ResetsAtUtc.Value ==
                new DateTime(2027, 1, 1, 8, 30, 0, DateTimeKind.Utc), "date-time reset honoured");

            // A block without a percent is skipped too; a plan alone then reads as Ok.
            UsageSnapshot noPercent = CopilotProvider.Parse(
                "{\"copilot_plan\":\"pro\",\"quota_snapshots\":{\"chat\":{\"entitlement\":5}}}");
            That(noPercent.Status == ProviderStatus.Ok && noPercent.Windows.Count == 0,
                "block without percent skipped");
            That(noPercent.Message == "no quota reported", "plan with no blocks -> Ok message");

            // No blocks at all: Ok with a plan, Error without one.
            UsageSnapshot planOnly = CopilotProvider.Parse("{\"copilot_plan\":\"free\"}");
            That(planOnly.Status == ProviderStatus.Ok && planOnly.Windows.Count == 0 &&
                planOnly.Message == "no quota reported", "plan-only -> Ok, no quota reported");
            UsageSnapshot bare = CopilotProvider.Parse("{}");
            That(bare.Status == ProviderStatus.Error && bare.Message == "unexpected response",
                "no plan and no blocks -> Error");
            UsageSnapshot garbage = CopilotProvider.Parse("nope");
            That(garbage.Status == ProviderStatus.Error, "garbage body -> Error");
        }

        static void CopilotTokenSources()
        {
            // The four candidate files, LOCALAPPDATA before the profile folder.
            string profile = Path.Combine(Path.GetTempPath(), "profile-fixture");
            string local = Path.Combine(Path.GetTempPath(), "local-fixture");
            string[] files = CopilotProvider.DefaultCredentialFiles(local, profile);
            That(files.Length == 4, "four credential candidates");
            That(files[0] == Path.Combine(local, "github-copilot", "apps.json"), "first candidate");
            That(files[3] == Path.Combine(profile, ".config", "github-copilot", "hosts.json"),
                "last candidate");

            // Token extraction from both file shapes.
            That(CopilotProvider.FirstOAuthToken(
                "{\"github.com\":{\"user\":\"octocat\",\"oauth_token\":\"tok-hosts\"}}") ==
                "tok-hosts", "hosts.json shape");
            That(CopilotProvider.FirstOAuthToken(
                "{\"github.com:device\":{\"oauth_token\":\"tok-apps\"}}") == "tok-apps",
                "apps.json shape");
            That(CopilotProvider.FirstOAuthToken("{\"other\":{\"oauth_token\":\"tok\"}}") == null,
                "non-github key ignored");
            That(CopilotProvider.FirstOAuthToken("{\"github.com\":{\"oauth_token\":\"\"}}") == null,
                "empty token ignored");
            That(CopilotProvider.FirstOAuthToken("{\"github.com\":\"plain\"}") == null,
                "non-object value ignored");
            That(CopilotProvider.FirstOAuthToken("nope") == null, "garbage file ignored");

            // The first existing file wins.
            string root = Path.Combine(AppPaths.DataDir, "copilot");
            Directory.CreateDirectory(root);
            string hosts = Path.Combine(root, "hosts.json");
            string apps = Path.Combine(root, "apps.json");
            File.WriteAllText(apps, "{\"github.com\":{\"oauth_token\":\"tok-apps\"}}");
            That(CopilotProvider.FindOAuthToken(new[] { hosts, apps }) == "tok-apps",
                "missing first file skipped");
            File.WriteAllText(hosts, "{\"github.com\":{\"oauth_token\":\"tok-hosts\"}}");
            That(CopilotProvider.FindOAuthToken(new[] { hosts, apps }) == "tok-hosts",
                "first file wins");
            That(CopilotProvider.FindOAuthToken(null) == null, "no files -> no token");

            // Detection and the no-token fetch states, against temp files only.
            string missingApps = Path.Combine(root, "nope-apps.json");
            string missingHosts = Path.Combine(root, "nope-hosts.json");
            var absent = new CopilotProvider(new[] { missingApps, missingHosts });
            That(!absent.IsDetected(), "no credential file -> not detected");
            UsageSnapshot s = absent.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(s.Status == ProviderStatus.NotConfigured, "no token -> NotConfigured, no network");

            var present = new CopilotProvider(new[] { hosts, apps });
            That(present.IsDetected(), "any credential file -> detected");
            File.WriteAllText(hosts, "{\"github.com\":{}}");
            File.WriteAllText(apps, "{}");
            UsageSnapshot noToken = present.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(noToken.Status == ProviderStatus.NotConfigured,
                "files without tokens -> NotConfigured");
        }

        // --- registry and suggestions ---------------------------------------------------

        static void NewProvidersRegistryAndHelp()
        {
            var ids = new List<string>();
            foreach (IUsageProvider p in ProviderRegistry.All) ids.Add(p.Id);
            That(ids.Count == 9, "nine providers, got " + ids.Count);
            That(ids[0] == "claude-code" && ids[1] == "codex" && ids[2] == "zai" &&
                ids[3] == "cursor" && ids[4] == "github-copilot" && ids[5] == "openrouter" &&
                ids[6] == "antigravity" && ids[7] == "opencode-go" && ids[8] == "muse",
                "registry order, got " + string.Join(",", ids.ToArray()));
            That(ProviderRegistry.ExperimentalIds.Length == 3 &&
                ProviderRegistry.ExperimentalIds[0] == "antigravity" &&
                ProviderRegistry.ExperimentalIds[1] == "cursor" &&
                ProviderRegistry.ExperimentalIds[2] == "github-copilot", "experimental ids");

            That(new OpenRouterProvider().Id == "openrouter" &&
                new OpenRouterProvider().DisplayName == "OpenRouter", "openrouter identity");
            That(new CursorProvider().Id == "cursor" &&
                new CursorProvider().DisplayName == "Cursor", "cursor identity");
            That(new CopilotProvider().Id == "github-copilot" &&
                new CopilotProvider().DisplayName == "GitHub Copilot", "copilot identity");

            That(KnownTools.HelpText("cursor") == "Experimental: enable under Subscriptions",
                "cursor help text");
            That(KnownTools.HelpText("github-copilot") == "Experimental: enable under Subscriptions",
                "copilot help text");
            That(KnownTools.HelpText("antigravity") == "Install the agy CLI and sign in to see usage",
                "antigravity help unchanged");
            That(KnownTools.HelpText("gemini-cli") == null, "other tools keep no help line");
        }

    }
}
