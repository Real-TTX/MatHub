/* MatHub – the three editors of a script (Pages/Scripts/Code):
   - the code editor: a real textarea with the highlighting, the line numbers and the current line laid under it; a toolbar (snippets, comment, search, full
     screen) and a status bar; Tab indents, Enter keeps the indent, pairs close themselves, Ctrl+/ comments, Ctrl+D doubles a line, Alt+Up/Down moves it,
     Ctrl+F searches, Ctrl+S saves;
   - the block editor: steps one after the other, each with its fields, an optional condition ("only if") and "go on after an error";
   - the flow editor: steps and branches on a canvas (drag a node by its head, drag from a port to a node to connect it; the properties beside it name the
     next steps too, so it works with the keyboard and on a phone).
   The visual editors keep their model in the hidden field ModelJson; the server compiles it on save. While a person works, /live/scripts/compile shows the
   PowerShell it becomes. Every text a person typed goes into the page as text, never as markup. */
import { t, el, icon, uid, request, debounce } from './util.js';

// ------------------------------------------------------------------------------------------------------------------------- highlighting
const KEYWORDS = {
  powershell: /^(if|else|elseif|foreach|for|while|do|until|switch|function|param|return|break|continue|try|catch|finally|throw|exit|begin|process|end|in|trap|filter|class|enum)$/i,
  cmd: /^(if|else|for|in|do|goto|call|exit|set|setlocal|endlocal|echo|not|exist|defined|errorlevel|equ|neq|lss|leq|gtr|geq|rem|shift|pushd|popd|start)$/i,
  bash: /^(if|then|else|elif|fi|for|while|until|do|done|case|esac|in|function|return|exit|local|export|readonly|declare|set|unset|shift|break|continue|source)$/i,
};

const escapeHtml = text => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

/** The highlighted markup of a text: only spans with the classes of the toolkit around escaped text. */
export function highlight(code, language) {
  const rules = [
    { cls: 'com', re: language === 'cmd' ? /^(?:::|rem\b)[^\n]*/i : /^#[^\n]*/ },
    { cls: 'com', re: language === 'powershell' ? /^<#[\s\S]*?(?:#>|$)/ : /^(?!)/ },
    { cls: 'str', re: /^"(?:[^"\\`]|[\\`].)*"?/ },
    { cls: 'str', re: /^'[^']*'?/ },
    { cls: 'attr', re: language === 'cmd' ? /^%[\w~:-]*%?|^%%?\w/ : /^\$\{[^}]*\}|^\$[\w:?]+/ },
    { cls: 'num', re: /^\b\d+(?:\.\d+)?\b/ },
    { cls: 'tag', re: language === 'powershell' ? /^\b[A-Z][a-z]+-[A-Za-z]+\b/ : /^(?!)/ },
    { cls: 'pun', re: language === 'powershell' ? /^-[a-z]+\b/i : /^(?!)/ },
    { cls: 'word', re: /^[A-Za-z_][\w-]*/ },
  ];
  let out = '';
  let rest = code;
  let atLineStart = true;
  while (rest.length > 0) {
    let matched = false;
    for (const rule of rules) {
      if (rule.cls === 'com' && language === 'cmd' && !atLineStart) continue;
      const m = rule.re.exec(rest);
      if (!m || m[0].length === 0) continue;
      const text = m[0];
      if (rule.cls === 'word') {
        out += KEYWORDS[language]?.test(text) ? `<span class="mat-code__kw">${escapeHtml(text)}</span>` : escapeHtml(text);
      } else {
        out += `<span class="mat-code__${rule.cls}">${escapeHtml(text)}</span>`;
      }
      rest = rest.slice(text.length);
      atLineStart = false;
      matched = true;
      break;
    }
    if (!matched) {
      const ch = rest[0];
      out += escapeHtml(ch);
      rest = rest.slice(1);
      if (ch === '\n') atLineStart = true;
      else if (!/\s/.test(ch)) atLineStart = false;
    }
  }
  // a last empty line has to have a height, or the caret and the highlighting drift apart
  return out + (code.endsWith('\n') ? ' ' : '');
}

// ------------------------------------------------------------------------------------------------------------------------- code editor
/** Pieces of code to start from, per language; "|" marks where the caret goes. */
const SNIPPETS = {
  powershell: [
    ['if', "if (|) {\n    \n}"],
    ['ifelse', "if (|) {\n    \n}\nelse {\n    \n}"],
    ['foreach', "foreach ($item in |) {\n    \n}"],
    ['try', "try {\n    |\n}\ncatch {\n    Write-Error $_\n    exit 1\n}"],
    ['function', "function |Name {\n    param([string]$Value)\n    \n}"],
    ['registry', "Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\|' -Name 'Name' -Value 'Value'"],
    ['service', "Restart-Service -Name '|' -Force"],
    ['file', "if (Test-Path -Path '|') {\n    \n}"],
    ['output', "Write-Output \"|\""],
    ['exit', "exit |0"],
  ],
  cmd: [
    ['if', "if exist \"|\" (\n    \n)"],
    ['for', "for %%f in (|) do (\n    \n)"],
    ['errorlevel', "if %errorlevel% neq 0 exit /b %errorlevel%|"],
    ['output', "echo |"],
    ['exit', "exit /b |0"],
  ],
  bash: [
    ['if', "if [ | ]; then\n    \nfi"],
    ['for', "for item in |; do\n    \ndone"],
    ['function', "|name() {\n    \n}"],
    ['output', "echo \"|\""],
    ['exit', "exit |0"],
  ],
};

const PAIRS = { '(': ')', '[': ']', '{': '}', '"': '"', "'": "'" };

function codeEditor(root) {
  const area = root.querySelector('textarea');
  const language = root.dataset.language || 'powershell';
  const comment = language === 'cmd' ? 'REM ' : '# ';
  const lines = el('pre', { class: 'mh-code__lines', 'aria-hidden': 'true' });
  const code = el('code');
  const layer = el('pre', { class: 'mh-code__highlight mat-code', 'aria-hidden': 'true' }, code);
  const current = el('div', { class: 'mh-code__current', 'aria-hidden': 'true' });
  const body = el('div', { class: 'mh-code__body' });
  area.before(body);
  body.append(lines, current, layer, area);

  // ---- the toolbar: snippets, search, comment, full screen
  const readOnly = area.readOnly;
  const bar = el('div', { class: 'mh-code__bar', role: 'toolbar', 'aria-label': t('Code.Tools') });
  const tool = (iconName, label, onclick, extra = {}) => el('button', { type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--sm mh-code__tool', title: label, 'aria-label': label, onclick, ...extra }, icon(iconName), el('span', { text: label }));
  const snippetSelect = el('select', { class: 'mat-select mh-code__snippets', 'aria-label': t('Code.Insert') },
    el('option', { value: '', text: t('Code.Insert') }),
    ...(SNIPPETS[language] || []).map(([key]) => el('option', { value: key, text: t('Code.Snippet.' + key) })));
  snippetSelect.addEventListener('change', () => {
    const found = (SNIPPETS[language] || []).find(([key]) => key === snippetSelect.value);
    snippetSelect.value = '';
    if (found) insertSnippet(found[1]);
  });
  const languageTag = el('span', { class: 'mat-tag mh-code__lang', text: language === 'powershell' ? 'PowerShell' : language === 'cmd' ? 'cmd' : 'bash' });
  const fullButton = tool('external', t('Code.FullScreen'), () => toggleFull(), { 'aria-pressed': 'false' });
  if (!readOnly) bar.append(snippetSelect, tool('code', t('Code.Comment'), () => toggleComment()));
  bar.append(tool('search', t('Code.Find'), () => openFind()), el('span', { class: 'mh-code__spacer' }), languageTag, fullButton);
  root.prepend(bar);

  // ---- the search bar (Ctrl+F): the next or previous place, the matches counted
  const findInput = el('input', { class: 'mat-input mh-code__find-input', type: 'search', placeholder: t('Code.FindPlaceholder'), 'aria-label': t('Code.Find') });
  const findCount = el('span', { class: 'mh-code__find-count', 'aria-live': 'polite' });
  const findBar = el('div', { class: 'mh-code__find', hidden: '' },
    findInput, findCount,
    el('button', { type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm', 'aria-label': t('Code.Previous'), title: t('Code.Previous'), onclick: () => find(-1) }, icon('chevron-up')),
    el('button', { type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm', 'aria-label': t('Code.Next'), title: t('Code.Next'), onclick: () => find(1) }, icon('chevron-down')),
    el('button', { type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm', 'aria-label': t('Editor.Close'), title: t('Editor.Close'), onclick: () => closeFind() }, icon('x')));
  bar.after(findBar);

  // ---- the status bar: line, column, lines
  const status = el('div', { class: 'mh-code__status', 'aria-live': 'off' });
  root.append(status);

  const lineHeight = () => parseFloat(getComputedStyle(area).lineHeight) || 20;
  const padTop = () => parseFloat(getComputedStyle(area).paddingTop) || 0;

  const caret = () => {
    const before = area.value.slice(0, area.selectionStart);
    const line = before.split('\n').length;
    const column = area.selectionStart - before.lastIndexOf('\n');
    return { line, column };
  };
  const showCaret = () => {
    const { line, column } = caret();
    const count = area.value.split('\n').length;
    const selected = Math.abs(area.selectionEnd - area.selectionStart);
    status.textContent = t('Code.Status', line, column, count) + (selected > 0 ? ' · ' + t('Code.Selected', selected) : '');
    current.style.transform = 'translateY(' + (padTop() + (line - 1) * lineHeight() - area.scrollTop) + 'px)';
    current.style.height = lineHeight() + 'px';
  };

  const render = () => {
    code.innerHTML = highlight(area.value, language);
    const count = area.value.split('\n').length;
    lines.textContent = Array.from({ length: count }, (_, i) => i + 1).join('\n');
    sync();
  };
  const sync = () => {
    layer.scrollTop = area.scrollTop;
    layer.scrollLeft = area.scrollLeft;
    lines.scrollTop = area.scrollTop;
    showCaret();
  };
  const changed = () => { render(); area.dispatchEvent(new Event('change', { bubbles: true })); };

  function lineBounds(from, to) {
    const value = area.value;
    const start = value.lastIndexOf('\n', from - 1) + 1;
    let end = value.indexOf('\n', to > from && value[to - 1] === '\n' ? to - 1 : to);
    if (end < 0) end = value.length;
    return { start, end };
  }

  function insertSnippet(text) {
    if (readOnly) return;
    const { selectionStart: s, value } = area;
    const lineStart = value.lastIndexOf('\n', s - 1) + 1;
    const indent = /^[ \t]*/.exec(value.slice(lineStart, s))[0];
    const placed = text.replace(/\n/g, '\n' + indent);
    const at = placed.indexOf('|');
    area.focus();
    area.setRangeText(placed.replace('|', ''), s, area.selectionEnd, 'start');
    area.selectionStart = area.selectionEnd = s + Math.max(0, at);
    changed();
  }

  function toggleComment() {
    if (readOnly) return;
    const { start, end } = lineBounds(area.selectionStart, area.selectionEnd);
    const block = area.value.slice(start, end);
    const rows = block.split('\n');
    const marker = comment.trim();
    const all = rows.filter(r => r.trim()).every(r => r.trimStart().toUpperCase().startsWith(marker.toUpperCase()));
    const next = rows.map(r => {
      if (!r.trim()) return r;
      const lead = /^\s*/.exec(r)[0];
      if (all) return lead + r.slice(lead.length + marker.length).replace(/^ /, '');
      return lead + comment + r.slice(lead.length);
    }).join('\n');
    area.focus();
    area.setRangeText(next, start, end, 'select');
    changed();
  }

  function duplicateLine() {
    const { start, end } = lineBounds(area.selectionStart, area.selectionEnd);
    const text = area.value.slice(start, end);
    area.setRangeText(text + '\n', start, start, 'preserve');
    area.selectionStart = area.selectionStart + text.length + 1;
    area.selectionEnd = area.selectionEnd + text.length + 1;
    changed();
  }

  function moveLine(direction) {
    const { start, end } = lineBounds(area.selectionStart, area.selectionEnd);
    const value = area.value;
    if (direction < 0 && start === 0) return;
    if (direction > 0 && end >= value.length) return;
    const block = value.slice(start, end);
    const offset = area.selectionStart - start;
    const length = area.selectionEnd - area.selectionStart;
    if (direction < 0) {
      const prevStart = value.lastIndexOf('\n', start - 2) + 1;
      const prev = value.slice(prevStart, start - 1);
      area.setRangeText(block + '\n' + prev, prevStart, end, 'preserve');
      area.selectionStart = prevStart + offset;
    } else {
      let nextEnd = value.indexOf('\n', end + 1);
      if (nextEnd < 0) nextEnd = value.length;
      const next = value.slice(end + 1, nextEnd);
      area.setRangeText(next + '\n' + block, start, nextEnd, 'preserve');
      area.selectionStart = start + next.length + 1 + offset;
    }
    area.selectionEnd = area.selectionStart + length;
    changed();
  }

  // ---- search
  function openFind() {
    findBar.hidden = false;
    const selected = area.value.slice(area.selectionStart, area.selectionEnd);
    if (selected && !selected.includes('\n')) findInput.value = selected;
    findInput.focus();
    findInput.select();
    countMatches();
  }
  function closeFind() { findBar.hidden = true; area.focus(); }
  function countMatches() {
    const term = findInput.value;
    if (!term) { findCount.textContent = ''; return; }
    const hay = area.value.toLowerCase();
    let n = 0;
    for (let i = hay.indexOf(term.toLowerCase()); i >= 0; i = hay.indexOf(term.toLowerCase(), i + term.length)) n++;
    findCount.textContent = n === 0 ? t('Code.NoMatch') : t('Code.Matches', n);
  }
  function find(direction) {
    const term = findInput.value.toLowerCase();
    if (!term) return;
    const hay = area.value.toLowerCase();
    let at = direction > 0 ? hay.indexOf(term, area.selectionEnd) : hay.lastIndexOf(term, Math.max(0, area.selectionStart - 1));
    if (at < 0) at = direction > 0 ? hay.indexOf(term) : hay.lastIndexOf(term);
    if (at < 0) return;
    area.focus();
    area.setSelectionRange(at, at + term.length);
    const line = area.value.slice(0, at).split('\n').length;
    area.scrollTop = Math.max(0, (line - 4) * lineHeight());
    sync();
  }
  findInput.addEventListener('input', countMatches);
  findInput.addEventListener('keydown', e => {
    if (e.key === 'Enter') { e.preventDefault(); find(e.shiftKey ? -1 : 1); }
    else if (e.key === 'Escape') { e.preventDefault(); closeFind(); }
  });

  // ---- full screen (Escape leaves it)
  function toggleFull(force) {
    const on = force ?? !root.classList.contains('is-full');
    root.classList.toggle('is-full', on);
    document.documentElement.classList.toggle('mh-code-full', on);
    fullButton.setAttribute('aria-pressed', String(on));
    area.focus();
    sync();
  }

  area.addEventListener('input', render);
  area.addEventListener('scroll', sync);
  ['keyup', 'click', 'select', 'focus'].forEach(name => area.addEventListener(name, showCaret));
  area.addEventListener('keydown', e => {
    const mod = e.ctrlKey || e.metaKey;
    if (mod && e.key.toLowerCase() === 'f') { e.preventDefault(); openFind(); return; }
    if (e.key === 'F3') { e.preventDefault(); find(e.shiftKey ? -1 : 1); return; }
    if (e.key === 'Escape' && root.classList.contains('is-full')) { e.preventDefault(); toggleFull(false); return; }
    if (readOnly) return;
    const { selectionStart: s, selectionEnd: end, value } = area;
    if (e.key === 'Tab') {
      e.preventDefault();
      const lineStart = value.lastIndexOf('\n', s - 1) + 1;
      if (s === end && !e.shiftKey) {
        area.setRangeText('    ', s, end, 'end');
      } else {
        // indent or outdent every line of the selection
        const block = value.slice(lineStart, end);
        const next = e.shiftKey ? block.replace(/^ {1,4}/gm, '') : block.replace(/^/gm, '    ');
        area.setRangeText(next, lineStart, end, 'select');
      }
      changed();
    } else if (e.key === 'Enter' && !mod) {
      e.preventDefault();
      const lineStart = value.lastIndexOf('\n', s - 1) + 1;
      const indent = /^[ \t]*/.exec(value.slice(lineStart, s))[0];
      const before = value.slice(lineStart, s);
      const opens = /[{(]\s*$/.test(before);
      const closes = opens && /^\s*[})]/.test(value.slice(end));
      if (closes) {
        // between a pair: the caret on an indented line of its own, the closing one under the opening line
        area.setRangeText('\n' + indent + '    \n' + indent, s, end, 'start');
        area.selectionStart = area.selectionEnd = s + 1 + indent.length + 4;
      } else {
        area.setRangeText('\n' + indent + (opens ? '    ' : ''), s, end, 'end');
      }
      changed();
    } else if (e.key === 'Backspace' && s === end && s > 0 && PAIRS[value[s - 1]] !== undefined && PAIRS[value[s - 1]] === value[s]) {
      e.preventDefault();
      area.setRangeText('', s - 1, s + 1, 'end');
      changed();
    } else if (PAIRS[e.key] && !mod && !e.altKey) {
      const close = PAIRS[e.key];
      const quote = e.key === close;
      if (quote && value[s] === e.key && s === end) {
        // typing the closing quote over the one that is there
        e.preventDefault();
        area.selectionStart = area.selectionEnd = s + 1;
        showCaret();
        return;
      }
      if (quote && s === end && /\w/.test(value[s - 1] || '')) return; // an apostrophe in a word
      e.preventDefault();
      const inner = value.slice(s, end);
      area.setRangeText(e.key + inner + close, s, end, 'preserve');
      area.selectionStart = s + 1;
      area.selectionEnd = s + 1 + inner.length;
      changed();
    } else if ((e.key === ')' || e.key === ']' || e.key === '}') && s === end && value[s] === e.key && !mod) {
      e.preventDefault();
      area.selectionStart = area.selectionEnd = s + 1;
      showCaret();
    } else if (mod && (e.key === '/' || e.code === 'Slash' || e.key === '#')) {
      e.preventDefault();
      toggleComment();
    } else if (mod && e.key.toLowerCase() === 'd') {
      e.preventDefault();
      duplicateLine();
    } else if (e.altKey && (e.key === 'ArrowUp' || e.key === 'ArrowDown')) {
      e.preventDefault();
      moveLine(e.key === 'ArrowUp' ? -1 : 1);
    } else if (mod && e.key.toLowerCase() === 's') {
      e.preventDefault();
      area.form?.requestSubmit();
    }
  });
  render();
}

// ------------------------------------------------------------------------------------------------------------------------- shared parts
function readModel(root) {
  const input = root.querySelector('[data-model]');
  try { return JSON.parse(input.value || '{}') || {}; } catch { return {}; }
}

function catalogOf(root) {
  const types = JSON.parse(root.dataset.catalog || '[]');
  return {
    types,
    byId: Object.fromEntries(types.map(x => [x.id, x])),
    categories: JSON.parse(root.dataset.categories || '{}'),
    secrets: JSON.parse(root.dataset.secrets || '[]'),
    fieldKinds: JSON.parse(root.dataset.fieldKinds || '{}'),
  };
}

/** The palette of actions: a search and the areas of the catalog, each one folding (options.open: the areas open at first). Calls back with the id of the action chosen. */
function palette(catalog, extra, onPick, options = {}) {
  const groups = {};
  for (const [key, list] of Object.entries(extra)) groups[key] = list;
  for (const type of catalog.types.filter(x => !x.isCondition)) (groups[type.category] ??= []).push(type);
  const search = el('input', { type: 'search', class: 'mat-input mat-input--sm mh-palette__search', placeholder: t('Editors.SearchActions'), 'aria-label': t('Editors.SearchActions') });
  const none = el('p', { class: 'mat-muted mh-palette__none', text: t('Editors.NoActions'), hidden: true });
  const sections = Object.entries(groups).map(([category, types]) => {
    const items = types.map(type => el('button', {
      type: 'button', class: 'mat-btn mat-btn--sm mh-palette__item ' + (options.buttonClass || 'mat-btn--ghost'), disabled: options.readonly,
      'data-tip': type.hint || null, 'data-search': (type.label + ' ' + (type.hint || '')).toLowerCase(), onclick: () => onPick(type.id),
    }, icon(type.icon), el('span', { text: type.label })));
    const details = el('details', { class: 'mh-palette__group', open: !options.open || options.open.includes(category) ? '' : null },
      el('summary', { class: 'mh-palette__label' }, el('span', { text: catalog.categories[category] || (category === 'flow' ? t('Editors.Flow') : category) }), el('span', { class: 'mh-palette__count', text: String(types.length) })),
      el('div', { class: 'mh-palette__items' }, ...items));
    return { details, items };
  });
  search.addEventListener('input', () => {
    const q = search.value.trim().toLowerCase();
    let shown = 0;
    for (const { details, items } of sections) {
      let any = false;
      for (const item of items) { const hit = !q || item.dataset.search.includes(q); item.hidden = !hit; any ||= hit; if (hit) shown++; }
      details.hidden = !any;
      if (q && any) details.open = true;
    }
    none.hidden = shown > 0;
  });
  return el('div', { class: 'mh-palette' }, search, ...sections.map(s => s.details), none);
}

/** The preview: the PowerShell the model becomes, from the server (it is the one compiler). */
function previewer(root, editor) {
  const pre = root.querySelector('[data-preview] code');
  const list = root.querySelector('[data-preview-errors]');
  let controller = null;
  return debounce(async model => {
    controller?.abort();
    const mine = controller = new AbortController();
    try {
      const result = await request('POST', '/live/scripts/compile', { editor, model }, { signal: mine.signal });
      if (pre) pre.innerHTML = highlight(result.code || '', 'powershell');
      if (list) list.replaceChildren(...(result.errors || []).map(e => el('li', { class: 'mh-preview__error' }, icon('alert'), el('span', { text: e }))));
    } catch {
      // a newer request replaced this one: nothing to say
      if (!mine.signal.aborted && list) list.replaceChildren(el('li', { class: 'mh-preview__error', text: t('Editors.PreviewFailed') }));
    }
  }, 350);
}

/** Fields with words for people (a dialog, a message) are in the font of the page; paths, ids and code in the font of code. */
const PLAIN_FIELDS = ['title', 'text', 'fullname', 'value'];

/** The inputs of a block's fields; every change calls back. */
function fieldInputs(type, values, readonly, onChange, idPrefix, catalog) {
  return type.fields.map(f => {
    const id = idPrefix + '-' + f.name;
    if (values[f.name] === undefined && f.default != null) values[f.name] = f.default;
    let input;
    if (f.kind === 'bool') {
      // a switch: "true" or empty
      input = el('input', { type: 'checkbox', id, disabled: readonly });
      input.checked = values[f.name] === 'true';
      input.addEventListener('change', () => { values[f.name] = input.checked ? 'true' : ''; onChange(); });
      return el('div', { class: 'mat-field mh-field--switch' }, el('label', { class: 'mat-switch' }, input, el('span', { class: 'mat-switch__track' }), el('span', { text: f.label })));
    }
    if (f.kind === 'fields') {
      return el('div', { class: 'mat-field mat-field--full' }, el('span', { class: 'mat-label', text: f.label }), dialogFields(values, f.name, readonly, onChange, catalog));
    }
    if (f.kind === 'secret') {
      // the name of a secret; the value never comes into the page
      const secrets = catalog?.secrets || [];
      input = el('select', { class: 'mat-select', id, disabled: readonly },
        el('option', { value: '', text: t('Editors.ChooseSecret') }),
        ...secrets.map(name => el('option', { value: name, text: name })));
      if (values[f.name] && !secrets.includes(values[f.name])) input.append(el('option', { value: values[f.name], text: values[f.name] + ' (' + t('Editors.SecretMissing') + ')' }));
      input.value = values[f.name] ?? '';
      input.addEventListener('change', () => { values[f.name] = input.value; onChange(); });
      return el('div', { class: 'mat-field' },
        el('label', { class: 'mat-label', for: id }, f.label, f.required ? null : el('span', { class: 'mat-opt', text: ' (' + t('Editors.Optional') + ')' })),
        input,
        el('p', { class: 'mat-hint' }, icon('key'), el('span', { text: secrets.length ? t('Editors.SecretHint') + ' ' : t('Editors.NoSecrets') + ' ' }), el('a', { href: '/secrets', target: '_blank', text: t('Editors.Secrets') })));
    }
    if (f.kind === 'select') {
      input = el('select', { class: 'mat-select', id, disabled: readonly }, (f.options || []).map(o => el('option', { value: o.value, text: o.label })));
      input.value = values[f.name] ?? f.options?.[0]?.value ?? '';
      if (values[f.name] === undefined && f.options?.length) values[f.name] = f.options[0].value;
    } else if (f.kind === 'variable') {
      input = el('input', { class: 'mat-input mat-mono', id, type: 'text', placeholder: f.placeholder || 'answer', readonly, pattern: '[A-Za-z][A-Za-z0-9_]{0,39}', spellcheck: 'false', autocomplete: 'off' });
      input.value = values[f.name] ?? '';
    } else if (f.kind === 'textarea') {
      input = el('textarea', { class: 'mat-textarea' + (PLAIN_FIELDS.includes(f.name) ? '' : ' mat-mono'), id, rows: 4, spellcheck: 'false', placeholder: f.placeholder || '', readonly });
      input.value = values[f.name] ?? '';
    } else {
      input = el('input', { class: 'mat-input' + (f.kind === 'number' || PLAIN_FIELDS.includes(f.name) ? '' : ' mat-mono'), id, type: f.kind === 'number' ? 'number' : 'text', placeholder: f.placeholder || '', readonly, inputmode: f.kind === 'number' ? 'numeric' : null });
      input.value = values[f.name] ?? '';
    }
    input.addEventListener('input', () => { values[f.name] = input.value; onChange(); });
    input.addEventListener('change', () => { values[f.name] = input.value; onChange(); });
    return el('div', { class: 'mat-field' + (f.kind === 'textarea' ? ' mat-field--full' : '') },
      el('label', { class: 'mat-label', for: id }, f.label, f.required ? null : el('span', { class: 'mat-opt', text: ' (' + t('Editors.Optional') + ')' })),
      input);
  });
}

const FIELD_KINDS = ['text', 'password', 'choice', 'check', 'number', 'multiline', 'info', 'page'];

/**
 * The fields of a dialog (a form, a wizard): rows of fields, and rows that start the next page. Kept as JSON in the value of the block; the server reads it
 * (ScriptSteps.Pages) and says what is wrong (a field without a name, a choice without options).
 */
function dialogFields(values, name, readonly, onChange) {
  let entries;
  try { entries = JSON.parse(values[name] || '[]'); } catch { entries = []; }
  if (!Array.isArray(entries)) entries = [];
  const wrap = el('div', { class: 'mh-dfields' });
  const save = () => { values[name] = JSON.stringify(entries); onChange(); };
  const move = (from, to) => { if (to < 0 || to >= entries.length) return; const [x] = entries.splice(from, 1); entries.splice(to, 0, x); save(); render(); };
  const render = () => {
    let page = entries[0]?.kind === 'page' ? 0 : 1;
    const rows = entries.map((entry, index) => {
      entry.kind ||= 'text';
      const isPage = entry.kind === 'page';
      if (isPage) page++;
      const kind = el('select', { class: 'mat-select mat-select--sm', 'aria-label': t('Editors.FieldKind'), disabled: readonly },
        FIELD_KINDS.map(k => el('option', { value: k, text: t('Editors.FieldKind.' + k) })));
      kind.value = entry.kind;
      kind.addEventListener('change', () => { entry.kind = kind.value; save(); render(); });
      const input = (prop, label, mono) => {
        const i = el('input', { class: 'mat-input mat-input--sm' + (mono ? ' mat-mono' : ''), type: 'text', placeholder: label, 'aria-label': label, readonly, spellcheck: mono ? 'false' : null });
        i.value = entry[prop] ?? '';
        i.addEventListener('input', () => { entry[prop] = i.value; save(); });
        return i;
      };
      const parts = [kind];
      if (isPage) {
        parts.push(input('label', t('Editors.PageTitle')), input('default', t('Editors.PageText')));
      } else {
        if (entry.kind !== 'info') parts.push(input('name', t('Editors.FieldName'), true));
        parts.push(input('label', entry.kind === 'info' ? t('Editors.InfoText') : t('Editors.FieldLabel')));
        if (entry.kind === 'choice') parts.push(input('options', t('Editors.FieldOptions')));
        if (entry.kind !== 'info' && entry.kind !== 'password') parts.push(input('default', entry.kind === 'check' ? t('Editors.FieldDefaultCheck') : t('Editors.FieldDefault')));
        if (!['info', 'check'].includes(entry.kind)) {
          const required = el('input', { type: 'checkbox', disabled: readonly });
          required.checked = !!entry.required;
          required.addEventListener('change', () => { entry.required = required.checked; save(); });
          parts.push(el('label', { class: 'mat-check mh-dfields__required' }, required, el('span', { class: 'mat-check__text', text: t('Editors.Required') })));
        }
      }
      const tool = (iconName, label, onclick, disabled) => el('button', { type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm', 'aria-label': label, 'data-tip': label, disabled, onclick }, icon(iconName));
      return el('li', { class: 'mh-dfields__row' + (isPage ? ' mh-dfields__row--page' : '') },
        isPage ? el('span', { class: 'mh-dfields__page', text: t('Editors.PageNumber', page) }) : null,
        el('div', { class: 'mh-dfields__inputs' }, ...parts),
        readonly ? null : el('span', { class: 'mh-dfields__tools' },
          tool('arrow-up', t('Editors.Up'), () => move(index, index - 1), index === 0),
          tool('arrow-down', t('Editors.Down'), () => move(index, index + 1), index === entries.length - 1),
          tool('trash', t('Editors.Remove'), () => { entries.splice(index, 1); save(); render(); })));
    });
    const add = (kind, iconName, label) => el('button', {
      type: 'button', class: 'mat-btn mat-btn--secondary mat-btn--sm',
      onclick: () => { entries.push(kind === 'page' ? { kind: 'page', label: '' } : { kind: 'text', name: 'field' + (entries.filter(e => e.kind !== 'page').length + 1), label: '' }); save(); render(); wrap.querySelector('.mh-dfields__row:last-child input')?.focus(); },
    }, icon(iconName), el('span', { text: label }));
    wrap.replaceChildren(
      entries.length ? el('ol', { class: 'mh-dfields__list' }, ...rows) : el('p', { class: 'mat-muted', text: t('Editors.NoFields') }),
      readonly ? '' : el('div', { class: 'mh-dfields__add' }, add('text', 'plus', t('Editors.AddField')), add('page', 'layers', t('Editors.AddPage'))),
      el('p', { class: 'mat-hint', text: t('Editors.FieldsHint') }));
  };
  render();
  return wrap;
}

/** The editor of a check (the condition of a block, the question of an "if"): which check, its fields, "not". */
function conditionEditor(catalog, condition, readonly, onChange, idPrefix) {
  const checks = catalog.types.filter(x => x.isCondition);
  if (!condition.type) condition.type = checks[0].id;
  condition.values ??= {};
  const wrap = el('div', { class: 'mh-condition' });
  const render = () => {
    const type = catalog.byId[condition.type] || checks[0];
    const select = el('select', { class: 'mat-select', id: idPrefix + '-check', disabled: readonly }, checks.map(c => el('option', { value: c.id, text: c.label })));
    select.value = type.id;
    select.addEventListener('change', () => { condition.type = select.value; condition.values = {}; render(); onChange(); });
    const negate = el('input', { type: 'checkbox', id: idPrefix + '-not', disabled: readonly });
    negate.checked = !!condition.negate;
    negate.addEventListener('change', () => { condition.negate = negate.checked; onChange(); });
    wrap.replaceChildren(
      el('div', { class: 'mat-field' }, el('label', { class: 'mat-label', for: idPrefix + '-check', text: t('Editors.Check') }), select),
      ...fieldInputs(type, condition.values, readonly, onChange, idPrefix + '-c', catalog),
      el('label', { class: 'mat-check' }, negate, el('span', { class: 'mat-check__text', text: t('Editors.Negate') })));
  };
  render();
  return wrap;
}

// ------------------------------------------------------------------------------------------------------------------------- block editor
function blockEditor(root) {
  const catalog = catalogOf(root);
  const readonly = root.dataset.readonly === 'true';
  const model = readModel(root);
  model.blocks ??= [];
  const input = root.querySelector('[data-model]');
  const list = root.querySelector('[data-blocks]');
  const add = root.querySelector('[data-add-block]');
  const preview = previewer(root, 'blocks');
  let dragFrom = -1;

  const changed = () => {
    input.value = JSON.stringify(model);
    preview(model);
  };

  const move = (from, to) => {
    if (to < 0 || to >= model.blocks.length) return;
    const [block] = model.blocks.splice(from, 1);
    model.blocks.splice(to, 0, block);
    render();
    changed();
    list.children[to]?.querySelector('.mh-block__title')?.focus();
  };

  const render = () => {
    list.replaceChildren(...model.blocks.map((block, index) => {
      const type = catalog.byId[block.type];
      const key = uid('b');
      block.values ??= {};
      const item = el('li', { class: 'mh-block mat-card', draggable: readonly ? null : 'true' });
      const head = el('div', { class: 'mh-block__head' },
        el('span', { class: 'mh-block__handle', 'aria-hidden': 'true' }, icon('menu')),
        el('span', { class: 'mh-block__number', text: String(index + 1) }),
        icon(type?.icon || 'alert'),
        el('h3', { class: 'mh-block__title', tabindex: '-1', text: type?.label || block.type }));
      if (!readonly) {
        const tool = (name, label, onclick, disabled) => el('button', { type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm', 'aria-label': label, 'data-tip': label, disabled, onclick }, icon(name));
        head.append(el('span', { class: 'mh-block__tools' },
          tool('arrow-up', t('Editors.Up'), () => move(index, index - 1), index === 0),
          tool('arrow-down', t('Editors.Down'), () => move(index, index + 1), index === model.blocks.length - 1),
          tool('copy', t('Editors.Duplicate'), () => { model.blocks.splice(index + 1, 0, structuredClone(block)); render(); changed(); }),
          tool('trash', t('Editors.Remove'), () => { model.blocks.splice(index, 1); render(); changed(); })));
      }

      const body = el('div', { class: 'mh-block__body' });
      if (type?.hint) body.append(el('p', { class: 'mat-hint', text: type.hint }));
      if (type) body.append(el('div', { class: 'mh-block__fields' }, ...fieldInputs(type, block.values, readonly, changed, key, catalog)));

      // the options of the step: only if …, go on after an error
      const when = el('input', { type: 'checkbox', id: key + '-when', disabled: readonly });
      when.checked = !!block.when;
      const conditionBox = el('div', { class: 'mh-block__when', hidden: !block.when });
      if (block.when) conditionBox.append(conditionEditor(catalog, block.when, readonly, changed, key + '-w'));
      when.addEventListener('change', () => {
        if (when.checked) { block.when = { type: '', values: {}, negate: false }; conditionBox.replaceChildren(conditionEditor(catalog, block.when, readonly, changed, key + '-w')); }
        else { delete block.when; conditionBox.replaceChildren(); }
        conditionBox.hidden = !when.checked;
        changed();
      });
      const goOn = el('input', { type: 'checkbox', id: key + '-goon', disabled: readonly });
      goOn.checked = !!block.continueOnError;
      goOn.addEventListener('change', () => { block.continueOnError = goOn.checked; changed(); });
      body.append(el('div', { class: 'mh-block__options' },
        el('label', { class: 'mat-switch' }, when, el('span', { class: 'mat-switch__track' }), el('span', { text: t('Editors.OnlyIf') })),
        el('label', { class: 'mat-switch' }, goOn, el('span', { class: 'mat-switch__track' }), el('span', { text: t('Editors.ContinueOnError') }))),
      conditionBox);
      item.append(head, body);

      // dragging a step to another place (the arrows do the same without a mouse)
      item.addEventListener('dragstart', e => { if (!e.target.closest('input, textarea, select')) { dragFrom = index; item.classList.add('is-dragging'); e.dataTransfer.effectAllowed = 'move'; } });
      item.addEventListener('dragend', () => item.classList.remove('is-dragging'));
      item.addEventListener('dragover', e => { if (dragFrom >= 0) { e.preventDefault(); item.classList.add('is-over'); } });
      item.addEventListener('dragleave', () => item.classList.remove('is-over'));
      item.addEventListener('drop', e => { e.preventDefault(); item.classList.remove('is-over'); const from = dragFrom; dragFrom = -1; if (from >= 0 && from !== index) move(from, index); });
      return item;
    }));
    if (model.blocks.length === 0) {
      list.append(el('li', { class: 'mh-blocks__empty mat-empty mat-empty--framed' }, el('p', { class: 'mat-empty__title', text: t('Editors.NoBlocks') }), el('p', { class: 'mat-muted', text: t('Editors.NoBlocksText') })));
    }
  };

  // the palette of steps, by area
  if (add && !readonly) {
    add.hidden = false;
    add.append(el('h2', { class: 'mh-blocks__add-title', text: t('Editors.AddStep') }),
      palette(catalog, {}, id => {
        model.blocks.push({ type: id, values: {} });
        render();
        changed();
        list.lastElementChild?.scrollIntoView({ block: 'center', behavior: 'smooth' });
        list.lastElementChild?.querySelector('input, textarea, select')?.focus({ preventScroll: true });
      }, { buttonClass: 'mat-btn--secondary', open: ['software', 'files', 'system'] }));
  }

  render();
  changed();
}

// ------------------------------------------------------------------------------------------------------------------------- flow editor
const NODE_W = 200;
const NODE_H = 64;

function flowEditor(root) {
  const catalog = catalogOf(root);
  const readonly = root.dataset.readonly === 'true';
  const model = readModel(root);
  model.nodes ??= [];
  model.edges ??= [];
  if (!model.nodes.some(n => n.type === 'start')) model.nodes.unshift({ id: 'start', type: 'start', values: {}, x: 60, y: 40 });
  const input = root.querySelector('[data-model]');
  const canvas = root.querySelector('[data-canvas]');
  const paletteBox = root.querySelector('[data-palette]');
  const props = root.querySelector('[data-props]');
  const preview = previewer(root, 'flow');
  const svgNs = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(svgNs, 'svg');
  svg.setAttribute('class', 'mh-flow__edges');
  svg.setAttribute('aria-hidden', 'true');
  const layer = el('div', { class: 'mh-flow__nodes' });
  // the stage holds the edges and the nodes and is scaled; the sizer around it gives the canvas the size to scroll
  const stage = el('div', { class: 'mh-flow__stage' }, svg, layer);
  const sizer = el('div', { class: 'mh-flow__sizer' }, stage);
  canvas.append(sizer);
  let zoom = 1;
  let contentSize = [0, 0];
  const zoomLabel = el('span', { class: 'mh-flow__zoom', 'aria-live': 'polite' });
  const setZoom = value => {
    zoom = Math.min(1.5, Math.max(0.4, Math.round(value * 100) / 100));
    stage.style.transform = 'scale(' + zoom + ')';
    sizer.style.width = contentSize[0] * zoom + 'px';
    sizer.style.height = contentSize[1] * zoom + 'px';
    zoomLabel.textContent = Math.round(zoom * 100) + ' %';
  };
  const fit = () => {
    const xs = model.nodes.map(n => n.x + NODE_W + 40), ys = model.nodes.map(n => n.y + NODE_H + 40);
    const w = Math.max(...xs, 1), h = Math.max(...ys, 1);
    setZoom(Math.min(1, (canvas.clientWidth - 16) / w, (canvas.clientHeight - 16) / h));
    canvas.scrollLeft = 0;
    canvas.scrollTop = 0;
  };
  const zoomTool = (iconName, label, onclick) => el('button', { type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm', title: label, 'aria-label': label, onclick }, icon(iconName));
  const tools = el('div', { class: 'mh-flow__tools', role: 'toolbar', 'aria-label': t('Flow.View') },
    zoomTool('minus', t('Flow.ZoomOut'), () => setZoom(zoom - 0.1)), zoomLabel, zoomTool('plus', t('Flow.ZoomIn'), () => setZoom(zoom + 0.1)),
    el('button', { type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--sm', onclick: () => setZoom(1) }, el('span', { text: '100 %' })),
    el('button', { type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--sm', onclick: fit }, icon('grid'), el('span', { text: t('Flow.Fit') })));
  const column = el('div', { class: 'mh-flow__main' });
  canvas.before(column);
  column.append(tools, canvas);
  canvas.addEventListener('wheel', e => {
    if (!e.ctrlKey) return;
    e.preventDefault();
    setZoom(zoom - Math.sign(e.deltaY) * 0.1);
  }, { passive: false });
  let selected = null;
  let selectedEdge = null;

  const label = node => node.type === 'start' ? t('Editors.Start') : node.type === 'end' ? t('Editors.End') : node.type === 'if' ? t('Editors.If') : (catalog.byId[node.type]?.label || node.type);
  const iconOf = node => node.type === 'start' ? 'play' : node.type === 'end' ? 'stop' : node.type === 'if' ? 'route' : (catalog.byId[node.type]?.icon || 'alert');
  const portsOf = node => node.type === 'end' ? [] : node.type === 'start' ? ['next'] : node.type === 'if' ? ['true', 'false'] : ['next', 'error'];
  const portLabel = port => t('Editors.Port.' + port);

  const changed = () => {
    input.value = JSON.stringify(model);
    preview(model);
  };

  const portPoint = (node, port) => {
    if (port === 'in') return [node.x + NODE_W / 2, node.y];
    if (port === 'error') return [node.x + NODE_W, node.y + NODE_H / 2];
    if (port === 'true') return [node.x + NODE_W * 0.25, node.y + NODE_H];
    if (port === 'false') return [node.x + NODE_W * 0.75, node.y + NODE_H];
    return [node.x + NODE_W / 2, node.y + NODE_H];
  };

  const curve = ([x1, y1], [x2, y2], sideways) => {
    const dy = Math.max(40, Math.abs(y2 - y1) / 2);
    return sideways
      ? `M${x1},${y1} C${x1 + 60},${y1} ${x2},${y2 - dy} ${x2},${y2}`
      : `M${x1},${y1} C${x1},${y1 + dy} ${x2},${y2 - dy} ${x2},${y2}`;
  };

  const drawEdges = (temp) => {
    const parts = [];
    for (const edge of model.edges) {
      const from = model.nodes.find(n => n.id === edge.from);
      const to = model.nodes.find(n => n.id === edge.to);
      if (!from || !to) continue;
      const path = document.createElementNS(svgNs, 'path');
      path.setAttribute('d', curve(portPoint(from, edge.port), portPoint(to, 'in'), edge.port === 'error'));
      path.setAttribute('class', 'mh-flow__edge mh-flow__edge--' + edge.port + (edge === selectedEdge ? ' is-selected' : ''));
      path.addEventListener('click', e => { e.stopPropagation(); selectedEdge = edge; selected = null; render(); });
      parts.push(path);
    }
    if (temp) {
      const path = document.createElementNS(svgNs, 'path');
      path.setAttribute('d', curve(temp.from, temp.to, temp.port === 'error'));
      path.setAttribute('class', 'mh-flow__edge mh-flow__edge--temp');
      parts.push(path);
    }
    svg.replaceChildren(...parts);
    const width = Math.max(canvas.clientWidth / zoom, ...model.nodes.map(n => n.x + NODE_W + 80));
    const height = Math.max(canvas.clientHeight / zoom, 480, ...model.nodes.map(n => n.y + NODE_H + 120));
    svg.setAttribute('width', width);
    svg.setAttribute('height', height);
    layer.style.width = width + 'px';
    layer.style.height = height + 'px';
    stage.style.width = width + 'px';
    stage.style.height = height + 'px';
    contentSize = [width, height];
    sizer.style.width = width * zoom + 'px';
    sizer.style.height = height * zoom + 'px';
  };

  const connect = (from, port, to) => {
    model.edges = model.edges.filter(e => !(e.from === from && e.port === port));
    if (to && to !== from) model.edges.push({ from, to, port });
  };

  const render = () => {
    layer.replaceChildren(...model.nodes.map(node => {
      const box = el('div', { class: 'mh-node mh-node--' + (['start', 'end', 'if'].includes(node.type) ? node.type : 'step') + (node === selected ? ' is-selected' : ''), 'data-node': node.id });
      box.style.left = node.x + 'px';
      box.style.top = node.y + 'px';
      const head = el('button', { type: 'button', class: 'mh-node__head', title: label(node), 'aria-pressed': node === selected ? 'true' : 'false' }, icon(iconOf(node)), el('span', { class: 'mh-node__label', text: label(node) }));
      const summary = node.type === 'if' && node.condition ? (catalog.byId[node.condition.type]?.label || '') : Object.values(node.values || {}).filter(Boolean)[0] || '';
      box.append(head, el('span', { class: 'mh-node__summary', text: node.type === 'end' && summary ? t('Editors.ExitCodeIs', summary) : summary, title: summary || null }));
      if (node.type !== 'start') box.append(el('span', { class: 'mh-node__port mh-node__port--in', 'aria-hidden': 'true' }));
      for (const port of portsOf(node)) {
        const handle = el('span', { class: 'mh-node__port mh-node__port--' + port, title: portLabel(port), 'aria-hidden': 'true' });
        if (!readonly) handle.addEventListener('pointerdown', e => startConnect(e, node, port));
        box.append(handle);
      }

      head.addEventListener('click', () => { selected = node; selectedEdge = null; render(); });
      head.addEventListener('keydown', e => {
        const step = e.shiftKey ? 40 : 10;
        const moves = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] };
        if (moves[e.key] && !readonly) { e.preventDefault(); node.x = Math.max(0, node.x + moves[e.key][0]); node.y = Math.max(0, node.y + moves[e.key][1]); render(); changed(); layer.querySelector(`[data-node="${node.id}"] .mh-node__head`)?.focus(); }
        if ((e.key === 'Delete' || e.key === 'Backspace') && !readonly && node.type !== 'start') { e.preventDefault(); removeNode(node); }
      });
      if (!readonly) head.addEventListener('pointerdown', e => startDrag(e, node, box));
      return box;
    }));
    drawEdges();
    renderProps();
  };

  const startDrag = (e, node, box) => {
    if (e.button !== 0) return;
    const sx = e.clientX, sy = e.clientY, ox = node.x, oy = node.y;
    let moved = false;
    const onMove = ev => {
      const dx = (ev.clientX - sx) / zoom, dy = (ev.clientY - sy) / zoom;
      if (!moved && Math.abs(dx) + Math.abs(dy) < 4) return;
      moved = true;
      node.x = Math.max(0, Math.round((ox + dx) / 10) * 10);
      node.y = Math.max(0, Math.round((oy + dy) / 10) * 10);
      box.style.left = node.x + 'px';
      box.style.top = node.y + 'px';
      drawEdges();
    };
    const onUp = () => { window.removeEventListener('pointermove', onMove); window.removeEventListener('pointerup', onUp); if (moved) changed(); };
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
  };

  const startConnect = (e, node, port) => {
    e.preventDefault();
    e.stopPropagation();
    const rect = canvas.getBoundingClientRect();
    const from = portPoint(node, port);
    const onMove = ev => drawEdges({ from, to: [(ev.clientX - rect.left + canvas.scrollLeft) / zoom, (ev.clientY - rect.top + canvas.scrollTop) / zoom], port });
    const onUp = ev => {
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      const target = document.elementFromPoint(ev.clientX, ev.clientY)?.closest('[data-node]');
      if (target && target.dataset.node !== node.id && target.dataset.node !== 'start') { connect(node.id, port, target.dataset.node); changed(); }
      drawEdges();
      render();
    };
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
  };

  const removeNode = node => {
    model.nodes = model.nodes.filter(n => n !== node);
    model.edges = model.edges.filter(e => e.from !== node.id && e.to !== node.id);
    selected = null;
    render();
    changed();
  };

  const addNode = type => {
    const view = { x: (canvas.scrollLeft + 40) / zoom, y: (canvas.scrollTop + 40) / zoom };
    const last = selected || model.nodes[model.nodes.length - 1];
    const node = { id: uid('n'), type, values: {}, x: last ? last.x : view.x, y: last ? last.y + NODE_H + 50 : view.y };
    if (type === 'if') node.condition = { type: '', values: {}, negate: false };
    // a new step follows the selected one when its next port is free
    if (last && portsOf(last)[0] && !model.edges.some(e => e.from === last.id && e.port === portsOf(last)[0])) connect(last.id, portsOf(last)[0], node.id);
    model.nodes.push(node);
    selected = node;
    render();
    changed();
    layer.querySelector(`[data-node="${node.id}"] .mh-node__head`)?.focus();
  };

  const renderProps = () => {
    if (selectedEdge) {
      props.replaceChildren(el('h2', { class: 'mh-flow__props-title', text: t('Editors.Arrow') }),
        el('p', { class: 'mat-muted', text: portLabel(selectedEdge.port) }),
        readonly ? '' : el('button', { type: 'button', class: 'mat-btn mat-btn--danger mat-btn--sm', onclick: () => { model.edges = model.edges.filter(e => e !== selectedEdge); selectedEdge = null; render(); changed(); } }, icon('trash'), el('span', { text: t('Editors.RemoveArrow') })));
      return;
    }
    if (!selected) {
      props.replaceChildren(el('p', { class: 'mat-muted', text: t('Editors.SelectNode') }));
      return;
    }
    const node = selected;
    const key = 'p-' + node.id;
    const parts = [el('h2', { class: 'mh-flow__props-title' }, icon(iconOf(node)), el('span', { text: label(node) }))];
    const type = catalog.byId[node.type];
    if (type?.hint) parts.push(el('p', { class: 'mat-hint', text: type.hint }));
    if (type && !type.isCondition) parts.push(...fieldInputs(type, node.values ??= {}, readonly, () => { changed(); drawEdges(); }, key, catalog));
    if (node.type === 'if') parts.push(conditionEditor(catalog, node.condition ??= { type: '', values: {} }, readonly, changed, key));
    if (node.type === 'end') {
      node.values ??= {};
      parts.push(...fieldInputs({ fields: [{ name: 'code', kind: 'number', required: false, label: t('Editors.ExitCode'), placeholder: '0' }] }, node.values, readonly, changed, key));
    }

    // the next steps as selects: the same as dragging an arrow, with the keyboard and on a phone
    for (const port of portsOf(node)) {
      const id = key + '-port-' + port;
      const current = model.edges.find(e => e.from === node.id && e.port === port)?.to || '';
      const select = el('select', { class: 'mat-select', id, disabled: readonly },
        el('option', { value: '', text: port === 'error' ? t('Editors.StopOnError') : t('Editors.Finish') }),
        ...model.nodes.filter(n => n.id !== node.id && n.type !== 'start').map(n => el('option', { value: n.id, text: label(n) + (Object.values(n.values || {}).filter(Boolean)[0] ? ' – ' + Object.values(n.values).filter(Boolean)[0] : '') })));
      select.value = current;
      select.addEventListener('change', () => { connect(node.id, port, select.value || null); drawEdges(); changed(); });
      parts.push(el('div', { class: 'mat-field' }, el('label', { class: 'mat-label', for: id, text: portLabel(port) }), select));
    }

    if (!readonly && node.type !== 'start') {
      parts.push(el('button', { type: 'button', class: 'mat-btn mat-btn--danger mat-btn--sm', onclick: () => removeNode(node) }, icon('trash'), el('span', { text: t('Editors.RemoveNode') })));
    }
    props.replaceChildren(...parts);
  };

  // the palette: what can be added (searchable, the areas fold; "if" and "end" first)
  if (paletteBox) {
    paletteBox.append(palette(catalog, { flow: [{ id: 'if', label: t('Editors.If'), icon: 'route', hint: t('Editors.IfHint') }, { id: 'end', label: t('Editors.End'), icon: 'stop', hint: t('Editors.EndHint') }] },
      id => addNode(id), { readonly, open: ['flow', 'software'] }));
  }

  canvas.addEventListener('click', e => { if (e.target === canvas || e.target === layer || e.target === svg || e.target === stage || e.target === sizer) { selected = null; selectedEdge = null; render(); } });
  canvas.addEventListener('keydown', e => {
    if ((e.key === 'Delete' || e.key === 'Backspace') && selectedEdge && !readonly) { model.edges = model.edges.filter(x => x !== selectedEdge); selectedEdge = null; render(); changed(); }
  });
  window.addEventListener('resize', debounce(() => drawEdges(), 150));
  render();
  // the first view shows the whole flow when it is larger than the canvas
  const extent = [Math.max(...model.nodes.map(n => n.x + NODE_W + 40)), Math.max(...model.nodes.map(n => n.y + NODE_H + 40))];
  if (extent[0] > canvas.clientWidth || extent[1] > canvas.clientHeight) fit(); else setZoom(1);
  changed();
}

// ------------------------------------------------------------------------------------------------------------------------- start
document.querySelectorAll('[data-code-editor]').forEach(codeEditor);
document.querySelectorAll('[data-block-editor]').forEach(blockEditor);
document.querySelectorAll('[data-flow-editor]').forEach(flowEditor);

// a person who leaves with changes that are not saved is asked first
const form = document.querySelector('[data-script-form]');
if (form) {
  let dirty = false;
  form.addEventListener('input', () => { dirty = true; });
  form.addEventListener('click', e => { if (e.target.closest('.mh-palette__item, .mh-block__tools, .mh-blocks__add')) dirty = true; });
  form.addEventListener('submit', () => { dirty = false; });
  window.addEventListener('beforeunload', e => { if (dirty) { e.preventDefault(); e.returnValue = ''; } });
}
