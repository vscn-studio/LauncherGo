// Browser integration: PLAYWRIGHT_MODULE may point to an existing Playwright installation.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const webRoot = path.resolve(__dirname, '../LauncherGo.ServerMapHost/WebRoot');

async function main() {
  const browser = await chromium.launch({ headless: true });
  try {
    for (const width of [1280, 390]) {
      const page = await browser.newPage({ viewport: { width, height: 844 }, locale: 'zh-CN', isMobile: width < 700, hasTouch: width < 700 });
      const errors = [], reads = [];
      let self = { name: 'Tester', x: 1200, z: 800 }, user = { authenticated: true, admin: false, name: 'Tester' };
      page.on('pageerror', error => errors.push(error.message));
      await page.addInitScript(() => {
        window.EventSource = class extends EventTarget { constructor() { super();window.testEvents = this; } };
        window.testHidden = false;
        Object.defineProperty(document, 'hidden', { get: () => window.testHidden });
      });
      await page.route('http://settings.test/**', async route => {
        const url = new URL(route.request().url()), name = url.pathname;
        const json = value => route.fulfill({ json: value });
        if (name.startsWith('/api/')) reads.push(name);
        if (name.endsWith('/map/metadata')) return json({ maxZoom: 12, maxZoomOut: 12, spawn: { x: 0, z: 0 }, colormapReady: true });
        if (name.endsWith('/layers/manifest')) return json({ layers: ['players', 'mounts'].map(id => ({ id, visible: true })) });
        if (name.includes('/layers/')) return json({ features: [] });
        if (name.endsWith('/auth/me')) return json(user);
        if (name.endsWith('/auth/logout')) { user = { authenticated: false };return json(user); }
        if (name.endsWith('/players')) return json([self]);
        if (name.endsWith('/announcement')) return json({ html: '<p>Settings fixture</p>', management: { fogEnabled: false } });
        if (name.includes('/tiles/')) return route.fulfill({ path: path.join(webRoot, 'assets/icons/spawn.png'), contentType: 'image/png' });
        if (name.endsWith('/area-markers')) return json({ revision: 0, markers: [] });
        if (name.startsWith('/api/')) return json([]);
        const file = path.resolve(webRoot, name === '/' ? 'index.html' : name.slice(1));
        assert.ok(file.startsWith(webRoot + path.sep));
        let body = await fs.readFile(file);
        if (name === '/') body = body.toString().replace('const map=L.map', 'const map=window.testMap=L.map');
        return route.fulfill({ body, contentType: { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png', '.svg': 'image/svg+xml' }[path.extname(file)] || 'text/plain' });
      });
      const open = () => page.locator('#webSettingsButton').click();
      const close = () => page.locator('#webSettingsDialog .dialog-close').click();
      const checked = (key, value) => page.locator('#web-setting-' + key).setChecked(value);
      const centered = x => page.waitForFunction(x => Math.abs(testMap.getCenter().lng * 4096 - x) < 1, x);
      const hidden = value => page.evaluate(value => { testHidden = value;document.dispatchEvent(new Event('visibilitychange')); }, value);
      try {
        await page.goto('http://settings.test/');
        await page.waitForSelector('img.leaflet-tile-loaded');
        assert.equal(await page.evaluate(() => document.getElementById('languageButton').nextElementSibling.id), 'webSettingsButton');
        assert.equal(await page.locator('img.leaflet-tile').first().evaluate(el => getComputedStyle(el).mixBlendMode), 'normal');
        await open();
        assert.equal(await page.locator('#webSettingsDialog input[type=checkbox]').count(), 5);
        assert.ok(await page.locator('#webSettingsDialog').evaluate(el => el.getBoundingClientRect().right <= innerWidth));
        await checked('compatibleTiles', false);
        assert.equal(await page.locator('img.leaflet-tile').first().evaluate(el => getComputedStyle(el).mixBlendMode), 'plus-lighter');
        await checked('stars', false);
        assert.equal(await page.locator('#starCanvas').isVisible(), false);
        await checked('effects', false);
        assert.equal(await page.locator('#webSettingsDialog').evaluate(el => getComputedStyle(el).boxShadow), 'none');
        await checked('followSelf', true);
        await close();await centered(self.x);
        // Following is independent of whether the player marker layer is visible.
        await page.evaluate(() => { const input = document.querySelector('#layers [data-layer=players] input');input.checked = false;input.dispatchEvent(new Event('change')); });
        self = { ...self, x: 1600 };await centered(self.x);
        await page.evaluate(() => testMap.fire('dragstart'));
        await open();assert.equal(await page.locator('#web-setting-followSelf').isChecked(), false);await close();
        await page.reload();await page.waitForSelector('img.leaflet-tile-loaded');
        await open();
        for (const key of ['compatibleTiles', 'stars', 'effects', 'followSelf']) assert.equal(await page.locator('#web-setting-' + key).isChecked(), false);
        await checked('followSelf', true);await close();await centered(self.x);
        // Pause routine requests, but continue handling visibility revocation.
        await hidden(true);await page.waitForTimeout(700);reads.length = 0;
        await page.waitForTimeout(width < 700 ? 5300 : 2300);
        assert.equal(reads.filter(name => name === '/api/v1/players' || name.startsWith('/api/v1/layers/')).length, 0);
        await page.evaluate(() => testEvents.dispatchEvent(new MessageEvent('visibility', { data: '{}' })));
        await page.waitForTimeout(500);
        assert.ok(reads.some(name => name.includes('hidden-regions')), 'privacy invalidation is not paused');
        self = { ...self, x: 2200 };await hidden(false);await centered(self.x);
        await open();await checked('pauseBackground', false);await close();await hidden(true);
        self = { ...self, x: 2600 };await centered(self.x);await hidden(false);
        // Missing/hidden position is never interpreted as the world origin.
        self = { ...self, x: null, z: null };
        await page.waitForTimeout(width < 700 ? 5300 : 2300);
        assert.ok(Math.abs(await page.evaluate(() => testMap.getCenter().lng * 4096) - 2600) < 1);
        await open();await page.locator('#webSettingsDialog .web-settings-actions button').first().click();
        assert.equal(await page.locator('#web-setting-compatibleTiles').isChecked(), true);
        assert.equal(await page.locator('#web-setting-followSelf').isChecked(), false);
        await page.keyboard.press('Escape');assert.equal(await page.locator('#webSettingsDialog').isVisible(), false);
        assert.deepEqual(errors, []);console.log(`web settings OK (${width}px)`);
      } finally { await page.close(); }
    }
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error);process.exitCode = 1; });
