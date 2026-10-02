# Dashio

A Windows 11 app that groups everything each app starts by itself (services, scheduled tasks,
startup entries) and lets the user switch those items off and on. See `README.md` for the
user-facing description.

## Commands

```powershell
dotnet build Dashio.slnx
dotnet run --project src\Dashio.App
dotnet test tests\Dashio.Core.Tests --filter "Category!=Live&Category!=Integration&Category!=Report"
dotnet test tests\Dashio.Core.Tests --filter "Category=Live"          # read-only, real machine
dotnet test tests\Dashio.Core.Tests --filter "Category=Integration"   # user-level throwaway entries
pwsh tools\ui-tests.ps1                                               # drives a background copy of the real window
pwsh tools\make-icon.ps1                                              # regenerates Assets\AppIcon.ico
pwsh tools\publish.ps1                                                # release folder and installer in artifacts\
pwsh tools\ui-tests.ps1 -Exe artifacts\Dashio-0.1.0-x64\Dashio.exe    # UI tests against the release folder
```

Close a running `Dashio.exe` before building; it locks the output.

The solution build writes the app to `bin\x64\Debug`, but `dotnet run` and `tools\ui-tests.ps1`
use `bin\Debug`. Run `dotnet build src\Dashio.App` before the UI tests, or they drive a stale copy.

The release bundles .NET and the Windows App SDK, and installs to Program Files (`installer\Dashio.iss`,
needs Inno Setup 6). It is not trimmed: `Dashio.Core` uses reflection-based JSON, `dynamic` COM calls
and built-in COM interop, and the helper shares the app's runtime files. Sort those out before trimming.

## Rules that are easy to break

- **The app is unpackaged** (`WindowsPackageType=None`). A packaged app's `HKCU` writes go to a
  private hive, so a "disabled" startup entry would stay enabled. Do not add packaging.
- **The window never runs elevated.** Admin work goes through `Dashio.Helper`, started once per
  batch behind a UAC prompt. An elevated window would also block `winapp ui`.
- **Disable and enable only.** Nothing is deleted. Every change goes through `ChangeCoordinator`,
  which re-reads the item from Windows before and after and writes the journal.
- **The helper trusts only item ids and target states.** It looks each item up again and refuses
  Windows components itself. Never make it run a command or path taken from the request.
- **Windows components are decided by a trusted signature** (`ProtectionPolicy`), judged on the
  file that actually runs, not on a host such as `cmd.exe` or `svchost.exe`.
- **No network access and no telemetry**, anywhere.
- **Tests must clean up** anything they create on the machine, and fixtures must not contain a
  real user name or a personal list of installed apps.

## Structure

- `Dashio.Core/Collectors` read one source each and never throw for a single bad entry.
- `Dashio.Core/Attribution/AttributionEngine` is pure (no I/O). New matching behaviour gets a
  test in `AttributionEngineTests` built from hand-written items, plus a look at the live report
  (`--filter "FullyQualifiedName~LiveReport.Groups"`). A wrong merge is worse than a missed one.
- `Dashio.App` uses `x:Bind` with explicit modes, `CommunityToolkit.Mvvm` partial properties,
  and code-behind only for navigation, dialogs and event wiring.

## Code style

- Never use an em dash in strings or comments. Use a comma, a colon, brackets or two sentences.
- Keep comments minimal: only where the code cannot say why it does something. No comment that
  repeats what the code already shows.

## UI rules

- Built-in WinUI controls and Windows Community Toolkit settings controls first. No custom
  control where a stock one fits.
- Theme resources only: no hard-coded colours. Light, dark and high contrast must all work.
- One card level; no card inside a card. Each page has one scrolling region.
- Spacing on the 4 px grid. Pages are centred and at most 1480 wide, set by `Ui.FitToPage` (a
  `MaxWidth` alone lets the width follow the content, so pages jump when something expands). Wide pages
  add a 344 side column of titled panels, in the style of the Windows Settings home page.
- Size layouts from the page's own width in a `SizeChanged` handler. `AdaptiveTrigger` compares
  against physical pixels here, so it fires at the wrong widths on scaled displays.
- Status colours (green, yellow, red) always come with an icon and words. "On" is not good news,
  so it uses the accent colour rather than green.
- Every interactive element has an accessible name; fixed controls also get an `AutomationId`.
- After changing UI, run the app and look at it. A process that started is not proof that the
  window is right.

## Working agreements

- Commits are GPG-signed and need the owner's passphrase. Do not commit; the owner tests first
  and commits. Never bypass signing.
- `docs/superpowers/` (specs and plans) is local only and gitignored.
