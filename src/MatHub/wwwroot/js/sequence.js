/* MatHub – the steps of a sequence (Pages/Sequences/Edit): add a step of a kind from its template, move a step up or down, remove it, and number the rows
   before the form is sent (Steps[0].Kind ...), so the server reads them in their order. Works without a mouse: every row has its buttons. */
(() => {
  const box = document.querySelector('[data-steps]');
  if (!box) return;
  const list = box.querySelector('[data-step-list]');
  const empty = box.querySelector('[data-step-empty]');
  const count = box.querySelector('[data-step-count]');
  const max = Number(box.dataset.max || 50);
  const countText = count?.textContent || '';

  const renumber = () => {
    const rows = [...list.querySelectorAll(':scope > [data-step]')];
    rows.forEach((row, i) => {
      row.querySelector('[data-step-number]').textContent = String(i + 1);
      row.querySelectorAll('[data-part]').forEach(input => {
        input.name = 'Steps[' + i + '].' + input.dataset.part;
        if (input.type !== 'hidden') {
          const id = 'step-' + i + '-' + input.dataset.part.toLowerCase();
          const label = input.id ? box.querySelector('label[for="' + input.id + '"]') : null;
          input.id = id;
          if (label) label.htmlFor = id;
        }
      });
      row.querySelector('[data-step-up]')?.toggleAttribute('disabled', i === 0);
      row.querySelector('[data-step-down]')?.toggleAttribute('disabled', i === rows.length - 1);
    });
    if (empty) empty.hidden = rows.length > 0;
    if (count) count.textContent = countText.replace(/\d+/, String(rows.length));
    box.querySelectorAll('[data-step-add]').forEach(b => { b.disabled = rows.length >= max; });
  };

  box.querySelectorAll('[data-step-add]').forEach(button => button.addEventListener('click', () => {
    const template = box.querySelector('[data-step-template="' + button.dataset.stepAdd + '"]');
    if (!template || list.children.length >= max) return;
    const row = template.content.firstElementChild.cloneNode(true);
    list.append(row);
    renumber();
    row.classList.add('is-new');
    row.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    (row.querySelector('select, input:not([type=hidden]):not([type=checkbox])') || row.querySelector('[data-step-remove]'))?.focus({ preventScroll: true });
  }));

  list.addEventListener('click', e => {
    const row = e.target.closest('[data-step]');
    if (!row) return;
    if (e.target.closest('[data-step-remove]')) {
      const next = row.nextElementSibling || row.previousElementSibling;
      row.remove();
      renumber();
      next?.querySelector('[data-step-remove]')?.focus();
    } else if (e.target.closest('[data-step-up]') && row.previousElementSibling) {
      row.previousElementSibling.before(row);
      renumber();
      row.querySelector('[data-step-up]:not([disabled])')?.focus() ?? row.querySelector('[data-step-down]')?.focus();
    } else if (e.target.closest('[data-step-down]') && row.nextElementSibling) {
      row.nextElementSibling.after(row);
      renumber();
      row.querySelector('[data-step-down]:not([disabled])')?.focus() ?? row.querySelector('[data-step-up]')?.focus();
    }
  });

  box.closest('form')?.addEventListener('submit', renumber);
  renumber();
})();
