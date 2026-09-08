const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');

const webRoot = process.env.MAP_WEB_ROOT ? path.resolve(process.env.MAP_WEB_ROOT) : path.resolve(__dirname, '../LauncherGo.ServerMapHost/WebRoot');
const screenshotRoot = process.env.MAP_SCREENSHOTS;
const point = (coordinates, properties) => ({ type: 'Feature', geometry: { type: 'Point', coordinates }, properties });
const collection = features => ({ type: 'FeatureCollection', features });

async function checkSizes(page) {
    await page.waitForFunction(() => {
        const images = [...document.querySelectorAll('#map img.map-marker')];
        return images.length === 4 && images.every(image => image.complete && image.naturalWidth > 0);
    });
    for (const [icon, size] of [['spawn.png', 20], ['player.svg', 20], ['spiral.svg', 19]]) {
        const boxes = await page.locator(`#map img.map-marker[src="assets/icons/${icon}"]`).evaluateAll(images =>
            images.map(image => ({ width: image.getBoundingClientRect().width, height: image.getBoundingClientRect().height })));
        assert.ok(boxes.length > 0, `Missing ${icon}`);
        // Transformed Leaflet coordinates can introduce subpixel rounding.
        for (const box of boxes) for (const axis of ['width', 'height'])
            assert.ok(Math.abs(box[axis] - size) < 0.001, `${icon} ${axis}: expected ${size}, got ${box[axis]}`);
    }
    for (const selector of ['.poi-label', '.claim-text']) {
        // Leaflet briefly retains fading tooltips after a layer refresh.
        await page.waitForFunction(value => document.querySelectorAll(value).length === 1, selector);
        await page.locator(selector).waitFor({state:'attached'});
        assert.equal(await page.locator(selector).isVisible(),!(await page.locator('#map').evaluate(e=>e.classList.contains('map-labels-distant'))),selector+' distant visibility');
        const style=await page.locator(selector).evaluate(element=>{const s=getComputedStyle(element);return{font:s.fontSize,weight:s.fontWeight,stroke:s.webkitTextStrokeWidth,family:s.fontFamily,background:s.backgroundColor};});
        assert.equal(style.font,'13px',selector);assert.equal(style.weight,'600');assert.equal(style.stroke,'1.5px');assert.doesNotMatch(style.family,/Arial Narrow/);assert.equal(style.background,'rgba(0, 0, 0, 0)');
    }
}

async function main() {
    if (screenshotRoot) await fs.mkdir(screenshotRoot, { recursive: true });
    const browser = await chromium.launch({ headless: true });
    try {
        for (const viewport of [{ width: 1280, height: 800 }, { width: 390, height: 844 }]) {
            for (const zoom of [12, 6, 3, 2, 0, -3]) {
                const page = await browser.newPage({ viewport, locale: 'zh-CN' });
                await page.addInitScript(() => localStorage.setItem('servermap-pinned', 'unpinned'));
                await page.addInitScript(()=>{window.copied=[];Object.defineProperty(navigator,'clipboard',{value:{writeText:async value=>window.copied.push(value)},configurable:true});});
                const errors = [];
                page.on('pageerror', error => errors.push(error.message));
                // Keep fixtures separated in screen space at each initial zoom.
                const resolution = 2 ** zoom;
                const xy = (x, z) => [x * resolution, z * resolution];
                const fixtures = {
                    spawn: [point([0, 0], { name: 'Spawn', y: 110 })],
                    players: [point(xy(-80, 55), { name: 'Player', y: 110, yaw: 0 })],
                    pois: [{ ...point(xy(75, -40), { name: '\u5730\u70b9\u6807\u8bb0', rotation: 30, editable: true }), id: 'test-poi' }],
                    claims: [{ type: 'Feature', geometry: { type: 'Polygon', coordinates: [[xy(-120, -90), xy(-40, -90), xy(-40, -40), xy(-120, -40), xy(-120, -90)]] }, properties: { name: '\u9886\u5730\u6587\u5b57', owner: 'Player' } }],
                    translocators: [{ type: 'Feature', geometry: { type: 'LineString', coordinates: [xy(-65, 115), xy(65, 115)] }, properties: { y: 110, targetY: 110 } }]
                };
                await page.route('http://servermap.test/**', async route => {
                    const url = new URL(route.request().url());
                    const json = value => route.fulfill({ json: value });
                    if (url.pathname === '/api/v1/map/metadata') return json({ maxZoom: 12, maxZoomOut: 12, spawn: { x: 0, z: 0 }, center: { x: 0, z: 0 }, updatedAt: '2026-09-08T00:00:00Z', serverMapVersion: 'test', tileVersion: 'test', colormapReady: true });
                    if (url.pathname === '/api/v1/layers/manifest') return json({ layers: Object.keys(fixtures).map(id => ({ id, visible: true })) });
                    if (url.pathname.startsWith('/api/v1/layers/')) return json(collection(fixtures[url.pathname.split('/').pop()] || []));
                    if (url.pathname === '/api/v1/auth/me') return json({ authenticated: true, admin: true });
                    if (url.pathname === '/api/v1/announcement') return json({ html: '<span></span>' });
                    if (['/api/v1/my-waypoints','/api/v1/routes','/api/v1/hidden-regions'].includes(url.pathname)) return json([]);
                    if (url.pathname === '/api/v1/render-progress') return json({phase:'idle',queued:0});
                    if (url.pathname === '/api/v1/events') return route.fulfill({ contentType: 'text/event-stream', body: ': test\n\n' });
                    if (url.pathname.startsWith('/api/v1/tiles/')) return route.fulfill({ path: path.join(webRoot, 'assets/sky.png'), contentType: 'image/png' });
                    const asset = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
                    const file = path.resolve(webRoot, asset);
                    assert.ok(file.startsWith(webRoot + path.sep), 'Asset escaped WebRoot');
                    const contentType = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png' }[path.extname(file)] || 'text/plain';
                    let body=await fs.readFile(file);
                    if(asset==='index.html')body=body.toString().replace('  (() => {','  const makeMap=L.map;L.map=(...args)=>(window.testMap=makeMap(...args));\n  (() => {');
                    return route.fulfill({ body, contentType });
                });
                await page.goto(`http://servermap.test/?zoom=${zoom}`);
                await checkSizes(page);
                if(zoom===3){
                    for(const selector of ['.poi-label','.claim-text']){
                        await page.evaluate(selector=>{const layers=Object.values(window.testMap._layers),layer=layers.find(l=>selector==='.poi-label'?l.getElement?.()?.querySelector('.poi-label'):l.getTooltip?.()?.getElement()?.matches(selector));layer.openPopup();},selector);
                        await page.waitForFunction(selector=>getComputedStyle(document.querySelector(selector)).visibility==='visible',selector);
                        assert.equal(await page.locator(selector).evaluate(e=>e.classList.contains('map-label-selected')),true);
                        await page.evaluate(()=>window.testMap.setZoom(8,{animate:false}));
                        assert.equal(await page.locator(selector).isVisible(),true,'Selection survives zoom out');
                        await page.evaluate(()=>window.testMap.closePopup());
                        assert.equal(await page.locator(selector).isVisible(),false,'Deselected distant text hides again');
                        await page.evaluate(()=>window.testMap.setZoom(9,{animate:false}));
                    }
                    await page.evaluate(()=>{document.querySelector('#measure').click();window.testMap.fire('click',{latlng:window.testMap.getCenter()});});
                    const endpoint=page.locator('.route-marker-label');await endpoint.waitFor({state:'attached'});assert.equal(await endpoint.isVisible(),false);
                    await page.evaluate(()=>Object.values(window.testMap._layers).find(l=>l.options.routeEndpoint==='start').fire('click'));
                    assert.equal(await endpoint.isVisible(),true,'Clicked route endpoint remains labeled');
                    await page.evaluate(()=>window.testMap.closePopup());assert.equal(await endpoint.isVisible(),false);
                    await page.evaluate(()=>document.querySelector('#measure').click());
                }
                if (screenshotRoot) await page.screenshot({ path: path.join(screenshotRoot, `${viewport.width}-zoom-${zoom}.png`) });
                if(zoom===0){
                    assert.equal(await page.locator('label[for="notebook-toggle-myMarkers"]').textContent(),'游戏标记');
                    await page.evaluate(()=>Object.values(window.testMap._layers).find(l=>l.getElement?.()?.querySelector('.poi-label')).openPopup());
                    const popup=page.locator('.leaflet-popup .notebook-popup');
                    const buttons=await popup.locator('button').evaluateAll(bs=>bs.map(b=>({label:b.getAttribute('aria-label'),title:b.title,text:b.textContent,icons:b.querySelectorAll('svg').length,y:b.getBoundingClientRect().y,width:b.getBoundingClientRect().width})));
                    assert.deepEqual(buttons.map(b=>b.label),['复制地点链接','编辑地点标记']);
                    assert.equal(new Set(buttons.map(b=>b.y)).size,1);
                    for(const b of buttons){assert.equal(b.text,'');assert.equal(b.icons,1);assert.equal(b.title,b.label);assert.ok(b.width>=(viewport.width<700?44:34));}
                    await popup.getByRole('button',{name:'复制地点链接'}).click();
                    await page.waitForFunction(()=>window.copied.length===1);
                    const placeUrl=new URL(await page.evaluate(()=>window.copied[0]));
                    assert.equal(placeUrl.searchParams.get('x'),'75');assert.equal(placeUrl.searchParams.get('z'),'-40');assert.equal(placeUrl.searchParams.get('point'),'1');
                    await popup.getByRole('button',{name:'编辑地点标记'}).click();
                    assert.equal(await page.locator('#poiNameInput').inputValue(),'地点标记');
                    await page.locator('[data-close-modal="poiModal"]').click();
                    for(const [endpoint,x] of [[0,-65],[1,65]]){
                        await page.evaluate(endpoint=>Object.values(window.testMap._layers).filter(l=>l.getElement?.()?.querySelector('img[src="assets/icons/spiral.svg"]'))[endpoint].openPopup(),endpoint);
                        await page.waitForFunction(()=>document.querySelectorAll('.leaflet-popup .notebook-popup').length===1);
                        assert.equal(await popup.locator('button').count(),1);
                        assert.equal(await popup.locator('button svg').count(),1);
                        await popup.getByRole('button',{name:'复制传送器链接'}).click();
                        await page.waitForFunction(count=>window.copied.length===count,endpoint+2);
                        const url=new URL(await page.evaluate(()=>window.copied.at(-1)));
                        assert.equal(url.searchParams.get('x'),String(x));assert.equal(url.searchParams.get('z'),'115');assert.equal(url.searchParams.get('point'),'1');
                    }
                    await page.evaluate(()=>window.testMap.closePopup());
                    const poi=page.locator('.poi-label');
                    assert.equal(await poi.evaluate(e=>getComputedStyle(e).webkitTextStrokeColor),'rgb(32, 37, 43)');
                    fixtures.pois[0].properties.color='#151b22';
                    await page.evaluate(()=>window.dispatchEvent(new Event('resize')));
                    // Moving the map fetches updated POIs through the regular refresh path.
                    await page.locator('.leaflet-control-zoom-in').click();
                    await page.waitForFunction(()=>document.querySelector('.poi-label')?.style.color==='rgb(21, 27, 34)');
                    assert.equal(await poi.evaluate(e=>getComputedStyle(e).webkitTextStrokeColor),'rgb(244, 246, 248)');
                    await page.locator('.leaflet-control-zoom-out').click();
                    await page.waitForFunction(()=>new URL(location.href).searchParams.get('zoom')==='0'&&!document.querySelector('#map').classList.contains('leaflet-zoom-anim'));
                    if(screenshotRoot){
                        await page.addStyleTag({content:'#map{background:#e8eadf!important}.leaflet-tile-pane{visibility:hidden}'});
                        await page.screenshot({path:path.join(screenshotRoot,`${viewport.width}-labels-light.png`)});
                    }
                }
                const direction = zoom === -3 ? 'out' : 'in';
                await page.locator(`.leaflet-control-zoom-${direction}`).click();
                const expectedZoom = zoom + (direction === 'in' ? -1 : 1);
                await page.waitForFunction(expected => new URL(location.href).searchParams.get('zoom') === String(expected) && !document.querySelector('#map').classList.contains('leaflet-zoom-anim'), expectedZoom);
                await checkSizes(page);
                assert.deepEqual(errors, [], 'Browser errors');
                console.log(`PASS ${viewport.width}x${viewport.height}: zoom ${zoom} -> ${expectedZoom}, distant labels hidden except selection`);
                await page.close();
            }
        }
    } finally {
        await browser.close();
    }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
