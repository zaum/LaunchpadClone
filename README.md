# LaunchpadClone - Core layer (Phase 1: app discovery + icon extraction, Phase 2: cache + watcher + launcher)

## Layout
- **src/LaunchpadClone.Core** - discovery, icon extraction, models, UI-free
  - `Models/AppItem.cs` - stable hashed Id, display name, target, kind
  - `Discovery/AppDiscoveryService.cs` - Start Menu .lnk scan (parallel, STA-marshalled COM)
  - `Discovery/UwpPackageProvider.cs` - Store/UWP apps via PackageManager + logo export
  - `Discovery/AppWatcher.cs` - FileSystemWatcher + debounce, delta updates (upsert/remove)
  - `Cache/AppCache.cs` - on-disk JSON list cache for instant startup (`%LOCALAPPDATA%/LaunchpadClone/apps.json`)
  - `Icons/IconExtractionService.cs` - shell icon to PNG cache (`%LOCALAPPDATA%/LaunchpadClone/icons/*.png`)
  - `Launch/AppLauncher.cs` - launch .lnk via ShellExecute, UWP via `shell:AppsFolder/AUMID`
  - `Native/ShellLinkResolver.cs`, `Native/IconInterop.cs` - IShellLinkW COM + SHGetFileInfo P/Invoke
- **tools/LaunchpadClone.Poc** - console test harness: lists found apps, extracts icons
- **files/** - original drop of the phase-1 sources (kept for reference, not built)

## Run (on Windows, Visual Studio 2022 / .NET 8 SDK)
```
cd tools/LaunchpadClone.Poc
dotnet run
```
First run takes a few seconds (COM + filesystem calls), then every app shows
`[icon OK]` or `[no icon]`. Icons go to `%LOCALAPPDATA%/LaunchpadClone/icons/*.png`.

## Worth knowing / verify at build time
- **UWP projection**: `Windows.Management.Deployment` and `Windows.ApplicationModel.Core`
  namespaces come automatically with the `net8.0-windows10.0.19041.0` TFM. If the
  compiler cannot find them, add the `Microsoft.Windows.SDK.Contracts` NuGet package.
- **PackageManager permission**: `FindPackagesForUser("")` from an unpackaged desktop
  app worked for the current user packages with no manifest restriction so far -
  but verify on first run on the target machine, it can differ by Windows version.
- **System.Drawing.Common**: Windows-only at runtime (fine here, the whole project is
  Windows-only), but note it has been marked explicitly Windows-only since .NET 6+.
- **STA COM**: `IShellLinkW` resolve runs on a dedicated STA thread per shortcut,
  because the parallel scan uses MTA pool threads.
- This code was **not compiled/tested** in this environment (Linux sandbox, no
  Windows/WinRT) - logically and API-level reviewed, but expect small compile
  errors after the first Windows build.

## What is next vs the plan
- [x] **FileSystemWatcher + delta refresh** (phase 2) - `AppWatcher` with debounce,
  `AppsUpserted` / `AppsRemoved` events
- [x] **On-disk app-list cache** (JSON) so startup needs no full scan - `AppCache`
- [x] **Launcher** - `AppLauncher` (.lnk + UWP)
- [ ] **`AppKind.Executable`** branch if Program Files should be included directly
  without shortcuts
- [ ] UI shell (WinUI 3 / WPF): grid, search, drag icons - consumes Core as-is
