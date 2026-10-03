# Dashio

A Windows 11 dashboard that shows what each app is using right now and groups everything each app
starts by itself (services, scheduled tasks, startup entries), and lets the user switch those
items off and on. See `README.md` for the
user-facing description.

## Commands

```powershell
dotnet build Dashio.slnx
dotnet run --project src\Dashio.App
dotnet test tests\Dashio.Core.Tests --filter "Category!=Live&Category!=Integration&Category!=Report"
dotnet test tests\Dashio.Core.Tests --filter "Category=Live"          # read-only, real machine
dotnet test tests\Dashio.Core.Tests --filter "Category=Integration"   # user-level throwaway entries
pwsh tools\ui-tests.ps1                                               # drives a background copy of the real window
pwsh tools\make-icon.ps1                                              # builds Assets\AppIcon.ico from the SVGs beside it
pwsh tools\publish.ps1                                                # release folder and installer in artifacts\
pwsh tools\ui-tests.ps1 -Exe artifacts\Dashio-0.1.2-x64\Dashio.exe    # UI tests against the release folder
```

Close a running `Dashio.exe` before building; it locks the output.

For screenshots, `DASHIO_HIDE_APPS` (parts of names, separated by semicolons) leaves those apps out
of every list (`DemoFilter`).

The UI tests must never interrupt whoever is using the PC. Invoking a control through UI
Automation activates an ordinary window, so with `DASHIO_NO_ACTIVATE=1` the window refuses
activation, sits off the screen, and keeps its popups from being drawn (`MainWindow.StayInBackground`).
`tools\FrontWatch.cs` watches every run and fails it if the window came to the front or onto the
screen. Use the same variable for any copy you start to look at the app, and do not use
`winapp ui focus`, `click`, `hover` or `send-keys --via send-input`: they take the real pointer or keyboard.

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
- **Dashio deletes nothing by itself.** Items are switched off and on, never removed. Every change
  goes through `ChangeCoordinator`, which re-reads the item from Windows before and after and
  writes the journal. Ending a running program goes through `EndCoordinator` and is journaled too.
  Uninstalling (`Uninstaller`) starts the uninstaller the app registered with Windows, or asks
  Windows to remove a Store package, exactly as Windows Settings does and only after asking; it
  runs unelevated, is journaled, and cannot be undone. Never delete an app's files directly.
- **Files are deleted only where the user picked them.** The Storage page's Delete goes through
  `FileRemover`, after asking, to the Recycle Bin unless told otherwise. It is journaled, runs
  unelevated and never through the helper, and refuses a drive, Windows and the folders programs
  and profiles live in (`FileRemover.WhyNot`). Nothing else in Dashio deletes a file.
- **An uninstall always ends in the journal.** `UninstallActions` writes it to `PendingUninstalls`
  before starting, follows the uninstaller and what it starts (`UninstallerProcesses`,
  `UninstallFollower`), and records Applied only once Windows no longer lists the app. Removing a
  Store package whose app is running takes Windows half a minute. A `PackageManager` operation
  that nothing holds is collected during such a wait and then never reports back, so keep it and
  the manager alive past the `await` (`GC.KeepAlive`); the package list is asked as well. Check a change here against a
  throwaway package and a throwaway `HKCU` uninstall entry, with the app running and not running.
- **The helper trusts only item ids with target states, process ids with start times, and a
  drive letter.** It looks each item and process up again and refuses Windows components itself,
  and checks the letter against this PC's fixed drives before reading. Never make it run a
  command or open a path taken from the request. Reading a drive only reads.
- **A process is its id and its start time together.** Windows reuses ids, so an id alone must
  never be ended. `ProcessEnder` treats a different start time as "already gone".
- **Windows components are decided by a trusted signature** (`ProtectionPolicy`), judged on the
  file that actually runs, not on a host such as `cmd.exe` or `svchost.exe`.
- **No telemetry, and no network use except one place.** `UpdateChecker` asks GitHub for the list
  of releases, and only when the user presses "Check for updates". It sends nothing about the PC
  and downloads nothing: the release page opens in the browser. Nothing else may use the network,
  and nothing may use it without being asked.
- **Tests must clean up** anything they create on the machine, and fixtures must not contain a
  real user name or a personal list of installed apps.

## Structure

- `Dashio.Core/Collectors` read one source each and never throw for a single bad entry.
- `Dashio.Core/Attribution/AttributionEngine` is pure (no I/O). New matching behaviour gets a
  test in `AttributionEngineTests` built from hand-written items, plus a look at the live report
  (`--filter "FullyQualifiedName~LiveReport.Groups"`). A wrong merge is worse than a missed one.
- `Dashio.Core/Processes` measures what is running. `ProcessSampler` reads every process in one
  call without opening any of them, so it needs no admin rights. `ProcessAttributor` is pure and
  places a process in a group from the scan or in a group of its own; check changes against
  `--filter "FullyQualifiedName~LiveProcessReport"`. It must never change which group an item is in.
- `Dashio.Core/Inventory` lists what is installed. `InventoryBuilder` and `UsageResolver` are pure;
  check changes against `--filter "FullyQualifiedName~LiveInventoryReport"`. Folders and installed
  entries get their app from `ProcessAttributor`, so ids agree across Overview, Apps and Installed.
  A folder nobody can be shown to own stays unclaimed. An app is only called "not opened lately"
  when a record that would have noticed it exists (`UsageResolver.CoversFrom`); never guess.
- `Dashio.Core/Storage` reads a drive's folders (`DiskScanner`, several folders at once, read-only;
  `--filter "FullyQualifiedName~DiskScanSpeedReport"` prints how fast)
  and lays out the map (`Treemap`, pure; `TreemapLayout` in the app glides tiles to new places). A `FolderNode` is read by the UI while the scan fills it
  in: totals only grow and lists are replaced whole. Keep it that way; do not add locks around it.
  Only a finished scan is changed afterwards, by `FolderNode.Forget` when something was deleted.
  `MftReader` builds the same tree from the NTFS file table for the helper (fast, complete, needs
  admin); its tests run against a hand-built image, so a change there also needs a real
  "Scan as administrator" by the owner. `FolderTreeFile` keeps the last reading of each drive.
- `SeenItems` remembers when each startup item first appeared, so later additions can be marked new.
  Only ordinary scans feed it: tasks the admin scan adds were there all along.
- `InstalledApp.IsComponent` (`ComponentRule`) marks runtimes, drivers and codecs, which the Apps
  page leaves out unless asked. The words it goes by are `componentWords` in
  `attribution-overrides.json`. It only says so when every entry of the app says so itself:
  leaving out a real app is worse than listing a part. Look at
  `--filter "FullyQualifiedName~LiveInventoryReport.Apps_list"` after changing it.
- `AppCatalog` (app) joins the scan, the installed list and what is running into one list of apps
  by id. The Apps page and the search box both read it, so they cannot disagree about what exists.
- `ResourceMonitor` (app) runs the measuring loop off the UI thread and raises `Updated` on it.
  Pages update rows in place on each tick; they do not rebuild or re-sort lists, so rows do not jump.
  The one exception is the folder list while a drive is being read: it starts out in no useful
  order, so it follows the sizes until the reading is done.
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
- Lists are tables: facts go in columns under headings that sort, not in badges. A mark that
  needs explaining is an icon with a tooltip, and is listed in the page's side panel. A cell is
  never left blank: it says "Not running", "0", "Unknown", in the tertiary text colour.
- Something that needs the administrator prompt says so ("Administrator access needed") and
  offers a "Grant access" button with the shield icon, where the missing information would be.
- A message in the banner belongs to the page it appeared on and closes when the user leaves it.
- The title-bar search jumps to things (`SearchIndex`); it never filters a page. Each list page
  has its own filter box and implements `IFilterPage`.
- Every interactive element has an accessible name; fixed controls also get an `AutomationId`.
- After changing UI, run the app and look at it. A process that started is not proof that the
  window is right.

## Working agreements

- Commits are GPG-signed and need the owner's passphrase. Do not commit; the owner tests first
  and commits. Never bypass signing.
- A commit message is one line, `type: short message` (`feat`, `fix`, `docs`, `ci`, `chore`).
  Never add a description under it.
- `docs/superpowers/` (specs and plans) is local only and gitignored.
