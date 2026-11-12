/* MatHub – the conditions of a dynamic group (Pages/Groups/Edit): add a row from the template, remove one (the last one stays and is emptied),
   and number the rows before the form is sent (Conditions[0].Field ...), so the server reads them in their order. */
(() => {
  const box = document.querySelector('[data-conditions]');
  if (!box) return;
  const list = box.querySelector('[data-condition-list]');
  const template = box.querySelector('[data-condition-template]');
  const add = box.querySelector('[data-condition-add]');
  const max = Number(box.dataset.max || 10);

  const renumber = () => {
    list.querySelectorAll('[data-condition]').forEach((row, i) => {
      row.querySelectorAll('[data-part]').forEach(input => {
        const part = input.dataset.part.toLowerCase();
        input.name = 'Conditions[' + i + '].' + input.dataset.part;
        input.id = 'cond-' + i + '-' + (part === 'operator' ? 'op' : part);
        const label = input.closest('.mat-field')?.querySelector('label');
        if (label) label.htmlFor = input.id;
      });
    });
    if (add) add.disabled = list.children.length >= max;
  };

  add?.addEventListener('click', () => {
    if (list.children.length >= max) return;
    list.append(template.content.firstElementChild.cloneNode(true));
    renumber();
    list.lastElementChild.querySelector('[data-part="Value"]')?.focus();
  });

  list.addEventListener('click', e => {
    const button = e.target.closest('[data-condition-remove]');
    if (!button) return;
    const row = button.closest('[data-condition]');
    if (list.children.length > 1) {
      row.remove();
      renumber();
      list.querySelector('[data-part="Value"]')?.focus();
    } else {
      row.querySelector('[data-part="Value"]').value = '';
    }
  });

  box.closest('form')?.addEventListener('submit', renumber);
  renumber();
})();
