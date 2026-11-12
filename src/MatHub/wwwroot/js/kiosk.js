/* MatHub – the preview of an offer of the kiosk (Pages/Kiosk/Edit): the tile as the people see it in the window of the agent, while the form is filled in.
   Only text is set (textContent) and the icon is taken from the sprite by its name. */
(() => {
  const form = document.querySelector('[data-kiosk-form]');
  if (!form) return;
  const name = form.querySelector('[name="Name"]');
  const text = form.querySelector('[name="Description"]');
  const category = form.querySelector('[name="Category"]');
  const out = {
    name: form.querySelector('[data-preview-name]'),
    text: form.querySelector('[data-preview-text]'),
    category: form.querySelector('[data-preview-category]'),
    icon: form.querySelector('[data-preview-icon] use'),
  };
  const fallback = { name: out.name.textContent, text: out.text.textContent, category: out.category.textContent };
  const update = () => {
    out.name.textContent = name.value.trim() || fallback.name;
    out.text.textContent = text.value.trim() || fallback.text;
    out.category.textContent = category.value.trim() || fallback.category;
    const icon = form.querySelector('[name="Icon"]:checked')?.value;
    if (icon && out.icon) {
      const href = out.icon.getAttribute('href') || '';
      out.icon.setAttribute('href', href.replace(/#mat-i-[a-z0-9-]+$/, '#mat-i-' + icon));
    }
  };
  form.addEventListener('input', update);
  form.addEventListener('change', update);
})();
