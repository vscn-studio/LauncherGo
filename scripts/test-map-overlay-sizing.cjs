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
        for (const box of boxes) assert.deepEqual(box, { width: size, height: size }, icon);
    }
    for (const selector of ['.poi-label', '.claim-text']) {
        // Leaflet briefly retains fading tooltips after a layer refresh.
        await page.waitForFunction(value => document.querySelectorAll(value).length === 1, selector);
        await page.locator(selector).waitFor();
        assert.equal(await page.locator(selector).evaluate(element => getComputedStyle(element).fontSize), '10px', selector);
    }
}

async function main() {
    if (screenshotRoot) await fs.mkdir(screenshotRoot, { recursive: true });
    const browser = await chromium.launch({ headless: true });
    try {
        for (const viewport of [{ width: 1280, height: 800 }, { width: 390, height: 844 }]) {
            for (const zoom of [12, 6, 0, -3]) {
                const page = await browser.newPage({ viewport, locale: 'zh-CN' });
                await page.addInitScript(() => localStorage.setItem('servermap-pinned', 'unpinned'));
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
                    if (url.pathname === '/api/v1/events') return route.fulfill({ contentType: 'text/event-stream', body: ': test\n\n' });
                    if (url.pathname.startsWith('/api/v1/tiles/')) return route.fulfill({ path: path.join(webRoot, 'assets/sky.png'), contentType: 'image/png' });
                    const asset = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
                    const file = path.resolve(webRoot, asset);
                    assert.ok(file.startsWith(webRoot + path.sep), 'Asset escaped WebRoot');
                    const contentType = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png' }[path.extname(file)] || 'text/plain';
                    return route.fulfill({ body: await fs.readFile(file), contentType });
                });
                await page.goto(`http://servermap.test/?zoom=${zoom}`);
                await checkSizes(page);
                if (screenshotRoot) await page.screenshot({ path: path.join(screenshotRoot, `${viewport.width}-zoom-${zoom}.png`) });
                const direction = zoom === -3 ? 'out' : 'in';
                await page.locator(`.leaflet-control-zoom-${direction}`).click();
                const expectedZoom = zoom + (direction === 'in' ? -1 : 1);
                await page.waitForFunction(expected => new URL(location.href).searchParams.get('zoom') === String(expected) && !document.querySelector('#map').classList.contains('leaflet-zoom-anim'), expectedZoom);
                await checkSizes(page);
                assert.deepEqual(errors, [], 'Browser errors');
                console.log(`PASS ${viewport.width}x${viewport.height}: zoom ${zoom} -> ${expectedZoom}, fixed icons and 10px labels`);
                await page.close();
            }
        }
    } finally {
        await browser.close();
    }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
