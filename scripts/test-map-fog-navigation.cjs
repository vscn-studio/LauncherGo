// Run with Node and Playwright (or set PLAYWRIGHT_MODULE to an existing installation).
// MAP_TEST_INDEX + MAP_FOG_PROFILE_ONLY=1 can profile an older index.html with the same fixture.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const webRoot = path.resolve(__dirname, '../LauncherGo.ServerMapHost/WebRoot');
const profileOnly = process.env.MAP_FOG_PROFILE_ONLY === '1';
const coverage = Array.from({ length: 4096 }, (_, i) => ({ x: (i % 64) - 32, z: Math.floor(i / 64) - 32 }));
// Sparse explored pieces throughout a large world. Each region contains 256 native cells.
const cells = coverage.flatMap(tile => [{ x: tile.x * 16, z: tile.z * 16 }, { x: tile.x * 16 + 8, z: tile.z * 16 + 8 }]);

async function main() {
  const browser = await chromium.launch({ headless: true });
  try {
    for (const width of profileOnly ? [1280] : [1280, 390]) {
      for (const mode of ['fogged', 'bypass', 'anonymous']) {
        const page = await browser.newPage({ viewport: { width, height: 844 }, locale: 'zh-CN', isMobile: width < 700, hasTouch: width < 700 });
        const errors = [], tiles = [], privateReads = [];
        let regionRequests = 0, user = { authenticated: mode !== 'anonymous', admin: mode === 'bypass', name: mode };
        let policy = { fogEnabled: true, adminsBypassFog: true, shareExploration: false };
        page.on('pageerror', error => errors.push(error.message));
        await page.addInitScript(() => {
          window.EventSource = class extends EventTarget { constructor() { super(); window.testEvents = this; } };
          localStorage.setItem('servermap-pinned', 'pinned');
          window.longTasks = [];
          new PerformanceObserver(list => window.longTasks.push(...list.getEntries().map(e => e.duration))).observe({ type: 'longtask' });
        });
        await page.route('http://fog.test/**', async route => {
          const request = route.request(), url = new URL(request.url()), name = url.pathname;
          const json = value => route.fulfill({ json: value });
          if (name.endsWith('/map/metadata')) return json({ maxZoom: 12, maxZoomOut: 12, spawn: { x: 0, z: 0 }, colormapReady: true });
          if (name.endsWith('/layers/manifest')) return json({ layers: ['players', 'spawn', 'pois'].map(id => ({ id, visible: true })) });
          if (name.includes('/layers/')) return json({ features: [] });
          if (name.endsWith('/auth/me')) return json(user);
          if (name.endsWith('/auth/logout')) { user = { authenticated: false, admin: false }; return json(user); }
          if (name.endsWith('/announcement')) return json({ html: '<p>Navigation fixture</p>', poiImagesEnabled: false, management: policy });
          if (name.endsWith('/fog/regions')) {
            regionRequests++;
            return json({ enabled: policy.fogEnabled, bypass: user.admin && policy.adminsBypassFog, coverage, cells: user.authenticated ? cells : [] });
          }
          if (name.includes('/tiles/')) {
            tiles.push(url);
            // Fixture imagery is served as a tile, just as the backend serves already-masked PNGs.
            return route.fulfill({ path: path.join(webRoot, 'assets/icons/spawn.png'), contentType: 'image/png' });
          }
          if (name.endsWith('/area-markers')) { privateReads.push('areas'); return json({ revision: 0, zoomRanges: true, markers: [] }); }
          if (name.endsWith('/hidden-regions')) { privateReads.push('regions'); return json([]); }
          if (name.startsWith('/api/')) return json([]);
          const file = path.resolve(webRoot, name === '/' ? 'index.html' : name.slice(1));
          assert.ok(file.startsWith(webRoot + path.sep));
          let body = await fs.readFile(name === '/' && process.env.MAP_TEST_INDEX ? process.env.MAP_TEST_INDEX : file);
          if (name === '/') body = body.toString().replace('const map=L.map', 'const map=window.testMap=L.map');
          return route.fulfill({ body, contentType: { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png', '.svg': 'image/svg+xml' }[path.extname(file)] || 'text/plain' });
        });
        try {
          await page.goto('http://fog.test/?zoom=8&x=0&z=0');
          await page.waitForFunction(() => !document.querySelector('#go').disabled);
          await page.waitForTimeout(300);
          await page.evaluate(() => { window.longTasks.length = 0; });
          for (const zoom of [3, 2, 4]) {
            await page.evaluate(zoom => { testMap.setZoom(zoom, { animate: false }); testMap.panBy([200, 80], { animate: false }); }, zoom);
            await page.waitForTimeout(250);
          }
          await page.mouse.move(width / 2, 500);
          await page.mouse.down();
          await page.mouse.move(width / 2 + 90, 560, { steps: 15 });
          await page.mouse.up();
          await page.setViewportSize({ width, height: 800 });
          await page.waitForTimeout(300);
          const result = await page.evaluate(() => ({
            invisibleFogPaths: Object.values(testMap._layers).filter(layer => layer.options?.pane === 'unexploredFog').length,
            longestTaskMs: Math.round(Math.max(0, ...window.longTasks)),
            totalLongTaskMs: Math.round(window.longTasks.reduce((sum, ms) => sum + ms, 0))
          }));
          console.log(JSON.stringify({ width, mode, regionRequests, ...result }));
          assert.deepEqual(errors, []);
          assert.ok(tiles.length > 0, 'navigation still fetches terrain tiles');
          assert.ok(tiles.every(url => url.searchParams.has('privacy') && url.searchParams.get('hideRegions') === '1'));
          if (profileOnly) continue;
          // Performance invariants: no world-sized cell response, expansion, or invisible paths on navigation.
          assert.equal(regionRequests, 0, 'panning/zooming never requests the redundant world-sized fog geometry');
          assert.equal(result.invisibleFogPaths, 0, 'no invisible rectangles accumulate on the map');
          const currentPrivacy = () => Math.max(...tiles.map(url => Number(url.searchParams.get('privacy'))));
          async function expectTileRefresh(action) {
            const version = currentPrivacy();
            await action();
            await page.waitForFunction(version => Object.values(testMap._layers).some(layer => layer._tiles && Object.values(layer._tiles).some(tile => tile.el?.src && Number(new URL(tile.el.src).searchParams.get('privacy')) > version)), version);
          }
          if (mode === 'fogged') {
            const areasBefore = privateReads.filter(k => k === 'areas').length;
            await expectTileRefresh(() => page.evaluate(() => testEvents.dispatchEvent(new MessageEvent('exploration', { data: '{"changed":true}' }))));
            assert.ok(privateReads.filter(k => k === 'areas').length > areasBefore, 'new exploration refreshes clipped area markers');
            await expectTileRefresh(() => page.evaluate(() => testEvents.dispatchEvent(new MessageEvent('visibility', { data: '{"changed":true}' }))));
            if (width < 700) await page.locator('#mobileMenu').click();
            await expectTileRefresh(() => page.locator('#logoutButton').click());
            await page.waitForFunction(() => document.querySelector('#logoutButton').hidden);
          }
          policy = { ...policy, fogEnabled: false };
          await expectTileRefresh(() => page.evaluate(management => testEvents.dispatchEvent(new MessageEvent('settings', { data: JSON.stringify({ management, poiImagesEnabled: false }) })), policy));
          policy = { ...policy, fogEnabled: true };
          await expectTileRefresh(() => page.evaluate(management => testEvents.dispatchEvent(new MessageEvent('settings', { data: JSON.stringify({ management, poiImagesEnabled: false }) })), policy));
          assert.equal(regionRequests, 0, 'visibility refreshes keep terrain masking without rebuilding fog geometry');
          assert.deepEqual(errors, []);
          console.log('PASS fog navigation and visibility refresh ' + width + ' ' + mode);
        } finally { await page.close(); }
      }
    }
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
