# AI Usage Widget — design

Always-on-top Windows 10/11 widget showing usage of AI subscriptions per tool: 5-hour, weekly and
monthly windows, colour-coded, refreshed every 5 minutes or on demand. The widget reads the login
file a tool already keeps on this PC — or runs the vendor's own CLI — calls that vendor's usage
endpoint, and draws the result. Idle memory target: under 50 MB working set.

## 1. Purpose and constraints

**WinForms on .NET Framework 4.8, custom-drawn, compiled with the in-box `csc` (C# 5).**

| Option | Idle working set / private (measured on the prototype) | Verdict |
|---|---|---|
| WinForms, custom-drawn | 37 MB / 24 MB | chosen |
| WPF | 80 MB / 61 MB (58 / 47 with software rendering) | over target |
| WebView2 / Electron | 100 MB+ (not measured) | rejected |

Consequences: no SDK or runtime install, one small exe, **C# 5 syntax only** (no `$""`, `?.`,
`nameof`, expression-bodied members, auto-property initialisers, `out var`). All widget UI is
drawn in `OnPaint`; there are no child controls. No NuGet packages. Framework references only:
`System`, `System.Core`, `System.Drawing`, `System.Windows.Forms`, `System.Net.Http`,
`System.Web.Extensions` (JSON via `JavaScriptSerializer`), `System.Security` (DPAPI).

After each completed refresh the widget trims its working set (`GC.Collect` plus
`SetProcessWorkingSetSize(-1, -1)`) to hand unused pages back to Windows.

## 2. Source layout

```
build.ps1            -Version x.y.z [-OutDir dir] [-Run]; writes AssemblyInfo, csc /warnaserror+, builds and runs the tests exe
release.ps1          -Version x.y.z; build + sha256 + signed latest.json + gh release create; needs the signing key
app.manifest         per-monitor DPI aware (PerMonitorV2), asInvoker, Windows 10+ supportedOS
assets/app.ico       exe and tray icon (embedded as a named resource)
assets/make-icons.ps1  regenerates the icon set
tools/new-update-key.ps1  generates the RSA update-signing key pair
src/
  Program.cs         entry point: single-instance mutex, exception handlers, --updated
  WidgetForm.cs      the window: paint, hit-testing, drag/resize, menus, tray, timers, detection
  Painter.cs         drawing and hit-testing for both layouts, row/view-model building
  Theme.cs           dark and light colour palettes
  SettingsForm.cs    the settings window (pure value copy-in/copy-out, self-tested)
  ConsentForm.cs     the first-run window (pure row plan, self-tested); shared row drawing
  (tests live in tests\: SelfTest.cs, Screenshots.cs, TestProgram.cs -> AiUsageWidget.Tests.exe)
  Core/
    UsageSnapshot.cs     data contract (UsageSnapshot, UsageWindow, WindowKind, ProviderStatus)
    IUsageProvider.cs    provider interface
    ProviderRegistry.cs  every provider, in row order; ExperimentalIds
    ProviderInfo.cs      one-line disclosure per provider for consent/settings rows
    Detection.cs         existence-only probing helpers (files, folders, PATH)
    KnownTools.cs        tools without a provider; suggestions; help lines; issue URLs
    RefreshService.cs    parallel fetch, per-provider timeout, backoff, cache persistence
    SnapshotCache.cs     cache.json read/write
    History.cs           history.json read/write, per-window samples, pruning
    Forecast.cs          pace percent and the limit forecast (pure)
    Notifier.cs          threshold-crossing and reset messages (pure)
    Settings.cs          settings.json plus AppPaths (all on-disk locations)
    Http.cs              one shared HttpClient, TLS 1.2, stable User-Agent
    Json.cs              JavaScriptSerializer helpers + string-safe indented printer
    Errors.cs            exceptions and HTTP statuses as short user-facing text
    CliProcess.cs        hidden cmd.exe runner for the CLI providers; kills the tree on timeout
    Sqlite.cs            read-only single-value reads from Cursor's state.vscdb via winsqlite3.dll
    SecretStore.cs       DPAPI-protected named secrets (the OpenRouter key)
    TokenStore.cs        DPAPI-protected GitHub update token
    UpdateKey.cs         the public half of the update-signing key (generated; empty = unsigned build)
    Updater.cs           GitHub Releases auto-update: check, verify, swap, rollback
    Log.cs               log.txt writer with rotation
  Providers/
    ClaudeCodeProvider.cs  CodexProvider.cs  ZaiProvider.cs  CursorProvider.cs
    CopilotProvider.cs     OpenRouterProvider.cs  AntigravityProvider.cs
    OpenCodeGoProvider.cs  MuseProvider.cs
tests/fixtures/      redacted real captures and hand-written synthetic shapes (see section 5)
```

Data directory: `%LOCALAPPDATA%\AiUsageWidget\` — `settings.json`, `cache.json`, `history.json`,
`log.txt` (rotated to `log.old.txt` at 256 KB), `secret-<name>.bin`, `update-token.bin`,
`update\`. Overridable with the `AI_USAGE_WIDGET_DATA` environment variable, which the self-test
uses so it never touches real data.

## 3. Data contract

```csharp
public enum WindowKind { FiveHour, Daily, Weekly, Monthly, Other }
public enum ProviderStatus { Ok, Stale, AuthRequired, NotConfigured, Error }

public sealed class UsageWindow {
    public string Key;                // stable id, e.g. "5h", "7d", "cycle"
    public string Label;              // row label; null draws the default label for the kind
    public bool Primary = true;       // false for a vendor's supplemental windows
    public int? DurationSeconds;      // window length, when the vendor gives one
    public string Unit;               // e.g. "credits", "requests", "USD", "cents"
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
    public DateTime FetchedAtUtc;     // stamped by RefreshService
    public ProviderStatus Status;
    public string Message;            // short, user-facing; never contains a token
}
```

Vendors report percent used and a reset time, not absolute counts, so the contract is
percent-first. A window a plan does not have is absent from `Windows` and is not drawn.
`UsageWindow.KindForDuration` maps a duration to a kind with tolerance (18000 ± 1800 s →
FiveHour, 86400 ± 8640 → Daily, 604800 ± 60480 → Weekly, 2419200 s or more → Monthly, else
Other); individual providers match by exact length where the vendor documents it.

`FetchAsync` never throws for expected conditions: it returns a snapshot with `NotConfigured`
(no local login), `AuthRequired` (expired login or 401/403) or `Error`. Unexpected exceptions are
caught by `RefreshService` and become `Error`.

## 4. Provider interface and rules

```csharp
public interface IUsageProvider {
    string Id { get; }
    string DisplayName { get; }
    Task<UsageSnapshot> FetchAsync(CancellationToken ct);
    bool IsDetected();
}
```

Rules every provider follows:

1. **Pure `Parse`.** The response mapping is a static `Parse` with no I/O and no clock, directly
   callable from the self-test against a fixture. `FetchAsync` does the reading and the network
   call and never throws for expected conditions.
2. **A redacted fixture.** Real captures with every identifier redacted go under `tests/fixtures/`;
   shapes that have never been captured live are clearly named `*-synthetic.json`.
3. **Read, use, forget.** Tokens and keys are read from disk on each fetch, kept in local
   variables for the one request, and never logged, cached or written anywhere.
4. **Never write another tool's files.** The widget never refreshes or rewrites a CLI's token or
   auth file; an expired login is reported and the user re-logins with the tool itself. When a
   vendor offers an official CLI command for usage, run that CLI instead of touching its
   credentials at all.
5. **Experimental providers.** `ProviderRegistry.ExperimentalIds` (`antigravity`, `cursor`,
   `github-copilot`) lists providers whose endpoint is unofficial or whose command has never been
   observed. They are never auto-shown: only an explicit `{ "id": "<id>", "enabled": true }` entry
   shows one (section 6).
6. **A disclosure row.** `ProviderInfo` carries one sentence on what is read locally and who is
   contacted per provider id; the consent and settings rows draw it, and the self-test asserts
   every registered id is covered.

## 5. Providers

Registry order (= default row order): `claude-code`, `codex`, `zai`, `cursor`, `github-copilot`,
`openrouter`, `antigravity`, `opencode-go`, `muse`.

### Claude Code (`claude-code`) — unofficial endpoint; main windows verified

- Login: `%USERPROFILE%\.claude\.credentials.json` → `claudeAiOauth.accessToken`. Missing or no
  token → `NotConfigured`. `expiresAt` (epoch ms) in the past → `AuthRequired` "sign in needed:
  open Claude Code" straight from the file, with no network call (these logins last about 8 hours).
- `GET https://api.anthropic.com/api/oauth/usage`, headers `Authorization: Bearer <token>`,
  `anthropic-beta: oauth-2025-04-20`. 401/403 → `AuthRequired` (same message).
- Map: `five_hour.utilization` → FiveHour key `5h` (18000 s); `seven_day.utilization` → Weekly key
  `7d` (604800 s); `resets_at` (ISO 8601 with offset) → `ResetsAtUtc`. Supplemental windows, never
  primary: `seven_day_opus` / `seven_day_sonnet` → Weekly keys `7d-opus` / `7d-sonnet`, labels
  "week · Opus" / "week · Sonnet"; `extra_usage` when `is_enabled` and it has a `utilization` →
  Other key `extra`, label "extra usage", unit `credits`, `Used` = `used_credits`,
  `Limit` = `monthly_limit`.
- Plan name from the credentials file, not the response: `subscriptionType` `max` with
  `rateLimitTier` containing `5x` → "Max 5x", `20x` → "Max 20x", otherwise "Max"; `pro` → "Pro";
  anything else → the raw value with a capital first letter.
- Verification: `tests/fixtures/claude-usage.json` is a redacted live capture (five-hour and
  weekly windows). The Opus/Sonnet and extra-credits windows have never been observed populated —
  the real capture has those blocks null and disabled, and `claude-usage-extra.json` is
  hand-written to the same shape.

### Codex (`codex`) — unofficial endpoint; main windows verified

- Login: `%CODEX_HOME%\auth.json`, else `%USERPROFILE%\.codex\auth.json` → `tokens.access_token`
  (+ `tokens.account_id` as a header when present). Missing → `NotConfigured`.
- `GET https://chatgpt.com/backend-api/wham/usage`. 401/403 → `AuthRequired` "sign in needed:
  run codex login".
- Map: `rate_limit.primary_window` / `secondary_window` and `code_review_rate_limit` windows are
  matched **by `limit_window_seconds`, never by primary/secondary position** (on the observed plan
  the primary window is the weekly one). 18000 → FiveHour, 604800 → Weekly, 2592000 or more →
  Monthly, any other length → ignored. `used_percent` → `UsedPercent`, `reset_at` (epoch seconds)
  → `ResetsAtUtc`. Keys are the duration in seconds; review windows are prefixed `review-`, marked
  not primary, and labelled "code review · <window>".
- Plan name: `plan_type` `plus` → "Plus", `pro` → "Pro", `prolite` → "Pro Lite", `team` → "Team",
  otherwise the raw value.
- Verification: `tests/fixtures/codex-usage.json` is a redacted live capture (single weekly
  window). The code-review windows have never been observed populated — the real capture has
  `code_review_rate_limit: null`, and `codex-usage-review.json` is hand-written to the same shape.
- The endpoint is undocumented and may change; a response that parses but yields no windows is
  `Error` "unexpected response", not an empty Ok row.

### Z.ai GLM (`zai`) — your own Z.ai key; verified

- Key sources, first match wins: `zai-coding-plan.key` in
  `%USERPROFILE%\.local\share\opencode\auth.json`; the `ZAI_API_KEY` environment variable;
  `provider.zai.options.apiKey` in `%USERPROFILE%\.zcode\cli\config.json`. No source →
  `NotConfigured`; detection reads the same sources presence-only.
- `GET https://api.z.ai/api/monitor/usage/quota/limit`, header `Authorization: Bearer <key>`.
  HTTP 401/403, or a 200 body with `success` false and `code` 401 → `AuthRequired` "sign in
  needed: check Z.ai key".
- Map each entry of `data.limits`: `type` `TOKENS_LIMIT` with `unit` 3 → FiveHour `5h`, `unit` 6 →
  Weekly `7d`; `type` `TIME_LIMIT` → Monthly key `30d-tools`, label "web tools", unit `requests`
  (the one window with absolute numbers: `Used` = `currentValue`, `Limit` = `usage`).
  `percentage` → `UsedPercent`, `nextResetTime` (epoch ms) → `ResetsAtUtc`. Anything else ignored.
- Plan name: `data.level` `lite` → "Coding Lite", `pro` → "Coding Pro", `max` → "Coding Max",
  otherwise the raw value.
- Verification: `tests/fixtures/zai-quota.json` is a redacted live capture.

### OpenRouter (`openrouter`) — official, documented API

- Key: the `OPENROUTER_API_KEY` environment variable, else the key entered in the settings window
  (stored DPAPI-encrypted by `SecretStore` under the name `openrouter`). Neither present →
  `NotConfigured`.
- `GET https://openrouter.ai/api/v1/key`, header `Authorization: Bearer <key>`. 401/403 →
  `AuthRequired` "check OpenRouter key".
- Map `data`: `is_free_tier` → plan "Free", else the plan is unknown. With `limit` and
  `limit_remaining` present → one window key `limit`, label "key limit", unit `USD`:
  `Used` = `limit` − `limit_remaining`, `Limit` = `limit`, kind and duration from `limit_reset`
  (`daily` 86400 s / `weekly` 604800 s / `monthly` 2592000 s / other), `ResetsAtUtc` computed
  from the cadence (next UTC midnight; next Monday 00:00 UTC; the first of the next month 00:00
  UTC — the API gives no wall-clock reset time). Without a limit → Ok, no windows, message
  "no limit set: $<usage_monthly> used this month".
- Verification: follows the documented `/api/v1/key` response; `tests/fixtures/openrouter-key.json`
  is a saved sample of that shape.

### Cursor (`cursor`) — experimental, off by default; unverified

- Login: the access token in `%APPDATA%\Cursor\User\globalStorage\state.vscdb` (SQLite
  `ItemTable`, key `cursorAuth/accessToken`), read through `Core.Sqlite` — a minimal read-only
  wrapper over the `winsqlite3.dll` that ships with Windows 10/11; `SQLITE_BUSY` (Cursor keeps the
  database open) reads as "no value this time". The refresh token in the same table is never
  touched. Missing database or token → `NotConfigured`.
- The token is a JWT; an `exp` in the past → `AuthRequired` "sign in needed: open Cursor" straight
  from the database, with no network call. An unreadable token counts as not expired and the
  endpoint gets the final say. Plan hint: `cursorAuth/stripeMembershipType`.
- `POST https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage`, empty JSON
  object body, headers `Authorization: Bearer <token>`, `Connect-Protocol-Version: 1` (this
  provider builds its own POST on the shared HttpClient, which otherwise does GETs). 401/403 →
  `AuthRequired`.
- Map: `planUsage.totalPercentUsed`, falling back to `totalPercentUsed` at the root, → one Monthly
  window key `cycle`, label "billing cycle"; `Used` = `planUsage.totalSpend`, `Limit` =
  `planUsage.limit`, unit `cents` (only when present); `billingCycleStart`/`billingCycleEnd` (ISO
  8601 text or epoch milliseconds, quoted or not) → duration and `ResetsAtUtc`. Plan name is the
  capitalised membership type. Fields are read tolerantly because the endpoint has not been
  captured live.
- Verification: unverified — `tests/fixtures/cursor-usage-synthetic.json` is hand-written;
  `tests/fixtures/state.vscdb` is a real database shape for the SQLite reader. Experimental:
  unofficial, unpinned endpoint.

### GitHub Copilot (`github-copilot`) — experimental, off by default; unverified

- Login: the first `oauth_token` under a key starting with `github.com` in the first readable of:
  `%LOCALAPPDATA%\github-copilot\apps.json`, `hosts.json`,
  `%USERPROFILE%\.config\github-copilot\apps.json`, `hosts.json`. None → `NotConfigured`.
- `GET https://api.github.com/copilot_internal/user`, headers `Authorization: token <token>`,
  `Accept: application/json`, `Editor-Version: vscode/1.96.2`,
  `Editor-Plugin-Version: copilot-chat/0.26.7`, `X-Github-Api-Version: 2025-04-01`.
  401/403/404 → `AuthRequired` "sign in needed: open your editor".
- Map: `copilot_plan` → plan name; `quota_reset_date` (ISO date) → the reset time of both windows.
  `quota_snapshots.premium_interactions` (primary, label "premium requests") and `.chat` (label
  "chat"): a block that is `unlimited` or has no `percent_remaining` yields no window; otherwise
  one Monthly window, unit `requests`, `UsedPercent` = 100 − `percent_remaining`, and when
  `entitlement` and `remaining` are both present `Limit` = `entitlement`, `Used` = entitlement −
  remaining.
- Verification: unverified — `tests/fixtures/copilot-user-synthetic.json` is hand-written.
  Experimental: unofficial, unpinned endpoint.

### Antigravity (`antigravity`) — experimental, off by default; unverified

Hard rule: this provider never opens, reads or references any Antigravity or Gemini credential or
state file and never calls Google endpoints itself — it only runs the official `agy` command,
which Google permits third-party tools to run under the user's own login.

- Command: `cmd.exe /c agy -p /usage --output-format json`, no window, stdout and stderr
  captured, 15 s timeout with the process tree killed (`Core.CliProcess`, shared with Muse).
  Exit 9009 or cmd's "is not recognized" → `NotConfigured`; timeout → `Error`.
- Detection: `agy.exe` / `.cmd` / `.bat` in a PATH folder, PATH read fresh from the registry so a
  CLI installed after launch is found. The Antigravity desktop app alone does not count; in that
  case the Antigravity suggestion stays, with the help line "Install the agy CLI and sign in to
  see usage". When the provider is detected, the suggestion is suppressed.
- Parsing is deliberately tolerant because the JSON shape has never been observed: walk the whole
  JSON tree; every object with a remaining fraction (`remaining_fraction` / `remainingFraction`,
  null skipped) is a bucket. `UsedPercent` = (1 − fraction) × 100, rounded to two decimals and
  clamped to 0..100. Kind from the bucket's own `id`/`name`/`displayName` plus the keys on the
  path, lower-cased: contains `5h`, `hour` or `five` → FiveHour; `week` → Weekly; `month` →
  Monthly; otherwise ignored. Reset from `reset_time` / `resetTime` (ISO 8601) or now +
  `reset_in_seconds` / `resetInSeconds`. Keys `5h` / `7d` / `30d`. Antigravity reports several
  model groups with one bucket each per kind; per kind the highest `UsedPercent` wins. Plan name:
  a top-level `tier` or `plan`, capitalised.
- State rules: valid JSON with buckets and exit 0 → `Ok` whatever words the text happens to
  contain. An auth signal must be one of the explicit phrases ("not logged in", "not signed in",
  "sign in", "log in", "login required", "unauthenticated", "unauthorized", "401") in the output
  or stderr → `AuthRequired` "sign in needed: run agy login"; the bare substring "auth" is not
  one. Non-zero exit → `Error` with the first stderr line (60 chars) or the exit code. Exit 0 with
  no buckets → `Error` "unexpected response".
- Verification: unverified — `tests/fixtures/antigravity-usage-synthetic.json` is hand-written
  from Antigravity's documented statusline fields and must be replaced by a real capture.

### OpenCode Go (`opencode-go`) — placeholder

The vendor has no usage API: limits are visible only in the web console and the API key is
rejected by every usage route. The provider always returns `NotConfigured` "no usage API from
vendor" — replace it when one exists. Detection: an `opencode-go` entry (presence only) in
`%USERPROFILE%\.local\share\opencode\auth.json`.

An `Ok` snapshot with no windows draws its `Message` dim in place of the bars (Muse's
"no usage yet in this window" is the live example).

### Muse (`muse`) — official CLI; populated shape unverified

- Command: `cmd.exe /c muse usage --json`, no window, 15 s timeout, process tree killed on
  timeout. The CLI manages its own login, so this provider never reads or writes a Muse token.
  Exit 9009 or "is not recognized" → `NotConfigured`.
- Output: a non-zero exit mentioning "login" (in stderr or output) → `AuthRequired` "sign in
  needed: run muse login"; any other non-zero exit → `Error` with the stderr (60 chars) or the
  exit code. `allowance` null with `reason` `server_sent_no_usage` → Ok, no windows, message
  "no usage yet in this window"; `allowance` null otherwise → `AuthRequired` (same message).
- `allowance` object: `tier` → plan name (capital first letter); `window.usedPercent` with
  `window.windowDurationMins` 300 → FiveHour `5h` (other durations ignored); `weekly.usedPercent`
  → Weekly `7d`; `resetsAtMs` (epoch ms, per block, falling back to `allowance.resetsAtMs`) →
  `ResetsAtUtc`.
- Verification: the empty-allowance path matches a real capture
  (`tests/fixtures/muse-usage-empty.json`). The populated shape is taken from Muse's exported wire
  schema (`SubscriptionUsage`) and has not been observed live —
  `tests/fixtures/muse-usage-synthetic.json` is hand-written from it.

## 6. Detection, the shown/hidden rule, suggestions

`IsDetected()` is a cheap, local, existence-only check: it never reads a token value, never
starts a process and never uses the network. Detection runs at startup and at the start of every
refresh, and the result is cached so painting, menus and suggestions never rescan.

| Provider | Detected when |
|---|---|
| Claude Code | `%USERPROFILE%\.claude\.credentials.json` exists |
| Codex | `auth.json` exists under `%CODEX_HOME%` or `%USERPROFILE%\.codex` |
| Z.ai GLM | a Z.ai key source exists (section 5) |
| Cursor | the Cursor `state.vscdb` exists |
| GitHub Copilot | any of the four credential files exists |
| OpenRouter | `OPENROUTER_API_KEY` is set, or a key is stored for the widget |
| Antigravity | `agy` is on `PATH` (`.exe`, `.cmd` or `.bat`) |
| OpenCode Go | `auth.json` has an `opencode-go` entry |
| Muse | `muse` is on `PATH` (`.exe`, `.cmd` or `.bat`) |

`Settings.SelectShown` is the pure shown/hidden rule: registry order in, ordered shown list out.

- Listed in `settings.providers` with `enabled: true` → shown, detected or not.
- Listed with `enabled: false` → hidden.
- Unlisted → shown when `autoDetect` is false (the pre-detection behaviour), otherwise shown when
  detected.
- **Unlisted experimental providers (`ExperimentalIds`) never show**, whatever `autoDetect` and
  detection say; an explicit enabled entry still shows one.

A provider that becomes detected while the widget runs appears on that refresh with one balloon
"Detected <name>: added to the widget" (never on the very first run, where there is no settings
file yet and ids are just seeded into `knownProviders`). One that stops being detected disappears
without a message. If nothing is shown, the body draws one dim line: "No AI subscriptions
detected on this PC".

The tray **Subscriptions** submenu lists every registered provider with a check mark when shown
and a suffix "(detected)", "(not detected)" or "(hidden)"; clicking toggles it by writing an
explicit entry to `settings.providers` (hiding an auto-detected provider writes one disabled
entry on the first click). **Auto-detect** toggles `autoDetect`.

The first-run window (`ConsentForm`) shows the same choice before anything is fetched: one tick
per provider with its `ProviderInfo` disclosure (what is read, who is contacted, and
"unofficial endpoint, may stop working" for non-official paths). Detected providers come first and
start ticked; the rest start unticked and are marked "not found on this PC"; experimental ones are
never ticked and carry the "experimental" mark. Continue stores the ticked list; nothing is
fetched before the user continues. The settings window's Subscriptions tab reuses the same rows.

### Suggestions for tools without a provider

`KnownTools` is a static catalogue of AI tools the widget has no usage provider for, each with an
existence-only signature:

| Tool | Detected when |
|---|---|
| Cursor | `%USERPROFILE%\.cursor` exists, or `cursor-agent` is on PATH |
| GitHub Copilot | `%LOCALAPPDATA%\github-copilot` or `%USERPROFILE%\.config\github-copilot` exists |
| Gemini CLI | `%USERPROFILE%\.gemini\oauth_creds.json` exists, or `gemini` is on PATH (the bare `.gemini` folder is also created by Antigravity, so it is not a signal) |
| Grok Build | `%USERPROFILE%\.grok` exists, or `grok` is on PATH |
| Antigravity | `%USERPROFILE%\.antigravity` exists, or `agy` is on PATH |

A detected, not-dismissed tool is a **suggestion**; suggestions never become usage rows. The
footer shows dim text "+N not supported yet" when N > 0, and the tray menu gets a "Detected, no
usage support yet" submenu: per tool, **Request support…** (opens
`https://github.com/<update.repo>/issues/new?title=Add+provider:+<name>`; disabled when
`update.repo` is empty), **Dismiss** (adds the id to `dismissedSuggestions`), and a disabled help
line where one exists: Cursor and GitHub Copilot → "Experimental: enable under Subscriptions";
Antigravity → "Install the agy CLI and sign in to see usage". The first time a tool is detected,
one balloon — ids already announced are kept in `announcedSuggestions`. The Antigravity
suggestion is suppressed while the agy CLI is detected (the provider covers it).

## 7. Refresh

- A WinForms timer fires every `refreshMinutes` (default 5). The shown set is recomputed at the
  start of every refresh (detection may have changed it) and handed to the service.
- Manual refresh (header button, menu, the widget) is ignored while a refresh is in flight or
  when one started less than 30 s ago; a throttled manual refresh says so with a short
  "just refreshed" header note instead of doing nothing.
- Shown providers fetch in parallel, each with a 15 s timeout plus a 5 s grace — a provider that
  ignores its cancellation token is abandoned a few seconds after the deadline and its late
  failure is observed. Each provider's result is applied the moment it completes, on the UI
  thread, so one slow provider never delays the rows of the fast ones.
- Success (or `NotConfigured`) → the snapshot replaces the cached one and `cache.json` is
  rewritten (temp file, then replace). `FetchedAtUtc` comes from the service's clock.
- `Error` or timeout → per-provider backoff: skip scheduled refreshes for 10, then 20, then 30
  minutes (cap), reset on success; last-good data keeps showing as `Stale`. With no previous good
  snapshot the `Error` row is shown but never written to the cache. While the machine is offline
  (`NetworkInterface.GetIsNetworkAvailable`) there is no backoff and the message reads "offline" —
  the failure is the connection, not the provider.
- `AuthRequired` must not wipe last-known usage: with windows to show, the previous snapshot stays
  (as `Stale`, its original fetch time intact) and only the message is swapped for the sign-in
  hint; the row reads "stale 42m · sign in needed: …". With no previous windows, it replaces the
  snapshot (a current fact, no backoff), like `NotConfigured`.
- Login tokens are read from disk on each fetch, kept only in local variables, never logged,
  never cached, never written anywhere; the widget never refreshes or rewrites a CLI's token file.
- Refreshes also run when the PC resumes and when the network comes back: those events schedule
  one delayed refresh about 5 s out, coalesced while one is already pending, marshalled onto the
  UI thread.

## 8. Cache, history, forecast, notifications

- **`cache.json`** — the last-known snapshot per provider, last-good only (`Error` snapshots are
  never persisted). Written atomically; loaded at startup so the widget paints real data before
  the first refresh lands. Timestamps are epoch seconds; windows carry the full section 3 shape.
- **`history.json`** — per-window usage samples keyed `providerId/windowKey`, written atomically.
  A sample is the compact array `[epochSeconds, usedPercent, resetEpochSeconds | null]`; only
  `Ok` snapshots are recorded, percents are rounded to two decimals, and a sample is appended only
  when the percent changed, the reset moved, or more than 30 minutes passed since the previous
  one — so a month of samples stays far below 1 MB. `Prune(historyDays)` drops samples older than
  the setting (default 30 days, clamped 1..365). Load and save never throw for I/O problems; they
  log instead.
- **Pace and forecast** (`Core.Forecast`, pure) — the pace marker is the percent that would be
  used by now at an even rate given the window started at `ResetsAtUtc − DurationSeconds`
  (clamped 0..100; null when either is unknown). The forecast projects the burn from history: a
  least-squares rate over the last 90 minutes when there are at least three samples spanning at
  least 10 minutes, otherwise the even pace since the window start, otherwise nothing. Only the
  current window generation counts (samples carrying the same reset time, or taken at or after the
  window start). When 100 % would be reached before the window resets, the row shows
  "limit in 1h 20m" (`Painter.Duration` of the time remaining); a rate that would need more than a
  century to hit the limit is not a forecast.
- **Notifications** (`Core.Notifier`, pure) — the notifier remembers the last seen percent and
  reset time of every window (`providerId/key`, falling back to the kind), so one `Evaluate` per
  refresh returns exactly the messages to show as tray balloons. A window crossing a threshold
  (previous below, current at or above; several crossed at once announce once, for the highest)
  → "<name> <label>: <n>% used, resets in <duration>". A reset (the reset instant moved forward
  while usage fell by 20 points or more) and `notifications.onReset` on → "<name> <label>:
  reset". The state advances whether or not anything was announced, and while notifications are
  disabled — a reset therefore re-arms the thresholds. Default thresholds 80 and 95.

## 9. Window and drawing

- Borderless, top-most, not in the taskbar, out of Alt+Tab (`WS_EX_TOOLWINDOW`), system drop
  shadow, rounded corners on Windows 11 (DWM; the painter skips its own border there) and a 1 px
  frame on Windows 10. Drag anywhere to move (`WM_NCHITTEST` → `HTCAPTION`); in Detailed layout
  the left and right edges resize the width; no vertical resizing (the height always fits the
  content); no maximise on double-click. Esc hides to the tray. Single instance: a second launch
  broadcasts a registered window message and the running widget shows itself.
- The window is exactly as tall as its rows, capped to the screen; a window in the lower half of
  the screen keeps its bottom edge in place. Compact width tracks the widest title with the right
  edge in place. Position, size, layout and always-on-top persist; a saved position off every
  screen falls back to the bottom-right of the primary work area.
- Header: title, a note ("just refreshed" / "updating…" / "updated N min ago", re-rendered each
  minute), then ⋯ (menu), ⟳ (refresh) and ✕ (hide to tray) buttons. Footer: the "+N not supported
  yet" note and Detailed / Compact chips.
- **Detailed**: one card per tool; a label column fitted to the row's longest label (48–110 px);
  per window a bar, the coloured percent, and "resets 2h 10m" or the `used / limit` detail with
  its unit.
- **Compact**: one line per tool; columns are the kinds some shown row has a *primary* window
  for, in kind order, each an 82 px cell with a slim bar and percent, "—" where a row has no
  window of that kind; message rows span the columns.
- Windows sort primary-first, then by kind (five hours, day, week, month, other), regardless of
  vendor field order.
- Row states: Ok (windows, or its message when it has none); Stale (bars and text dimmed, a
  yellow "stale 42m · message" tag after the title); AuthRequired (yellow message, e.g. "sign in
  needed: run codex login"); NotConfigured (dim title and message); Error (dim message).
- **Colour rule**: below `warnPercent` green, `warnPercent`..`criticalPercent` inclusive yellow,
  above red (defaults 50 / 70). **Themes**: `Theme.Dark` and `Theme.Light` palettes with roles
  (background, card, line, text, dim text, green, yellow, red, accent, chip glyph); `theme:
  "system"` follows the Windows app-light setting (`AppsUseLightTheme`) and flips live.
  Idle opacity is `opacity` (40..100 %); fully opaque while the pointer is over the widget.
- **Per-monitor DPI** (PerMonitorV2 manifest): on `WM_DPICHANGED` the painter is rebuilt at the
  new scale and the suggested bounds applied (re-entrancy guarded). All layout maths scales by
  the DPI factor.
- **Hit-testing shares its geometry with drawing** (`MenuRect`, card and line boxes, compact
  cells), so hover, tooltips and clicks always agree with what is on screen. Hover fills on
  buttons, chips and cards; a hand cursor on the clickable ones.
- **Tooltips** (7 s, re-shown only when the element under the pointer changes): header buttons
  ("Menu", "Refresh now", "Hide to tray"); a row title (provider, plan, data age, full message);
  a usage window (label, one-decimal percent, used/limit with unit, absolute reset time in local
  time).
- **Menu**: one `ContextMenuStrip` instance, shown by the tray icon, by right-click anywhere on
  the widget, and by the ⋯ button. Refresh now · Layout ▸ Detailed/Compact · Subscriptions (▸
  every provider, Auto-detect) · Detected, no usage support yet · Always on top · Start with
  Windows (HKCU `…\CurrentVersion\Run`, value `AiUsageWidget`) · Settings… · Check for updates ·
  Install update vX.Y.Z (when one is verified and waiting) · Set update token… · version line ·
  Exit. Rebuilt wholesale on state changes.
- **Tray**: left-click toggles the widget (hide/show), double-click shows it; the one-time
  "still running in the tray" balloon is kept in `trayHintShown`. The tray text line carries the
  version and the updater status (capped at 63 characters, Windows' limit). The tray icon fills
  and changes colour with the worst window, and the tray tooltip lists the worst windows.

## 10. Settings (`settings.json`)

Missing or malformed file → defaults. Unknown keys are preserved on save. The file is written
atomically (temp file, then replace) as indented JSON, since hand-editing is how most settings are
changed. Values are clamped on load and on save.

| Key | Default | Meaning |
|---|---|---|
| `refreshMinutes` | `5` | minutes between refreshes; clamped 1–120 |
| `layout` | `"detailed"` | `"detailed"` or `"compact"` |
| `alwaysOnTop` | `true` | keep the widget above other windows |
| `bounds` | `0,0,340,560` | `{ "x", "y", "width", "height" }`; first run goes bottom-right (no saved bounds), an off-screen position falls back there too |
| `providers` | `[]` | explicit `{ "id", "enabled" }` entries; they control order as well as show/hide; unlisted providers follow the detection rule |
| `autoDetect` | `true` | show a provider when its tool is detected on this PC |
| `theme` | `"system"` | `"system"`, `"dark"` or `"light"` |
| `opacity` | `100` | idle window opacity, clamped 40–100 |
| `warnPercent` | `50` | bars turn yellow from here; 1–98 |
| `criticalPercent` | `70` | and red above this; strictly above `warnPercent`, at most 99 |
| `notifications.enabled` | `true` | show usage notifications |
| `notifications.thresholds` | `[80, 95]` | percent-used thresholds; each 1–100, sorted, de-duplicated |
| `notifications.onReset` | `false` | also notify when a limit resets |
| `historyDays` | `30` | how long usage samples are kept; clamped 1–365 |
| `trayHintShown` | `false` | the one-time "still running in the tray" balloon was shown |
| `consentDone` | `false` | the first-run "choose what to show" window was answered |
| `dismissedSuggestions` | `[]` | tools dismissed in the tray menu |
| `announcedSuggestions` | `[]` | suggestion balloons already shown |
| `knownProviders` | `[]` | providers whose appearance was already announced |
| `update.mode` | `"notify"` | `auto` (install when found), `notify` (ask first), `off`; unknown values fall back to `notify` |
| `update.repo` | `"SalahSaad93/ai-usage-widget"` | GitHub Releases `owner/name` to check; empty disables the updater |
| `update.requiresToken` | `false` | send the stored token with update checks (private repository) |

Provider ids: `claude-code`, `codex`, `zai`, `cursor`, `github-copilot`, `openrouter`,
`antigravity`, `opencode-go`, `muse`.

## 11. Secrets

- **`SecretStore`** — named secrets the user gives the widget itself: DPAPI
  (`DataProtectionScope.CurrentUser`), one `secret-<name>.bin` per secret. Today: the OpenRouter
  API key entered in the settings window, stored under the name `openrouter`. The key is never
  displayed back (the field shows a placeholder), only a freshly typed value is stored, and the
  "Clear key" button deletes the file.
- **`TokenStore`** — the optional GitHub update token, DPAPI-protected in `update-token.bin`,
  entered through the menu's "Set update token…". Required when `update.requiresToken` is set;
  until then update checks are skipped with an "updates: token needed" tray note.
- Neither ever lands in `settings.json`, `log.txt`, or a user-facing message.

## 12. Logging and crash handling

- `log.txt` is plain UTF-8, one line per entry (`2026-11-14T15:04:05Z LEVEL message`), rotated to
  `log.old.txt` at 256 KB. Callers pass plain messages and exceptions only: no token, key,
  Authorization header or credentials-file content ever reaches the log. Nothing in the logger
  throws; a failed write is dropped. Embedded newlines are flattened so the file stays
  line-oriented.
- `Program` installs three handlers: UI-thread exceptions are logged and the widget keeps running
  (`CatchException` mode — no stock crash dialog); `AppDomain.UnhandledException` is logged
  (including the terminating case); unobserved task exceptions are logged and marked observed.
  A refresh or update check that throws is additionally caught at its call site and logged.

## 13. Auto-update

Source: GitHub Releases of `update.repo`. Disabled when `repo` is empty or `mode` is `off`, and
on an unsigned build (an empty `UpdateKey.PublicKeyXml` — checks report "updates disabled
(unsigned build)"). Checks run at startup (after the first paint), every 24 h, and from the menu.

- **Check, public repo** (`requiresToken` false, the default): no API call. The widget fetches
  `https://github.com/<repo>/releases/latest/download/latest.json` and `latest.json.sig`,
  verifies the signature, reads the version from the signed manifest and, when it is strictly
  newer, fetches `releases/download/v<version>/AiUsageWidget.exe`. GitHub's API allows only 60
  anonymous requests per hour per IP address, which a shared address exhausts; the download
  links have no such limit.
- **Check, private repo** (`requiresToken` true): `GET https://api.github.com/repos/<repo>/releases/latest`
  (`Accept: application/vnd.github+json`, `Authorization: Bearer <token>`; without a stored
  token: "updates: token needed"). The leading `v` of `tag_name` is stripped and compared with
  the running assembly version as a `System.Version`; only strictly newer proceeds.
- Every status the updater reports is also written to `log.txt`.
- **Download**: the `latest.json`, `latest.json.sig` and `AiUsageWidget.exe` release assets into
  `%LOCALAPPDATA%\AiUsageWidget\update\`. Private-repo assets are fetched from the asset's API
  `url` (`Accept: application/octet-stream`). Only `https` URLs on `api.github.com`,
  `github.com` or `*.githubusercontent.com` are followed, and the token is attached only to
  `api.github.com`.
- **Verify — signature first, then hash**: the RSA-SHA256 signature over the `latest.json` bytes
  must verify against the public key compiled into the exe (base64, strict charset). A bad
  signature deletes all three files and reports "update failed: bad signature". Then `latest.json`
  must name the same version and asset, and the SHA-256 of the downloaded exe must match
  (case-insensitive); a mismatch deletes the download and reports "update failed". **Nothing
  unverified is ever executed.**
- **Install from verified bytes**: the install re-reads the exe from disk and re-verifies it
  against the hash remembered at download time — the verified file may sit in the update folder a
  long time before the user clicks install — then swaps in exactly the bytes that were just
  verified; nothing can be swapped in between the check and the install. On mismatch: delete,
  clear the pending state, "update failed".
- **Swap and rollback**: rename the running exe to `AiUsageWidget.old.exe`, write the verified
  bytes to the running path, start the new exe with `--updated <oldPid>`, exit. Any failure rolls
  the rename back and the old widget keeps running. The new process waits up to 20 s for the old
  pid, then deletes the `.old` file.
- **Modes**: `auto` installs when found; `notify` (the default) shows a balloon "v<version> is
  available" and a menu item "Install update vX.Y.Z"; `off` disables checks.
- **Signing**: `tools/new-update-key.ps1` generates a 3072-bit RSA pair — the private XML goes to
  `%USERPROFILE%\.ai-usage-widget\update-signing-key.xml` (never committed; back it up), the
  public half is written into `src/Core/UpdateKey.cs`. `release.ps1 -Version x.y.z` refuses a
  dirty git tree, builds and self-tests, hashes the exe, writes `latest.json`
  `{ "version", "sha256", "asset" }`, signs it with the private key, cross-checks that signature
  against the public key compiled into the exe, and publishes exe + manifest + signature with
  `gh release create`. Releasing without the signing key is not possible, and losing it means
  installed widgets cannot accept further updates.

## 14. Self-tests and the build

`AiUsageWidget.Tests.exe [--out <file>]` (a separate exe that references the widget, so no test code ships; `--screenshots <dir>` renders every window with sample data) runs headless with `AI_USAGE_WIDGET_DATA`
pointed at a fresh temp folder and makes no network calls (providers are pointed at temp files,
fake runners keep real processes out). It prints one `PASS <name>` or `FAIL <name>: <reason>` line
per check and exits non-zero on any failure; the exe is a windowed executable, hence the optional
log file. Around sixty checks cover: provider fixture mapping (Codex, Claude Code incl. plan
names and extra windows, Z.ai, Muse, Antigravity, OpenRouter, Cursor, Copilot), window kinds by
length, colour thresholds for both the built-in rule and the settings-driven one, cache and
history round-trips and pruning, refresh transitions (stale, backoff, offline, auth-required
keeps last good, per-provider apply), the shown/hidden rule incl. experimental providers,
detection helpers, settings defaults/clamping/round-trips, consent and settings value round-trips,
provider-info coverage, history dedup and file size, pace and forecast, notifier thresholds and
resets, updater version comparison, hash and signature verification, exe swap and rollback,
install re-verification, unsigned-build behaviour, token store, log rotation, error text, and
hit-testing both layouts.

`build.ps1 -Version x.y.z` compiles every file under `src/` with the in-box compiler
(`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, falling back to the 32-bit one) with
`/langversion:5 /warnaserror+ /optimize+`, the app manifest and the icon (embedded both as the
exe icon and as a named resource for the tray), a generated `AssemblyInfo.cs` carrying the
version, and the seven framework references — then runs the self-test and fails on any non-PASS
line. `-OutDir` builds elsewhere while an installed widget runs from `bin\`. CI
(`.github/workflows/build.yml`) runs the same script on `windows-latest` for every push and pull
request and uploads the exe and `selftest.log`.

## 15. Adding a provider

1. Add `src/Providers/<Name>Provider.cs` implementing `IUsageProvider`: a pure static `Parse`
   plus `FetchAsync` that reads the login, makes the one request, and returns
   `NotConfigured`/`AuthRequired`/`Error` snapshots instead of throwing. Give it an
   existence-only `IsDetected()` and constructor overrides (file paths, runner, clock) so the
   self-test can drive it without touching real files or processes.
2. Add one line to `ProviderRegistry.All` — the array order is the row order.
3. Add a `ProviderInfoEntry` for the id: what is read locally, who is contacted, and whether the
   path is official. The self-test asserts every registered id is covered.
4. Save a redacted real response under `tests/fixtures/` (hand-written shapes get a
   `-synthetic` name) and add self-test checks for the mapping and the state transitions.
5. If the endpoint is unofficial or the command has never been observed, add the id to
   `ProviderRegistry.ExperimentalIds` so it stays off until explicitly enabled.
6. Follow the token rules in section 4: read, use, forget; never write another tool's files.
