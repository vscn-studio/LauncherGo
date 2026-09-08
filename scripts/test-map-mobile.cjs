// Run with Node and Playwright (or set PLAYWRIGHT_MODULE to an existing installation).
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const webRoot = path.resolve(__dirname, '../LauncherGo.ServerMapHost/WebRoot');
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const point = (id, x, z, properties) => ({ type: 'Feature', id, geometry: { type: 'Point', coordinates: [x, z] }, properties });

async function main() {
  const browser = await chromium.launch({ headless: true });
  try {
    for (const viewport of [{ width: 360, height: 640 }, { width: 390, height: 844 }, { width: 844, height: 390 }, { width: 1280, height: 800 }]) {
      const mobile = viewport.width !== 1280;
      const page = await browser.newPage({ viewport, isMobile: mobile, hasTouch: mobile, locale: 'zh-CN' });
      const errors = [], counts = {}; let playerX = 20, responseDelay = 0;
      page.on('pageerror', error => errors.push(error.message));
      await page.addInitScript(() => {
        window.EventSource = class extends EventTarget {
          constructor() { super(); window.testEvents = this; }
        };
        localStorage.setItem('servermap-pinned', 'pinned');
      });
      await page.route('http://servermap.test/**', async route => {
        const url = new URL(route.request().url()), name = url.pathname;
        counts[name] = (counts[name] || 0) + 1;
        const json = value => route.fulfill({ json: value });
        if (name.endsWith('/map/metadata')) return json({ maxZoom: 12, maxZoomOut: 12, spawn: { x: 0, z: 0 }, center: { x: 0, z: 0 }, updatedAt: '2026-09-08', serverMapVersion: 'test', tileVersion: 'test', colormapReady: true });
        if (name.endsWith('/layers/manifest')) return json({ layers: ['players', 'pois'].map(id => ({ id, visible: true })) });
        if (name.endsWith('/layers/players')) {
          const feature = point('player', playerX, 30, { name: 'Player', y: 110, yaw: 0 });
          if (responseDelay) await delay(responseDelay);
          return json({ features: [feature] });
        }
        if (name.endsWith('/layers/pois')) return json({ features: [point('poi', -40, -40, { name: '地点标记', editable: true })] });
        if (name.endsWith('/auth/me')) return json({ authenticated: true, admin: true });
        if (name.endsWith('/announcement')) return json({ html: '<h3>服务器公告</h3><p>Mobile layout test</p>' });
        if (['/api/v1/my-waypoints','/api/v1/routes','/api/v1/hidden-regions'].includes(name)) return json([]);
        if (name.endsWith('/render-progress')) return json({phase:'idle',queued:0});
        if (name.includes('/tiles/')) return route.fulfill({ path: path.join(webRoot, 'assets/sky.png'), contentType: 'image/png' });
        const file = path.resolve(webRoot, name === '/' ? 'index.html' : name.slice(1));
        assert.ok(file.startsWith(webRoot + path.sep));
        let body = await fs.readFile(file);
        if (name === '/') body = body.toString().replace('  (() => {', `
          const originalMap = L.map; L.map = (...args) => (window.testMap = originalMap(...args));
          const originalUpdate = L.TileLayer.prototype._update;
          window.tileUpdates = 0;
          L.TileLayer.prototype._update = function(...args) { window.tileUpdates++; return originalUpdate.apply(this, args); };
          (() => {`).replace('    const events=new EventSource', '    window.testCacheSizes=()=>({versions:tileVersions.size,pending:pendingTiles.size});\n    const events=new EventSource');
        return route.fulfill({ body, contentType: { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png' }[path.extname(file)] || 'text/plain' });
      });
      const emit = (type, data, times = 1) => page.evaluate(({ type, data, times }) => {
        for (let i = 0; i < times; i++) window.testEvents.dispatchEvent(new MessageEvent(type, { data: JSON.stringify(data) }));
      }, { type, data, times });
      await page.goto('http://servermap.test/');
      await page.locator('.poi-label').waitFor();
      await page.waitForFunction(() => document.querySelector('.player-item'));
      if (mobile) {
        assert.equal(await page.locator('#sidebar').evaluate(el => getComputedStyle(el).visibility), 'hidden');
        assert.equal(await page.locator('#topTools').isVisible(), false);
        assert.equal(await page.locator('#announcement').isVisible(), false);
        await page.locator('#mobileMenu').tap();
        assert.equal(await page.locator('#sidebar').evaluate(el => el.inert), false);
        await page.locator('#manageButton').tap();
        assert.equal(await page.locator('#manageModal').isVisible(), true);
        await page.locator('[data-close-modal="manageModal"]').tap();
        await page.locator('#mobileSearch').tap();
        assert.equal(await page.locator('#sidebar').evaluate(el => el.inert), true);
        assert.equal(await page.locator('#topTools').isVisible(), true);
        assert.equal(await page.locator('#jumpX').evaluate(el => getComputedStyle(el).fontSize), '16px');
        await page.locator('#mobileAnnouncement').tap();
        assert.equal(await page.locator('#topTools').isVisible(), false);
        assert.equal(await page.locator('#announcement').isVisible(), true);
        await page.locator('#mobilePoint').tap();
        const box = await page.locator('#contextMenu').boundingBox();
        assert.ok(box.x >= 0 && box.y >= 0 && box.x + box.width <= viewport.width && box.y + box.height <= viewport.height);
        await page.locator('#contextMenu [data-action="poi"]').tap();
        const modal = await page.locator('#poiForm').boundingBox();
        assert.ok(modal.y >= 0 && modal.y + modal.height <= viewport.height);
        await page.locator('[data-close-modal="poiModal"]').tap();
        await page.locator('.poi-label').tap();
        await page.locator('.edit-poi').tap();
        assert.equal(await page.locator('#poiNameInput').inputValue(), '地点标记');
        await page.locator('[data-close-modal="poiModal"]').tap();
        assert.equal(await page.locator('.player-tooltip').count(), 0);
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      } else {
        assert.equal(await page.locator('#mobileTools').isVisible(), false);
        assert.equal(await page.locator('#sidebar').evaluate(el => el.classList.contains('show')), true);
      }
      // Unchanged and moving players reuse marker and sidebar DOM nodes.
      await page.evaluate(() => { window.oldPlayer = document.querySelector('#map img[src="assets/icons/player.svg"]'); window.oldRow = document.querySelector('.player-item'); });
      const before = counts['/api/v1/layers/players'];
      await emit('layer', { layer: 'players' }, 500);
      await page.waitForTimeout(1000);
      assert.ok(counts['/api/v1/layers/players'] - before <= 2, 'Layer burst not coalesced');
      playerX = 70;
      await emit('layer', { layer: 'players' });
      await page.waitForTimeout(650);
      assert.equal(await page.evaluate(() => window.oldPlayer === document.querySelector('#map img[src="assets/icons/player.svg"]') && window.oldRow === document.querySelector('.player-item')), true);
      // A slow response followed by a newer invalidation must eventually fetch the latest state.
      responseDelay = 650; playerX = 90;
      const slowBefore = counts['/api/v1/layers/players'];
      await emit('layer', { layer: 'players' });
      while (counts['/api/v1/layers/players'] === slowBefore) await page.waitForTimeout(50);
      playerX = 110; await emit('layer', { layer: 'players' }, 20);
      await page.waitForTimeout(2200); responseDelay = 0;
      assert.ok(counts['/api/v1/layers/players'] - slowBefore >= 2, 'Invalidation lost during active request');
      await page.evaluate(() => document.querySelector('.player-item').click());
      await page.waitForFunction(() => new URL(location.href).searchParams.get('x') === '110');
      await page.waitForTimeout(700);
      const updates = await page.evaluate(() => window.tileUpdates);
      await emit('tile', { renderer: 'basic', zoom: 0, x: 0, z: 0 }, 1000);
      await page.waitForTimeout(950);
      assert.equal(await page.evaluate(() => window.tileUpdates) - updates, 1, 'Tile burst must cause exactly one update');
      await page.evaluate(() => {
        for (let i = 100; i < 10100; i++) window.testEvents.dispatchEvent(new MessageEvent('tile', { data: JSON.stringify({ renderer: 'basic', zoom: 0, x: i, z: i }) }));
      });
      const cache = await page.evaluate(() => window.testCacheSizes());
      assert.ok(cache.versions <= 2048 && cache.pending <= 256, 'Unbounded tile history');
      // Gesture deferral and hidden-tab deferral use the same production events/predicate.
      for (const hidden of [false, true]) {
        await page.evaluate(hidden => {
          if (hidden) { Object.defineProperty(document, 'hidden', { configurable: true, value: true }); document.dispatchEvent(new Event('visibilitychange')); }
          else window.testMap.fire('movestart');
        }, hidden);
        await page.waitForTimeout(100);
        const pausedCount = counts['/api/v1/layers/players'], pausedUpdates = await page.evaluate(() => window.tileUpdates);
        await emit('layer', { layer: 'players' }, 100);
        await emit('tile', { renderer: 'basic', zoom: 0, x: 0, z: 0 }, 100);
        await page.waitForTimeout(1100);
        assert.equal(counts['/api/v1/layers/players'], pausedCount);
        assert.equal(await page.evaluate(() => window.tileUpdates), pausedUpdates);
        await page.evaluate(hidden => {
          if (hidden) { Object.defineProperty(document, 'hidden', { configurable: true, value: false }); document.dispatchEvent(new Event('visibilitychange')); }
          else window.testMap.fire('moveend');
        }, hidden);
        await page.waitForTimeout(1100);
        assert.ok(counts['/api/v1/layers/players'] > pausedCount);
      }
      if (process.env.MAP_SCREENSHOTS) {
        await fs.mkdir(process.env.MAP_SCREENSHOTS, { recursive: true });
        await page.screenshot({ path: path.join(process.env.MAP_SCREENSHOTS, `mobile-${viewport.width}x${viewport.height}.png`) });
      }
      assert.deepEqual(errors, []);
      console.log(`PASS ${viewport.width}x${viewport.height}: layout, retained markers, event batching, slow requests, gesture/background pause`);
      await page.close();
    }
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
