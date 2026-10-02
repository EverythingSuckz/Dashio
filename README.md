# Dashio

Dashio shows what every app on your Windows 11 PC is using right now and what it has set up to
start by itself, grouped by app, and lets you switch those things off and back on.

Windows tracks services, scheduled tasks and startup entries in separate places, and Task
Manager's Startup tab only shows some of them. An app can install four services and a
scheduled task and appear nowhere in that tab. Dashio puts all of an app's pieces on one
page.

It only switches things on and off. It never deletes anything, every change is recorded, and
any change can be undone.

## What it reads

| Kind | Where it comes from |
|---|---|
| Services | The Service Control Manager (kernel drivers are left out) |
| Scheduled tasks | Task Scheduler |
| Startup entries | The `Run` registry keys, for you and for all users |
| Startup folder | Your Startup folder and the all-users one |
| Store app startup | Startup tasks declared by Store packages |

Parts of Windows itself are recognised by their signature, hidden by default and read-only.

## What is running

The Overview page shows memory and processor use for the whole PC and for each app, updated
every two seconds (the interval can be changed or paused in Settings). An app's figure is the
sum of everything it is running: its windows, its background programs and its services. Open
an app to see each of its processes.

Memory is the private working set, the same figure Task Manager's Memory column shows.
Processor use is the share of the whole processor. Dashio reads these for every process
without administrator rights, and measures nothing while its window is minimised.

A running program is tied to an app by the services it hosts, the folder it runs from or its
signature, never by a shared word in its name. Apps that run but start nothing by themselves
appear on Overview only; the Apps page stays a list of what starts by itself.

## How items are matched to apps

No single clue is enough, so Dashio applies five rules in order and records which one placed
each item. Expand an item to see the reason.

1. The file is inside a Store package.
2. The file is inside an installed app's folder.
3. The file was installed by a driver package.
4. The file sits in a vendor or product folder, such as `Program Files\Vendor\Product`.
5. The file names a company, or is signed by one.

Groups that are the same product under different names are then merged, and a short
overrides file (`src/Dashio.Core/Attribution/attribution-overrides.json`) fixes vendors whose
files describe themselves badly. Anything Dashio cannot place goes into "Unmatched" instead of
being guessed.

## Changing things

Flipping a switch queues a change; nothing happens until you choose **Review and apply**.
Changes to your own startup entries apply directly. Changes to services, machine-wide entries
and most tasks need administrator rights, so Windows shows one admin prompt per batch.

| Kind | Off means |
|---|---|
| Service | Start type set to Disabled, and the service stopped |
| Scheduled task | Disabled in Task Scheduler |
| Startup entry, Startup folder | The same on/off flag Task Manager uses |
| Store app startup | The startup task set to disabled |

After applying, Dashio reads each item back from Windows to confirm the change took effect.
Every change is written to `%LOCALAPPDATA%\Dashio\journal.jsonl`, and **History** can undo any
entry. Undoing a service restores its exact start type.

Some scheduled tasks are invisible to ordinary apps. **Settings → Scan with administrator
rights** lists them, including tasks that are in the registry but hidden from Task Scheduler.

## Privacy

Dashio never connects to the internet and collects nothing.

## Build and run

Requirements: Windows 11 22H2 or later, the .NET 10 SDK, and the
[WinApp CLI](https://github.com/microsoft/WinAppCli) for the UI tests.

```powershell
dotnet build Dashio.slnx
dotnet run --project src\Dashio.App
```

The app is unpackaged on purpose: a packaged app's registry changes go to a private copy and
would not affect the real startup entries.

## Tests

```powershell
# Unit tests: pure logic, no access to the system.
dotnet test tests\Dashio.Core.Tests --filter "Category!=Live&Category!=Integration&Category!=Report"

# Live tests: read-only checks against this machine, including the match rate.
dotnet test tests\Dashio.Core.Tests --filter "Category=Live"

# Integration tests: create a throwaway startup entry and task for the current user,
# switch them off and on, then remove them. No administrator rights needed.
dotnet test tests\Dashio.Core.Tests --filter "Category=Integration"

# UI tests: drive the real window. Build first.
pwsh tools\ui-tests.ps1
```

`Category=Report` prints what the scan sees on this machine, for reading rather than asserting.

## Layout

| Project | What it holds |
|---|---|
| `src/Dashio.Core` | Collectors, the matching engine, change logic and the change log. No UI. |
| `src/Dashio.App` | The WinUI 3 window. Never runs as administrator. |
| `src/Dashio.Helper` | A small program that runs elevated for one batch of changes, then exits. |
| `tests/Dashio.Core.Tests` | The tests above. |
| `tools` | The UI test script and the icon generator. |

## Status

This is the first slice: autostart items grouped by app, with disable, enable and undo.
Planned next: live memory and CPU by app, storage, change alerts, and unused-app detection.
There is no installer and the builds are not signed yet.

## License

[MIT](LICENSE)
