# LaunchpadClone — Changelog

* **Total commits:** 12
* **Date range:** 2026-09-13 – 2026-09-14
* **Environment / Context:** Master branch, development iteration

---

### <sup><sub style="font-size: 0.7em;">2026-09-14</sub></sup> · 🎨 UI/UX · WPF-UI Fluent Integration & Hover-to-Group

* Integrated the WPF-UI library to adopt a Windows 11 Fluent design system, including dark theme resources and styled controls.
* Implemented macOS-style "hover-to-group": hovering a dragged tile over a target tile with a timer now creates an app folder.
* Refactored mouse event handling to use Preview events for better control over the UI interaction pipeline.
* Optimized initial loading with concurrent asynchronous loads of order, groups, and cache data.
* Adjusted default tile and icon dimensions for a more compact, macOS-like density.
* Improved visual rendering with aliased edge mode on tiles to prevent shimmering during animations.

`Direct Commit` · `35f0454`

---

### <sup><sub style="font-size: 0.7em;">2026-09-14</sub></sup> · 🚀 Features · Discovery Progress Reporting & STA Refactor

* Added progress reporting during full app scans for better UI feedback in both the WPF shell and the console PoC.
* Introduced a shared `StaRunner` utility so all COM-bound shortcut resolution runs on dedicated STA threads consistently.
* Serialized `AppWatcher` batch processing with `SemaphoreSlim` to avoid overlapping delta updates.
* Added working directory handling for raw executable launches.
* Ensured settings and layout storage directories are created on demand.
* Added cancellation token support for icon extraction to prevent overlapping runs.

`Direct Commit` · `5c79b9f`

---

### <sup><sub style="font-size: 0.7em;">2026-09-13</sub></sup> · 🐛 Fixes · Settings Gear, Icon Loading & Interaction Robustness

* Added a named `SettingsGearButton` reference with an `IsWithinSettingsGear()` check so clicking the gear opens settings instead of freezing the launcher.
* Skipped the hold timer for interactive controls so the settings gear no longer triggers close.
* Reverted synchronous disk-cache icon loading that froze the UI thread; icons are now extracted asynchronously for the visible page only.
* Added an `IsWithinControl()` check so clicks on buttons, sliders, and menus no longer pre-emptively close the launcher.

`Direct Commit` · `7bbe851` + `3358bac` + `c4c9beb` + `3e88a93`

---

### <sup><sub style="font-size: 0.7em;">2026-09-13</sub></sup> · 🚀 Features · Smart Startup Caching

* Skipped the background rescan when the app-list cache is younger than one minute.
* Extracted icons only for the visible page instead of all apps.
* Persisted `IconCachePath` in the JSON cache so icons are not re-extracted on subsequent starts.
* Re-saved the cache after icon extraction with updated icon paths.

`Direct Commit` · `039208e`

---

### <sup><sub style="font-size: 0.7em;">2026-09-13</sub></sup> · 🐛 Fixes · Tile Click Visual Tree Walk

* Fixed broken tile clicks: `FindTileContainer`/`FindMemberContainer` relied on `Control.Parent`, which returns `null` for `Grid`, `Image`, and `TextBlock` elements inside data templates.
* Switched to `VisualTreeHelper.GetParent()` so the traversal always finds the owning `ListBoxItem`.

`Direct Commit` · `0c2a52c`

---

### <sup><sub style="font-size: 0.7em;">2026-09-13</sub></sup> · 🎨 UI/UX · Tooltips, Context Menu, Slider Polish & Chrome Cleanup

* Added tile tooltips showing the full app name (path removed in a follow-up pass for clarity).
* Added a right-click context menu with an "Open folder" action that also closes the launcher.
* Improved the grid-size slider with a circular thumb, blue stroke, and a live `8 × 6` dimensions label positioned above the track.
* Overrode the default `ToolTip` control template so the dark background with rounded corners renders correctly (WPF's default theme ignores `ToolTip.Background`).
* Replaced the settings gear button's default template with a minimal chrome-free one.
* Made the search clear (`×`) button a plain `TextBlock` with no hover effect.
* Clicking empty space outside tiles and the search box now closes the launcher.

`Direct Commit` · `45bb1a1` + `688a59e` + `2576ed0` + `8b3fa4c`

---

### <sup><sub style="font-size: 0.7em;">2026-09-13</sub></sup> · 🚀 Features · Initial Commit — macOS-Style App Launcher for Windows

* **Core layer:** app discovery (Start Menu `.lnk`, UWP packages, Program Files executables), icon extraction and PNG caching, JSON app-list cache, `FileSystemWatcher` delta updates, fuzzy search, app launching, uninstaller service, app groups, tile layout persistence, and settings storage.
* **UI layer:** WPF fullscreen blurred overlay, top-center fuzzy search, paged icon grid with slide animation, drag-to-group folders, jiggle-uninstall mode, drag-reorder, hot-corner and global hotkey activation, and a settings overlay.
* **Tools:** console PoC harness for discovery and icon extraction.

`Direct Commit` · `982ad56`
