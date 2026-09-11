// Run with Node and Playwright; PLAYWRIGHT_MODULE can point to an existing installation.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const webRoot = path.resolve(__dirname, '../LauncherGo.ServerMapHost/WebRoot');
const intersects = (a, b) => a && b && a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height;

async function main() {
  const browser = await chromium.launch({ headless: true });
  try {
    for (const [width, height, mobile] of [[320,640,true],[360,640,true],[390,844,true],[844,390,true],[768,800,false],[1280,800,false],[1920,1080,false]]) {
      const page = await browser.newPage({ viewport:{width,height}, isMobile:mobile, hasTouch:mobile, locale:'zh-CN' });
      const errors = [];
      let authenticated = true;
      page.on('pageerror', error => errors.push(error.message));
      await page.addInitScript(() => {
        window.EventSource = class extends EventTarget {};
        localStorage.setItem('servermap-pinned', 'pinned');
      });
      await page.route('http://servermap.test/**', async route => {
        const name = new URL(route.request().url()).pathname;
        const json = value => route.fulfill({json:value});
        if (name.endsWith('/map/metadata')) return json({maxZoom:12,maxZoomOut:12,spawn:{x:0,z:0},center:{x:0,z:0},updatedAt:'2026-09-11',colormapReady:true});
        if (name.endsWith('/layers/manifest')) return json({layers:['players','spawn','translocators','pois'].map(id=>({id,visible:true}))});
        if (name.endsWith('/auth/me')) return json({authenticated,admin:authenticated});
        if (name.endsWith('/auth/logout')) { authenticated=false; return json({authenticated:false,admin:false}); }
        if (name.endsWith('/announcement')) return json({html:'<h3>公告</h3><p>Toolbar test</p>'});
        if (name.endsWith('/area-markers')) return json({revision:0,markers:[]});
        if (name.endsWith('/render-progress')) return json({phase:'idle',queued:0});
        if (name.includes('/layers/')) return json({features:[]});
        if (name.includes('/tiles/')) return route.fulfill({path:path.join(webRoot,'assets/sky.png'),contentType:'image/png'});
        if (name.startsWith('/api/')) return json([]);
        const file = path.resolve(webRoot, name === '/' ? 'index.html' : name.slice(1));
        assert.ok(file.startsWith(webRoot + path.sep));
        return route.fulfill({body:await fs.readFile(file),contentType:{'.html':'text/html','.js':'text/javascript','.css':'text/css','.svg':'image/svg+xml','.png':'image/png'}[path.extname(file)] || 'text/plain'});
      });
      await page.goto('http://servermap.test/');
      await page.waitForFunction(()=>!document.querySelector('#go').disabled && !document.querySelector('#planRoute')?.hidden);
      const actions = page.locator('#mapActions');
      assert.equal(await actions.locator('button').count(), 4);
      assert.equal(await page.locator('#locate').count(), 0);
      assert.equal(await page.locator('#planRoute').getAttribute('title'), '添加地图轨迹');
      assert.equal(await page.locator('#measure').getAttribute('title'), '传送器路线规划');
      assert.equal(await actions.locator('.icon-tabler-route').count(), 1);
      assert.equal(await actions.locator('.icon-tabler-map-route').count(), 1);
      const bounds = await actions.boundingBox();
      assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= width, 'toolbar fits viewport');
      for (const id of ['planRoute','measure','home','nearestTranslocator','language']) {
        const box = await page.locator('#'+id).boundingBox();
        assert.ok(box.x >= bounds.x && box.x + box.width <= bounds.x + bounds.width, id+' fits toolbar');
        if (mobile) assert.ok(box.width>=44 && box.height>=44, id+' has touch target');
      }
      if (mobile) {
        assert.ok(!intersects(bounds, await page.locator('#mobileTools').boundingBox()), 'mobile controls do not overlap');
        await page.locator('#mobileMenu').click();
        assert.ok(!intersects(bounds, await page.locator('#sidebar').boundingBox()), 'sidebar does not cover toolbar');
        await page.locator('#mobileSearch').click();
        assert.ok(!intersects(bounds, await page.locator('#topTools').boundingBox()), 'search does not cover toolbar');
        await page.locator('#mobileAnnouncement').click();
        assert.ok(!intersects(bounds, await page.locator('#announcement').boundingBox()), 'news does not cover toolbar');
      } else {
        assert.ok(!intersects(bounds, await page.locator('#sidebar').boundingBox()), 'desktop sidebar does not overlap');
        assert.ok(!intersects(bounds, await page.locator('#topTools').boundingBox()), 'desktop search does not overlap');
      }
      if (process.env.MAP_ACTIONS_SCREENSHOTS) {
        if (mobile) await page.locator('#mobileAnnouncement').click();
        await page.screenshot({path:path.join(process.env.MAP_ACTIONS_SCREENSHOTS,'map-actions-'+width+'.png')});
      }
      const select = page.locator('#language');
      assert.deepEqual(await select.locator('option').evaluateAll(options=>options.map(o=>o.value)), ['zh','en','ru','de','fr','es','pl','pt']);
      for (const language of ['en','ru','de','fr','es','pl','pt','zh']) {
        await select.selectOption(language);
        assert.equal(await select.locator('option').count(), 8, 'switching preserves options');
        assert.equal(await page.evaluate(()=>localStorage.getItem('servermap-language')), language);
        assert.equal(await page.locator('html').getAttribute('lang'), language==='zh'?'zh-CN':language==='pt'?'pt-BR':language);
        if (!['zh','en'].includes(language)) {
          assert.notEqual(await page.locator('#measure').getAttribute('title'), 'Translocator route planning');
          assert.notEqual(await page.locator('#planRoute').getAttribute('title'), 'Add map track');
          assert.notEqual(await page.locator('[data-i18n="mapTypes"]').textContent(), 'Map Types');
        }
        assert.equal(await page.locator('#planRoute svg').count(), 1, 'language changes preserve icon');
      }
      await page.locator('#measure').click();
      assert.equal(await page.locator('#measure').getAttribute('aria-pressed'), 'true');
      assert.equal(await page.locator('#routeInfo').isVisible(), true);
      await page.locator('#planRoute').click();
      assert.equal(await page.locator('#measure').getAttribute('aria-pressed'), 'false');
      assert.equal(await page.locator('#notebookToolbar').isVisible(), true);
      await page.keyboard.press('Escape');
      assert.equal(await page.locator('#notebookToolbar').isVisible(), false);
      await page.locator('#home').click();
      await page.locator('#nearestTranslocator').click();
      if (mobile) await page.locator('#mobileMenu').click();
      for (const renderer of ['basic','sepia']) {
        const icon = page.locator('[data-renderer="'+renderer+'"] .renderer-icon');
        assert.ok((await icon.evaluate(el=>getComputedStyle(el).backgroundImage)).includes(renderer+'.svg'));
        await page.locator('[data-renderer="'+renderer+'"]').click();
      }
      await select.selectOption('de');
      await page.reload();
      await page.waitForFunction(()=>!document.querySelector('#go').disabled);
      assert.equal(await select.inputValue(), 'de', 'language persists after reload');
      if (mobile) await page.locator('#mobileMenu').click();
      await page.locator('#logoutButton').click();
      await page.waitForFunction(()=>document.querySelector('#planRoute').hidden);
      assert.equal(await page.locator('#planRoute').isVisible(), false, 'track button hidden after logout');
      assert.equal(await page.locator('#measure').isVisible(), true);
      assert.deepEqual(errors, []);
      console.log('PASS map actions '+width+'x'+height);
      await page.close();
    }
  } finally { await browser.close(); }
}
main().catch(error=>{console.error(error);process.exitCode=1;});
