const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const {spawnSync} = require('node:child_process');
const {chromium} = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const webRoot = path.resolve(__dirname, '../LauncherGo.ServerMapHost/WebRoot');
const feature = (id, coordinates, properties) => ({id, type: 'Feature', geometry: {type: 'LineString', coordinates}, properties});

async function main() {
    const geometry = spawnSync('dotnet', ['run', '--project', path.join(__dirname, 'test-fixtures/MapRoads/MapRoads.csproj'), '--verbosity', 'quiet',
        ...(process.env.MAP_ROAD_SURFACE ? ['--', '--surface', process.env.MAP_ROAD_SURFACE] : [])], {encoding: 'utf8', maxBuffer: 64 * 1024 * 1024});
    assert.equal(geometry.status, 0, geometry.stderr || geometry.stdout);
    const junction = JSON.parse(geometry.stdout);
    process.stdout.write(geometry.stderr);
    const browser = await chromium.launch({headless: true});
    try {
        for (const mobile of [false, true]) {
            const page = await browser.newPage({viewport: mobile ? {width: 390, height: 844} : {width: 1280, height: 800}, isMobile: mobile, hasTouch: mobile});
            const errors = [];
            page.on('pageerror', e => errors.push(e.message));
            const translocators = [feature('A', [[0, 0], [10000, 10000]], {y: 64, targetY: 64}),
                feature('B', [[10800, 10000], [1000, 0]], {y: 64, targetY: 64})];
            let roads = [feature('slow', [[10000, 10000, 64], [10400, 10000, 64]], {speedMultiplier: 1.3, color: '#FFFFFF'}),
                feature('fast', [[10400, 10000, 64], [10800, 10000, 64]], {speedMultiplier: 2.5, color: '#FFFF00'})];
            let networkRequests = 0, delayNextNetwork = false, pendingNetwork = null;
            await page.addInitScript(() => {
                localStorage.setItem('servermap-pinned', 'unpinned');
                window.EventSource = class extends EventTarget { constructor() { super(); window.testEvents = this; } };
            });
            await page.route('http://servermap.test/**', async request => {
                const url = new URL(request.request().url()), name = url.pathname;
                const json = value => request.fulfill({json: value});
                if (name.endsWith('/map/metadata')) return json({maxZoom: 12, maxZoomOut: 12, spawn: {x: 0, z: 0}, colormapReady: true});
                if (name.endsWith('/layers/manifest')) return json({layers: [{id: 'translocators', visible: true}, {id: 'roads', visible: false}]});
                if (name.endsWith('/layers/translocators')) return json({features: translocators});
                if (name.endsWith('/layers/roads')) {
                    if (!url.searchParams.has('bbox')) {
                        networkRequests++;
                        if (delayNextNetwork) { delayNextNetwork = false; pendingNetwork = request; return; }
                    }
                    return json({features: roads});
                }
                if (name.endsWith('/height')) return json({y: 64});
                if (name.endsWith('/auth/me')) return json({authenticated: false});
                if (name.endsWith('/announcement')) return json({html: '<span></span>'});
                if (name.endsWith('/area-markers')) return json({revision: 0, markers: []});
                if (name.endsWith('/render-progress')) return json({phase: 'idle'});
                if (name.includes('/tiles/')) return request.fulfill({path: path.join(webRoot, 'assets/icons/spawn.png'), contentType: 'image/png'});
                if (name.startsWith('/api/')) return json([]);
                const asset = name === '/' ? 'index.html' : name.slice(1), file = path.resolve(webRoot, asset);
                assert.ok(file.startsWith(webRoot + path.sep));
                let body = await fs.readFile(file);
                if (asset === 'index.html') body = body.toString().replace('const map=L.map', 'const map=window.testMap=L.map')
                    .replace('currentRouteResult=result;', 'currentRouteResult=window.testRoute=result;');
                return request.fulfill({body, contentType: {'.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png'}[path.extname(file)] || 'text/plain'});
            });
            await page.goto('http://servermap.test/');
            const toggle = page.locator('[data-layer="roads"] input');
            await toggle.waitFor({state: 'attached'});
            assert.equal(await toggle.isChecked(), false);
            await page.locator('#measure').click();
            async function selectRoute(start, end) {
                await page.evaluate(([a, b]) => {
                    const map = window.testMap;
                    map.fire('click', {latlng: L.latLng(a[1] / 4096, a[0] / 4096)});
                    map.fire('click', {latlng: L.latLng(b[1] / 4096, b[0] / 4096)});
                }, [start, end]);
            }
            await selectRoute([0, 0], [1000, 0]);
            await page.waitForFunction(() => window.testRoute?.jumps === 0);
            assert.equal(networkRequests, 0, 'inactive roads never load the routing network');
            if (mobile) await page.locator('#mobileMenu').click();
            await toggle.check({force: true});
            await page.waitForFunction(() => window.testRoute?.jumps === 2);
            assert.ok(networkRequests > 0, 'routing includes roads outside the start/end bounds');
            const enabled = await page.evaluate(() => window.testRoute);
            assert.ok(Math.abs(enabled.travelCost - (400 + 400 / 1.3 + 400 / 2.5)) < 1e-6);
            await toggle.uncheck({force: true});
            await page.waitForFunction(() => window.testRoute?.jumps === 0);

            delayNextNetwork = true;
            const pending = page.waitForRequest(r => new URL(r.url()).pathname.endsWith('/layers/roads') && !new URL(r.url()).searchParams.has('bbox'));
            await toggle.check({force: true});
            await pending;
            assert.ok(pendingNetwork);
            await toggle.uncheck({force: true});
            await page.waitForFunction(() => window.testRoute?.jumps === 0);
            await pendingNetwork.fulfill({json: {features: roads}});
            await page.waitForFunction(() => document.querySelector('#routeInfo').textContent.includes('1000'));
            await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
            assert.equal(await page.evaluate(() => window.testRoute.jumps), 0, 'a stale enabled result cannot overwrite a disabled route');

            await toggle.check({force: true});
            await page.waitForFunction(() => window.testRoute?.jumps === 2);
            roads = roads.map(f => ({...f, properties: {...f.properties, speedMultiplier: 1}}));
            await page.evaluate(() => window.testEvents.dispatchEvent(new MessageEvent('layer', {data: JSON.stringify({layer: 'roads'})})));
            await page.waitForFunction(() => window.testRoute?.jumps === 0);

            roads = [feature('corner', [[0, 0, 64], [0, 120, 64], [120, 120, 64]], {speedMultiplier: 2.5, color: '#FFFF00'})];
            await page.evaluate(() => window.testEvents.dispatchEvent(new MessageEvent('layer', {data: JSON.stringify({layer: 'roads'})})));
            await selectRoute([0, 0], [120, 120]);
            await page.waitForFunction(() => Math.abs((window.testRoute?.travelCost ?? 0) - 96) < 1e-6);
            const walks = await page.evaluate(() => Object.values(window.testMap._layers).filter(l => l.options.className === 'route-walk').map(l => l.getLatLngs().length));
            assert.equal(walks.length, 1, 'one continuous polyline per walking leg');
            assert.ok(walks[0] > 3, 'the walking line contains its road vertices');
            if (mobile) await page.locator('#mobileMenu').click();
            await page.evaluate(() => window.testMap.setView([60 / 4096, 60 / 4096], 12, {animate: false}));
            const roadPixels = await page.evaluate(() => [...document.querySelectorAll('.leaflet-overlay-pane canvas')].reduce((count, canvas) => {
                const data = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
                for (let i = 0; i < data.length; i += 4) if (data[i] > 220 && data[i + 1] > 175 && data[i + 2] < 120 && data[i + 3] > 100) count++;
                return count;
            }, 0));
            assert.ok(roadPixels > 100, 'the Canvas actually renders the selected road route');
            if (process.env.MAP_ROAD_SCREENSHOTS) {
                await fs.mkdir(process.env.MAP_ROAD_SCREENSHOTS, {recursive: true});
                await page.screenshot({path: path.join(process.env.MAP_ROAD_SCREENSHOTS, mobile ? 'roads-mobile.png' : 'roads-desktop.png')});
            }
            roads = [];
            const beforePrivacy = networkRequests;
            await page.evaluate(() => window.testEvents.dispatchEvent(new Event('visibility')));
            await page.waitForFunction(() => document.querySelector('#measure').getAttribute('aria-pressed') === 'false');
            await page.locator('#measure').click();
            await selectRoute([0, 0], [120, 120]);
            await page.waitForFunction(() => Math.abs((window.testRoute?.travelCost ?? 0) - Math.hypot(120, 120)) < 1e-6);
            assert.ok(networkRequests > beforePrivacy, 'visibility changes discard cached roads before planning again');

            await page.locator('#measure').click();
            if (mobile) await page.locator('#mobileMenu').click();
            await page.locator('[data-layer="translocators"] input').uncheck({force: true});
            if (mobile) await page.locator('#mobileMenu').click();
            roads = junction.features;
            await page.evaluate(() => window.testEvents.dispatchEvent(new MessageEvent('layer', {data: JSON.stringify({layer: 'roads'})})));
            await page.waitForFunction(count => Object.values(window.testMap._layers).filter(layer => layer.options.className === 'road-line').length === count, roads.length);
            const points = roads.flatMap(road => road.geometry.coordinates);
            const min = [Math.min(...points.map(p => p[0])), Math.min(...points.map(p => p[1]))];
            const max = [Math.max(...points.map(p => p[0])), Math.max(...points.map(p => p[1]))];
            await page.evaluate(([a, b, zoom]) => {
                window.testMap.fitBounds([[a[1] / 4096, a[0] / 4096], [b[1] / 4096, b[0] / 4096]], {animate: false, maxZoom: zoom, padding: [80, 80]});
            }, [min, max, mobile ? 14 : 15]);
            await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
            const gaps = await page.evaluate(() => {
                const map = window.testMap, mapRect = map.getContainer().getBoundingClientRect();
                const canvases = [...document.querySelectorAll('.leaflet-overlay-pane canvas')].map(canvas => ({canvas, rect: canvas.getBoundingClientRect(),
                    data: canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data}));
                function painted(point) {
                    for (const {canvas, rect, data} of canvases) {
                        const x = Math.round((point.x + mapRect.left - rect.left) * canvas.width / rect.width);
                        const y = Math.round((point.y + mapRect.top - rect.top) * canvas.height / rect.height);
                        for (let dx = -1; dx <= 1; dx++) for (let dy = -1; dy <= 1; dy++) {
                            if (x + dx < 0 || y + dy < 0 || x + dx >= canvas.width || y + dy >= canvas.height) continue;
                            const i = ((y + dy) * canvas.width + x + dx) * 4;
                            if (data[i] > 200 && data[i + 1] > 200 && data[i + 3] > 80) return true;
                        }
                    }
                    return false;
                }
                const missing = [];
                for (const layer of Object.values(map._layers).filter(layer => layer.options.className === 'road-line')) {
                    const line = layer.getLatLngs().map(point => map.latLngToContainerPoint(point));
                    for (let i = 1; i < line.length; i++) {
                        const a = line[i - 1], b = line[i], steps = Math.max(1, Math.ceil(a.distanceTo(b) * 2));
                        for (let step = 0; step <= steps; step++) {
                            const point = {x: a.x + (b.x - a.x) * step / steps, y: a.y + (b.y - a.y) * step / steps};
                            if (!painted(point)) missing.push(point);
                        }
                    }
                }
                return missing.slice(0, 10);
            });
            assert.deepEqual(gaps, [], 'server-generated road lines render continuously through every junction and material boundary');
            if (process.env.MAP_ROAD_SCREENSHOTS) await page.screenshot({path: path.join(process.env.MAP_ROAD_SCREENSHOTS,
                `roads-${process.env.MAP_ROAD_SURFACE ? 'saved' : 'junction'}-${mobile ? 'mobile' : 'desktop'}.png`)});
            assert.deepEqual(errors, []);
            console.log(`PASS road routing ${mobile ? 'mobile' : 'desktop'}: layer toggle, intermediate jumps, mixed speeds, stale requests, live updates, privacy, continuous corners, server-generated junction Canvas pixels`);
            await page.close();
        }
    } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
