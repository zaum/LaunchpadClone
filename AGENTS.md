# AGENTS.md - Windows Launchpad

Speak Hungarian with the user, but ALL file content (comments, README, XML docs) in English.
Beginner-friendly tone: explain jargon, add context. Code comments/README stay technical English.

## Project layout (authoritative)
- `src/LaunchpadClone.Core/` - UI-free core: Models, Discovery, Native, Icons, Cache, Launch, Search (FuzzySearch), Groups (AppGroup/AppGroupStore).
- `src/LaunchpadClone.UI/` - WPF shell (fullscreen blurred overlay, top-center fuzzy search, paged icon grid with slide animation, drag-to-group folders, single-click/Enter launch, jiggle-uninstall, drag-out of groups, page indicator dots, settings gear: hot-corner / grid-size slider / global open-hotkey, drag-reorder order persistence, watcher deltas, 10-min full rescan). Consumes Core as-is.
- `tools/LaunchpadClone.Poc/` - console harness (`dotnet run` on Windows).
- `files/` - LEGACY phase-1 drop. Reference only, NOT built. Do not edit; port fixes into `src/` instead.
- Build only `src/` + `tools/`. Never add `files/*.cs` to a csproj.

## Build
- Target: `net8.0-windows10.0.19041.0`, `EnableWindowsTargeting=true` (Linux edit safe).
- Real build/test only on Windows with .NET 8 SDK (`cd tools/LaunchpadClone.Poc && dotnet run`).
- PATH QUIRK: an x86 `dotnet` (no SDKs) sits first on PATH — build with the
  full path `"C:\Program Files\dotnet\dotnet.exe"` (SDKs 8.0.425 / 10.0.302).
- Poc verified on this machine: full scan ~9-50 s, discovery + icons + cache working.

## Conventions
- `AppItem.Id` is stable hash of lnk path / AUMID / exe path - never change the scheme silently (breaks icon + JSON cache).
- IShellLinkW COM is STA: always resolve via dedicated STA thread (see AppDiscoveryService).
- Watcher emits upsert/remove only; UWP and raw Executable items need periodic full rescan.
- Never leave half-written cache: temp file + atomic move; delete partial PNGs on failure.
- No personal data, absolute local paths, passwords, IPs in committed files. Check before publish.
- WinRT via CsWinRT: `PackageManager` is NOT IDisposable (no using), WinRT streams are plain `using` (not `await using`), qualify `Windows.ApplicationModel.PackageSignatureKind`.
- Shortcut icons are cached as `{id}.jumbo.png` extracted from the RESOLVED target (no Explorer arrow overlay); UWP logos stay `{id}.png`.
- Groups (groups.json) reference apps by AppItem.Id; stale ids are ignored, an emptied group deletes itself.

## Next
- ExecutableAppProvider still over-includes (~168 items) — needs de-noising/ranking later.
- Global open-hotkey is polled via user32 GetAsyncKeyState (no window hook).
- Reorder drops slot the tile by pixel position and persist; per-index reflow while
  dragging is live via a placeholder cell (macOS "others flow around").
