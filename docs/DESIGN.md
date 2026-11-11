# AI Usage Widget — design specification

Always-on-top Windows 10/11 widget showing usage of AI subscriptions per tool: 5-hour, weekly and
monthly windows, colour-coded, refreshed every 5 minutes or on demand. Idle memory target: under
50 MB working set.

## 1. Framework

**WinForms on .NET Framework 4.8, custom-drawn, compiled with the in-box `csc` (C# 5).**

| Option | Idle working set / private (measured on the prototype) | Verdict |
|---|---|---|
| WinForms, custom-drawn | 37 MB / 24 MB | chosen |
| WPF | 80 MB / 61 MB (58 / 47 with software rendering) | over target |
| WebView2 / Electron | 100 MB+ (not measured) | rejected |

Consequences: no SDK or runtime install, one small exe, **C# 5 syntax only** (no `$""`, `?.`,
`nameof`, expression-bodied members, auto-property initialisers, `out var`). All UI is drawn in
`OnPaint`; there are no child controls. No NuGet packages. Framework references only:
`System`, `System.Core`, `System.Drawing`, `System.Windows.Forms`, `System.Net.Http`,
`System.Web.Extensions` (JSON via `JavaScriptSerializer`), `System.Security` (DPAPI).

The approved look is `prototype/PrototypeWinForms.cs`. Production code is written fresh in `src/`
and reproduces that look; the prototype is reference only and is not modified.

## 2. Layout

```
build.ps1            -Version x.y.z [-Run]; writes AssemblyInfo, csc /warnaserror+, runs --selftest
release.ps1          -Version x.y.z; build + selftest + sha256 + latest.json + gh release create
app.manifest         per-monitor DPI aware, asInvoker
src/
  Program.cs         entry point, single-instance mutex, --selftest [--selftest-out <file>], --updated
  WidgetForm.cs      window: paint, hit-testing, drag/resize, tray menu, scrolling
  Painter.cs         Detailed and Compact row drawing, colour rule
  Core/UsageSnapshot.cs    data contract
  Core/IUsageProvider.cs   provider interface
  Core/ProviderRegistry.cs explicit provider list
  Core/RefreshService.cs   timer, parallel fetch, timeout, backoff
  Core/SnapshotCache.cs    last-good snapshots on disk
  Core/Settings.cs         settings.json
  Core/Http.cs             shared HttpClient, TLS 1.2
  Core/Json.cs             JavaScriptSerializer helpers
  Core/Updater.cs          auto-update
  Core/TokenStore.cs       DPAPI-protected update token
  Providers/CodexProvider.cs
  Providers/StubProviders.cs   ClaudeCode, Zai, Muse, OpenCodeGo -> NotConfigured
  SelfTest.cs
tests/fixtures/codex-usage.json   real response, identifiers redacted
prototype/           throwaway reference
```

Data directory: `%LOCALAPPDATA%\AiUsageWidget\` (`settings.json`, `cache.json`, `update\`,
`update-token.bin`). Overridable with the `AI_USAGE_WIDGET_DATA` environment variable, which the
self-test uses so it never touches real data.

## 3. Data contract

```csharp
public enum WindowKind { FiveHour, Weekly, Monthly }
public enum ProviderStatus { Ok, Stale, AuthRequired, NotConfigured, Error }

public sealed class UsageWindow {
    public WindowKind Kind;
    public double UsedPercent;        // 0..100, always set
    public double? Used;              // absolute, only when the vendor reports it
    public double? Limit;
    public DateTime? ResetsAtUtc;
}

public sealed class UsageSnapshot {
    public string ProviderId;         // "codex"
    public string DisplayName;        // "Codex"
    public string PlanName;           // "Pro Lite"; empty when unknown
    public List<UsageWindow> Windows; // only the windows this plan has; never null
    public DateTime FetchedAtUtc;
    public ProviderStatus Status;
    public string Message;            // short, user-facing; never contains a token
}

public interface IUsageProvider {
    string Id { get; }
    string DisplayName { get; }
    Task<UsageSnapshot> FetchAsync(CancellationToken ct);
}
```

Vendors report percent used and a reset time, not absolute counts, so the contract is
percent-first. A window a plan does not have is absent from `Windows` and is not drawn.

`FetchAsync` never throws for expected conditions: it returns a snapshot with `NotConfigured`
(no local login), `AuthRequired` (401/403) or `Error`. Unexpected exceptions are caught by
`RefreshService` and become `Error`.

## 4. Colour rule

One function, `Painter.ColorFor(percent)`: `< 50` green `#3FB950`, `50..70` inclusive yellow
`#D29922`, `> 70` red `#F85149`. Stale rows draw the same colours at reduced alpha.

## 5. Window

- Borderless, `TopMost`, not in the taskbar, resizable from all edges, drag anywhere to move
  (`WM_NCHITTEST`), no maximise on double-click, mouse-wheel scrolling when content is taller than
  the window. Single instance.
- Header: title, "updated N min ago" (re-rendered each minute), refresh button, close button
  (close hides to tray; tray Exit quits).
- Two layouts, set by `settings.json: layout`: `"detailed"` (default: one card per tool, a bar per
  window with percent and "resets 2h 10m") and `"compact"` (one line per tool, three bar columns).
- Row states: Ok; Stale (dimmed, "stale 42m" tag); AuthRequired (yellow message, e.g.
  "sign in needed: run codex login"); NotConfigured (dim "not configured"); Error (dim message).
- Tray menu: Refresh now · Layout ▸ Detailed / Compact · Always on top · Start with Windows
  (HKCU `Software\Microsoft\Windows\CurrentVersion\Run`) · Check for updates · Set update token… ·
  version line · Exit.
- Position, size, layout, always-on-top are persisted. A saved position that is off every screen
  falls back to the bottom-right of the primary work area.
- No "PROTOTYPE" footer and no layout chips in production; layout is changed from the tray menu.

## 6. Settings (`settings.json`)

```json
{
  "refreshMinutes": 5,
  "layout": "detailed",
  "alwaysOnTop": true,
  "bounds": { "x": 0, "y": 0, "width": 340, "height": 560 },
  "providers": [ { "id": "codex", "enabled": true } ],
  "update": { "mode": "auto", "repo": "", "requiresToken": true }
}
```

Missing or malformed file → defaults. Unknown keys are preserved on save where practical.
`providers` controls order and enablement; a registered provider missing from the list is appended
enabled. `refreshMinutes` is clamped to 1..120.

## 7. Refresh and caching

- A `System.Windows.Forms.Timer` fires every `refreshMinutes`. Manual refresh (header button, tray)
  is ignored when a refresh started less than 30 s ago or one is in flight.
- Enabled providers fetch in parallel, each with a 15 s timeout. One provider failing or timing
  out never delays or blanks the others.
- Success → the snapshot replaces the cached one and `cache.json` is rewritten (write to a temp
  file, then replace).
- Startup paints `cache.json` immediately, then refreshes.
- Failure (`Error` or timeout) with a previous good snapshot → keep showing it as `Stale`.
  Without one → show the `Error` snapshot.
- Backoff per provider after `Error`/timeout/HTTP 429: skip scheduled refreshes for 10, then 20,
  then 30 minutes (cap); reset on success. Manual refresh bypasses backoff.
- `AuthRequired` and `NotConfigured` replace the cached snapshot (they are current facts) and do
  not back off.
- Login tokens are read from disk on each fetch, kept only in local variables, never logged,
  never cached, never written anywhere. **The widget never refreshes or rewrites a CLI's token
  file**: a second refresher can invalidate the CLI's own login. An expired login shows
  `AuthRequired` until the user runs that CLI again (Codex logins last 10 days).

## 8. Codex provider (verified 2026-11-11)

1. Read `%CODEX_HOME%\auth.json`, else `%USERPROFILE%\.codex\auth.json`. Missing file, or no
   `tokens.access_token` → `NotConfigured`.
2. `GET https://chatgpt.com/backend-api/wham/usage`, headers `Authorization: Bearer <access_token>`,
   `ChatGPT-Account-Id: <tokens.account_id>` (when present), `User-Agent: ai-usage-widget`.
3. 401/403 → `AuthRequired` "sign in needed: run codex login". 429/5xx/network → `Error`.
4. Map the response (see `tests/fixtures/codex-usage.json`):
   - `plan_type` → `PlanName` (known ids mapped to display names: `plus`→"Plus", `pro`→"Pro",
     `prolite`→"Pro Lite", `team`→"Team"; otherwise the raw value).
   - `rate_limit.primary_window` and `rate_limit.secondary_window` (either may be `null`):
     `used_percent` → `UsedPercent`; `reset_at` (epoch seconds) → `ResetsAtUtc`;
     `limit_window_seconds` decides the kind: 18000 → `FiveHour`, 604800 → `Weekly`,
     2592000 or more → `Monthly`. **Match by length, never by primary/secondary position**: on the
     current plan `primary_window` is the weekly window and there is no 5-hour window. A window
     with any other length is ignored.

The endpoint is undocumented and may change; a response that parses but yields no windows is
`Error` "unexpected response", not an empty Ok row.

## 9. Auto-update

Source: GitHub Releases of `settings.update.repo` (`owner/name`). Private repo now
(`requiresToken: true`), public later by setting `requiresToken: false`; no code change. Empty
`repo` → updater disabled.

- **Release** (`release.ps1 -Version x.y.z`): build, self-test, compute SHA-256, write
  `latest.json` `{ "version": "x.y.z", "sha256": "<hex>", "asset": "AiUsageWidget.exe" }`, then
  `gh release create vx.y.z AiUsageWidget.exe latest.json`. Run manually; requires a clean tree.
- **Check**: at startup (after first paint) and every 24 h, plus tray "Check for updates".
  `GET https://api.github.com/repos/<repo>/releases/latest` (`Accept: application/vnd.github+json`,
  `Authorization: Bearer <token>` when required). Compare `tag_name` (leading `v` stripped) with
  the running assembly version using `System.Version`; only strictly newer proceeds.
- **Download** `latest.json` and the exe asset to `%LOCALAPPDATA%\AiUsageWidget\update\`. Private
  assets are fetched from the asset's API `url` with `Accept: application/octet-stream`. Only
  `https` URLs on `api.github.com`, `github.com` or `*.githubusercontent.com` are followed, and the
  token is sent only to `api.github.com`.
- **Verify**: SHA-256 of the downloaded exe must equal `latest.json.sha256` (case-insensitive) and
  `latest.json.version` must equal the release tag. Any mismatch deletes the download and reports
  "update failed" in the tray tooltip. Nothing unverified is ever executed.
- **Swap**: rename the running exe to `AiUsageWidget.old.exe`, move the new exe into place, start
  it with `--updated <oldPid>`, exit. If the move or start fails, rename back and keep running. On
  `--updated`, the new process waits for the old pid to exit and deletes the `.old` file.
- **Modes** (`update.mode`): `auto` (install when found), `notify` (tray balloon and a menu item
  "Install update x.y.z"), `off`.
- **Token**: fine-grained GitHub token, read-only Contents on the one repo. Entered through tray
  "Set update token…" (a small input dialog), stored with `ProtectedData.Protect(...,
  DataProtectionScope.CurrentUser)` in `update-token.bin`. Never in `settings.json`, logs or
  messages. Required but missing → checks are skipped and the tray shows "updates: token needed".

## 10. Self-test

`AiUsageWidget.exe --selftest [--selftest-out <file>]` runs headless, prints one `PASS <name>` or
`FAIL <name>: <reason>` line per check, and exits non-zero on any failure. It uses a temp data
directory and no network. Checks: Codex mapping from the fixture (plan name, weekly window, no
5-hour window, reset time); window kind by length for 18000 / 604800 / 2592000 / unknown; both
windows `null` → Error; colour thresholds at 49.9 / 50 / 70 / 70.1; cache round-trip; stale and
backoff transitions with a fake provider and an injectable clock; missing auth file →
NotConfigured; settings defaults and clamping; version comparison; hash-mismatch rejection;
exe swap and rollback in a temp folder; token store round-trip.

`build.ps1` runs the self-test after compiling and fails the build on any non-PASS line.

## 11. Adding a provider

1. Add `src/Providers/<Name>Provider.cs` implementing `IUsageProvider`. Split it into a pure
   `Parse(string json)` (testable from a fixture) and the I/O in `FetchAsync`.
2. Add one line to `ProviderRegistry.All`.
3. Save a real response with identifiers redacted under `tests/fixtures/` and add self-test checks.
4. Follow the token rule in section 7: read, use, forget; never refresh or write.

Starting points for the current stubs, each to be verified with one live call before coding:

| Provider | Local login source |
|---|---|
| Claude Code | `%USERPROFILE%\.claude\.credentials.json` → `claudeAiOauth` |
| Z.ai GLM | `%USERPROFILE%\.local\share\opencode\auth.json` → `zai-coding-plan` |
| OpenCode Go | same file → `opencode-go` |
| Muse | `%USERPROFILE%\.config\muse\auth.json` → `providers` |
