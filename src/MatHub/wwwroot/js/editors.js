/* MatHub – the three editors of a script (Pages/Scripts/Code):
   - the code editor: a real textarea with the highlighting and the line numbers laid under it (Tab indents, Enter keeps the indent, Ctrl+S saves);
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
function codeEditor(root) {
  const area = root.querySelector('textarea');
  const language = root.dataset.language || 'powershell';
  const lines = el('pre', { class: 'mh-code__lines', 'aria-hidden': 'true' });
  const code = el('code');
  const layer = el('pre', { class: 'mh-code__highlight mat-code', 'aria-hidden': 'true' }, code);
  const body = el('div', { class: 'mh-code__body' });
  area.before(body);
  body.append(lines, layer, area);

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
  };

  area.addEventListener('input', render);
  area.addEventListener('scroll', sync);
  area.addEventListener('keydown', e => {
    if (area.readOnly) return;
    if (e.key === 'Tab') {
      e.preventDefault();
      const { selectionStart: s, selectionEnd: end, value } = area;
      const lineStart = value.lastIndexOf('\n', s - 1) + 1;
      if (s === end && !e.shiftKey) {
        area.setRangeText('    ', s, end, 'end');
      } else {
        // indent or outdent every line of the selection
        const block = value.slice(lineStart, end);
        const changed = e.shiftKey ? block.replace(/^ {1,4}/gm, '') : block.replace(/^/gm, '    ');
        area.setRangeText(changed, lineStart, end, 'select');
      }
      render();
    } else if (e.key === 'Enter' && !e.ctrlKey && !e.metaKey) {
      e.preventDefault();
      const { selectionStart: s, value } = area;
      const lineStart = value.lastIndexOf('\n', s - 1) + 1;
      const indent = /^[ \t]*/.exec(value.slice(lineStart, s))[0];
      const opens = /[{(]\s*$/.test(value.slice(lineStart, s));
      area.setRangeText('\n' + indent + (opens ? '    ' : ''), s, area.selectionEnd, 'end');
      render();
    } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
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
  return { types, byId: Object.fromEntries(types.map(x => [x.id, x])), categories: JSON.parse(root.dataset.categories || '{}') };
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

/** The inputs of a block's fields; every change calls back. */
function fieldInputs(type, values, readonly, onChange, idPrefix) {
  return type.fields.map(f => {
    const id = idPrefix + '-' + f.name;
    let input;
    if (f.kind === 'select') {
      input = el('select', { class: 'mat-select', id, disabled: readonly }, (f.options || []).map(o => el('option', { value: o.value, text: o.label })));
      input.value = values[f.name] ?? f.options?.[0]?.value ?? '';
      if (values[f.name] === undefined && f.options?.length) values[f.name] = f.options[0].value;
    } else if (f.kind === 'textarea') {
      input = el('textarea', { class: 'mat-textarea mat-mono', id, rows: 4, spellcheck: 'false', placeholder: f.placeholder || '', readonly });
      input.value = values[f.name] ?? '';
    } else {
      input = el('input', { class: 'mat-input' + (f.kind === 'number' ? '' : ' mat-mono'), id, type: f.kind === 'number' ? 'number' : 'text', placeholder: f.placeholder || '', readonly, inputmode: f.kind === 'number' ? 'numeric' : null });
      input.value = values[f.name] ?? '';
    }
    input.addEventListener('input', () => { values[f.name] = input.value; onChange(); });
    input.addEventListener('change', () => { values[f.name] = input.value; onChange(); });
    return el('div', { class: 'mat-field' + (f.kind === 'textarea' ? ' mat-field--full' : '') },
      el('label', { class: 'mat-label', for: id }, f.label, f.required ? null : el('span', { class: 'mat-opt', text: ' (' + t('Editors.Optional') + ')' })),
      input);
  });
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
      ...fieldInputs(type, condition.values, readonly, onChange, idPrefix + '-c'),
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
      if (type) body.append(el('div', { class: 'mh-block__fields' }, ...fieldInputs(type, block.values, readonly, changed, key)));

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
    const groups = {};
    for (const type of catalog.types.filter(x => !x.isCondition)) (groups[type.category] ??= []).push(type);
    add.append(el('h2', { class: 'mh-blocks__add-title', text: t('Editors.AddStep') }));
    for (const [category, types] of Object.entries(groups)) {
      add.append(el('div', { class: 'mh-palette__group' },
        el('span', { class: 'mh-palette__label', text: catalog.categories[category] || category }),
        el('div', { class: 'mh-palette__items' }, ...types.map(type => el('button', {
          type: 'button', class: 'mat-btn mat-btn--secondary mat-btn--sm', 'data-tip': type.hint || null,
          onclick: () => { model.blocks.push({ type: type.id, values: {} }); render(); changed(); list.lastElementChild?.querySelector('input, textarea, select')?.focus(); },
        }, icon(type.icon), el('span', { text: type.label }))))));
    }
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
  const palette = root.querySelector('[data-palette]');
  const props = root.querySelector('[data-props]');
  const preview = previewer(root, 'flow');
  const svgNs = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(svgNs, 'svg');
  svg.setAttribute('class', 'mh-flow__edges');
  svg.setAttribute('aria-hidden', 'true');
  const layer = el('div', { class: 'mh-flow__nodes' });
  canvas.append(svg, layer);
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
    const width = Math.max(canvas.clientWidth, ...model.nodes.map(n => n.x + NODE_W + 80));
    const height = Math.max(480, ...model.nodes.map(n => n.y + NODE_H + 120));
    svg.setAttribute('width', width);
    svg.setAttribute('height', height);
    layer.style.width = width + 'px';
    layer.style.height = height + 'px';
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
      const head = el('button', { type: 'button', class: 'mh-node__head', 'aria-pressed': node === selected ? 'true' : 'false' }, icon(iconOf(node)), el('span', { class: 'mh-node__label', text: label(node) }));
      const summary = node.type === 'if' && node.condition ? (catalog.byId[node.condition.type]?.label || '') : Object.values(node.values || {}).filter(Boolean)[0] || '';
      box.append(head, el('span', { class: 'mh-node__summary', text: summary }));
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
      const dx = ev.clientX - sx, dy = ev.clientY - sy;
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
    const onMove = ev => drawEdges({ from, to: [ev.clientX - rect.left + canvas.scrollLeft, ev.clientY - rect.top + canvas.scrollTop], port });
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
    const view = { x: canvas.scrollLeft + 40, y: canvas.scrollTop + 40 };
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
    if (type && !type.isCondition) parts.push(...fieldInputs(type, node.values ??= {}, readonly, () => { changed(); drawEdges(); }, key));
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

  // the palette: what can be added
  if (palette) {
    const groups = { flow: [{ id: 'if', label: t('Editors.If'), icon: 'route' }, { id: 'end', label: t('Editors.End'), icon: 'stop' }] };
    for (const type of catalog.types.filter(x => !x.isCondition)) (groups[type.category] ??= []).push(type);
    palette.append(...Object.entries(groups).map(([category, types]) => el('div', { class: 'mh-palette__group' },
      el('span', { class: 'mh-palette__label', text: category === 'flow' ? t('Editors.Flow') : (catalog.categories[category] || category) }),
      el('div', { class: 'mh-palette__items' }, ...types.map(type => el('button', {
        type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--sm mh-palette__item', disabled: readonly, 'data-tip': type.hint || null, onclick: () => addNode(type.id),
      }, icon(type.icon), el('span', { text: type.label })))))));
  }

  canvas.addEventListener('click', e => { if (e.target === canvas || e.target === layer || e.target === svg) { selected = null; selectedEdge = null; render(); } });
  canvas.addEventListener('keydown', e => {
    if ((e.key === 'Delete' || e.key === 'Backspace') && selectedEdge && !readonly) { model.edges = model.edges.filter(x => x !== selectedEdge); selectedEdge = null; render(); changed(); }
  });
  window.addEventListener('resize', debounce(() => drawEdges(), 150));
  render();
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
