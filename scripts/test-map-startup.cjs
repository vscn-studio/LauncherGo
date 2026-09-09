const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const webRoot = path.resolve(__dirname, '../LauncherGo.ServerMapHost/WebRoot');

async function main() {
  const browser = await chromium.launch({ headless: true });
  try {
    for (const viewport of [{ width: 1280, height: 800 }, { width: 390, height: 844 }]) {
      for (const query of ['', '?renderer=basic&zoom=0&x=0&z=0', '?renderer=sepia&zoom=3&x=-123&z=456']) {
        const page = await browser.newPage({ viewport });
        let links = [];
        await page.addInitScript(() => { window.EventSource = class extends EventTarget { constructor() { super(); window.testEvents = this; } close() {} }; });
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        await page.route('http://servermap.test/**', async route => {
          const url = new URL(route.request().url());
          const json = value => route.fulfill({ json: value });
          if (url.pathname === '/api/v1/map/metadata') return json({ maxZoom: 12, maxZoomOut: 8, spawn: { x: 511288, z: 512121 }, center: { x: 123, z: 456 }, colormapReady: true, tileVersion: 'test', colorVersion: 'test' });
          if (url.pathname === '/api/v1/layers/manifest') return json({ layers: [{ id: 'translocators', visible: true }] });
          if (url.pathname.startsWith('/api/v1/layers/')) return json({ type: 'FeatureCollection', version: links.length, features: links });
          if (url.pathname === '/api/v1/auth/me') return json({ authenticated: false });
          if (url.pathname === '/api/v1/announcement') return json({ html: '' });
          if (url.pathname === '/api/v1/render-progress') return json({ phase: 'idle' });
          if (url.pathname === '/api/v1/events') return route.fulfill({ contentType: 'text/event-stream', body: ': test\n\n' });
          if (url.pathname.startsWith('/api/v1/tiles/')) return route.fulfill({ path: path.join(webRoot, 'assets/sky.png'), contentType: 'image/png' });
          if (url.pathname.startsWith('/api/')) return json([]);
          const asset = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
          const file = path.resolve(webRoot, asset);
          assert.ok(file.startsWith(webRoot + path.sep));
          let body = await fs.readFile(file);
          if (asset === 'index.html') body = body.toString().replace('  (() => {', '  const originalMap=L.map;L.map=(...args)=>(window.testMap=originalMap(...args));\n  (() => {');
          const contentType = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png' }[path.extname(file)];
          return route.fulfill({ body, contentType });
        });
        await page.goto('http://servermap.test/' + query);
        await page.waitForFunction(() => document.querySelector('#layers li'));
        await page.waitForTimeout(700);
        const expected = new URLSearchParams(query);
        for (let reload = 0; reload < 2; reload++) {
          const state = await page.evaluate(() => ({ query: location.search, center: window.testMap.getCenter(), zoom: window.testMap.getZoom() }));
          const actual = new URLSearchParams(state.query);
          for (const name of ['x', 'z', 'zoom']) assert.equal(actual.get(name), expected.get(name) || '0', `${viewport.width}: ${name} after reload ${reload}`);
          assert.equal(actual.get('renderer'), expected.get('renderer') || 'basic');
          assert.ok(Math.abs(state.center.lng * 4096 - 511288 - Number(expected.get('x') || 0)) < 0.01);
          assert.ok(Math.abs(state.center.lat * 4096 - 512121 - Number(expected.get('z') || 0)) < 0.01);
          assert.equal(state.zoom, 12 - Number(expected.get('zoom') || 0));
          await page.reload();
          await page.waitForFunction(() => document.querySelector('#layers li'));
          await page.waitForTimeout(700);
        }
        const beforeLinks = page.url();
        const link = (id, coordinates) => ({ type: 'Feature', id, geometry: { type: 'LineString', coordinates }, properties: { name: 'Translocator', y: 60, targetY: 80 } });
        links = [link('near', [[511000, 512000], [511100, 512100]])];
        await page.evaluate(() => window.testEvents.dispatchEvent(new MessageEvent('layer', { data: JSON.stringify({ layer: 'translocators', version: 2 }) })));
        await page.waitForFunction(() => document.querySelectorAll('img[src="assets/icons/spiral.svg"]').length === 2);
        // Missing an SSE event during restart must not cache a partial network forever.
        links.push(link('far', [[0, 0], [10000, 10000]]));
        await page.evaluate(() => window.testEvents.dispatchEvent(new Event('open')));
        await page.waitForFunction(() => document.querySelectorAll('img[src="assets/icons/spiral.svg"]').length === 4);
        assert.equal(page.url(), beforeLinks, 'Receiving a remote translocator must not move the camera');
        await page.reload();
        await page.waitForFunction(() => document.querySelectorAll('img[src="assets/icons/spiral.svg"]').length === 4);
        assert.equal(page.url(), beforeLinks, 'Reloading the recovered network must preserve the intended view');
        assert.deepEqual(errors, []);
        console.log(`PASS ${viewport.width}: ${query || 'spawn default'}, reload and translocator updates/reconnect`);
        await page.close();
      }
    }
    console.log('Map startup/reload checks passed.');
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
