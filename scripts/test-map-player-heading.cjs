const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const webRoot = path.resolve(__dirname, '../LauncherGo.ServerMapHost/WebRoot');

async function main() {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    // Expected screen vectors for south, east, north, west, then diagonals.
    const cases = [[0, 0, 1], [Math.PI / 2, 1, 0], [Math.PI, 0, -1], [3 * Math.PI / 2, -1, 0],
      [Math.PI / 4, Math.SQRT1_2, Math.SQRT1_2], [-Math.PI / 4, -Math.SQRT1_2, Math.SQRT1_2]];
    let yaw = 0, version = 1;
    await page.addInitScript(() => { window.EventSource = class extends EventTarget {
      constructor() { super(); window.testEvents = this; } close() {}
    }; });
    await page.route('http://servermap.test/**', async route => {
      const url = new URL(route.request().url());
      const json = value => route.fulfill({ json: value });
      if (url.pathname === '/api/v1/map/metadata') return json({ maxZoom: 12, maxZoomOut: 8, spawn: { x: 0, z: 0 }, colormapReady: true });
      if (url.pathname === '/api/v1/layers/manifest') return json({ layers: [{ id: 'players', visible: true }] });
      if (url.pathname === '/api/v1/layers/players') return json({ type: 'FeatureCollection', version, features: [{ type: 'Feature', id: 'player', geometry: { type: 'Point', coordinates: [0, 0] }, properties: { name: 'Heading test', yaw } }] });
      if (url.pathname === '/api/v1/auth/me') return json({ authenticated: false });
      if (url.pathname === '/api/v1/announcement') return json({ html: '' });
      if (url.pathname === '/api/v1/render-progress') return json({ phase: 'idle' });
      if (url.pathname.startsWith('/api/v1/tiles/')) return route.fulfill({ path: path.join(webRoot, 'assets/sky.png'), contentType: 'image/png' });
      if (url.pathname.startsWith('/api/')) return json([]);
      const file = path.resolve(webRoot, url.pathname === '/' ? 'index.html' : url.pathname.slice(1));
      assert.ok(file.startsWith(webRoot + path.sep));
      return route.fulfill({ body: await fs.readFile(file), contentType: { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png' }[path.extname(file)] });
    });
    const selector = '.leaflet-marker-icon[title="Heading test"] img';
    async function check(x, y) {
      await page.waitForFunction(({ selector, x, y }) => {
        const icon = document.querySelector(selector);
        if (!icon) return false;
        const matrix = new DOMMatrix(getComputedStyle(icon).transform);
        // player.svg points up at zero rotation. Transform its (0,-1) vector.
        return Math.abs(-matrix.c - x) < 0.00001 && Math.abs(-matrix.d - y) < 0.00001;
      }, { selector, x, y });
    }
    // Cover both initial marker creation and the in-place layer update path.
    for (const [angle, x, y] of cases) {
      yaw = angle;
      await page.goto('http://servermap.test/');
      await check(x, y);
    }
    const icon = await page.locator(selector).elementHandle();
    for (const [angle, x, y] of cases) {
      yaw = angle; version++;
      await page.evaluate(version => window.testEvents.dispatchEvent(new MessageEvent('layer', { data: JSON.stringify({ layer: 'players', version }) })), version);
      await check(x, y);
      assert.equal(await icon.evaluate((element, selector) => element === document.querySelector(selector), selector), true, 'Live update should reuse the marker');
    }
    assert.deepEqual(errors, []);
    console.log('PASS player headings: four cardinal directions and diagonals, initial render and live updates');
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
