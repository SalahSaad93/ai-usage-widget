# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.4.1] - 2026-11-14

### Added

- "Send test notification" in Settings, and `AiUsageWidget.exe --test-notification`, to check
  that Windows lets the tray notification through (Do Not Disturb hides it).
- The Gemini CLI suggestion now says why there is no provider: its terms do not allow other apps
  to use its sign-in.

### Fixed

- Starting the widget a second time now really shows the running one. The request was sent as a
  broadcast window message, which never reaches the widget's window; it is a named event now.
- The first-run window no longer clips long descriptions.
- Update checks no longer fail on shared internet addresses. They used GitHub's API, which allows
  60 anonymous requests per hour per address; public-repo updates now use the plain release
  download links, and the version is read from the signed manifest.
- Every updater status is written to `log.txt`, so a failed check is no longer silent.

### Changed

- The tests are a separate `AiUsageWidget.Tests.exe`; no test code ships in the widget. Project
  files for IDEs and `dotnet build` were added.

## [0.4.0] - 2026-11-14

### Added

- OpenRouter, Cursor and GitHub Copilot providers. OpenRouter uses its documented key endpoint.
  Cursor and Copilot read the local sign-in and call unofficial endpoints, so both are
  experimental and shown only when explicitly enabled under Subscriptions.
- Generalised usage windows: a window carries a key, label, duration, unit and a primary flag,
  and one row can list several windows. Claude Code reports the Opus and Sonnet weekly limits and
  extra usage, Codex its code-review limits, Z.ai labels its web-tools quota. Detailed lists every
  window with used/limit numbers; Compact builds its columns from the primary windows.
- A settings window, opened with **Settings…** in the widget or tray menu: refresh, layout,
  theme, opacity, colour thresholds, subscriptions, an OpenRouter key, notifications and updates.
- A first-run window: you choose which subscriptions to follow, and nothing is fetched until you
  continue.
- Local usage history (`history.json`), kept for `historyDays` days (default 30). It feeds a pace
  marker on every bar and a forecast — "limit in 1h 20m" — shown when a window reaches its limit
  before it resets.
- Threshold notifications: the tray announces when a window crosses 80 % or 95 % (configurable)
  and when a window resets.
- The tray icon fills and changes colour with the worst window, and its tooltip lists the worst
  windows.
- Light and dark themes that can follow Windows, tooltips with exact numbers and reset times, a
  menu on right-click or the new header button, and rounded corners and a shadow on Windows 11.
- A DPAPI-backed secret store: a key you enter in settings (currently the OpenRouter key) is
  stored encrypted in `secret-*.bin`.
- Continuous integration: every push and pull request builds the exe and runs the self-tests on
  Windows.
- Issue templates for bug reports and provider requests.

### Changed

- Settings gained keys for theme, colour thresholds, opacity, notification thresholds, history
  retention and secrets.
- The build is AnyCPU, so the exe also runs natively on ARM64.
- A single click on the tray icon toggles the widget, and the first hide explains the tray.
- New installs no longer need a GitHub token.

### Fixed

- A stalled response body can no longer hang a refresh, and a provider that ignores its deadline
  is abandoned after a short grace period.
- A CLI run cancelled while draining its pipes reports the cancellation instead of a bogus
  result.

### Security

- Updates now require a signed manifest: `latest.json` must carry an RSA signature made with the
  key whose public half is compiled into the exe, and the signature is checked before the version
  and hash. A build without a key disables update checks entirely.
- The updater installs the exact bytes it just verified instead of re-reading the file, and the
  hash is re-verified right before the swap.
- `release.ps1` signs and verifies the manifest; `tools/new-update-key.ps1` creates the signing
  key pair.

## [0.3.2] - 2026-11-14

### Added

- A size-capped log file (`log.txt`, rotating into `log.old.txt`) and global exception handlers
  replace the stock crash dialog.
- A second launch asks the running widget to show its window.
- Refreshes run when the PC resumes and when the network comes back.

### Changed

- Settings are saved atomically as indented JSON; duplicate ids in the settings lists are removed.
- The updater guards against concurrent checks, re-verifies the download hash right before
  installing, and defaults to notify mode.
- Antigravity stays experimental: shown only when explicitly enabled in settings, and it reports
  sign-in only on explicit phrases in the CLI output.
- `build.ps1 -OutDir` builds into another folder so a running installed widget does not lock the
  output.

### Fixed

- Footer layout chips are clickable, and hiding a detected provider takes one click.
- The widget follows per-monitor DPI changes, stays out of Alt+Tab and does not take focus when it
  appears.
- Height is always automatic; width is chosen by the user in Detailed layout only.
- Each provider's result is applied as soon as it arrives, so one slow provider no longer delays
  the others.
- CLI providers honour the refresh timeout.
- Being offline no longer triggers backoff; the row says "offline" instead of showing a network
  error.
- An expired login keeps the last-known usage visible as stale instead of blanking the row.
- Network errors are shown as plain text.
- Tool detection reads the current PATH, so a CLI installed after launch is found; a plain failure
  clears an earlier sign-in note on a stale row.

## [0.3.1] - 2026-11-12

### Added

- Antigravity provider: usage is read only by running the official `agy` CLI; no Antigravity or
  Google credential files or endpoints are touched.

### Changed

- The Muse and Antigravity providers share one hidden-process runner.

### Fixed

- Gemini CLI is no longer suggested just because Antigravity created a `.gemini` folder; the
  Antigravity suggestion explains that the `agy` CLI is needed.

## [0.3.0] - 2026-11-11

### Added

- Auto-detection: the widget shows the subscriptions it finds on this PC and hides the rest; a
  provider appearing or disappearing is announced with one balloon.
- A Subscriptions tray submenu to show or hide each provider and toggle auto-detection.
- Suggestions for detected AI tools the widget cannot report on yet: a "+N not supported yet"
  footer note, a tray submenu with "Request support…" and "Dismiss" per tool, and one balloon per
  new tool.

## [0.2.2] - 2026-11-11

### Fixed

- Row order and the Compact layout.

## [0.2.1] - 2026-11-11

### Changed

- The layout was matched to the approved prototype.

## [0.2.0] - 2026-11-11

### Added

- Claude Code, Z.ai GLM and Muse providers.
- Application icon.

## [0.1.0] - 2026-11-11

### Added

- First release: the always-on-top widget with Detailed and Compact layouts, tray icon and menu,
  the Codex provider, last-good caching with per-provider backoff, the settings file, GitHub
  Releases auto-update, and the headless self-test run by the build.
