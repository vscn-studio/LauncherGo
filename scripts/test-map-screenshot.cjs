// Real Leaflet projection and browser PNG export: run with PLAYWRIGHT_MODULE if installed outside this repo.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const webRoot = path.resolve(__dirname, '../LauncherGo.ServerMapHost/WebRoot');

async function main() {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1200, height: 800 }, locale: 'zh-CN' });
    const errors = [], requests = [];
    page.on('pageerror', error => errors.push(error.message));
    const makePng = transparent => page.evaluate(transparent => {
      const canvas = document.createElement('canvas'); canvas.width = canvas.height = 512;
      const ctx = canvas.getContext('2d');
      if (!transparent) ['#287a35', '#3264a0', '#967244', '#788234'].forEach((color, i) => {
        ctx.fillStyle = color; ctx.fillRect((i % 2) * 256, Math.floor(i / 2) * 256, 256, 256);
      });
      return canvas.toDataURL().split(',')[1];
    }, transparent);
    const terrain = Buffer.from(await makePng(false), 'base64'), transparent = Buffer.from(await makePng(true), 'base64');
    let tileMode = 'terrain';
    await page.addInitScript(() => {
      window.EventSource = class extends EventTarget {};
      localStorage.setItem('servermap-language', 'zh');
      Object.defineProperty(navigator, 'clipboard', { value: { write: async items => { window.copiedImage = await items[0].getType('image/png'); } }, configurable: true });
      window.ClipboardItem = class { constructor(data) { this.data = data; } async getType(type) { return this.data[type]; } };
    });
    await page.route('http://servermap.test/**', async route => {
      const url = new URL(route.request().url()), name = url.pathname;
      const json = value => route.fulfill({ json: value });
      if (name.endsWith('/map/metadata')) return json({ maxZoom: 12, maxZoomOut: 8, spawn: { x: 0, z: 0 }, center: { x: 1024, z: 2048 }, tileVersion: 'fixture', colorVersion: 'fixture', colormapReady: true });
      if (name.endsWith('/layers/manifest')) return json({ layers: [{ id: 'pois', visible: true }, { id: 'claim-areas', visible: true }] });
      if (name.endsWith('/layers/pois')) return json({ features: [{ id: 'label', type: 'Feature', geometry: { type: 'Point', coordinates: [1024, 2048] }, properties: { name: '截图测试', color: '#ff00ff' } }] });
      if (name.endsWith('/layers/claim-areas')) return json({ features: [{ id: 'claim', type: 'Feature', geometry: { type: 'Polygon', coordinates: [[[920, 2000], [960, 2000], [960, 2030], [920, 2030], [920, 2000]]] }, properties: { color: '#00ffff' } }] });
      if (name.endsWith('/render-progress')) return json({ phase: 'idle' });
      if (name.endsWith('/auth/me')) return json({ authenticated: false });
      if (name.endsWith('/announcement')) return json({ html: '' });
      if (name.endsWith('/hidden-regions')) return json([]);
      if (name.includes('/tiles/')) {
        requests.push(name);
        // The native map is server zoom 0. Missing parent tiles intentionally
        // match the real server's successful transparent placeholder response.
        const available = tileMode !== 'transparent' && /\/tiles\/[^/]+\/0\//.test(name) && (tileMode !== 'partial' || !name.endsWith('/2_4.png'));
        return route.fulfill({ body: available ? terrain : transparent, contentType: 'image/png' });
      }
      if (name.startsWith('/api/')) return json({ features: [] });
      const file = path.resolve(webRoot, name === '/' ? 'index.html' : name.slice(1));
      assert.ok(file.startsWith(webRoot + path.sep));
      let body = await fs.readFile(file);
      if (name === '/') body = body.toString().replace('  (() => {', '  const makeMap=L.map;L.map=(...args)=>(window.testMap=makeMap(...args));\n  (() => {');
      return route.fulfill({ body, contentType: { '.html': 'text/html', '.css': 'text/css', '.js': 'text/javascript', '.svg': 'image/svg+xml', '.png': 'image/png' }[path.extname(file)] || 'text/plain' });
    });
    await page.goto('http://servermap.test/');
    await page.locator('.poi-label').waitFor();
    await page.waitForFunction(() => document.querySelector('#notebookProgress')?.dataset.phase === 'idle');

    async function capture(zoom = 12, x = 1024, z = 2048) {
      requests.length = 0;
      const result = await page.evaluate(async ({ zoom, x, z }) => {
        const map = window.testMap;
        map.setView([z / 4096, x / 4096], zoom, { animate: false });
        const layer = Object.values(map._layers).find(layer => layer instanceof L.TileLayer);
        const bounds = L.latLngBounds(map.containerPointToLatLng([440, 300]), map.containerPointToLatLng([760, 500]));
        try {
          const result = await ServerMapScreenshot.capture({ map, bounds, tileLayer: layer, nativeZoom: 12, signal: new AbortController().signal });
          const image = await createImageBitmap(result.blob), canvas = document.createElement('canvas');
          canvas.width = image.width; canvas.height = image.height;
          const ctx = canvas.getContext('2d'); ctx.drawImage(image, 0, 0);
          const data = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
          let opaque = 0, magenta = 0;
          for (let i = 0; i < data.length; i += 4) {
            if (data[i + 3]) opaque++;
            if (data[i] > 180 && data[i + 1] < 90 && data[i + 2] > 180) magenta++;
          }
          const ratio = result.width / 320;
          return { width: result.width, height: result.height, missingTiles: result.missingTiles, opaque, magenta,
            sample: [...ctx.getImageData(Math.floor(20 * ratio), Math.floor(20 * ratio), 1, 1).data],
            vectorSample: [...ctx.getImageData(Math.floor(80 * ratio), Math.floor(70 * ratio), 1, 1).data] };
        } catch (error) { return { error: error.message }; }
      }, { zoom, x, z });
      return result;
    }
    const first = await capture();
    assert.equal(first.error, undefined, JSON.stringify(first));
    assert.equal(first.width, 640); assert.equal(first.height, 400);
    assert.equal(first.opaque, first.width * first.height, 'Export must contain real terrain across the selected rectangle');
    assert.deepEqual(first.sample, [120, 130, 52, 255], 'Selected world coordinate must use its native terrain pixel');
    assert.ok(first.magenta > 10, 'Enabled DOM label must be exported');
    assert.notDeepEqual(first.vectorSample, first.sample, 'Enabled Canvas geometry must be exported');
    assert.ok(requests.every(url => /\/tiles\/basic\/0\//.test(url)), requests.join('\n'));
    await page.locator('#layers [data-layer="pois"] input').uncheck({ force: true });
    await page.locator('#layers [data-layer="claim-areas"] input').uncheck({ force: true });
    const hidden = await capture(); assert.equal(hidden.magenta, 0, 'Unchecked label layer must not be exported');
    assert.deepEqual(hidden.vectorSample, first.sample, 'Unchecked Canvas geometry must not be exported');
    for (const zoom of [10, 14]) {
      const result = await capture(zoom, -1024, -2048);
      assert.equal(result.error, undefined, JSON.stringify(result));
      assert.equal(result.opaque, result.width * result.height, 'Terrain must remain correctly positioned across zooms and negative coordinates');
      assert.equal(result.width, zoom === 10 ? 1280 : 640);
    }
    await capture();
    await page.mouse.click(440, 300, { button: 'right' });
    assert.equal(await page.locator('#contextMenu').getByRole('button', { name: '框选截图区域', exact: true }).count(), 1);
    assert.equal(await page.locator('#contextMenu').getByText('captureRegion', { exact: true }).count(), 0);
    await page.locator('#contextMenu').getByRole('button', { name: '框选截图区域', exact: true }).click();
    await page.mouse.click(760, 500);
    await page.locator('.notebook-screenshot-preview').waitFor();
    await page.getByRole('button', { name: '复制到粘贴板', exact: true }).click();
    assert.ok(await page.evaluate(() => window.copiedImage instanceof Blob && window.copiedImage.type === 'image/png'));
    const downloadPromise = page.waitForEvent('download');
    await page.getByRole('button', { name: '下载', exact: true }).click();
    const download = await downloadPromise;
    assert.match(download.suggestedFilename(), /^servermap-.*\.png$/);
    const exported = await fs.readFile(await download.path());
    assert.equal(exported.subarray(1, 4).toString(), 'PNG');
    assert.equal(exported.readUInt32BE(16), 640); assert.equal(exported.readUInt32BE(20), 400);
    await page.locator('.notebook-screenshot-close').click();
    tileMode = 'partial';
    const partial = await capture();
    assert.equal(partial.error, undefined); assert.equal(partial.missingTiles, 1);
    assert.equal(partial.opaque, partial.width * partial.height * .75, 'Missing or private terrain must remain transparent without a synthetic background');
    tileMode = 'transparent';
    const empty = await capture();
    assert.equal(empty.error, 'captureEmpty', 'A successful HTTP transparent placeholder must not be offered as a completed screenshot');
    assert.deepEqual(errors, []);
    console.log('Screenshot browser checks passed: native terrain pixels, PNG copy/download, zoom/negative coordinates, DOM and Canvas layer toggles, transparent placeholders.');
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
