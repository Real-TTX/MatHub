#!/usr/bin/env node
// Writes the logo of MatHub in its levels of detail (Guideline Logo): the mark (64), the word mark for light and dark surfaces, the favicon (32, fewer details,
// thicker lines), the maskable icon (full bleed, the mark in the safe zone), a one-colour mark, and the glyph of the app sprite (24, lines like the toolkit's).
//   node tools/logo.js            -> docs/logo/*.svg, src/MatHub/wwwroot/img/logo/*.svg, src/MatHub/wwwroot/favicon.svg, src/MatHub/wwwroot/img/app-icons.svg
// The PNG sizes are made from these files by tools/logo-png.js (Edge through Playwright).
// The idea: a hub in the middle and four clients around it, joined by spokes – one place that manages many computers.
const fs = require('fs');
const path = require('path');

const ACCENT = '#B45309';
const DARK = '#7C3A06';
const TINT = '#FCE3C4';
const DARK_TEXT = '#1F1E1B';
const LIGHT_TEXT = '#F3F2EE';
const DARK_ACCENT_TEXT = '#F4B66F';

// the glyph on a 64 grid: four client screens at the corners, spokes to the hub in the middle
function glyph({ detail }) {
  const tiles = [[9, 10], [41, 10], [9, 43], [41, 43]];
  const spoke = detail ? 3.5 : 5;
  let s = `<path d="M23 21 32 32M41 21 32 32M23 43 32 32M41 43 32 32" stroke="#fff" stroke-width="${spoke}" stroke-linecap="round"/>`;
  for (const [x, y] of tiles) {
    s += `<rect x="${x}" y="${y}" width="14" height="11" rx="2.5" fill="#fff"/>`;
    if (detail) {
      s += `<rect x="${x + 2.5}" y="${y + 2.5}" width="9" height="6" rx="1" fill="${TINT}"/>`;
    }
  }
  s += `<circle cx="32" cy="32" r="${detail ? 8 : 9}" fill="#fff"/>`;
  if (detail) {
    s += `<circle cx="32" cy="32" r="3.2" fill="${ACCENT}"/>`;
  }
  return s;
}

const mark = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="MatHub">
  <rect width="64" height="64" rx="14" fill="${ACCENT}"/>
  ${glyph({ detail: true })}
</svg>
`;

const favicon = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="MatHub">
  <rect width="64" height="64" rx="14" fill="${ACCENT}"/>
  ${glyph({ detail: false })}
</svg>
`;

const maskable = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="MatHub">
  <rect width="64" height="64" fill="${ACCENT}"/>
  <g transform="translate(9.6 9.6) scale(0.7)">${glyph({ detail: true })}</g>
</svg>
`;

const mono = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="MatHub" fill="currentColor">
  <path d="M23 21 32 32M41 21 32 32M23 43 32 32M41 43 32 32" stroke="currentColor" stroke-width="4" stroke-linecap="round"/>
  <rect x="9" y="10" width="14" height="11" rx="2.5"/><rect x="41" y="10" width="14" height="11" rx="2.5"/>
  <rect x="9" y="43" width="14" height="11" rx="2.5"/><rect x="41" y="43" width="14" height="11" rx="2.5"/>
  <path fill-rule="evenodd" d="M32 23a9 9 0 1 1 0 18 9 9 0 0 1 0-18zm0 5.5a3.5 3.5 0 1 0 0 7 3.5 3.5 0 0 0 0-7z"/>
</svg>
`;

function wordmark(text, accentText) {
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 150 40" width="150" height="40" role="img" aria-label="MatHub">
  <rect x="2" y="2" width="36" height="36" rx="8" fill="${ACCENT}"/><g transform="translate(2 2) scale(0.5625)">${glyph({ detail: true })}</g>
  <text x="48" y="28.5" font-family="'Segoe UI','Helvetica Neue',Arial,sans-serif" font-size="22" font-weight="700" letter-spacing="-0.4" fill="${text}">Mat<tspan fill="${accentText}">Hub</tspan></text>
</svg>
`;
}

// the glyph of the sprite: 24 x 24, lines of 2 with round ends like the toolkit's icons (fill none, stroke currentColor through .mat-icon)
const sprite = `<svg xmlns="http://www.w3.org/2000/svg" class="mat-sprite" width="0" height="0" aria-hidden="true" focusable="false">
  <!-- The icons only MatHub has, drawn like the toolkit's (24 x 24, a line of 2, round ends; the colour is the text colour). -->
  <symbol id="mat-i-mathub" viewBox="0 0 24 24"><rect x='2.5' y='3' width='6' height='4.5' rx='1'/><rect x='15.5' y='3' width='6' height='4.5' rx='1'/><rect x='2.5' y='16.5' width='6' height='4.5' rx='1'/><rect x='15.5' y='16.5' width='6' height='4.5' rx='1'/><circle cx='12' cy='12' r='3'/><path d='M8.5 7.5 10 9.8M15.5 7.5 14 9.8M8.5 16.5 10 14.2M15.5 16.5 14 14.2'/></symbol>
  <symbol id="mat-i-power" viewBox="0 0 24 24"><path d='M12 3v8'/><path d='M6.3 6.8a8 8 0 1 0 11.4 0'/></symbol>
  <symbol id="mat-i-blocks" viewBox="0 0 24 24"><rect x='4' y='3.5' width='16' height='4.5' rx='1.2'/><rect x='4' y='10' width='16' height='4.5' rx='1.2'/><rect x='4' y='16.5' width='10' height='4.5' rx='1.2'/></symbol>
  <symbol id="mat-i-flow" viewBox="0 0 24 24"><rect x='9' y='2.5' width='6' height='4.5' rx='1'/><path d='M12 7v3'/><path d='M12 10l4 3.5-4 3.5-4-3.5z'/><path d='M8 13.5H5v4M16 13.5h3v4'/><rect x='2.5' y='17.5' width='5' height='4' rx='1'/><rect x='16.5' y='17.5' width='5' height='4' rx='1'/></symbol>
  <symbol id="mat-i-windows" viewBox="0 0 24 24"><path d='M3.5 5.5 10.5 4.5v7h-7zM13 4.2l7.5-1v8.3H13zM3.5 13h7v6.5l-7-1zM13 13h7.5v8l-7.5-1z'/></symbol>
  <symbol id="mat-i-cpu" viewBox="0 0 24 24"><rect x='6' y='6' width='12' height='12' rx='1.5'/><rect x='9.5' y='9.5' width='5' height='5' rx='.5'/><path d='M9.5 2.5V6M14.5 2.5V6M9.5 18v3.5M14.5 18v3.5M2.5 9.5H6M2.5 14.5H6M18 9.5h3.5M18 14.5h3.5'/></symbol>
  <symbol id="mat-i-printer" viewBox="0 0 24 24"><path d='M6.5 9V3.5h11V9'/><rect x='3' y='9' width='18' height='8' rx='2'/><path d='M6.5 14h11v6.5h-11z'/><path d='M17.5 12h.01'/></symbol>
  <symbol id="mat-i-wifi" viewBox="0 0 24 24"><path d='M2.5 9a14 14 0 0 1 19 0'/><path d='M5.5 12.5a9.5 9.5 0 0 1 13 0'/><path d='M8.5 16a5 5 0 0 1 7 0'/><circle cx='12' cy='19.5' r='.9'/></symbol>
  <symbol id="mat-i-linux" viewBox="0 0 24 24"><path d='M12 3c-2.2 0-3.5 1.8-3.5 4.2 0 1.6.4 2.5-.8 4.3C6.4 13.4 5 15.5 5.8 18c.7 2 3 3 6.2 3s5.5-1 6.2-3c.8-2.5-.6-4.6-1.9-6.5-1.2-1.8-.8-2.7-.8-4.3C15.5 4.8 14.2 3 12 3z'/><circle cx='10.5' cy='8' r='.6'/><circle cx='13.5' cy='8' r='.6'/></symbol>
</svg>
`;

const root = path.join(__dirname, '..');
const targets = [path.join(root, 'docs', 'logo'), path.join(root, 'src', 'MatHub', 'wwwroot', 'img', 'logo')];
const files = {
  'mathub-mark.svg': mark,
  'mathub-favicon.svg': favicon,
  'mathub-maskable.svg': maskable,
  'mathub-mono.svg': mono,
  'mathub-logo.svg': wordmark(DARK_TEXT, ACCENT),
  'mathub-logo-dark.svg': wordmark(LIGHT_TEXT, DARK_ACCENT_TEXT),
};
for (const dir of targets) {
  fs.mkdirSync(dir, { recursive: true });
  for (const [name, content] of Object.entries(files)) {
    fs.writeFileSync(path.join(dir, name), content);
  }
}
fs.writeFileSync(path.join(root, 'src', 'MatHub', 'wwwroot', 'favicon.svg'), favicon);
fs.writeFileSync(path.join(root, 'src', 'MatHub', 'wwwroot', 'img', 'app-icons.svg'), sprite);
console.log('MatHub logo written (accent ' + ACCENT + ', dark ' + DARK + ').');
