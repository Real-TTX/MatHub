/* MatHub – a form whose button has data-busy says that it works (a download of a catalog, an installer can take a while) and is not sent twice. */
document.addEventListener('submit', e => {
  const button = e.target.querySelector('button[data-busy]');
  if (!button) return;
  if (button.dataset.sent) { e.preventDefault(); return; }
  button.dataset.sent = '1';
  button.setAttribute('aria-busy', 'true');
  const label = button.querySelector('span') || button;
  label.textContent = button.dataset.busy;
  setTimeout(() => { button.disabled = true; }, 0);
});
