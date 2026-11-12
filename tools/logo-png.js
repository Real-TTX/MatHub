#!/usr/bin/env node
// Renders the PNG icons of MatHub from its SVG logo with the installed Edge (Playwright): the favicon (32), the Apple touch icon (180), the PWA icons (192, 512),
// the maskable icon (512), the mark for the README (512) and the icon of the agent (mathub-agent.ico: 16 to 256 px, PNG inside).
//   node tools/logo-png.js            (needs playwright-core: set PLAYWRIGHT_CORE to its folder, or run it from a folder that has it)
const fs = require('fs');
const path = require('path');
const { chromium } = require(process.env.PLAYWRIGHT_CORE || 'playwright-core');

const root = path.join(__dirname, '..');
const logo = name => fs.readFileSync(path.join(root, 'src', 'MatHub', 'wwwroot', 'img', 'logo', name), 'utf8');
const jobs = [
  ['mathub-favicon.svg', 32, 'src/MatHub/wwwroot/img/favicon-32.png'],
  ['mathub-mark.svg', 180, 'src/MatHub/wwwroot/img/apple-touch-icon.png', true],
  ['mathub-mark.svg', 192, 'src/MatHub/wwwroot/img/icon-192.png'],
  ['mathub-mark.svg', 512, 'src/MatHub/wwwroot/img/icon-512.png'],
  ['mathub-maskable.svg', 512, 'src/MatHub/wwwroot/img/icon-maskable-512.png'],
  ['mathub-mark.svg', 512, 'docs/logo/mathub-mark-512.png'],
];

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const page = await browser.newPage();
  for (const [name, size, target, square] of jobs) {
    // the Apple touch icon has no transparency: iOS rounds the corners itself, so the background fills the square
    let svg = logo(name).replace('<svg ', `<svg width="${size}" height="${size}" `);
    if (square) {
      svg = svg.replace(/<rect width="64" height="64" rx="14"/, '<rect width="64" height="64"');
    }

    await page.setViewportSize({ width: size, height: size });
    await page.setContent(`<!DOCTYPE html><html><body style="margin:0;background:transparent">${svg}</body></html>`);
    await page.locator('svg').first().screenshot({ path: path.join(root, target), omitBackground: !square });
    console.log(`${target} (${size} px)`);
  }

  // the icon of the agent's program, its kiosk and its symbol beside the clock: an ICO with a PNG per size (the small sizes from the favicon, drawn for them)
  const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
  const images = [];
  for (const size of sizes) {
    const svg = logo(size <= 32 ? 'mathub-favicon.svg' : 'mathub-mark.svg').replace('<svg ', `<svg width="${size}" height="${size}" `);
    await page.setViewportSize({ width: size, height: size });
    await page.setContent(`<!DOCTYPE html><html><body style="margin:0;background:transparent">${svg}</body></html>`);
    images.push(await page.locator('svg').first().screenshot({ omitBackground: true }));
  }

  const header = Buffer.alloc(6 + 16 * sizes.length);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(sizes.length, 4);
  let offset = header.length;
  sizes.forEach((size, i) => {
    const at = 6 + 16 * i;
    header.writeUInt8(size >= 256 ? 0 : size, at);
    header.writeUInt8(size >= 256 ? 0 : size, at + 1);
    header.writeUInt16LE(1, at + 4);
    header.writeUInt16LE(32, at + 6);
    header.writeUInt32LE(images[i].length, at + 8);
    header.writeUInt32LE(offset, at + 12);
    offset += images[i].length;
  });
  fs.writeFileSync(path.join(root, 'src', 'MatHub.Agent', 'mathub-agent.ico'), Buffer.concat([header, ...images]));
  console.log('src/MatHub.Agent/mathub-agent.ico (' + sizes.join(', ') + ' px)');

  await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
