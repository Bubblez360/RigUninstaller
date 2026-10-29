# RigUninstaller

A Windows desktop uninstaller. It removes programs with their own uninstallers, then helps you find and remove what they left behind. C# / .NET 8 / WPF, MVVM, no paid libraries. It asks for administrator rights when it starts.

## Run it

Ready-made exe (needs the .NET 8 Desktop Runtime, which this PC has):

```
publish\RigUninstaller.exe
```

From source, in an **administrator** terminal:

```
dotnet run --project src\RigUninstaller
```

Change the app icon: `powershell -File tools\make-icon.ps1 -Source <picture>` writes `src\RigUninstaller\Assets\app.ico` (cropped to a square; `-CenterX/-CenterY` move the crop), then run `publish.cmd`.

Make a new exe: `publish.cmd`. Run the tests: `dotnet test` (add `-c Release` if the app is open).

## What it does

| Where | Feature |
|---|---|
| Programs tab | Searchable, sortable list: icon, name, publisher, version, size, install date, last used, signed by, winget. Status bar shows the count and total size. Light and dark mode. |
| Uninstall button | Confirmation first, then the program's own uninstaller (`msiexec /x {GUID}` for Windows Installer entries). Optional quiet uninstall, restore point and Microsoft Defender scan. Waits for the uninstaller and everything it starts, then rescans. Warns if the program is running. |
| Leftovers window | After an uninstall: files, folders, Start Menu shortcuts and registry keys that match the program or its publisher, rated High, Medium or Low. Low is hidden by default. Nothing is ticked and nothing is deleted until you choose. |
| Batch uninstall | Tick programs (the box on the left), click "Uninstall N ticked". One restore point, a progress list, a stop button. "Tick bloatware" ticks everything on your bloatware list. |
| Right-click > Force remove | For programs whose uninstaller is missing or broken: removes the registry entry and the install folder, after a strong warning. |
| Track new install | Snapshot, install something, "Finish tracking". The changes are saved as a trace log linked to the new program, so its leftovers are removed exactly later. |
| Target (crosshair) | Press, hold, drag onto any open window, let go: its program is selected in the list. |
| Broken entry tag | Marks programs whose uninstaller file or install folder is gone. Clean them with Force remove. |
| Startup tab | Run keys, Startup folders and logon tasks, each with the program's icon (a grey "no icon" picture when none is found). Turn off and on again, never delete. "Add app..." picks an .exe, .bat, .cmd or shortcut and makes it start with Windows for your account only (a value under `HKCU\...\Run`); it asks first, refuses missing files, network paths and programs that already start, and the new item can be turned off in the list. |
| Undo center tab | Every backup the app made (registry exports, Recycle Bin items, restore points, startup changes) with a Restore button. |
| Bloatware list | `Data\bloatware.json`, yours to edit ("Bloatware list" button, then Rescan). Matches are tinted and tagged. |
| Signature column | Who signed each uninstaller. Unsigned or untrusted ones are highlighted. |
| Right-click > Scan with Microsoft Defender | `MpCmdRun.exe -Scan -ScanType 3 -File <folder>` on the install folder. |
| Disk usage tab | "Drives and folders": every drive (C:, E:, F:, G:...) with used and free space, "almost full" under 10% free. Click "See folders" and then any folder to look inside it, biggest first, with Up, Stop and Open in Explorer. Read-only. "Biggest programs": bar chart of the 15 biggest programs; a `~` means the size was measured from the folder. |
| Last used column | From Windows Prefetch data; otherwise the file's access time, shown with a `~` because other programs touch files too. |
| Export CSV | The shown list, opens correctly in Excel. |
| Right-click menu | Open install folder, open registry key, search online, Keep, winget uninstall and update. |
| Store apps tab | `Get-AppxPackage` / `Remove-AppxPackage`, with each app's tile logo read from its manifest. Frameworks and Windows' own packages cannot be removed. |
| winget | "winget check" shows which programs winget knows and which have updates. |
| Keep | Right-click > Keep. A kept program cannot be uninstalled, force removed, batch removed or removed through winget. |
| Cleanup tab: Temp and cache | Shows how much space each kind of cache uses: your Temp folder, Windows Temp, thumbnail cache, crash dumps and error reports, shader caches, browser caches (Chrome, Edge, Brave, Opera, Firefox), Discord and Steam web caches, and Prefetch (not ticked by default). Tick what to clean; one confirmation. These files are deleted for good (not sent to the Recycle Bin) because Windows and your programs make them again. Files in use are skipped. Only the folders in the built-in list can be touched, junctions are never followed, and cookies, history and logins are never deleted. |
| Cleanup tab: Old leftovers | Finds folders in AppData, ProgramData and Program Files that no installed or running program owns and that have not changed for 45 days. They are guesses: nothing is ticked for you, "Look" opens the folder, and ticked folders go to the Recycle Bin (journaled, so the Undo center restores them). |
| Remembered settings | `Data\settings.json` keeps the theme you picked, the list's sort order and the window's size and place (only if it is still on a screen). |
| Risk warnings | Drivers, runtimes, security software and hardware tools carry a "Risky" or "Caution" tag with the reason, and ask before uninstalling. |
| Drive column | Which drive each program is on. Type `F:` in the search box to see only that drive. |

## Safety rules

- Everything that deletes goes through one gate (`SafetyGuard`): nothing inside `C:\Windows`, no drive roots, no `Program Files` itself, no Windows folders in Program Files, no `.sys` drivers, no network paths. Registry: only `HKLM\SOFTWARE` and `HKCU\Software`, never the Windows core keys, Classes or Policies.
- A System Restore point is offered before deleting.
- Every registry key is exported to a `.reg` file first, and is only deleted if the export worked.
- Files and folders go to the Recycle Bin. Windows asks before anything too big for the bin is deleted for good.
- Nothing is ever deleted automatically.
- Every action is written to `Data\Logs\riguninstaller.log` (View log button).

## Where it keeps things

Next to the exe, in `Data\`:

| File | What |
|---|---|
| `Logs\riguninstaller.log` | The action log. |
| `Backups\Registry\<time>_<program>\*.reg` | Registry exports. Double-click one to restore it. |
| `Backups\journal.jsonl` | The list the Undo center reads. |
| `Traces\*.json` | Install trace logs. |
| `keep.json`, `bloatware.json` | Your Keep tags and bloatware list. |

## Things to know

- Sizes are what each program reports to Windows. Some report none.
- "Last used" with a `~` is approximate.
- A restore point can only be made once every 24 hours by default; the app says so and asks whether to go on.
- Removed Store apps can be installed again from the Microsoft Store.
- Windows protects some of its own files with catalog signatures, so the signature column leaves Windows components blank.
