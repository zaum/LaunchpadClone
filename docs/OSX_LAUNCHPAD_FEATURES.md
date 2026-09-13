# macOS Launchpad — feature reference

## Opening and closing

- Launchpad opens from the taskbar icon,or mouse moving to the corner, this is configurable, on the top right size a small gear icon opening the configuration: enable/disable corner start (with a simple graphic 4 L shapes represents the corner, selected is highlighted) toggle button to enable/disable, configurable grid size: a slider with live preview, configurable keystroke to start the app
- It is a fullscreen overlay over the desktop; the desktop is dimmed behind
  it.
- It closes on: clicking the desktop outside the app grid, pressing Esc,
  or launching an app (the overlay dismisses once the app opens).

## Layout and pages

- Apps appear as a uniform grid of icons with labels underneath.
- The grid is split into **pages** when there are more apps than fit on one
  screen (default 7x5 grid, configurable in Settings ).
- You move between pages by **swiping/dragging left or right** (trackpad
  two-finger swipe, or click-drag with a mouse). There is no scrollbar; the
  page content slides with a soft animation.
- **Page indicator dots** appear near the bottom of the screen while
  dragging; they fade out otherwise.
- The mouse scroll wheel also flips pages (direction depends on natural
  scrolling).

## Folders (groups)

- **Create:** drag an app icon **onto another app icon** — a
  drop zone; releasing creates a new folder containing both apps and opens
  it.
- **Add to folder:** drag more apps onto the folder icon. Alternatively,
  drop an app onto an open folder's window.
- **Folder icon:** shows a rounded-rect tile with a mini grid of up to
  9 member icons (3x3); folders with more members just fill the 9 slots.
- **Rename:** open the folder and click its title above the grid — the
  title becomes an editable text field.
- **Open:** single click. **Close:** click outside the folder, or Esc.
- **Remove an app:** drag it out of the open folder back onto the
  Launchpad pages (it returns to the main grid).
- **Delete a folder:** removing every app deletes the folder automatically.
- Folders live on pages like apps; they are sorted together with apps
  alphabetically.

## Search

- With Launchpad open, **typing immediately filters** all apps — including
  apps inside folders (matching members surface as if ungrouped).
- There is a search field at the top of the screen; results replace the
  grid (grouped into a result page).
- Pressing Enter launches the selected result; arrow keys move the
  selection.

## Launching and organizing

- Single click (or Enter) launches the app; Launchpad closes.
- **Rearrange:** drag icons to any position on any page; other icons flow
  around. Dropping an icon at a page edge moves it to that page.
- **Delete/uninstall:** only Launchpad offers the jiggle-mode long press
  : click and hold until icons wiggle; user-apps show an "x" to
  uninstall - launches windows uninstaller
- ## Settings that affect Launchpad
- Launchpad app positions are saved
- ## Implications for LaunchpadClone

1. Overlay + dimmed/blurred backdrop, closes on launch/Esc. (done)
2. Top-center search that fuzzy-filters, including group members. (done)
3. Pages with drag/wheel flip and slide animation. (done)
4. Drop icon onto icon -> folder; folder tile with mini icon preview;
   open/rename/remove members; drag-out removes from group; empty folder
   auto-deletes; drag-order is persisted. (done)
5. Jiggle-mode uninstall: long-press wiggles tiles and shows a red ✕ that
   launches the Windows uninstaller. (done)
6. Settings gear: hot-corner toggle with corner graphic, grid-size slider
   (live preview), configurable open-keystroke. (done)
7. Page indicator dots near the bottom; single click (and Enter) launches. (done)
