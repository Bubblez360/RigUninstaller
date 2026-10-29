# RigUninstaller

**A free Windows uninstaller that finds the junk programs leave behind, warns you what is risky to remove, and keeps an undo list.**

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/Bubblez360/RigUninstaller)](https://github.com/Bubblez360/RigUninstaller/releases/latest)
![Windows 10 and 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)

[**Download for Windows**](https://github.com/Bubblez360/RigUninstaller/releases/latest) · [How it works](#a-quick-tour) · [Safety rules](#safety-rules)

When you uninstall a program, Windows often leaves files, folders and registry entries behind. RigUninstaller runs the program's own uninstaller, then scans for what is left, rates each item High, Medium or Low, and removes only what you tick. It also clears temp files and caches, manages startup items, and shows what is using your disk space.

![Programs tab, dark theme](docs/screenshots/programs-dark.png)

<!-- Add the demo GIF or video here once it is recorded. -->

**Why you can trust it**

- Free and open source (MIT). The full source is in this repo, and you can build it yourself.
- Nothing is deleted on its own. You tick it, or you press the button.
- Uninstall cleanups can be undone: registry keys are exported to `.reg` files first, files go to the Recycle Bin, and a restore point is offered. The one exception is the Cleanup tab's temp and cache clearing, which is permanent and asks first.
- Windows folders, drivers and the core registry keys are blocked by one safety gate.
- No telemetry. It sends nothing anywhere unless you ask it to run `winget` or open a web search.
- 800+ automated tests. Built with C#, .NET 8 and WPF, with no paid libraries.
- Not code-signed yet, so Windows shows a SmartScreen warning on first run. [Here is how to check the download](#download).

## Highlights

- **Uninstall properly.** Runs the program's own uninstaller (including MSI), waits for it and everything it starts, then rescans. Batch uninstall for many programs at once.
- **Finds leftovers.** After an uninstall it looks for files, folders, shortcuts and registry keys that belong to the removed program and rates each one High, Medium or Low. Nothing is removed until you tick it.
- **Warns before you break something.** Drivers, runtimes (.NET, Visual C++), security software and hardware tools carry a **Risky** or **Caution** tag with the reason, and ask first.
- **Undo center.** Every registry key is exported to a `.reg` file before it is touched, files go to the Recycle Bin, and restore points are offered. Restore from one list.
- **Cleanup tab.** Clears temp files, Windows Temp, thumbnail cache, crash dumps, shader caches, browser caches and more, and finds old folders that programs left behind.
- **Cleans up broken entries.** Programs whose files are already gone (so the uninstaller cannot run) are tagged and can be cleaned out of the list safely.
- **Light and dark theme**, remembered between runs, along with your sort order and window position.

| Cleanup: temp files and caches | Disk usage: biggest programs per drive |
|---|---|
| ![Cleanup tab](docs/screenshots/cleanup-dark.png) | ![Biggest programs](docs/screenshots/disk-dark.png) |

![Programs tab, light theme](docs/screenshots/programs-light.png)

## Download

1. Open the [**latest release**](https://github.com/Bubblez360/RigUninstaller/releases/latest) and download `RigUninstaller-v…-win-x64.zip` (about 64 MB).
2. Unzip it to a normal folder, for example `C:\Tools\RigUninstaller`.
3. Double-click `RigUninstaller.exe` and answer **Yes** to the Windows permission prompt.

There is nothing to install: the .NET runtime is inside the exe, so the first start takes a few seconds longer.

**"Windows protected your PC"?** The exe is not code-signed yet, so Windows warns about every new unsigned program. Click **More info**, then **Run anyway**. You can check the download first: compare its SHA-256 with the one on the release page (`Get-FileHash <file>` in PowerShell), or upload the file to [VirusTotal](https://www.virustotal.com). The full source code is in this repo.

## Requirements

- Windows 10 or 11, 64-bit
- Administrator rights. The app asks for them when it starts (UAC prompt), because uninstallers and registry changes need them.
- To build from source only: the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). The downloaded zip needs nothing.

## Build it yourself

The download zip is made by `tools\make-release.ps1` (self-contained, single file, with checksums). For a smaller build that needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) installed:

```
publish.cmd
```

It writes `publish\RigUninstaller.exe`. Or run from source, in an **administrator** terminal:

```
dotnet run --project src\RigUninstaller
```

Run the tests:

```
dotnet test
```

(Add `-c Release` if the app is open: a running exe locks the Debug build.)

## A quick tour

| Where | What it does |
|---|---|
| **Programs** | Searchable, sortable list: icon, name, publisher, drive, version, size, install date, last used, who signed the uninstaller. Type a drive such as `F:` in the search box to see only that drive. Right-click for more. |
| **Uninstall** | Confirmation first, then the program's own uninstaller (`msiexec /x` for MSI). Optional quiet uninstall, restore point and Microsoft Defender scan. Warns if the program is running. |
| **Leftovers window** | After an uninstall: items that match the program or its publisher, rated High, Medium or Low. Low is hidden by default. |
| **Force remove** | Right-click. For programs whose uninstaller is missing or broken: removes the registry entry and the install folder, after a strong warning. |
| **Find by window** | Hold the crosshair button and drag onto any open window (or click it, then click the window). Its program is selected in the list. |
| **Track new install** | Take a snapshot, install something, finish tracking: the exact changes are saved and linked to the program, so its leftovers are removed precisely later. |
| **Disk usage** | Every drive with used and free space, folder-by-folder browsing, and a chart of the biggest programs with a button per drive and totals. Read-only. |
| **Store apps** | Lists Microsoft Store apps with their icons; remove them (frameworks and Windows' own packages are protected). |
| **Startup** | Run keys, Startup folders and logon tasks, with icons. Turn items off and on again (never deleted). **Add app...** makes a program start with Windows. |
| **Cleanup** | *Temp and cache*: tick the categories to clean. *Old leftovers*: folders in AppData, ProgramData and Program Files that no installed program owns. |
| **Undo center** | Every backup the app made (registry exports, Recycle Bin items, restore points, startup changes) with a Restore button. |
| **More tools** | Check winget (shows which programs winget knows and which have updates), export the list to CSV, edit the bloatware list, tick all bloatware. |
| **Keep** | Right-click a program and choose Keep: it cannot be uninstalled, force removed, batch removed or removed through winget. |

## Safety rules

- Everything that deletes goes through one gate (`SafetyGuard`): nothing inside `C:\Windows`, no drive roots, no `Program Files` itself, no Windows folders in Program Files, no `.sys` drivers, no network paths. Registry: only `HKLM\SOFTWARE` and `HKCU\Software`, never the Windows core keys, Classes or Policies.
- A System Restore point is offered before deleting.
- Every registry key is exported to a `.reg` file first, and is only deleted if the export worked.
- Files and folders go to the **Recycle Bin**. The one deliberate exception is the Cleanup tab's *Temp and cache*: those files are disposable and are deleted for good, after one confirmation. See below.
- Nothing is ever deleted automatically. You tick it, or you press the button.
- Every action is written to the log (**View log** button).

### What the cache cleaner can and cannot touch

It works from a fixed list of cache folders and deletes the **contents** only, never the folder. It never follows shortcuts or junctions, refuses protected folders (your profile, Documents, Downloads, the Windows folder as a whole...), and skips files that another program has open. Browser cleaning removes caches only: cookies, history and saved logins stay. Prefetch is not ticked by default, because Windows rebuilds it and programs can start a little slower afterwards. Cache cleaning cannot be undone, so the confirmation lists exactly what will go.

## Where it keeps things

Next to the exe, in `Data\` (so a new build in a new folder starts with an empty `Data`):

| File | What |
|---|---|
| `Logs\riguninstaller.log` | The action log. |
| `Backups\Registry\<time>_<program>\*.reg` | Registry exports. Double-click one to restore it. |
| `Backups\journal.jsonl` | The list the Undo center reads. |
| `Traces\*.json` | Install trace logs. |
| `keep.json`, `bloatware.json` | Your Keep tags and bloatware list. |
| `settings.json` | Theme, sort order and window position. |

## Things to know

- Sizes are what each program reports to Windows. Some report none; the Disk usage chart measures the install folder for those and marks them with `~`.
- "Last used" with a `~` is approximate (from file access times). The exact dates come from Windows Prefetch data, so clearing Prefetch makes them approximate too.
- A restore point can only be made once every 24 hours by default; the app says so and asks whether to go on.
- Removed Store apps can be installed again from the Microsoft Store.
- A **Broken entry** tag means the uninstaller file or install folder is gone. Use **Uninstall > Clean up entry** (or right-click > Force remove) to remove the leftover entry.
- The signature column leaves Windows components blank, because Windows protects them with catalog signatures.
- RigUninstaller itself sends nothing anywhere. It only runs `winget` and opens web searches when you ask it to.

## Support

RigUninstaller is free, and made by one person in their spare time. If it saved you time or cleaned up a mess, you can say thanks on Ko-fi. It is never required and nothing in the app is locked behind it.

[**Support on Ko-fi**](https://ko-fi.com/bubzwot)

Bug reports and ideas are just as welcome: open an issue and say what you saw (the log from the **View log** button helps).

## For developers

```
src\RigUninstaller        the app (WPF, MVVM written by hand)
  Models\                 plain data types
  Services\               scanning, uninstalling, cleaning, safety (all behind interfaces)
  ViewModels\             screens' logic, no UI types
  Views\ Themes\ Helpers\ XAML, light/dark styles, small UI helpers
tests\RigUninstaller.Tests   xUnit: 800+ tests with fakes for every service
tools\make-icon.ps1       turns a picture into src\RigUninstaller\Assets\app.ico
docs\screenshots          pictures used in this README (made from sample data)
```

Design notes:

- Real uninstallers, restore points, winget uninstalls and cache cleaning only ever run behind interfaces with fakes in tests. Tests that touch the machine only read it, or use their own temporary folders and keys.
- Both color themes are checked by a test for WCAG contrast (4.5:1 for text) and must define the same keys.
- To change the app icon: `powershell -File tools\make-icon.ps1 -Source <picture>`, then run `publish.cmd`.

## License

[MIT](LICENSE). Use it, change it, share it; just keep the copyright notice. It comes with no warranty, so read the confirmation dialogs before you delete things.
