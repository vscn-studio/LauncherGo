// Run with test-map-notebook-api.ps1 -GameRoot <game> -TestScript test-map-hidden-regions.cjs.
// Uses the isolated fixture's real tile/auth endpoints and the current WebRoot in Chromium.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const zlib = require('node:zlib');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const api = process.env.MAP_TEST_API;
if (!api) throw Error('Run against the isolated MapNotebook fixture');
const origin = new URL(api).origin;
const webRoot = path.resolve(__dirname, '../LauncherGo.ServerMapHost/WebRoot');
const terrain = [30, 60, 90, 255], transparent = [0, 0, 0, 0];

async function call(route, cookie, body, method = body === undefined ? 'GET' : 'POST') {
  const response = await fetch(api + route, {
    method,
    headers: { ...(cookie ? { Cookie: cookie } : {}), 'Content-Type': 'application/json', 'X-ServerMap-Request': '1' },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  assert.equal(response.status, 200, route);
  return response;
}
function pixel(png, x, y) {
  const chunks = [];
  for (let offset = 8; offset + 12 <= png.length;) {
    const length = png.readUInt32BE(offset);
    if (png.toString('ascii', offset + 4, offset + 8) === 'IDAT') chunks.push(png.subarray(offset + 8, offset + 8 + length));
    offset += 12 + length;
  }
  assert.equal(png.readUInt32BE(16), 512);
  assert.equal(png[25], 6, 'Fixture tiles use RGBA');
  const pixels = zlib.inflateSync(Buffer.concat(chunks)), row = y * (512 * 4 + 1);
  assert.equal(pixels[row], 0, 'Fixture encoder uses unfiltered rows');
  return [...pixels.subarray(row + 1 + x * 4, row + 5 + x * 4)];
}
async function checkTile(cookie, preview, inside, outside, legacy = false) {
  const response = await call(`/${legacy ? '2d' : 'tiles'}/basic/0/0_0.png?hideRegions=${preview}`, cookie);
  assert.equal(response.headers.get('cache-control'), 'no-store');
  assert.match(response.headers.get('vary'), /Cookie/);
  const png = Buffer.from(await response.arrayBuffer());
  assert.deepEqual(pixel(png, 64, 72), inside, `hidden pixel, preview=${preview}, legacy=${legacy}`);
  assert.deepEqual(pixel(png, 1, 1), outside, `outside pixel, preview=${preview}, legacy=${legacy}`);
}
async function checkRenderedTile(page, preview) {
  await page.waitForFunction(preview => {
    const tile = [...document.querySelectorAll('img.leaflet-tile')].find(img => {
      const url = new URL(img.src);
      return url.pathname.endsWith('/tiles/basic/0/0_0.png') && url.searchParams.get('hideRegions') === String(preview) && img.complete && img.naturalWidth;
    });
    if (!tile) return false;
    const canvas = document.createElement('canvas'); canvas.width = canvas.height = 512;
    const ctx = canvas.getContext('2d'); ctx.drawImage(tile, 0, 0);
    return ctx.getImageData(64, 72, 1, 1).data[3] === (preview ? 0 : 255) && ctx.getImageData(1, 1, 1, 1).data[3] === 255;
  }, preview);
  assert.equal(await page.evaluate(() => Object.values(window.testMap._layers).filter(layer => layer instanceof L.TileLayer).length), 1, 'Preview changes must remove stale tile layers');
}
const cells = rects => {
  const result = new Set();
  for (const [x1, z1, x2, z2] of rects) for (let x = x1; x < x2; x++) for (let z = z1; z < z2; z++) result.add(`${x},${z}`);
  return result;
};
async function checkDraft(page, expected) {
  assert.equal(await page.locator('.notebook-region-draft').count(), 1, 'The preview is one merged fill');
  const state = await page.evaluate(() => {
    const svg = document.querySelector('.notebook-region-draft'), box = svg.viewBox.baseVal, shape = svg.querySelector('path');
    const canvas = document.createElement('canvas'); canvas.width = box.width; canvas.height = box.height;
    const ctx = canvas.getContext('2d'); ctx.translate(-box.x, -box.y); ctx.globalAlpha = Number(shape.getAttribute('fill-opacity')); ctx.fill(new Path2D(shape.getAttribute('d')));
    const pixels = ctx.getImageData(0, 0, canvas.width, canvas.height).data, filled = [], alphas = new Set();
    for (let z = 0; z < canvas.height; z++) for (let x = 0; x < canvas.width; x++) {
      const alpha = pixels[(z * canvas.width + x) * 4 + 3];
      if (alpha) { filled.push(`${box.x + x},${box.y + z}`); alphas.add(alpha); }
    }
    const border = Object.values(window.testMap._layers).find(layer => layer.options?.className === 'notebook-region-draft-boundary');
    return { bounds: [box.x, box.y, box.width, box.height], filled, alphas: [...alphas], weight: border.options.weight, edges: border.getLatLngs().map(line => line.map(p => [p.lng * 4096, p.lat * 4096]).flat()) };
  });
  assert.deepEqual(new Set(state.filled), expected, 'Rendered selection matches the union, including holes');
  assert.equal(state.alphas.length, 1, 'Overlaps must not darken or brighten the fill');
  assert.equal(state.weight, 1, 'Selection outline is one screen pixel');
  const edgeKey = (x1, z1, x2, z2) => `${x1},${z1}:${x2},${z2}`;
  const boundary = new Set();
  for (const cell of expected) {
    const [x, z] = cell.split(',').map(Number);
    if (!expected.has(`${x},${z - 1}`)) boundary.add(edgeKey(x, z, x + 1, z));
    if (!expected.has(`${x},${z + 1}`)) boundary.add(edgeKey(x, z + 1, x + 1, z + 1));
    if (!expected.has(`${x - 1},${z}`)) boundary.add(edgeKey(x, z, x, z + 1));
    if (!expected.has(`${x + 1},${z}`)) boundary.add(edgeKey(x + 1, z, x + 1, z + 1));
  }
  const actual = new Set();
  for (const [x1, z1, x2, z2] of state.edges) {
    assert.ok([x1, z1, x2, z2].every(Number.isInteger), 'Edges snap to world pixels');
    for (let x = x1; x < x2; x++) actual.add(edgeKey(x, z1, x + 1, z1));
    for (let z = z1; z < z2; z++) actual.add(edgeKey(x1, z, x1, z + 1));
  }
  assert.deepEqual(actual, boundary, 'Only the outer and hole boundaries remain; no crossing rectangle seams');
  return state;
}
async function checkRubber(page, expected, draftBefore) {
  assert.equal(await page.locator('.notebook-region-rubber').count(), 1, 'Drag preview is one visible rectangle');
  const state=await page.evaluate(()=>{const layer=Object.values(window.testMap._layers).find(layer=>layer.options?.className==='notebook-region-rubber');const bounds=layer.getBounds();return [bounds.getWest()*4096,bounds.getSouth()*4096,bounds.getEast()*4096,bounds.getNorth()*4096];});
  assert.deepEqual(state,expected,'Drag preview retains the full pixel-aligned rectangle, including overlaps and holes');
  assert.deepEqual(await draftPaths(page),draftBefore,'Saved strokes do not merge or erase before pointer release');
  const erasing=await page.locator('[data-region-tool="erase"]').getAttribute('aria-pressed')==='true';
  assert.equal(await page.locator('.notebook-region-rubber').getAttribute('stroke'),erasing?'#ef8989':'#fff');
  return state;
}
const draftPaths=page=>page.evaluate(()=>[...document.querySelectorAll('.notebook-region-draft path,.notebook-region-draft-boundary')].map(p=>p.getAttribute('d')));
async function checkSelection(page, width, cookie) {
  const start = async () => {
    if (width < 700 && await page.locator('#mobileMenu').getAttribute('aria-expanded') === 'true') await page.locator('#mobileMenu').click();
    await page.evaluate(() => { window.testMap.setView([0, 0], 15, { animate: false }); window.testMap.fire('contextmenu', { latlng: window.testMap.getCenter() }); });
    await page.locator('#contextMenu').getByRole('button', { name: '框选隐藏区域', exact: true }).click();
  };
  const drag = async (from, to, expected) => {
    const before=await draftPaths(page),x1=Math.floor(Math.min(from[0],to[0])),z1=Math.floor(Math.min(from[1],to[1]));
    const rect=[x1,z1,Math.max(x1+1,Math.ceil(Math.max(from[0],to[0]))),Math.max(z1+1,Math.ceil(Math.max(from[1],to[1])))];
    const whileHeld=async()=>{
      await checkRubber(page,rect,before);
      if(process.env.MAP_SCREENSHOTS&&from[0]===2&&from[1]===8){await fs.mkdir(process.env.MAP_SCREENSHOTS,{recursive:true});await page.screenshot({path:path.join(process.env.MAP_SCREENSHOTS,`hidden-drag-rectangle-${width}.png`)});}
    };
    const points = await page.evaluate(points => points.map(([x, z]) => {
      const map = window.testMap, p = map.latLngToContainerPoint([z / 4096, x / 4096]), r = map.getContainer().getBoundingClientRect();
      return { x: r.x + p.x, y: r.y + p.y };
    }), [from, to]);
    if (width < 700) {
      const session = await page.context().newCDPSession(page);
      try {
        await session.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [points[0]] });
        await session.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [points[1]] });
        await whileHeld();
        await session.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
      } finally { await session.detach(); }
    } else {
      await page.mouse.move(points[0].x, points[0].y); await page.mouse.down(); await page.mouse.move(points[1].x, points[1].y, { steps: 4 });
      await whileHeld(); await page.mouse.up();
    }
    assert.equal(await page.locator('.notebook-region-rubber').count(),0,'Drag rectangle is removed on release');
    return checkDraft(page, expected);
  };
  const finish = () => page.locator('#notebookToolbar').getByRole('button', { name: '完成', exact: true }).click();
  const tool = name => page.locator('#notebookToolbar').getByRole('button', { name, exact: true }).click();
  const save = async name => {
    assert.equal(await page.locator('#notebookModal input[type="number"]').count(), 0, 'Pixel geometry has no rectangular coordinate inputs');
    await page.locator('#notebookModal input[name="name"]').fill(name);
    await page.locator('#notebookModal button[type="submit"]').click();
    await page.locator('#notebookModal').waitFor({ state: 'hidden' });
    assert.equal(await page.locator('.notebook-region-draft').count(), 0);
    assert.equal(await page.evaluate(() => window.testMap.dragging.enabled()), true);
  };
  await start();
  const single = cells([[-11, -11, -10, -10]]);
  const one = await drag([-10.25, -10.25], [-10.5, -10.5], single);
  assert.deepEqual(one.bounds, [-11, -11, 1, 1], 'A subpixel reverse drag selects exactly one base pixel');
  await finish(); await save(`Pixel ${width}`);
  const allRegions = async () => (await call('/hidden-regions', cookie)).json();
  const singleRegion = (await allRegions()).find(r => r.name === `Pixel ${width}`);
  assert.deepEqual([singleRegion.minX, singleRegion.minZ, singleRegion.maxX, singleRegion.maxZ], [-11, -11, -10, -10]);
  await start();
  const horizontal = cells([[-8, -2, 8, 2]]), cross = cells([[-8, -2, 8, 2], [-2, -8, 2, 8]]);
  await drag([-8, -2], [8, 2], horizontal);
  await tool('撤销');assert.equal(await page.locator('.notebook-region-draft').count(),0);
  assert.equal(await page.locator('#notebookToolbar').getByRole('button',{name:'完成',exact:true}).isDisabled(),true);
  await tool('恢复');await checkDraft(page,horizontal);
  await drag([2, 8], [-2, -8], cross);
  await drag([-8, -2], [8, 2], cross); // Repainting must not introduce overlaps.
  await page.keyboard.press('Control+z');await checkDraft(page,horizontal);
  await page.keyboard.press('Control+Shift+z');await checkDraft(page,cross);
  const expected = new Set([...cross, ...cells([[-12, -12, -9, -9]])]); expected.delete('-11,-11');
  await drag([-12, -12], [-9, -9], expected); // Existing hidden pixels are excluded.
  await tool('擦除');for(const cell of cells([[-1,-1,1,1]]))expected.delete(cell);
  await drag([-1,-1],[1,1],expected);
  await tool('移动地图');assert.equal(await page.evaluate(()=>window.testMap.dragging.enabled()),true);
  const centerBefore=await page.evaluate(()=>window.testMap.getCenter());
  if(width<700){const session=await page.context().newCDPSession(page);try{
    await session.send('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[{x:190,y:510}]});
    await session.send('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:[{x:150,y:540}]});
    await session.send('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});
  }finally{await session.detach();}}
  else{await page.mouse.move(640,510);await page.mouse.down();await page.mouse.move(600,540,{steps:4});await page.mouse.up();}
  await page.waitForFunction(before=>{const now=window.testMap.getCenter();return before.lat!==now.lat||before.lng!==now.lng;},centerBefore);
  await checkDraft(page,expected);await page.evaluate(()=>window.testMap.setView([0,0],15,{animate:false}));
  await tool('框选');assert.equal(await page.evaluate(()=>window.testMap.dragging.enabled()),false);
  if (process.env.MAP_SCREENSHOTS) {
    await fs.mkdir(process.env.MAP_SCREENSHOTS, { recursive: true });
    await page.screenshot({ path: path.join(process.env.MAP_SCREENSHOTS, `hidden-selection-${width}.png`) });
  }
  await finish();
  await page.keyboard.press('Escape'); // Return from properties to drawing, not cancel the draft.
  assert.equal(await page.locator('#notebookModal').isHidden(),true);
  await checkDraft(page, expected);
  await finish(); await save(`Merged ${width}`);
  const saved = (await allRegions()).filter(r => r.name === `Merged ${width}`);
  assert.equal(saved.length,1,'All strokes are saved as ONE hidden region');
  const rects = saved[0].rects.map(r => [r.minX, r.minZ, r.maxX, r.maxZ]);
  assert.deepEqual(cells(rects), expected, 'Every preview piece is saved');
  assert.equal(rects.reduce((sum, [x1, z1, x2, z2]) => sum + (x2 - x1) * (z2 - z1), 0), expected.size, 'Saved pieces do not overlap');
  assert.equal(await page.locator(`.notebook-region-label[data-region-id="${saved[0].id}"]`).count(),1,'Only one name per compound shape');
  await page.evaluate(()=>window.testMap.fire('contextmenu',{latlng:L.latLng(0,0)}));
  assert.equal(await page.locator('#contextMenu').getByRole('button',{name:'编辑隐藏区域',exact:true}).isVisible(),false,'Erased holes do not hit the bounding box');
  // Editing loads all pixels and keeps the same record, with additions and erasure.
  await page.evaluate(region => window.testMap.fire('contextmenu', { latlng: L.latLng(0,6 / 4096), notebookRegion: region }), saved[0]);
  await page.locator('#contextMenu').getByRole('button', { name: '编辑隐藏区域', exact: true }).click();
  assert.equal(await page.locator('#notebookModal').isHidden(),true);await checkDraft(page,expected);
  const edited=new Set([...expected,...cells([[10,10,12,12]])]);await drag([10,10],[12,12],edited);
  await tool('擦除');for(const cell of cells([[-8,-2,-6,2]]))edited.delete(cell);await drag([-8,-2],[-6,2],edited);
  await finish();await save(`Merged ${width}`);
  const record=(await allRegions()).find(r=>r.id===saved[0].id);assert.ok(record);assert.deepEqual(cells(record.rects.map(r=>[r.minX,r.minZ,r.maxX,r.maxZ])),edited);
  assert.equal((await allRegions()).filter(r=>r.name===`Merged ${width}`).length,1);
  await start();await drag([14,14],[15,15],cells([[14,14,15,15]]));await tool('取消');
  assert.equal(await page.locator('.notebook-region-draft').count(),0);assert.equal((await allRegions()).filter(r=>r.name===`Merged ${width}`).length,1);
  for (const region of [singleRegion, ...saved]) await call('/hidden-regions?id=' + region.id, cookie, undefined, 'DELETE');
  console.log(`PASS ${width}px: one-pixel mouse/touch, union, undo/redo, erase, pan, ONE record/label, drawing edits, cancel and holes`);
}
async function checkAlliesPanel(page,width,cookie,bobMapId,bobCookie,requesterUid='admin'){
  assert.equal(await page.locator('#alliesDialog').count(),0,'The old alliance modal is removed');
  await page.locator('#alliesButton').click();await page.locator('#alliesPanel').waitFor({state:'visible'});
  await page.waitForFunction(()=>document.querySelector('#ownMapId').value.length===32&&!document.querySelector('#allyJoin').disabled);
  assert.equal(await page.locator('label[for="ownMapId"]').count(),0,'No visible own-ID label');
  assert.equal(await page.locator('#alliesStatus').textContent(),'双方共享探索（添加盟友）');
  assert.equal(await page.locator('#ownMapId').getAttribute('aria-label'),'自己的玩家地图唯一 ID','The unlabeled input remains accessible');
  const own=await page.locator('#ownMapId').inputValue();assert.notEqual(own,bobMapId);
  const layout=await page.evaluate(()=>{const rect=id=>{const r=document.getElementById(id).getBoundingClientRect();return {x:r.x,y:r.y,right:r.right,bottom:r.bottom};};return {search:rect('searchForm'),form:rect('alliesForm'),input:rect('allyMapId'),join:rect('allyJoin'),panel:rect('alliesPanel')};});
  assert.ok(layout.form.y>=layout.search.bottom&&layout.form.y-layout.search.bottom<20,'Join row is immediately below search');
  assert.ok(layout.join.x>=layout.input.right,'Join is to the right of the ID input');
  assert.ok(layout.panel.x>=0&&layout.panel.right<=width,'Panel fits mobile and desktop');
  await page.locator('#allyMapId').fill(own);await page.locator('#allyJoin').click();
  await page.waitForFunction(()=>document.querySelector('#alliesStatus').textContent.includes('不能加入自己'));
  await page.locator('#allyMapId').fill(bobMapId);await page.locator('#allyMapId').press('Enter');
  await page.locator('.ally-row').waitFor({state:'visible'});
  assert.equal(await page.locator('.ally-name').textContent(),'bob');
  assert.equal(await page.locator('.ally-row button').textContent(),'等待');
  await page.waitForFunction(()=>{const img=document.querySelector('.ally-row img');return img.complete&&img.naturalWidth>0&&img.src.includes('/api/v1/avatars/');});
  const requested=await (await call('/allies',cookie)).json();assert.equal(requested.members.length,1);assert.equal(requested.members[0].pending,'outgoing');
  await call('/allies',bobCookie,{action:'accept',uid:requesterUid});
  await page.evaluate(()=>window.testEvents.dispatchEvent(new MessageEvent('visibility')));
  await page.waitForFunction(()=>document.querySelector('.ally-row button')?.textContent==='删除');
  const joined=await (await call('/allies',cookie)).json();assert.equal(joined.members.length,1);assert.equal(joined.members[0].pending,null);
  if(process.env.MAP_SCREENSHOTS)await page.screenshot({path:path.join(process.env.MAP_SCREENSHOTS,`allies-${width}.png`)});
  await page.locator('#alliesButton').click();assert.equal(await page.locator('#alliesPanel').isHidden(),true);
  await page.locator('#alliesButton').click();await page.locator('.ally-row').waitFor({state:'visible'});
  assert.equal(await page.locator('#ownMapId').inputValue(),own);
  await page.locator('.ally-row button').click();await page.locator('.ally-row').waitFor({state:'detached'});
  assert.equal((await (await call('/allies',cookie)).json()).members.length,0);
  await page.keyboard.press('Escape');assert.equal(await page.locator('#alliesPanel').isHidden(),true);
  console.log(`PASS ${width}px: inline map-ID panel, placement, self-validation, Enter request, approval, generated offline avatar and removal`);
}
async function checkBrowser(cookie, metadata, bobMapId, bobCookie) {
  const browser = await chromium.launch({ headless: true });
  try {
    for (const width of [1280, 390]) {
      const context = await browser.newContext({ viewport: { width, height: 844 }, locale: 'zh-CN', isMobile: width < 700, hasTouch: width < 700 });
      const equals = cookie.indexOf('=');
      await context.addCookies([{ name: cookie.slice(0, equals), value: cookie.slice(equals + 1), url: origin }]);
      const page = await context.newPage(), errors = [];
      page.on('pageerror', error => errors.push(error.message));
      await page.addInitScript(() => {
        window.EventSource = class extends EventTarget { constructor() { super(); window.testEvents = this; } };
        localStorage.setItem('servermap-pinned', 'pinned');
      });
      await page.route(origin + '/**', async route => {
        const pathname = new URL(route.request().url()).pathname;
        if (pathname.startsWith('/api/')) return route.continue();
        const file = path.resolve(webRoot, pathname === '/' ? 'index.html' : pathname.slice(1));
        assert.ok(file.startsWith(webRoot + path.sep));
        let body = await fs.readFile(file);
        if (pathname === '/') body = body.toString().replace('const map=L.map', 'const map=window.testMap=L.map');
        await route.fulfill({ body, contentType: { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png', '.svg': 'image/svg+xml' }[path.extname(file)] || 'application/octet-stream' });
      });
      const query = new URLSearchParams({ renderer: 'basic', zoom: '0', x: String(64 - metadata.spawn.x), z: String(72 - metadata.spawn.z) });
      await page.goto(origin + '/?' + query, { waitUntil: 'domcontentloaded' });
      await page.waitForFunction(() => document.querySelector('.notebook-region-label'));
      if (width < 700) await page.locator('#mobileMenu').click();
      const toggle = page.locator('#notebook-toggle-hiddenRegions');
      assert.equal(await toggle.isChecked(), true);
      await checkRenderedTile(page, 1);
      assert.equal(await page.locator('.notebook-fog').getAttribute('fill-opacity'), '0');
      await toggle.uncheck();
      await checkRenderedTile(page, 0);
      assert.equal(await page.locator('.notebook-region-label').count(), 0);
      await page.reload({ waitUntil: 'domcontentloaded' });
      await checkRenderedTile(page, 0);
      assert.equal(await toggle.isChecked(), false, 'Preview preference persists across reload');
      if (width < 700) await page.locator('#mobileMenu').click();
      await toggle.check();
      await checkRenderedTile(page, 1);
      // A visibility event must close private image previews and refresh region data.
      await page.evaluate(() => document.querySelector('#poiImageViewer').showModal());
      const refresh = page.waitForResponse(response => new URL(response.url()).pathname.endsWith('/hidden-regions'));
      await page.evaluate(() => window.testEvents.dispatchEvent(new MessageEvent('visibility')));
      await refresh;
      assert.equal(await page.locator('#poiImageViewer').evaluate(dialog => dialog.open), false);
      await page.waitForFunction(() => document.querySelector('.notebook-region-label'));
      assert.deepEqual(errors, [], 'Authentication and visibility updates must not throw');
      console.log(`PASS ${width}px: transparent preview, toggle, reload, visibility updates`);
      await checkSelection(page, width, cookie);
      await checkAlliesPanel(page, width, cookie, bobMapId, bobCookie);
      if(width===390){
        let release,arrived;const pending=new Promise(resolve=>arrived=resolve),hold=new Promise(resolve=>release=resolve);
        await page.route('**/api/v1/allies',async route=>{const response=await route.fetch();arrived();await hold;await route.fulfill({response});});
        await page.locator('#alliesButton').click();await pending;
        // Simulate a logout while the own-ID/list response is still in flight.
        await page.evaluate(()=>document.getElementById('logoutButton').click());
        await page.waitForFunction(()=>document.getElementById('alliesButton').hidden);
        const completed=page.waitForResponse(response=>new URL(response.url()).pathname.endsWith('/allies'));release();await completed;
        assert.equal(await page.locator('#alliesPanel').isHidden(),true);assert.equal(await page.locator('#ownMapId').inputValue(),'');assert.equal(await page.locator('.ally-row').count(),0);
        console.log('PASS auth race: stale alliance responses cannot restore IDs or members after logout');
      }
      assert.deepEqual(errors, [], 'Drawing and saving must not throw');
      await context.close();
    }
  } finally { await browser.close(); }
}
async function main() {
  const cookies = {};
  for (const playerName of ['admin', 'alice', 'bob']) {
    const response = await call('/auth/login', null, { playerName, password: 'notebook-test-password' });
    cookies[playerName] = response.headers.get('set-cookie').split(';')[0];
  }
  const announcement = await (await call('/announcement')).json();
  const management = Object.fromEntries(Object.entries(announcement.management).map(([key, value]) => [key[0].toLowerCase() + key.slice(1), value]));
  const setBypass = adminsBypassFog => call('/announcement', cookies.admin, { ...announcement, management: { ...management, adminsBypassFog } });
  await call('/hidden-regions', cookies.admin, { name: 'Preview regression', minX: 32, minZ: 32, maxX: 128, maxZ: 128 });
  await setBypass(true);
  for (const legacy of [false, true]) {
    await checkTile(cookies.admin, 1, transparent, terrain, legacy);
    await checkTile(cookies.admin, 0, terrain, terrain, legacy);
  }
  for (const cookie of [cookies.alice, null]) for (const preview of [0, 1]) await checkTile(cookie, preview, transparent, transparent);
  await setBypass(false);
  // Fixture users have no exploration; disabling preview cannot bypass exploration fog.
  for (const preview of [0, 1]) await checkTile(cookies.admin, preview, transparent, transparent);
  await setBypass(true);
  await checkTile(cookies.admin, 1, transparent, terrain);
  console.log('PASS HTTP: hidden-region preview is independent of exploration bypass; guests and players remain masked');
  const metadata = await (await call('/map/metadata')).json();
  await checkCompoundApi(cookies);
  const bobMapId=await checkAllianceApi(cookies,announcement,management);
  await checkBrowser(cookies.admin, metadata,bobMapId,cookies.bob);
}
async function expectStatus(route,cookie,body,status,header=true){
  const response=await fetch(api+route,{method:'POST',headers:{Cookie:cookie||'','Content-Type':'application/json',...(header?{'X-ServerMap-Request':'1'}:{})},body:JSON.stringify(body)});
  assert.equal(response.status,status,route+': '+await response.text());
}
async function checkCompoundApi(cookies){
  const payload={name:'Compound API',rects:[{minX:150,minZ:150,maxX:164,maxZ:152},{minX:150,minZ:160,maxX:164,maxZ:164},{minX:150,minZ:152,maxX:152,maxZ:160},{minX:162,minZ:152,maxX:164,maxZ:160}],hideInGame:true};
  await expectStatus('/hidden-regions',cookies.alice,payload,403);
  const {id}=await (await call('/hidden-regions',cookies.admin,payload)).json();
  const get=async()=> (await (await call('/hidden-regions',cookies.admin)).json()).find(r=>r.id===id);
  assert.ok((await get()).hideInGame);assert.equal((await get()).rects.length,4);
  const png=Buffer.from(await (await call('/tiles/basic/0/0_0.png?hideRegions=1',cookies.admin)).arrayBuffer());
  assert.deepEqual(pixel(png,150,150),transparent);assert.deepEqual(pixel(png,163,163),transparent);
  for(const p of [[160,156],[164,163],[163,164]])assert.deepEqual(pixel(png,...p),terrain,'Holes and exclusive edges stay visible');
  await expectStatus('/hidden-regions',cookies.admin,{...payload,id,rects:[]},400);
  await expectStatus('/hidden-regions',cookies.admin,{...payload,id,rects:[{minX:0.5,minZ:0,maxX:1,maxZ:1}]},400);
  assert.deepEqual((await get()).rects,payload.rects,'Invalid edit never changes the saved shape');
  await call('/hidden-regions?id='+id,cookies.admin,undefined,'DELETE');
  console.log('PASS HTTP: compound geometry, holes, exact edges, authorization and atomic invalid edits');
}
async function checkAllianceApi(cookies,announcement,management){
  const get=async cookie=>(await (await call('/allies',cookie)).json());
  const bob=await get(cookies.bob),alice=await get(cookies.alice);
  assert.match(bob.mapId,/^[0-9a-f]{32}$/);assert.notEqual(bob.mapId,alice.mapId);
  assert.equal((await get(null)).mapId,undefined);assert.equal(bob.candidates,undefined);assert.equal(bob.pending,undefined);
  const add={action:'add',mapId:bob.mapId};
  await expectStatus('/allies',null,add,401);await expectStatus('/allies',cookies.alice,add,403,false);
  await expectStatus('/allies',cookies.alice,add,403); // Sharing defaults to off.
  const sharing=enabled=>call('/announcement',cookies.admin,{...announcement,management:{...management,adminsBypassFog:true,shareExploration:enabled}});
  await sharing(true);
  await expectStatus('/allies',cookies.alice,{action:'invite',uid:'bob'},400);
  await expectStatus('/allies',cookies.alice,{action:'add',mapId:'bad-id'},400);
  await expectStatus('/allies',cookies.alice,{action:'add'},400);
  await expectStatus('/allies',cookies.alice,{action:'add',mapId:42},400);
  await expectStatus('/allies',cookies.alice,{action:'add',mapId:'0'.repeat(32)},404);
  await expectStatus('/allies',cookies.alice,{action:'add',mapId:alice.mapId},400);
  // Bob is offline and has no game-group memberships; Alice has no exploration record.
  await fs.writeFile(path.join(path.dirname(process.env.MAP_TEST_CONTROL),'explore-bob.test'),'1');
  const fog=async cookie=>(await (await call('/fog/regions?minX=150&minZ=150&maxX=180&maxZ=180',cookie)).json()).cells;
  const deadline=Date.now()+10000;while(!(await fog(cookies.bob)).length){assert.ok(Date.now()<deadline);await new Promise(resolve=>setTimeout(resolve,100));}
  assert.equal((await fog(cookies.alice)).length,0);
  const requested=await (await call('/allies',cookies.alice,add)).json();assert.equal(requested.members.length,1);assert.equal(requested.members[0].pending,'outgoing');
  const joined=await (await call('/allies',cookies.bob,{action:'accept',uid:'alice'})).json();assert.equal(joined.members.length,1);assert.equal(joined.members[0].pending,null);assert.equal(joined.members[0].online,false);
  assert.match(joined.members[0].avatar,/^api\/v1\/avatars\/[0-9a-f]{64}\.png$/);
  assert.equal((await get(cookies.bob)).members[0].name,'alice');assert.equal((await get(cookies.bob)).members[0].pending,null);assert.ok((await fog(cookies.alice)).length);
  const tile=async cookie=>Buffer.from(await (await call('/tiles/basic/0/0_0.png',cookie)).arrayBuffer());
  assert.deepEqual(pixel(await tile(cookies.alice),160,160),terrain,'ID alliance actually shares offline exploration without a game group');
  await expectStatus('/allies',cookies.alice,add,409);
  await sharing(false);assert.deepEqual(pixel(await tile(cookies.alice),160,160),transparent);
  await call('/allies',cookies.alice,{action:'remove',uid:'bob'});assert.equal((await get(cookies.bob)).members.length,0);
  await sharing(true);assert.deepEqual(pixel(await tile(cookies.alice),160,160),transparent,'Removal revokes shared visibility');
  assert.equal((await get(cookies.bob)).mapId,bob.mapId);
  console.log('PASS HTTP: stable private map IDs, request/approval flow, offline sharing, validation, permissions, disabled sharing and symmetric removal');
  return bob.mapId;
}
main().catch(error => { console.error(error); process.exitCode = 1; });
