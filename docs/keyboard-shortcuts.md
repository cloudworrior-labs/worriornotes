# Keyboard shortcuts

`Ctrl` on Windows and Linux, `⌘` on macOS. Application shortcuts are registered in one place:
`src/WorriorVex.UI/wwwroot/js/shortcuts.js`.

## Application

| Shortcut | Action |
| --- | --- |
| `Ctrl/⌘ + N` | New note (in the Inbox) |
| `Ctrl/⌘ + Shift + N` | New note from a template |
| `Ctrl/⌘ + F` | Find and replace in the open note |
| `Ctrl/⌘ + Shift + F` | Focus mode: only the note |
| `Ctrl/⌘ + S` | Save now |
| `Ctrl/⌘ + K` | Search |
| `Ctrl/⌘ + \` | Hide or show the sidebar |
| `↑` `↓` `Enter` in the search box | Move through the results, open one |
| `Esc` in the search box | Leave the search |
| `Enter` in the title | Move to the note body |
| `Esc` | Close a dialog or context menu |
| `Enter` / `Backspace` in the tag row | Add a tag / remove the last tag |
| `Enter` in a dialog | Confirm the name |

## Navigation tree and note list

| Shortcut | Action |
| --- | --- |
| `↑` `↓` | Previous / next row |
| `→` | Open a branch, or step into it |
| `←` | Close a branch, or step out to its parent |
| `Home` / `End` | First / last row |
| `Enter` or `Space` | Select the row |
| `Alt + ↑` / `Alt + ↓` | Move the row up or down among its neighbours |
| `F2` or double-click | Rename in place (`Enter` keeps, `Esc` gives up) |
| `Delete` | Move the row to the trash |

## Editor

| Shortcut | Action |
| --- | --- |
| `Ctrl/⌘ + B` / `I` / `U` | Bold / italic / underline |
| `Ctrl/⌘ + Shift + S` | Strikethrough |
| `Ctrl/⌘ + E` | Inline code |
| `Ctrl/⌘ + Alt + 1…3` | Heading 1–3 |
| `Ctrl/⌘ + Shift + 7` / `8` / `9` | Numbered list / bulleted list / checklist |
| `Ctrl/⌘ + Shift + B` | Block quote |
| `Ctrl/⌘ + Alt + C` | Code block |
| `Tab` / `Shift + Tab` in a list | Indent / outdent |
| `Tab` / `Shift + Tab` in a table | Next / previous cell |
| `Ctrl/⌘` + click on a link | Open it: a web page in the browser, a note in the app |
| `Ctrl/⌘ + V` with an image copied | Paste the image into the note |
| `Ctrl/⌘ + Z`, `Ctrl/⌘ + Shift + Z` | Undo, redo |
| `Ctrl/⌘ + Shift + H` | Highlight (marker) |
| `[[` | Link to another note |

The editor shortcuts are TipTap's defaults. Typing `# `, `- `, `1. `, `[ ] `, `> ` or three backticks at
the start of a line also starts the matching block.

## Planned

Focus editor, arrow-key navigation in the tree, command palette.
