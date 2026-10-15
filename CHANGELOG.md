# Changelog

## 0.6.9 — 2026-10-15

- Fixed: dragging notes, folders and notebooks did nothing on Windows. Dragging no longer relies on the
  browser's built-in drag and drop (which the Windows web view does not deliver here); the app follows
  the pointer itself, the same on Windows, macOS and Linux, with a label that travels with the pointer.
  Escape cancels a drag.

## 0.6.8 — 2026-10-15

- Notes and folders in a place share one order, shown in the tree: a new folder or note goes at the
  bottom, below what is already there (before, folders were always drawn above notes). Any note or
  folder can be dragged above or below any other (drop on the top or bottom half of a note's row),
  or moved with right-click **Move up / Move down** or **Alt+↑ / Alt+↓**.
- The note list's default order is now called "Your order (new at the bottom)" and follows the same
  order.

## 0.6.7 — 2026-10-12

- Reorder folders and notebooks: drop one on the top or bottom edge of another row to place it before
  or after (a line shows where); dropping on the middle still puts it inside. Right-click has
  **Move up** and **Move down** as well.

## 0.6.6 — 2026-10-10

- A note with many attachments keeps its room: the list under the note folds to one line
  ("▸ Attachments 15") and, when opened, takes at most a third of the window and scrolls. Open or
  closed is remembered. The links panel under a note is capped the same way.

## 0.6.5 — 2026-10-09

- The navigation tree draws its lines: a line runs down from each notebook or folder past its
  children, with an elbow to every child, so it is plain what sits inside what.

## 0.6.4 — 2026-10-07

- Drag a whole notebook onto another notebook or a folder: after a confirmation it becomes a folder
  there with all its notes and folders in place (the trash included). Notes and folders could already
  be dragged.

## 0.6.3 — 2026-10-06

- Update check: ticking "Check for a new version" now checks at once, and an open WorriorVex checks
  again every six hours (before, it only checked when starting). A newer version shows as a badge in
  the top bar that stays until you update, instead of a banner other messages could hide. Each check
  is noted in the log file.

## 0.6.2 — 2026-10-06

- Fixed: after using the calendar, moving to another page or creating a notebook froze the window
  (0.6.0) or showed "Something went wrong … ObjectDisposedException" (0.6.1). The closed note editor
  was still being asked to save.
- The calendar has its own **Calendar** heading in the navigation, with **Month view** and **All
  calendar notes**; its notebook no longer appears among the notebooks and cannot be renamed or
  deleted by accident. The notebook 0.6.0 created is taken over automatically.

## 0.6.1 — 2026-10-06

- A problem inside the interface no longer leaves a frozen window: a panel says what happened, with a
  Continue button, and the details go to the log file.

## 0.6.0 — 2026-10-05

- **Calendar:** a month view in the navigation. Click a day to add short notes, as many per day as
  you like; click one to change it, ↗ to open it in the editor, × to bin it. Entries are ordinary
  notes with a date, kept in a "Calendar" notebook, so search, tags, links, backup and export cover
  them. A dot marks days on which other notes were changed.

## 0.5.6 — 2026-10-04

- Every row in the tree has a **+** now. On a note it turns the note into a folder of the same name,
  keeps the note inside, and adds the new note or folder under it, the way KeepNote pages could have
  children.
- Adjusted settings are written back at once after an upgrade.

## 0.5.5 — 2026-10-04

- Deleting a note now asks for confirmation by default (Settings → Behaviour → "Ask before moving a
  note to the trash" can switch it off). Folders and notebooks always asked; they still do, naming
  what goes.

## 0.5.4 — 2026-10-04

- Settings files written by earlier versions carried "Last changed" as the note order even when nobody
  had chosen it; on first start they move to "In the order added" once. An order picked from the
  dropdown stays as picked.

## 0.5.3 — 2026-10-04

- Deleting is unmistakable now: the buttons above the note list read **Delete folder…** / **Delete
  notebook…** (and **Rename folder…** / **Rename notebook…**), the confirmation says "the whole folder,
  not one note" and counts what goes, and the note's own button reads **Delete note**. Nothing is ever
  lost either way: everything deleted sits in the Trash until you restore or remove it.
- New notes join at the bottom of their notebook or folder: the list and the tree now default to
  "In the order added"; the other orders remain in the list's dropdown.
- The tree drops a deleted note at once.

## 0.5.2 — 2026-10-04

- The + on a notebook or folder row now offers **New note** as well as **New folder**, so any place can
  hold as many notes as you like straight from the tree; choosing a note in the tree selects its folder,
  so "+ New" adds the next note beside it.

## 0.5.1 — 2026-10-03

- Fixed: after changing the language (or any time the workspace was rebuilt) the navigation pane's
  edge no longer followed the mouse. Both pane edges now re-attach whenever the page changes.
- Fixed: dragging the note list's edge measured from the wrong side and collapsed the list.

## 0.5.0 — 2026-10-03

- The interface and the built-in documentation are available in English, Dutch, Polish and German
  (Settings → Language; follows the system language by default).
- Notes appear as leaves of the navigation tree under their notebook and folder (Settings → Appearance to turn off).
- Rename notebooks, folders and notes in place: double-click or F2.
- Keyboard navigation of the tree: arrows, Home/End, Delete.
- Sort the note list by last change, newest or title; compact list density.
- Word count, reading time and last-saved time in the status bar.
- Editor: find & replace (Ctrl/⌘+F), highlight marker, callout boxes (info, tip, warning, danger),
  code blocks coloured by language, images resized by dragging a corner, table columns resized by
  dragging, merge and split cells, type `[[` to link to another note, cleaner paste from Word,
  Google Docs and web pages.
- Search: limit a search to the notebook or folder you were in ("Only in …"), save searches to the
  navigation pane, and "Did you mean …?" when a word is a letter or two off.
- Lists show how many links a note has; a "Loose ends" view lists notes with no tag and no link.
- Automatic backups: daily or weekly into the backups folder while the app is open, keeping the newest N.
- Import a `.worriorvex` package from another computer, or a folder of Markdown / text files (Obsidian,
  Joplin): folders, images, `[[wiki links]]`, front-matter and `#inline` tags come along.
- "Check attached files" on the Import & backup page: missing, changed and unused files.
- Appearance: accent colour, editor font (system, serif, sans, mono), two high-contrast themes, and a
  focus mode (Ctrl/⌘+Shift+F) that shows only the note.
- Big notebooks: folders start collapsed and the tree remembers what you opened; the note list only
  renders the rows in view. Measured with 10,000 notes (docs/performance.md).
- Open a `.worriorvex` package with WorriorVex (file association on Windows and Linux; command line
  everywhere) and it is imported straight away.
- Note templates: Ctrl/⌘+Shift+N (or the ▾ next to + New) makes a note from one of the notes in the
  "Templates" notebook, which is created with three starters; `{{date}}` becomes today's date.

## 0.4.0 — 2026-10-03

- Notebooks grow like a tree: a **+** at the end of every notebook and folder row adds a folder inside it,
  and the box at the left shows **−** when a branch is open and **+** when it is collapsed. The branch
  that holds the selected folder always opens.

## 0.3.0 — 2026-10-03

- The navigation and note-list panes can be resized by dragging their right edge (or with the arrow
  keys when the edge has focus); the widths are remembered. Long notebook names show in full on hover.
- Optional check for a new version (Settings → Updates, off by default; About → Check now).
- Homebrew: the cask moved to its own tap, `brew tap cloudworrior-labs/tap`.

## 0.2.0 — 2026-10-03

Everything since the first release.

### Writing
- Links to web pages, pictures (from a file, pasted or dropped) and tables in notes.
- Links between notes with a note picker; Ctrl/⌘+click follows them. *Links to*, *Linked from* and
  *Related by tags* under each note.
- History: earlier versions of a note are kept while you edit and can be viewed and restored.
- Attach any file to a note; open it, save a copy, rename or remove it; unused pictures can be cleaned up.

### Finding and organising
- Full-text search as you type, with highlighted matches, phrases and `tag:` / `in:` filters.
- Tags, favourites, pinned notes, a Recent list; move notes and folders by drag-and-drop or dialog;
  duplicate; right-click menus.

### Your data
- Import a KeepNote notebook (format 5 and 6) with a scan, a confirmation and a report.
- Backup to one zip, restore with a safety backup first, export as HTML, Markdown, JSON or a
  `.worriorvex` package.
- Settings: theme, text size, line height, spell check, autosave pause, confirm deletion, open where you left off.

### Safety
- Everything stored passes an allow-list sanitiser; the window loads nothing from the network.
- Script addresses carry the build version, so an upgrade never runs an old script.

## 0.1.0 — 2026-10-02

First release: Inbox, notes with autosave, notebooks, folders, trash, About and Documentation pages,
installers for Windows, macOS and Linux.
