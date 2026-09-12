// Real HTTP + Chromium regression in the isolated MapNotebook world.
// test-map-notebook-api.ps1 -GameRoot <game> -TestScript test-map-fog-areas.cjs
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const zlib = require('node:zlib');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const api = process.env.MAP_TEST_API;
if (!api || !process.env.MAP_TEST_CONTROL) throw Error('Use the isolated MapNotebook fixture');
const origin = new URL(api).origin, webRoot = path.resolve(__dirname, '../LauncherGo.ServerMapHost/WebRoot');
const normalize = value => Array.isArray(value) ? value.map(normalize) : value && typeof value === 'object'
  ? Object.fromEntries(Object.entries(value).map(([key, v]) => [key[0].toLowerCase() + key.slice(1), normalize(v)])) : value;
async function call(route, cookie, body, method = body === undefined ? 'GET' : 'POST') {
  const response = await fetch(api + route, { method, headers: { ...(cookie ? { Cookie: cookie } : {}), 'Content-Type': 'application/json', 'X-ServerMap-Request': '1' }, body: body === undefined ? undefined : JSON.stringify(body) });
  assert.equal(response.status, 200, route + ' ' + response.status);
  return response;
}
const json = async (...args) => normalize(await (await call(...args)).json());
async function until(fn) {
  const deadline = Date.now() + 15000;
  while (!await fn()) { assert.ok(Date.now() < deadline, 'Timed out waiting for fixture state'); await new Promise(resolve => setTimeout(resolve, 100)); }
}
async function alpha(route, cookie, x, z) {
  const png = Buffer.from(await (await call(route, cookie)).arrayBuffer()), chunks = [];
  for (let offset = 8; offset + 12 <= png.length;) {
    const length = png.readUInt32BE(offset);
    if (png.toString('ascii', offset + 4, offset + 8) === 'IDAT') chunks.push(png.subarray(offset + 8, offset + 8 + length));
    offset += length + 12;
  }
  const pixels = zlib.inflateSync(Buffer.concat(chunks));
  assert.equal(pixels[z * 2049], 0);
  return pixels[z * 2049 + 1 + x * 4 + 3];
}

async function run() {
  const cookies = {};
  for (const playerName of ['admin', 'alice', 'bob']) {
    const response = await call('/auth/login', null, { playerName, password: 'notebook-test-password' });
    cookies[playerName] = response.headers.get('set-cookie').split(';')[0];
  }
  let announcement = await json('/announcement');
  assert.equal(announcement.management.fogEnabled, true, 'Legacy/default worlds retain enabled fog');
  async function policy(changes) {
    announcement = await json('/announcement');
    return json('/announcement', cookies.admin, { ...announcement, management: { ...announcement.management, ...changes } });
  }
  const markers = async cookie => (await json('/area-markers', cookie)).markers;
  const fog = cookie => json('/fog/regions?minX=-32000000&minZ=-32000000&maxX=32000000&maxZ=32000000', cookie);
  async function add(name, rects, minZoom, maxZoom) {
    const snapshot = await json('/area-markers', cookies.admin);
    await call('/area-markers', cookies.admin, { revision: snapshot.revision, name, color: '#e69b5c', rects, minZoom, maxZoom, fillOpacity: .3, borderOpacity: .8, textOpacity: 1 });
    return (await markers(cookies.admin)).find(m => m.name === name);
  }
  const coast = await add('探索边界测试', [[16, 64, 400, 256]], 12, 15);
  const interior = await add('仅探索内部', [[0, 0, 512, 512]], 7, 9);
  const privateResponse = await call('/area-markers', cookies.bob);
  assert.equal(privateResponse.headers.get('cache-control'), 'no-store');
  assert.match(privateResponse.headers.get('vary'), /Cookie/);
  for (const cookie of [null, cookies.alice, cookies.bob]) assert.deepEqual(await markers(cookie), []);
  assert.deepEqual((await markers(cookies.admin))[0].rects, coast.rects);
  await policy({ adminsBypassFog: false });
  const adminMasked = await markers(cookies.admin);
  assert.ok(adminMasked.every(m => m.rects.length === 0 && m.borders.length === 0 && m.editRects.length));
  await fs.writeFile(path.join(path.dirname(process.env.MAP_TEST_CONTROL), 'explore-bob.test'), '1');
  await until(async () => (await fog(cookies.bob)).cells.length === 81);
  const clipped = (await markers(cookies.bob)).find(m => m.id === coast.id);
  assert.deepEqual(clipped.rects, [[32, 64, 320, 256]]);
  assert.deepEqual(new Set(clipped.borders.map(JSON.stringify)), new Set([[32, 64, 320, 64], [32, 256, 320, 256]].map(JSON.stringify)));
  assert.equal(clipped.editRects ?? null, null, 'Non-admin response never includes original geometry');
  assert.deepEqual((await markers(cookies.bob)).find(m => m.id === interior.id).borders, [], 'Interior exploration invents no border');
  assert.equal(await alpha('/tiles/basic/0/0_0.png', cookies.bob, 160, 160), 255);
  assert.equal(await alpha('/tiles/basic/0/0_0.png', cookies.bob, 1, 1), 0);

  // The master switch restores public map visibility without disabling hidden regions.
  await policy({ fogEnabled: false });
  await call('/pois', cookies.admin, { type: 'text', name: 'Outside exploration', text: '', color: '#aabbcc', x: 480, z: 10 });
  for (const cookie of [null, cookies.alice, cookies.bob, cookies.admin]) {
    assert.deepEqual((await markers(cookie)).find(m => m.id === coast.id).rects, coast.rects);
    assert.equal((await fog(cookie)).enabled, false);
    assert.equal(await alpha('/fog/0/0_0.png', cookie, 1, 1), 0);
    for (const route of ['/tiles/basic/0/0_0.png', '/2d/basic/0/0_0.png']) assert.equal(await alpha(route, cookie, 1, 1), 255);
    assert.ok((await json('/layers/pois', cookie)).features.some(f => f.properties.name === 'Outside exploration'));
  }
  const hidden = await json('/hidden-regions', cookies.admin, { name: 'Independent privacy', rects: [{ minX: 450, minZ: 450, maxX: 470, maxZ: 470 }] });
  for (const cookie of [null, cookies.alice, cookies.bob]) {
    assert.equal(await alpha('/tiles/basic/0/0_0.png?hideRegions=0', cookie, 460, 460), 0);
    assert.equal((await markers(cookie)).some(m => m.id === interior.id), false);
    assert.ok((await markers(cookie)).some(m => m.id === coast.id));
  }
  assert.equal(await alpha('/tiles/basic/0/0_0.png?hideRegions=1', cookies.admin, 460, 460), 0);
  assert.equal(await alpha('/tiles/basic/0/0_0.png?hideRegions=0', cookies.admin, 460, 460), 255);
  await call('/hidden-regions?id=' + hidden.id, cookies.admin, undefined, 'DELETE');
  await policy({ fogEnabled: true, shareExploration: true });
  assert.deepEqual((await markers(cookies.bob)).find(m => m.id === coast.id).rects, clipped.rects, 'Re-enabling preserves exploration');
  assert.deepEqual(await markers(null), []);
  const bobId = (await json('/allies', cookies.bob)).mapId;
  for (const uid of ['alice', 'admin']) await call('/allies', cookies[uid], { action: 'add', mapId: bobId });
  assert.deepEqual((await markers(cookies.alice)).find(m => m.id === coast.id).rects, clipped.rects, 'Offline ally works without own exploration');
  await call('/allies', cookies.alice, { action: 'remove', uid: 'bob' });
  assert.deepEqual(await markers(cookies.alice), []);
  console.log('PASS HTTP: fog on/off, persistent exploration, guests, admin bypass, privacy, offline allies, sparse clipping and original-only borders');

  const nativeArea = await add('原生探索增量测试', [[224, 320, 320, 480]], 12, 15);
  const metadata = await json('/map/metadata'), browser = await chromium.launch({ headless: true });
  try {
    for (const width of [1280, 390]) {
      await policy({ fogEnabled: true, shareExploration: true, adminsBypassFog: false });
      const contexts = [], errors = [];
      async function open(cookie) {
        const context = await browser.newContext({ viewport: { width, height: 844 }, locale: 'zh-CN', isMobile: width < 700, hasTouch: width < 700 });
        contexts.push(context);
        const equals = cookie.indexOf('=');
        await context.addCookies([{ name: cookie.slice(0, equals), value: cookie.slice(equals + 1), url: origin }]);
        const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
        await page.addInitScript(() => localStorage.setItem('servermap-pinned', 'pinned'));
        await page.route(origin + '/**', async route => {
          const pathname = new URL(route.request().url()).pathname;
          if (pathname.startsWith('/api/')) return route.continue();
          const file = path.resolve(webRoot, pathname === '/' ? 'index.html' : pathname.slice(1));
          assert.ok(file.startsWith(webRoot + path.sep));
          let body = await fs.readFile(file);
          if (pathname === '/') body = body.toString().replace('const map=L.map', 'const map=window.testMap=L.map').replace('areas=ServerMapAreas.create', 'areas=window.testAreas=ServerMapAreas.create');
          await route.fulfill({ body, contentType: { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png', '.svg': 'image/svg+xml' }[path.extname(file)] || 'application/octet-stream' });
        });
        const query = new URLSearchParams({ renderer: 'basic', zoom: '0', x: String(160 - metadata.spawn.x), z: String(160 - metadata.spawn.z) });
        await page.goto(origin + '/?' + query, { waitUntil: 'domcontentloaded' });
        await page.waitForFunction(() => window.testAreas && document.querySelector('#logoutButton')?.hidden === false);
        await page.locator('#areaMarkerToggle').evaluate(input => { input.checked = true; input.dispatchEvent(new Event('change')); });
        await page.locator(`.area-marker-overlay[data-area-id="${coast.id}"]`).waitFor();
        return page;
      }
      const bobPage = await open(cookies.bob), adminPage = await open(cookies.admin);
      async function checkTiles(page, fogEnabled) {
        await page.waitForFunction(fogEnabled => {
          const tile = [...document.querySelectorAll('img.leaflet-tile')].find(img => img.src.includes('/tiles/basic/0/0_0.png') && img.complete && img.naturalWidth);
          if (!tile) return false;
          const canvas = document.createElement('canvas'); canvas.width = canvas.height = 512;
          const ctx = canvas.getContext('2d'); ctx.drawImage(tile, 0, 0);
          return ctx.getImageData(1, 1, 1, 1).data[3] === (fogEnabled ? 0 : 255);
        }, fogEnabled);
        assert.equal(await page.evaluate(() => Object.values(window.testMap._layers).filter(layer => layer instanceof L.TileLayer).length), 1);
      }
      async function checkShape(page, rect, borderCount) {
        await page.waitForFunction(({ id, rect }) => document.querySelector(`.area-marker-overlay[data-area-id="${id}"]`)?.dataset.labelRect === rect.join(','), { id: coast.id, rect });
        const rendered = await page.evaluate(id => {
          const svg = document.querySelector(`.area-marker-overlay[data-area-id="${id}"]`), text = svg.querySelector('text');
          const box = svg.getBoundingClientRect(), label = text.getBoundingClientRect();
          const border = Object.values(window.testMap._layers).find(layer => layer.options?.className === 'area-marker-boundary' && layer.options.areaMarkerId === id);
          return { clip: !!svg.querySelector('g[clip-path]'), name: text.textContent, within: label.left >= box.left - 1 && label.right <= box.right + 1 && label.top >= box.top - 1 && label.bottom <= box.bottom + 1, borders: border.getLatLngs().map(line => line.map(p => [p.lng * 4096, p.lat * 4096]).flat()) };
        }, coast.id);
        assert.equal(rendered.name, coast.name); assert.ok(rendered.clip && rendered.within, 'Text fits and is clipped to the explored shape');
        assert.equal(rendered.borders.length, borderCount);
        if (borderCount === 2) assert.deepEqual(new Set(rendered.borders.map(JSON.stringify)), new Set(clipped.borders.map(JSON.stringify)));
      }
      await checkShape(bobPage, clipped.rects[0], 2); await checkShape(adminPage, clipped.rects[0], 2); await checkTiles(bobPage, true);
      async function toggleFog(enabled) {
        if (width < 700 && !await adminPage.locator('#manageButton').isVisible()) await adminPage.locator('#mobileMenu').click();
        await adminPage.locator('#manageButton').click();
        await adminPage.locator('[aria-controls="management-fog"]').click();
        assert.equal(await adminPage.locator('label[for="fogEnabled"]').textContent(), '启用战争迷雾');
        await adminPage.locator('#fogEnabled').setChecked(enabled);
        await adminPage.locator('#manageForm button[type="submit"]').click();
        await adminPage.locator('#manageModal').waitFor({ state: 'hidden' });
        assert.equal((await json('/announcement')).management.fogEnabled, enabled);
      }
      await toggleFog(false); await checkShape(bobPage, coast.rects[0], 4); await checkTiles(bobPage, false);
      // Hold an old full-geometry response across the settings event. It must
      // never overwrite the new clipped data, even if a request was pending.
      let received, release;
      const arrived = new Promise(resolve => received = resolve), hold = new Promise(resolve => release = resolve);
      await bobPage.route('**/api/v1/area-markers', async route => {
        const response = await route.fetch(); received(); await hold;
        try { await route.fulfill({ response }); } catch { /* request was correctly aborted */ }
      }, { times: 1 });
      await bobPage.evaluate(() => { void window.testAreas.refresh(); }); await arrived;
      await toggleFog(true); release(); await checkShape(bobPage, clipped.rects[0], 2); await checkTiles(bobPage, true);
      await checkShape(adminPage, clipped.rects[0], 2);
      // Explicit admin editing must load the original, not save the fog cut.
      if (width < 700 && await adminPage.locator('#manageButton').isVisible()) await adminPage.locator('#mobileMenu').click();
      await adminPage.locator('#areaManageButton').click();
      await adminPage.locator('#areaMarkerSearch').fill(coast.name);
      await adminPage.locator('.area-manager-row [data-area-action="edit"]').click();
      await adminPage.locator('#areaMarkerToolbar').waitFor({ state: 'visible' });
      const draftBounds = await adminPage.locator('.leaflet-areaMarkerDraft-pane .area-marker-overlay').evaluate(svg => { const b = svg.viewBox.baseVal; return [b.x, b.y, b.x + b.width, b.y + b.height]; });
      assert.deepEqual(draftBounds, coast.rects[0]);
      // Newly generated native pieces must refresh an unmoved browser through the real SSE/Host path.
      // They are additive: the administrator's in-progress area edit must remain intact.
      const nativeX = 8, nativeZ = width === 1280 ? 11 : 13;
      const nativePixel = [nativeX * 32 + 16, nativeZ * 32 + 16];
      async function checkNativePixels(page, visible) {
        try { await page.waitForFunction(({ point, visible }) => {
          return [...document.querySelectorAll('img.leaflet-tile')].some(tile => {
            const match = new URL(tile.src).pathname.match(/\/tiles\/basic\/(\d+)\/(-?\d+)_(-?\d+)\.png$/);
            if (!match || !tile.complete || !tile.naturalWidth) return false;
            const resolution = 2 ** Number(match[1]), x = point[0] / resolution - Number(match[2]) * 512, z = point[1] / resolution - Number(match[3]) * 512;
            if (x < 32 / resolution || x >= 512 - 32 / resolution || z < 0 || z >= 512) return false;
            const canvas = document.createElement('canvas'); canvas.width = canvas.height = 512;
            const ctx = canvas.getContext('2d'); ctx.drawImage(tile, 0, 0);
            return ctx.getImageData(x, z, 1, 1).data[3] === (visible ? 255 : 0)
              && ctx.getImageData(x - 32 / resolution, z, 1, 1).data[3] === 0
              && ctx.getImageData(x + 32 / resolution, z, 1, 1).data[3] === 0;
          });
        }, { point: nativePixel, visible }, { timeout: 7000 }); }
        catch(error) {
          console.error('Native pixel check state', await page.evaluate(() => ({ zoom:window.testMap.getZoom(),tiles:[...document.querySelectorAll('img.leaflet-tile')].map(img=>({src:img.src,loaded:img.complete,width:img.naturalWidth})) })));
          throw error;
        }
      }
      await checkNativePixels(bobPage, false);
      await fs.writeFile(path.join(path.dirname(process.env.MAP_TEST_CONTROL), 'explore-bob-native.test'), `${nativeX},${nativeZ}`);
      await checkNativePixels(bobPage, true); await checkNativePixels(adminPage, true);
      await bobPage.locator(`.area-marker-overlay[data-area-id="${nativeArea.id}"]`).first().waitFor();
      const nativeRects = (await markers(cookies.bob)).find(marker => marker.id === nativeArea.id).rects;
      assert.ok(nativeRects.some(rect => JSON.stringify(rect) === JSON.stringify([nativeX * 32, nativeZ * 32, (nativeX + 1) * 32, (nativeZ + 1) * 32])));
      assert.equal(await adminPage.locator('#areaMarkerToolbar').isVisible(), true, 'Additive native exploration cancelled an area draft');
      assert.deepEqual(await adminPage.locator('.leaflet-areaMarkerDraft-pane .area-marker-overlay').evaluate(svg => { const b = svg.viewBox.baseVal; return [b.x, b.y, b.x + b.width, b.y + b.height]; }), draftBounds);
      await adminPage.locator('#areaMarkerToolbar [data-area-action="finish"]').click();
      await adminPage.locator('#areaMarkerModal [data-area-action="save"]').click();
      await adminPage.locator('#areaMarkerToolbar').waitFor({ state: 'hidden' });
      assert.deepEqual((await markers(cookies.admin)).find(m => m.id === coast.id).editRects, coast.rects);
      assert.deepEqual(errors, []);
      if (process.env.MAP_SCREENSHOTS) { await fs.mkdir(process.env.MAP_SCREENSHOTS, { recursive: true }); await bobPage.screenshot({ path: path.join(process.env.MAP_SCREENSHOTS, `fog-area-${width}.png`) }); }
      for (const context of contexts) await context.close();
      console.log(`PASS ${width}px: master checkbox, live settings, clipping, stale responses, native single-cell SSE/Host refresh without movement, ally updates and preserved admin drafts`);
    }
  } finally { await browser.close(); }
  // The account/session was created as admin, but the real game's offline
  // player data now removes root. Historical map authority must not override it.
  await fs.writeFile(process.env.MAP_TEST_CONTROL, '1');
  await until(async () => (await json('/auth/me', cookies.admin)).admin === false);
  const demoted = await json('/area-markers', cookies.admin);
  assert.ok(demoted.markers.every(m => !m.editRects), 'Demotion removes original admin-only geometry');
  const mutation = await fetch(api + '/area-markers', { method: 'POST', headers: { Cookie: cookies.admin, 'Content-Type': 'application/json', 'X-ServerMap-Request': '1' }, body: JSON.stringify({ revision: demoted.revision, name: 'Forbidden', color: '#abcdef', rects: [[800, 800, 900, 900]], minZoom: 12 }) });
  assert.equal(mutation.status, 403, 'Historical admin cookie cannot mutate after game demotion');
  console.log('PASS real offline-game demotion overrides cached administrator session');
}
run().catch(error => { console.error(error); process.exitCode = 1; });
