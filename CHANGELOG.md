# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
