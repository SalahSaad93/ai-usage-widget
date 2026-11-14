# Privacy

AI Usage Widget runs on your PC. There are no accounts, no telemetry, no analytics and no crash
reporting. The only network traffic is usage requests to the vendor of each subscription you
already use, plus update checks against GitHub.

## What leaves your PC

Each provider reads a login that the matching tool already stored on your PC, then calls that
vendor's usage endpoint:

| Provider | Host contacted | What is sent |
|---|---|---|
| Claude Code | `api.anthropic.com` | a usage request with your Claude Code OAuth token in the `Authorization` header |
| Codex | `chatgpt.com` | a usage request with your Codex access token (and account id, when present) in the headers |
| Z.ai GLM | `api.z.ai` | a quota request with your Z.ai API key in the `Authorization` header |
| Updates | `api.github.com`, `github.com`, `*.githubusercontent.com` | release checks and update downloads; a GitHub token, if you set one, is sent only to `api.github.com` |

Muse and Antigravity are different: they run the vendor's own CLI (`muse usage --json`,
`agy -p /usage --output-format json`) under your existing login and read the output. They never
read, copy or transmit those tools' credentials, and no Muse or Google endpoint is contacted.

## Tokens and keys

- A token or key is read from disk at each refresh, held in memory only for the duration of that
  one request, and then dropped.
- It is never written anywhere, never logged, never cached, and never sent to any host other than
  the vendor in the table above.
- The widget never refreshes or rewrites another tool's token or auth file. When a login expires,
  the row says so and you re-login with that tool yourself; the widget's second refresher cannot
  invalidate a CLI's own login.
- The optional GitHub update token is stored in `update-token.bin`, encrypted with Windows DPAPI
  for your user account. It is never placed in `settings.json`, logs, or messages.

## What is stored locally, and where

Everything lives in `%LOCALAPPDATA%\AiUsageWidget\` (override with the `AI_USAGE_WIDGET_DATA`
environment variable):

- `settings.json` — your preferences: refresh interval, layout, window bounds, which providers are
  shown, update settings.
- `cache.json` — the last known-good usage per provider: percentages, plan names and reset times.
- `log.txt` (rotated to `log.old.txt` at 256 KB) — a plain text log. Tokens, keys and credentials
  file contents never reach it.
- `update-token.bin` — the optional DPAPI-protected GitHub token (only if you set one).
- `update\` — an update downloaded from GitHub Releases, kept until installed and then swapped
  into place next to the exe (the replaced exe remains briefly as `AiUsageWidget.old.exe`).

## Unofficial endpoints

The Claude Code and Codex usage endpoints are undocumented. They may change, stop working, or be
disallowed by the vendor at any time. This project is not affiliated with Anthropic, OpenAI,
Z.ai, Google, Sourcegraph (Muse) or any other vendor.

## Removing everything

1. Quit the widget: tray menu, **Exit** (if **Start with Windows** was on, uncheck it first — it
   wrote a value named `AiUsageWidget` under
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).
2. Delete the exe (and `AiUsageWidget.old.exe` next to it, if present).
3. Delete the `%LOCALAPPDATA%\AiUsageWidget\` folder. That removes the settings, the cache, the
   logs, the update token and any downloaded update.

Nothing is stored or left anywhere else.
