/**
 * Loading placeholder for a grid: pulsing grey rows shaped like the real ones, shown while the
 * first page is being fetched so the grid never sits empty under its header. The page's own render
 * replaces them (tbody.replaceChildren), and clearGridSkeleton() ends the busy state.
 */

// Widths vary from row to row so the placeholder reads like real data rather than a stripe.
const WIDTHS = {
  id: ['w-20', 'w-24', 'w-16', 'w-24', 'w-20', 'w-16'],
  text: ['w-28', 'w-20', 'w-32', 'w-24', 'w-28', 'w-20'],
  wide: ['w-40', 'w-32', 'w-48', 'w-36', 'w-44', 'w-32'],
  date: ['w-28', 'w-28', 'w-24', 'w-28', 'w-24', 'w-28']
};

function bar(classes) {
  const span = document.createElement('span');
  span.className = `block bg-line/80 ${classes}`;
  return span;
}

/**
 * @param {HTMLTableSectionElement} tbody The grid's body.
 * @param {string[]} shapes One per column: 'id', 'text', 'wide', 'stack' (two lines), 'pill', 'date' or 'menu'.
 * @param {number} [rows]
 */
export function showGridSkeleton(tbody, shapes, rows = 6) {
  const table = tbody.closest('table');
  table?.setAttribute('aria-busy', 'true');

  const rowsHtml = [];
  for (let r = 0; r < rows; r++) {
    const row = document.createElement('tr');
    row.className = 'border-t border-line';
    row.dataset.skeleton = '';
    row.setAttribute('aria-hidden', 'true');

    for (const shape of shapes) {
      const td = document.createElement('td');
      td.className = 'table-cell';
      const wrap = document.createElement('div');
      wrap.className = 'animate-pulse';

      switch (shape) {
        case 'stack':
          wrap.append(bar(`h-3 rounded ${WIDTHS.text[r % 6]}`), bar('mt-2 h-2.5 w-16 rounded'));
          break;
        case 'pill':
          wrap.appendChild(bar(`h-5 rounded-full ${['w-24', 'w-20', 'w-28'][r % 3]}`));
          break;
        case 'menu':
          td.classList.add('w-12');
          wrap.className = 'animate-pulse flex flex-col items-end gap-0.5 pr-2';
          wrap.append(bar('h-1 w-1 rounded-full'), bar('h-1 w-1 rounded-full'), bar('h-1 w-1 rounded-full'));
          break;
        default:
          wrap.appendChild(bar(`h-3 rounded ${(WIDTHS[shape] || WIDTHS.text)[r % 6]}`));
      }

      td.appendChild(wrap);
      row.appendChild(td);
    }

    rowsHtml.push(row);
  }

  tbody.replaceChildren(...rowsHtml);
}

/** Ends the busy state once real rows (or the empty state) are shown. */
export function clearGridSkeleton(tbody) {
  tbody.closest('table')?.removeAttribute('aria-busy');
}
