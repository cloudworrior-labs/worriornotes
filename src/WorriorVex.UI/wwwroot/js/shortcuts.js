// Application-wide keyboard shortcuts, handled in one place.
// See docs/keyboard-shortcuts.md for the list.
export function register(dotNetRef) {
  const onKeyDown = (event) => {
    const modifier = event.metaKey || event.ctrlKey;
    if (!modifier || event.altKey) {
      return;
    }

    const action = event.shiftKey ? SHIFT_SHORTCUTS[event.key.toLowerCase()] : SHORTCUTS[event.key.toLowerCase()];
    if (action) {
      event.preventDefault();
      dotNetRef.invokeMethodAsync('OnShortcut', action);
    }
  };

  // A file dropped outside the editor would otherwise make the window navigate to that file.
  const ignoreDrop = (event) => event.preventDefault();

  // Over a notebook or folder row, the top and bottom quarter mean "before" and "after" it, the
  // rest means "inside". A line or a highlight shows which; the app reads it when the drop comes.
  const onDragOver = (event) => {
    const row = event.target.closest?.('[data-drop-row]');
    if (!row) {
      clearDropZone();
      return;
    }
    const box = row.getBoundingClientRect();
    const y = event.clientY - box.top;
    const edge = Math.max(5, box.height / 4);
    const zone = y < edge ? 'before' : y > box.height - edge ? 'after' : 'into';
    if (row !== dropRow || zone !== dropZone) {
      clearDropZone();
      dropRow = row;
      dropZone = zone;
      row.classList.add(`wn-drop-${zone}`);
    }
  };
  const onDragEnd = () => setTimeout(clearDropZone, 0);

  // Dragging notes and folders is handled by the app; some engines only start a drag that carries data.
  const onDragStart = (event) => {
    if (event.target.closest?.('[draggable="true"]') && event.dataTransfer) {
      event.dataTransfer.setData('text/plain', 'worriorvex-item');
      event.dataTransfer.effectAllowed = 'move';
    }
  };

  window.addEventListener('keydown', onKeyDown);
  window.addEventListener('dragover', ignoreDrop);
  window.addEventListener('drop', ignoreDrop);
  window.addEventListener('dragstart', onDragStart);
  window.addEventListener('dragover', onDragOver);
  window.addEventListener('dragend', onDragEnd);
  window.addEventListener('drop', onDragEnd);
  return {
    dispose: () => {
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('dragover', onDragOver);
      window.removeEventListener('dragend', onDragEnd);
      window.removeEventListener('drop', onDragEnd);
      window.removeEventListener('dragstart', onDragStart);
      window.removeEventListener('dragover', ignoreDrop);
      window.removeEventListener('drop', ignoreDrop);
    },
  };
}

let dropRow = null;
let dropZone = 'into';

function clearDropZone() {
  dropRow?.classList.remove('wn-drop-before', 'wn-drop-after', 'wn-drop-into');
  dropRow = null;
}

/** Where on the row the last drag was: "before", "after" or "into". Read by the app when something is dropped. */
export function lastDropZone() {
  return dropZone;
}

const SHORTCUTS = {
  n: 'newNote',
  s: 'save',
  k: 'search',
  '\\': 'toggleSidebar',
  f: 'find',
};

const SHIFT_SHORTCUTS = {
  n: 'newFromTemplate',
  f: 'focusMode',
};

/**
 * Applies the appearance settings to the page: the theme (system, light or dark) and the size of
 * the note text. Called at start and whenever the settings change.
 */
export function applyAppearance(theme, fontSize, lineHeight, accent, font) {
  const root = document.documentElement;
  if (theme && theme !== 'system') {
    root.dataset.theme = theme;
  } else {
    delete root.dataset.theme;
  }
  if (accent && accent !== 'blue') {
    root.dataset.accent = accent;
  } else {
    delete root.dataset.accent;
  }
  if (font && font !== 'system') {
    root.dataset.font = font;
  } else {
    delete root.dataset.font;
  }
  root.style.setProperty('--wn-prose-size', `${fontSize}px`);
  root.style.setProperty('--wn-prose-line-height', String(lineHeight));
}

/**
 * Makes a pane edge draggable. While dragging, the CSS variable follows the pointer; when the
 * pointer is released, .NET is told the final width so it can be remembered.
 * @param {HTMLElement} handle   the separator element, at the pane's right edge
 * @param {string} paneSelector  the pane whose width changes; its left edge is where measuring starts
 * @param {string} variable      CSS custom property on :root holding the pane width, e.g. "--wn-nav-width"
 * @param {number} min           smallest width in pixels
 * @param {number} max           largest width in pixels
 * @param {object} dotNetRef     .NET object with OnPaneResized(variable, width)
 */
export function attachResizer(handle, paneSelector, variable, min, max, dotNetRef) {
  const root = document.documentElement;
  let width = null;
  let paneLeft = 0;
  const onMove = (event) => {
    width = Math.round(Math.min(max, Math.max(min, event.clientX - paneLeft)));
    root.style.setProperty(variable, `${width}px`);
  };
  const onDown = (event) => {
    if (event.button !== 0) {
      return;
    }
    event.preventDefault();
    // Measured once per drag: the pane's left edge does not move while its right edge is dragged.
    paneLeft = document.querySelector(paneSelector)?.getBoundingClientRect().left ?? 0;
    handle.setPointerCapture(event.pointerId);
    document.body.classList.add('is-resizing');
    handle.addEventListener('pointermove', onMove);
  };
  const onUp = (event) => {
    handle.removeEventListener('pointermove', onMove);
    document.body.classList.remove('is-resizing');
    if (handle.hasPointerCapture?.(event.pointerId)) {
      handle.releasePointerCapture(event.pointerId);
    }
    if (width !== null) {
      dotNetRef.invokeMethodAsync('OnPaneResized', variable, width);
      width = null;
    }
  };
  handle.addEventListener('pointerdown', onDown);
  handle.addEventListener('pointerup', onUp);
  handle.addEventListener('pointercancel', onUp);
  return {
    dispose: () => {
      handle.removeEventListener('pointerdown', onDown);
      handle.removeEventListener('pointerup', onUp);
      handle.removeEventListener('pointercancel', onUp);
      handle.removeEventListener('pointermove', onMove);
    },
  };
}

/** Sets a pane width without dragging (keyboard, or restoring the saved value). */
export function setPaneWidth(variable, width) {
  document.documentElement.style.setProperty(variable, `${width}px`);
}

/**
 * Arrow keys in the navigation tree: up and down move between rows, right opens a branch or steps
 * into it, left closes a branch or steps out to its parent, Home and End jump. Rows are the buttons
 * marked data-tree-item, in document order; branches carry data-expanded.
 */
export function attachTreeKeys(nav) {
  const rows = () => Array.from(nav.querySelectorAll('[data-tree-item]'));
  const onKeyDown = (event) => {
    const target = event.target.closest?.('[data-tree-item]');
    if (!target || event.altKey || event.metaKey || event.ctrlKey) {
      return;
    }
    const all = rows();
    const index = all.indexOf(target);
    const level = Number(target.dataset.level || 0);
    const twisty = target.closest('.wn-navrow')?.querySelector('[data-twisty]');
    let next = null;
    switch (event.key) {
      case 'ArrowDown':
        next = all[index + 1];
        break;
      case 'ArrowUp':
        next = all[index - 1];
        break;
      case 'Home':
        next = all[0];
        break;
      case 'End':
        next = all[all.length - 1];
        break;
      case 'ArrowRight':
        if (target.dataset.expanded === 'false') {
          twisty?.click();
        } else if (target.dataset.expanded === 'true') {
          next = all[index + 1];
        }
        break;
      case 'ArrowLeft':
        if (target.dataset.expanded === 'true') {
          twisty?.click();
        } else {
          for (let i = index - 1; i >= 0; i--) {
            if (Number(all[i].dataset.level || 0) < level) {
              next = all[i];
              break;
            }
          }
        }
        break;
      default:
        return;
    }
    event.preventDefault();
    next?.focus();
  };
  nav.addEventListener('keydown', onKeyDown);
  return { dispose: () => nav.removeEventListener('keydown', onKeyDown) };
}
