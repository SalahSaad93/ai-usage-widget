# Contributing

Bug reports and provider requests: use the issue templates. Code changes: keep one task per pull
request, and include self-test coverage for any new behaviour.

## Building and running the self-tests

You need Windows and nothing else — no .NET SDK, no NuGet. From the repository root:

    powershell -ExecutionPolicy Bypass -File build.ps1 -Version 0.0.1

This compiles with the in-box C# compiler, writes `bin\AiUsageWidget.exe`, then runs the headless
self-test. Success prints `build OK: N selftest checks passed`; any failure is listed in
`bin\selftest.log` and fails the build. To run the self-tests alone:

    bin\AiUsageWidget.exe --selftest --selftest-out selftest.log

(The exe is a windowed executable with no console output, hence the log file. A non-zero exit code
means at least one check failed.) While an installed widget is running from `bin\`, build into
another folder with `-OutDir bin\dev`.

## The C# 5 constraint

The build compiles with the compiler that ships in Windows
(`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, C# 5) and passes
`/langversion:5 /warnaserror+`, so any warning fails the build. That is what lets anyone on
Windows build the project with no SDK or package restore. In practice this means no string
interpolation (`$"..."`), no `?.`, no `nameof`, no expression-bodied members, no auto-property
initialisers, no `out var`, no pattern matching, no tuples, and no `await` in catch/finally.

## Provider rules

1. **Pure `Parse`.** Split each provider into `Parse(string json)` — no I/O, no clock, directly
   testable — and `FetchAsync`, which does the reading and the network call and never throws for
   expected conditions (it returns `NotConfigured`, `AuthRequired` or `Error` snapshots instead).
2. **A redacted fixture.** Save a real response with every identifier redacted under
   `tests/fixtures/` and cover the mapping with self-test checks.
3. **Read, use, forget.** Tokens and keys are read from disk on each fetch, kept in local
   variables for the one request, and never logged, cached or written anywhere.
4. **Never write another tool's files.** The widget never refreshes or rewrites a CLI's token or
   auth file; an expired login is reported and the user re-logins with the tool itself. When a
   vendor offers an official CLI command for usage (Muse, Antigravity), run that CLI instead of
   touching its credentials at all.

New providers also register in `ProviderRegistry.All` (the array order is the row order). An
experimental provider — one whose endpoint is unofficial or whose mapping is not yet verified
against live output — also goes in `ProviderRegistry.ExperimentalIds`, which keeps it hidden
until the user explicitly enables it. The full recipe is section 15 of `docs/DESIGN.md`; add new
self-test checks at the end of `SelfTest.cs`.

## Releases

A release is cut with `release.ps1`, which signs the update manifest (`latest.json`). The machine
cutting the release therefore needs the private signing key; `tools\new-update-key.ps1` creates
a key pair and saves the private half to
`%USERPROFILE%\.ai-usage-widget\update-signing-key.xml`. The public half is compiled into the exe
(`UpdateKey.PublicKeyXml`); a build without it disables update checks. Details are section 13 of
`docs/DESIGN.md`.

## One task per pull request

One pull request fixes one problem or adds one provider. Do not reformat or tidy code that the
task does not touch — the diff should tell the whole story by itself.
