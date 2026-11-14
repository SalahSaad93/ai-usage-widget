# AI Usage Widget

An always-on-top Windows widget that shows how much of your AI subscription allowances you have
used. Each tool gets a row with its 5-hour, weekly or monthly window, a colour-coded bar and the
reset time, refreshed every few minutes or on demand. The widget reads the login file a tool
already keeps on your PC — or runs the vendor's own CLI — calls that vendor's usage endpoint, and
draws the result in one small borderless window. It is WinForms on .NET Framework 4.8, fully
custom-drawn, a single exe with no installer and nothing else to install.

<!-- Screenshot placeholder: ![AI Usage Widget](docs/screenshot.png) — to be added -->

## Supported subscriptions

| Provider | What is shown | Where the login is read from | Status |
|---|---|---|---|
| Claude Code | 5-hour and weekly usage | `%USERPROFILE%\.claude\.credentials.json` | unofficial endpoint |
| Codex | ChatGPT plan windows (5-hour / weekly / monthly, as the plan reports) | `%USERPROFILE%\.codex\auth.json`, or `%CODEX_HOME%\auth.json` when that variable is set | unofficial endpoint |
| Z.ai GLM (coding plan) | 5-hour, weekly and monthly web-tool quotas | `zai-coding-plan` key in the OpenCode `auth.json` (`%USERPROFILE%\.local\share\opencode\auth.json`), else `ZAI_API_KEY`, else `provider.zai.options.apiKey` in `%USERPROFILE%\.zcode\cli\config.json` | your own Z.ai API key |
| Muse | 5-hour and weekly usage | none — runs the official `muse usage --json` | experimental |
| Antigravity | 5-hour and weekly usage (highest bucket per window kind) | none — runs the official `agy` CLI | experimental, off by default |
| OpenCode Go | nothing yet | presence in the OpenCode `auth.json` is detected | the vendor has no usage API |

Rows appear automatically for the tools the widget detects on the PC; you can pin or hide each
provider from the tray menu. The Claude Code and Codex endpoints are undocumented and may stop
working or be disallowed by the vendor — see [PRIVACY.md](PRIVACY.md).

## Install

1. Download `AiUsageWidget.exe` from the
   [Releases](https://github.com/SalahSaad93/ai-usage-widget/releases) page.
2. Put it in a folder your user can write to (the auto-updater replaces the exe in place).
3. Run it. The widget appears near the bottom-right of the screen and puts an icon in the tray.

Requirements: Windows 10 (1903 or newer) or Windows 11. There is no runtime to install — the exe
uses the .NET Framework 4.8 that ships in Windows.

Windows SmartScreen may warn on first run ("Windows protected your PC") because the exe is not
code-signed. Choose "More info", then "Run anyway".

## Using it

- Drag anywhere on the widget to move it. In Detailed layout the left and right edges resize the
  width; the height always fits the content.
- The header has a refresh button. The × button (and the Escape key) hide the widget to the tray —
  they do not quit. Double-click the tray icon to bring it back; quitting is **Exit** in the tray
  menu.
- Refreshes run every 5 minutes, when the PC wakes up, when the network comes back, and on demand.
- Colours: green below 50 %, yellow from 50 to 70 %, red above 70 %. When the last fetch for a
  provider failed, its row stays visible with the last-known bars, dimmed, and a "stale" tag.
- A second launch just brings the running widget to the front.

The tray menu:

- **Refresh now**
- **Layout** — *Detailed* (default: one card per tool, a bar per window with the percent and
  "resets 2h 10m") or *Compact* (one line per tool, three bar columns). The chips in the footer
  switch layouts too.
- **Subscriptions** — every provider with its state (shown and detected, not detected, or hidden);
  click an entry to show or hide it. **Auto-detect** toggles automatic detection.
- **Detected, no usage support yet** — AI tools found on this PC that the widget cannot report on:
  per tool, **Request support…** (opens a pre-filled issue) or **Dismiss**.
- **Always on top**
- **Start with Windows**
- **Check for updates**, **Install update vX.Y.Z** (when a verified update is waiting), and
  **Set update token…**
- the version line, and **Exit**

The mouse wheel scrolls when the content is taller than the window.

## Settings

Settings live in `%LOCALAPPDATA%\AiUsageWidget\settings.json`. A missing or malformed file falls
back to defaults, and unknown keys survive a save. The widget reads:

| Key | Default | Meaning |
|---|---|---|
| `refreshMinutes` | `5` | minutes between refreshes; clamped to 1–120 |
| `layout` | `"detailed"` | `"detailed"` or `"compact"` |
| `alwaysOnTop` | `true` | keep the widget above other windows |
| `bounds` | first run: bottom-right | `{ "x", "y", "width", "height" }`; a position off every screen falls back to the bottom-right |
| `providers` | `[]` | explicit list of `{ "id": "...", "enabled": true }`; it controls order as well as show/hide |
| `autoDetect` | `true` | show a provider when its tool is detected on this PC |
| `dismissedSuggestions` | `[]` | tools you dismissed in the tray menu |
| `announcedSuggestions` | `[]` | suggestion balloons already shown |
| `knownProviders` | `[]` | providers whose appearance was already announced |
| `update.mode` | `"notify"` | `auto` (install when found), `notify` (ask first), `off` |
| `update.repo` | `SalahSaad93/ai-usage-widget` | GitHub Releases `owner/name` to check; empty disables update checks |
| `update.requiresToken` | `true` | send the stored token with update checks (private repository) |

Provider ids: `claude-code`, `codex`, `zai`, `antigravity`, `opencode-go`, `muse`.

Antigravity is experimental and never turns on by auto-detection; the only way to show it is an
explicit entry: `{ "id": "antigravity", "enabled": true }`.

## Updates

At startup and then every 24 hours — plus **Check for updates** in the tray menu — the widget asks
GitHub Releases (`api.github.com`) for the latest release of `update.repo`. A newer version is
downloaded from the release assets (only `https` URLs on `api.github.com`, `github.com` or
`*.githubusercontent.com`), verified against the SHA-256 in the release's `latest.json`, and
re-verified right before the swap; nothing unverified is ever run. In `auto` mode the update
installs when found; in `notify` mode (the default) you get a balloon and an **Install update**
menu item; `off` disables checks. For a private repository, set a fine-grained read-only GitHub
token with **Set update token…** — it is stored DPAPI-encrypted in `update-token.bin`, and until
then update checks are skipped with an "updates: token needed" tray note.

## Building from source

    powershell -ExecutionPolicy Bypass -File build.ps1 -Version x.y.z

This needs only Windows: the build uses the C# compiler that ships with Windows
(`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, falling back to the 32-bit one when the
64-bit file is missing) and pulls no NuGet packages. The exe lands in `bin\AiUsageWidget.exe`,
after which the build runs the headless self-tests; success prints
`build OK: N selftest checks passed`, and any failure is listed in `bin\selftest.log`. Use
`-OutDir bin\dev` to build into another folder while an installed widget is running.

## Adding a provider

A provider is one class in `src/Providers/` implementing `IUsageProvider`. Keep the response
mapping in a pure `Parse(string json)` that the self-test can run against a saved fixture under
`tests/fixtures/`, and do the I/O in `FetchAsync`. Read the login on each fetch, use it for the
one request, then drop it; never refresh or rewrite another tool's files. The full recipe is
section 11 of [docs/DESIGN.md](docs/DESIGN.md).

## Privacy and safety

The widget reads local login files only to call the usage endpoint of the matching vendor. Tokens
are held in memory for the duration of one request and are never logged, written, or sent anywhere
else. There is no telemetry and no analytics. Which hosts are contacted, what is stored on disk,
and how to remove everything: [PRIVACY.md](PRIVACY.md). The Claude Code and Codex endpoints are
undocumented and may stop working or be disallowed by the vendor; this project is not affiliated
with any vendor.

## Licence

[MIT](LICENSE).
