/* MatHub – test a script from its editor (Pages/Scripts/Code): what is in the editor (saved or not) runs on the chosen computer, at once or as a trial run
   that changes nothing; the state and the output follow live under it. The computer is remembered by MatHub for the person. */
import { t, request } from './util.js';

const box = document.querySelector('[data-script-test]');
if (box) {
  const client = box.querySelector('[data-test-client]');
  const dry = box.querySelector('[data-test-dry]');
  const run = box.querySelector('[data-test-run]');
  const label = box.querySelector('[data-test-label]');
  const explain = box.querySelector('[data-test-explain]');
  const result = box.querySelector('[data-test-result]');
  const state = box.querySelector('[data-test-state]');
  const wait = box.querySelector('[data-test-wait]');
  const open = box.querySelector('[data-test-open]');
  const output = box.querySelector('[data-test-output]');
  const error = box.querySelector('[data-test-error]');
  let timer = 0;

  const mode = () => {
    label.textContent = dry.checked ? label.dataset.dry : label.dataset.real;
    explain.textContent = dry.checked ? explain.dataset.dry : explain.dataset.real;
    run.classList.toggle('mat-btn--primary', dry.checked);
    run.classList.toggle('mat-btn--danger', !dry.checked);
  };
  dry.addEventListener('change', mode);
  mode();

  const show = s => {
    if (s.done) run.disabled = false;
    state.className = 'mat-pill mat-pill--' + s.tone;
    state.textContent = s.statusText + (s.exitCode != null && s.done ? ' · ' + s.exitCode : '');
    wait.hidden = s.status !== 'queued';
    output.textContent = s.output || '';
    output.hidden = !s.output;
    output.scrollTop = output.scrollHeight;
    open.href = '/jobs/runs/' + s.runId;
  };

  const follow = runId => {
    clearTimeout(timer);
    const tick = async () => {
      try {
        const s = await request('GET', '/live/runs/' + runId);
        show(s);
        if (!s.done) timer = setTimeout(tick, 1500);
      } catch (e) {
        // the run is gone (deleted, or out of reach): nothing more to follow
        if (e.status === 404 || e.status === 403) return;
        timer = setTimeout(tick, 4000);
      }
    };
    tick();
  };

  run.addEventListener('click', async () => {
    error.textContent = '';
    if (!client.value) { error.textContent = t('Editors.ChooseTestClient'); client.focus(); return; }
    const form = document.querySelector('[data-script-form]');
    const code = form?.querySelector('[name=Code]');
    const model = form?.querySelector('[name=ModelJson]');
    run.disabled = true;
    clearTimeout(timer);
    state.className = 'mat-pill mat-pill--plain';
    state.textContent = '…';
    output.textContent = '';
    output.hidden = true;
    try {
      const s = await request('POST', '/live/scripts/' + box.dataset.script + '/test', {
        clientId: Number(client.value),
        dryRun: dry.checked,
        code: box.dataset.editor === 'code' ? code?.value ?? null : null,
        model: box.dataset.editor !== 'code' ? model?.value ?? null : null,
      });
      result.hidden = false;
      show(s);
      follow(s.runId);
      // another test may start while this one waits (a computer that does not take it, another computer): the button is free again
      setTimeout(() => { run.disabled = false; }, 1500);
      result.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    } catch (e) {
      run.disabled = false;
      error.textContent = e.body?.error || t('Editors.TestFailed');
    }
  });
}
