// Client-side drag rules mirroring the WPF DragOver handlers, so the drop cursor
// updates instantly instead of waiting for a server round trip.
(() => {
  const transparentImage = new Image();
  transparentImage.src = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';

  let pressTarget = null;
  let drag = null; // { kind: 'video' | 'column', column: string }

  window.addEventListener('mousedown', e => { pressTarget = e.target; }, true);

  window.addEventListener('dragstart', e => {
    const source = e.target instanceof Element ? e.target.closest('[data-drag]') : null;
    if (!source) return;

    // Like WPF, a press that starts on a button (Unsubscribe, remove) never starts a drag.
    const pressedButton = pressTarget instanceof Element ? pressTarget.closest('button') : null;
    if (pressedButton && source.contains(pressedButton)) {
      e.preventDefault();
      e.stopImmediatePropagation();
      return;
    }

    const column = source.closest('[data-column]');
    drag = { kind: source.dataset.drag, column: column ? column.dataset.column : null };
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', '');
    // WPF shows only the move cursor while dragging, not a ghost image.
    e.dataTransfer.setDragImage(transparentImage, 0, 0);
  }, true);

  window.addEventListener('dragover', e => {
    const column = e.target instanceof Element ? e.target.closest('[data-column]') : null;
    const allowed = drag && column && (drag.kind === 'column'
      ? column.dataset.important !== 'true'
      : column.dataset.column === drag.column);
    if (allowed) {
      e.preventDefault();
      e.dataTransfer.dropEffect = 'move';
    } else if (e.dataTransfer) {
      e.dataTransfer.dropEffect = 'none';
    }
  }, true);

  window.addEventListener('drop', e => e.preventDefault());
  window.addEventListener('dragend', () => { drag = null; }, true);
})();

// Emulates WPF TextBlock TextTrimming="WordEllipsis" with MaxHeight: lines that fit are kept as
// wrapped; the last visible line holds the rest of its paragraph unwrapped, trimmed at a word
// boundary with an ellipsis when it is too wide. Applies to [data-word-ellipsis] elements.
(() => {
  const ellipsis = '\u2026';
  const cache = new Map();
  let measure;
  let scheduled = false;

  const getMeasure = () => {
    if (!measure) {
      measure = document.createElement('div');
      measure.setAttribute('aria-hidden', 'true');
      measure.style.cssText = 'position:absolute;left:-10000px;top:0;visibility:hidden;contain:layout style;';
      document.body.appendChild(measure);
    }
    return measure;
  };

  const trim = (text, element, width) => {
    const style = getComputedStyle(element);
    const lineHeight = parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.33;
    const maxHeight = parseFloat(style.maxHeight);
    const key = `${width}|${style.font}|${maxHeight}|${text}`;
    if (cache.has(key)) return cache.get(key);

    const box = getMeasure();
    box.style.font = style.font;
    box.style.lineHeight = style.lineHeight;
    box.style.width = `${width}px`;
    box.style.whiteSpace = 'pre-wrap';
    box.style.overflowWrap = 'anywhere';
    const heightOf = value => { box.textContent = value; return box.getBoundingClientRect().height; };

    let result = text;
    const lines = Math.max(1, Math.floor((maxHeight + 0.01) / lineHeight));
    if (heightOf(text) > maxHeight + 0.5) {
      // Find where the last visible line starts: the longest prefix that fits in lines - 1.
      let low = 0, high = text.length;
      const limit = (lines - 1) * lineHeight + 0.5;
      while (low < high) {
        const mid = Math.ceil((low + high) / 2);
        if (lines > 1 && heightOf(text.slice(0, mid)) <= limit) low = mid; else high = mid - 1;
      }
      // A partial word can fit where the whole word wrapped; back up to the real break opportunity.
      if (low > 0 && low < text.length && !/[\s-]/.test(text[low - 1]) && !/\s/.test(text[low])) {
        let breakAt = low;
        while (breakAt > 0 && !/[\s-]/.test(text[breakAt - 1])) breakAt--;
        if (breakAt > 0 && heightOf(text.slice(0, breakAt).replace(/\s+$/, '')) > limit - lineHeight) low = breakAt;
      }
      const head = lines > 1 ? text.slice(0, low) : '';
      const rest = text.slice(head.length);
      const newline = rest.indexOf('\n');
      const line = newline >= 0 ? rest.slice(0, newline) : rest;

      box.style.whiteSpace = 'pre';
      box.style.width = 'auto';
      const widthOf = value => { box.textContent = value; return box.getBoundingClientRect().width; };

      let last = line.replace(/\s+$/, '');
      if (widthOf(last) > width + 0.5) {
        const words = last.split(/(?<=\s)/);
        let count = words.length - 1;
        while (count > 0 && widthOf(words.slice(0, count).join('').replace(/\s+$/, '') + ellipsis) > width + 0.5) count--;
        if (count > 0) {
          last = words.slice(0, count).join('').replace(/\s+$/, '') + ellipsis;
        } else {
          let chars = last.length;
          while (chars > 0 && widthOf(last.slice(0, chars) + ellipsis) > width + 0.5) chars--;
          last = last.slice(0, chars) + ellipsis;
        }
      }
      result = head + last;
      // Keep an empty last visible line's height, as WPF does, since a trailing newline adds no line box.
      if (last === '' && head.endsWith('\n')) result += '\u200B';
    }

    if (cache.size > 5000) cache.clear();
    cache.set(key, result);
    return result;
  };

  const run = () => {
    scheduled = false;
    const elements = [...document.querySelectorAll('[data-word-ellipsis]')];
    const widths = elements.map(e => e.clientWidth);
    const results = elements.map((e, i) => widths[i] > 0 ? trim(e.dataset.wordEllipsis, e, widths[i]) : null);
    elements.forEach((e, i) => {
      if (results[i] !== null && e.textContent !== results[i]) e.textContent = results[i];
    });
  };

  const schedule = () => {
    if (scheduled) return;
    scheduled = true;
    requestAnimationFrame(run);
  };

  const start = () => {
    new MutationObserver(mutations => {
      // Ignore the text this script writes into the trimmed elements themselves.
      if (mutations.some(m => m.target !== measure && !(m.type === 'childList' && m.target instanceof Element && m.target.hasAttribute('data-word-ellipsis')))) schedule();
    }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-word-ellipsis', 'class', 'style'] });
    window.addEventListener('resize', schedule);
    schedule();
  };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
})();
