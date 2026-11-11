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

## 12. Other providers (verified 2026-11-11)

All follow sections 3 and 7. Each has a pure `Parse` and a fixture under `tests/fixtures/`.

### Claude Code (`claude-code`)

- Login: `%USERPROFILE%\.claude\.credentials.json` → `claudeAiOauth.accessToken`. Missing →
  `NotConfigured`. `claudeAiOauth.expiresAt` (epoch ms) in the past → `AuthRequired`
  "sign in needed: open Claude Code", without a network call (these logins last about 8 hours).
- `GET https://api.anthropic.com/api/oauth/usage`, headers `Authorization: Bearer <token>`,
  `anthropic-beta: oauth-2025-04-20`. 401/403 → `AuthRequired` (same message).
- Map: `five_hour.utilization` → `FiveHour`, `seven_day.utilization` → `Weekly`; `resets_at` is
  ISO 8601 with offset. A `null` block is absent. Other keys are ignored.
- Plan name from the credentials file, not the response: `subscriptionType` `max` with
  `rateLimitTier` containing `5x` → "Max 5x", `20x` → "Max 20x", otherwise "Max"; `pro` → "Pro";
  anything else → the raw value with a capital first letter.

### Z.ai GLM (`zai`)

- Login: `%USERPROFILE%\.local\share\opencode\auth.json` → `zai-coding-plan.key`. Missing →
  `NotConfigured`.
- `GET https://api.z.ai/api/monitor/usage/quota/limit`, header `Authorization: Bearer <key>`.
  HTTP 401/403, or a 200 body with `success` false and `code` 401 → `AuthRequired`
  "sign in needed: check Z.ai key".
- Map each entry of `data.limits`: `type` `TOKENS_LIMIT` with `unit` 3 → `FiveHour`, `unit` 6 →
  `Weekly`; `type` `TIME_LIMIT` → `Monthly` (this is the monthly web-tool quota; `currentValue` →
  `Used`, `usage` → `Limit`). `percentage` → `UsedPercent`, `nextResetTime` (epoch ms) →
  `ResetsAtUtc`. Anything else is ignored.
- Plan name: `data.level` `lite` → "Coding Lite", `pro` → "Coding Pro", `max` → "Coding Max",
  otherwise the raw value.

### Muse (`muse`)

- Source: the official CLI command `muse usage --json`, run as `cmd.exe /c muse usage --json` with
  no window, stdout captured, 15 s timeout (kill the process tree on timeout). The CLI renews its
  own login, so this provider never reads Muse's token. CLI not found → `NotConfigured`.
- Output `{"schemaVersion":1,"allowance":{...}|null,"reason":...,"hint":...}`.
  - `allowance` null with `reason` `server_sent_no_usage` → `Ok`, no windows, message
    "no usage yet in this window".
  - `allowance` null with any other reason, or a non-zero exit code mentioning login → `AuthRequired`
    "sign in needed: run muse login"; otherwise `Error` with the reason.
  - `allowance` object: `tier` → `PlanName` (capital first letter); `window.usedPercent` with
    `window.windowDurationMins` 300 → `FiveHour` (other durations ignored);
    `weekly.usedPercent` → `Weekly`; `resetsAtMs` (epoch ms) → `ResetsAtUtc`.
  - The populated shape is taken from Muse's exported wire schema (`SubscriptionUsage`) and has
    not been observed live; `tests/fixtures/muse-usage-synthetic.json` is hand-written from it.

### OpenCode Go (`opencode-go`)

The vendor has no usage API: limits are visible only in the web console, and the API key is
rejected by every usage route tried. The provider returns `NotConfigured` with the message
"no usage API from vendor". Replace it when one exists.

An `Ok` snapshot with no windows draws its `Message` dim in place of the bars.

## 13. Auto-detection and suggestions

The widget works out which subscriptions are used on this PC and shows only those, and it points
out AI tools it finds but cannot report on yet.

### Detection

- `IUsageProvider` gains `bool IsDetected()`: a cheap, local, existence-only check. It never reads
  a token value, never starts a process and never uses the network.

  | Provider | Detected when |
  |---|---|
  | Claude Code | `%USERPROFILE%\.claude\.credentials.json` exists |
  | Codex | `auth.json` exists under `%CODEX_HOME%` or `%USERPROFILE%\.codex` |
  | Z.ai GLM | a Z.ai key source exists (see below) |
  | OpenCode Go | `%USERPROFILE%\.local\share\opencode\auth.json` has an `opencode-go` entry |
  | Muse | `muse` is on `PATH` (`muse.exe`, `muse.cmd` or `muse.bat` in a PATH folder) |

- Z.ai key sources, first match wins: `zai-coding-plan.key` in OpenCode's `auth.json`; the
  `ZAI_API_KEY` environment variable; `provider.zai.options.apiKey` in
  `%USERPROFILE%\.zcode\cli\config.json`. `ZaiProvider.FetchAsync` uses the same order.
- `settings.json` gains `"autoDetect": true` (default). A provider is **shown** when:
  - `settings.providers` lists it with `enabled: true` (always shown, detected or not), or
  - it is not listed, `autoDetect` is true and `IsDetected()` is true.
  It is **hidden** when listed with `enabled: false`, or not listed and not detected.
  With `autoDetect` false, unlisted providers are shown (the pre-detection behaviour).
- Detection runs at startup and at the start of every refresh. A provider that becomes detected
  appears on that refresh and the tray shows a balloon "Detected <name>: added to the widget". One
  that stops being detected disappears without a message. Order is still section 6's.
- If nothing is shown, the body draws one dim line: "No AI subscriptions detected on this PC".

### Tray menu: Subscriptions

A "Subscriptions" submenu lists every registered provider with a check mark when shown and a
suffix: "(detected)", "(not detected)" or "(hidden)". Clicking toggles it by writing an explicit
`enabled` entry to `settings.providers`. A last item "Auto-detect" toggles `autoDetect`.

### Suggestions for tools without a provider

`KnownTools` is a static catalogue of AI tools the widget has no usage provider for, each with a
display name and existence-only signatures:

| Tool | Detected when |
|---|---|
| Cursor | `%USERPROFILE%\.cursor` exists, or `cursor-agent` is on PATH |
| GitHub Copilot | `%LOCALAPPDATA%\github-copilot` or `%USERPROFILE%\.config\github-copilot` exists |
| Gemini CLI | `%USERPROFILE%\.gemini\oauth_creds.json` exists, or `gemini` is on PATH (the bare `.gemini` folder is also created by Antigravity, so it is not a signal) |
| Grok Build | `%USERPROFILE%\.grok` exists, or `grok` is on PATH |
| Antigravity | `%USERPROFILE%\.antigravity` exists, or `agy` is on PATH |

- A detected, not dismissed tool is a **suggestion**. Suggestions never appear as usage rows.
- The footer shows, left of the layout chips, dim text "+N not supported yet" when N > 0.
- The tray menu gets a "Detected, no usage support yet" submenu: one entry per suggestion with two
  actions, "Request support…" (opens
  `https://github.com/<update.repo>/issues/new?title=Add+provider:+<name>` in the default browser;
  disabled when `update.repo` is empty) and "Dismiss" (adds the tool id to
  `settings.dismissedSuggestions`).
- The first time a tool is detected, one balloon: "<name> found on this PC: no usage support yet".
  Ids already announced are kept in `settings.announcedSuggestions` so it is said once.

All path and PATH probing goes through one small `Detection` helper that takes the profile folder,
local-app-data folder and PATH string as parameters, so the self-test drives it with temp folders.

## 14. Antigravity provider (`antigravity`) — NOT verified against real output

Decision (2026-11-11): use only the official `agy` command. Google permits third-party tools to
run the unmodified `agy` binary under the user's own login and forbids reading Antigravity's saved
OAuth tokens, so this provider never touches any Antigravity credential or state file and never
calls Google endpoints itself.

- `IsDetected()`: `agy` is on PATH (`agy.exe`, `agy.cmd` or `agy.bat`). The Antigravity desktop app
  alone does not count; in that case the existing "Antigravity" suggestion stays, and its tray
  entry gains a third line of help text: "Install the agy CLI and sign in to see usage".
  When the provider is detected, the Antigravity suggestion is suppressed.
- Source: `cmd.exe /c agy -p /usage --output-format json`, no window, stdout and stderr captured,
  20 s timeout with the process tree killed on timeout. Same injectable-runner design as
  `MuseProvider`; share the process-running code between the two rather than copying it.
- Exit 9009 or "is not recognized" → `NotConfigured`. A non-zero exit or output mentioning login,
  sign in or auth → `AuthRequired` "sign in needed: run agy login". Timeout → `Error`.
- Parsing is deliberately tolerant because the JSON shape has not been observed. Walk the whole
  JSON tree and collect every object that has a remaining fraction (`remaining_fraction` or
  `remainingFraction`, a number 0..1; `null` is skipped) — a "bucket". For each bucket:
  - `UsedPercent` = (1 - fraction) × 100, clamped to 0..100.
  - Reset time from `reset_time` / `resetTime` (ISO 8601) or, failing that, now +
    `reset_in_seconds` / `resetInSeconds`.
  - Kind from the bucket's own id/name fields and the keys on the path leading to it, lower-cased:
    contains `5h`, `5-hour`, `5_hour`, `five` or `hour` → `FiveHour`; contains `week` → `Weekly`;
    contains `month` → `Monthly`; otherwise the bucket is ignored.
  - Antigravity has several model groups (for example Gemini and Claude/GPT), each with its own
    5-hour and weekly bucket. The contract has one window per kind, so keep, per kind, the bucket
    with the highest `UsedPercent` (the one closest to its limit).
- Plan name: a top-level `tier` or `plan` string when present, otherwise empty.
- No buckets found in valid JSON → `Error` "unexpected response". Non-JSON output → `Error`.
- Registry order: after Z.ai GLM, before OpenCode Go.
- Fixture `tests/fixtures/antigravity-usage-synthetic.json` is hand-written from Antigravity's
  documented statusline fields and must be replaced by a real capture once `agy` is installed.
