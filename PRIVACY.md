# Privacy

AI Usage Widget runs on your PC. There are no accounts, no telemetry, no analytics and no crash
reporting. The only network traffic is usage requests to the vendor of each subscription you
chose to follow, plus update checks against GitHub.

## The first-run choice

On first launch the widget shows a one-time window where you pick which subscriptions to follow.
Nothing is fetched until you make that choice and continue. Afterwards a provider is contacted
only while it is enabled in settings. The experimental providers (Cursor, GitHub Copilot,
Antigravity) stay off until you explicitly enable them.

## What leaves your PC

Each provider reads a login or key that the matching tool already stored on your PC, then calls
that vendor's usage endpoint:

| Provider | Host contacted | What is sent |
|---|---|---|
| Claude Code | `api.anthropic.com` | a usage request with your Claude Code OAuth token in the `Authorization` header |
| Codex | `chatgpt.com` | a usage request with your Codex access token (and account id, when present) in the headers |
| Z.ai GLM | `api.z.ai` | a quota request with your Z.ai API key in the `Authorization` header |
| Cursor | `api2.cursor.sh` | a usage request with your Cursor access token in the `Authorization` header |
| GitHub Copilot | `api.github.com` | a quota request with your Copilot OAuth token in the `Authorization` header |
| OpenRouter | `openrouter.ai` | a key-usage request with your OpenRouter API key in the `Authorization` header |
| Updates | `api.github.com`, `github.com`, `*.githubusercontent.com` | release checks and update downloads; a GitHub token, if you set one, is sent only to `api.github.com` |

`api.github.com` appears twice: the update check uses it, and so does the GitHub Copilot provider
if you enable it. The OpenRouter key can come from the `OPENROUTER_API_KEY` environment variable
or from a key you enter in settings; either way it is sent only to `openrouter.ai`.

Muse and Antigravity are different: they run the vendor's own CLI (`muse usage --json`,
`agy -p /usage --output-format json`) under your existing login and read the output. They never
read, copy or transmit those tools' credentials, and no Muse or Google endpoint is contacted.

## Tokens and keys

- A token or key is read from disk at each refresh, held in memory only for the duration of that
  one request, and then dropped.
- It is never written anywhere, never logged, never cached, and never sent to any host other than
  the vendor in the table above.
- The widget never refreshes or rewrites another tool's token or auth file. When a login expires,
  the row says so and you re-login with that tool yourself. The Cursor provider goes further: it
  reads only the access token from Cursor's own database and never touches the refresh token
  stored next to it, so it cannot refresh or invalidate your Cursor login.
- A key you enter in settings (currently only the OpenRouter key) is stored in `secret-*.bin`,
  encrypted with Windows DPAPI for your user account. On each refresh it is decrypted, used for
  the one request, and dropped.
- The optional GitHub update token is stored in `update-token.bin`, encrypted with Windows DPAPI
  for your user account. It is never placed in `settings.json`, logs, or messages.

## What is stored locally, and where

Everything lives in `%LOCALAPPDATA%\AiUsageWidget\` (override with the `AI_USAGE_WIDGET_DATA`
environment variable):

- `settings.json` — your preferences: refresh interval, layout, window bounds, which providers
  are shown, update settings.
- `cache.json` — the last known-good usage per provider: percentages, plan names and reset times.
- `history.json` — recent usage per provider: percentage and reset time samples, kept for
  `historyDays` days (default 30) and used for the pace marker and forecast. It holds no
  credentials, ever.
- `log.txt` (rotated to `log.old.txt` at 256 KB) — a plain text log. Tokens, keys and credentials
  file contents never reach it.
- `secret-*.bin` — DPAPI-protected secrets you entered in settings (currently only the OpenRouter
  key), named after the provider.
- `update-token.bin` — the optional DPAPI-protected GitHub token (only if you set one).
- `update\` — an update downloaded from GitHub Releases, kept until installed and then swapped
  into place next to the exe (the replaced exe remains briefly as `AiUsageWidget.old.exe`).

## Unofficial endpoints

The Claude Code, Codex, Cursor and GitHub Copilot usage endpoints are undocumented. They may
change, stop working, or be disallowed by the vendor at any time. The Z.ai, OpenRouter and update
endpoints are official and documented. This project is not affiliated with Anthropic, OpenAI,
Z.ai, Cursor, GitHub, Google, Sourcegraph (Muse), OpenRouter or any other vendor.

## Removing everything

1. Quit the widget: tray menu, **Exit** (if **Start with Windows** was on, uncheck it first — it
   wrote a value named `AiUsageWidget` under
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).
2. Delete the exe (and `AiUsageWidget.old.exe` next to it, if present).
3. Delete the `%LOCALAPPDATA%\AiUsageWidget\` folder. That removes the settings, the cache, the
   usage history, the secrets, the logs, the update token and any downloaded update.

Nothing is stored or left anywhere else.
