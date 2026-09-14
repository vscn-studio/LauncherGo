const assert = require('node:assert/strict');
const {test} = require('node:test');
const planner = require('../LauncherGo.ServerMapHost/WebRoot/vendor/webcartographer-route.js');

const road = (coordinates, speed = 2.5) => ({geometry: {type: 'LineString', coordinates}, properties: {speedMultiplier: speed}});
const close = (actual, expected) => assert.ok(Math.abs(actual - expected) < 1e-6, `${actual} != ${expected}`);
function plan(features, start, end, roadAware = true) {
    const graph = planner.buildGraphFromSegments([]);
    const roadEdges = roadAware ? planner.addRoadsToGraph(graph, features) : [];
    return planner.computeRouteCore(graph.pts, graph.pairs, start, end, 200, {roadAware, roadEdges});
}

test('roads are optional and follow the real corner instead of accelerating a diagonal', () => {
    const features = [road([[0, 0, 64], [0, 120, 64], [120, 120, 64]])];
    const enabled = plan(features, [0, 0, 64], [120, 120, 64]);
    close(enabled.travelCost, 240 / 2.5);
    close(enabled.walkDist, 240);
    assert.ok(enabled.path.some(i => enabled.allPts[i].x === 0 && enabled.allPts[i].y === 120));
    const disabled = plan(features, [0, 0, 64], [120, 120, 64], false);
    close(disabled.walkDist, Math.hypot(120, 120));
    assert.equal(disabled.path.length, 2);
});

test('branches share one junction and each material keeps its own edge speed', () => {
    const features = [road([[0, 0, 64], [100, 0, 64]], 1.15),
        road([[100, 0, 64], [200, 0, 64]], 2.5), road([[100, 0, 64], [100, 80, 64]], 1.3)];
    const result = plan(features, [0, 0, 64], [200, 0, 64]);
    close(result.travelCost, 100 / 1.15 + 100 / 2.5);
    assert.equal(result.allPts.filter(p => p.x === 100 && p.y === 0 && p.h === 64).length, 1);
    const branch = plan(features, [100, 0, 64], [100, 80, 64]);
    close(branch.travelCost, 80 / 1.3);
});

test('a road between intermediate translocators changes the selected route', () => {
    const graph = planner.buildGraphFromSegments([[[0, 0], [10000, 10000]], [[10500, 10000], [1000, 0]]]);
    graph.pts.forEach(p => p.h = 64);
    const start = [0, 0, 64], end = [1000, 0, 64];
    const disabled = planner.computeRouteCore(graph.pts, graph.pairs, start, end, 300);
    assert.equal(disabled.jumps, 0);
    const roadEdges = planner.addRoadsToGraph(graph, [road([[10000, 10000, 64], [10500, 10000, 64]], 2.5)]);
    const enabled = planner.computeRouteCore(graph.pts, graph.pairs, start, end, 300, {roadAware: true, roadEdges});
    assert.equal(enabled.jumps, 2);
    close(enabled.travelCost, 800);
    close(enabled.walkDist, 500);
});

test('a fast edge is not excluded by the ordinary walking search radius', () => {
    const graph = planner.buildGraphFromSegments([[[0, 0], [860, 0]]]);
    graph.pts.push({x: 100, y: 0}, {x: 1100, y: 0});
    const result = planner.computeRouteCore(graph.pts, graph.pairs, [0, 0], [1100, 0], 10,
        {roadAware: true, roadEdges: [[2, 3, 10]]});
    close(result.travelCost, 200);
    assert.equal(result.jumps, 0);
});

test('more than 4000 scanned points do not discard later roads', () => {
    const graph = planner.buildGraphFromSegments([]);
    const roadEdges = planner.addRoadsToGraph(graph, [road(Array.from({length: 6001}, (_, x) => [x, 0, 64])),
        road([[10000, 0, 64], [11000, 0, 64]], 2)]);
    assert.ok(graph.pts.length < 500, 'straight roads are compacted without dropping branches');
    assert.ok(graph.pts.some(p => p.x === 6000));
    const result = planner.computeRouteCore(graph.pts, graph.pairs, [10000, 0, 64], [11000, 0, 64], 200,
        {roadAware: true, roadEdges});
    close(result.travelCost, 500);
});

test('walking can join and leave the middle of a road', () => {
    const result = plan([road([[0, 0, 64], [1600, 0, 64]], 2)], [160, 16, 64], [1440, 16, 64]);
    assert.ok(result.travelCost < 700);
    const onRoad = result.path.map(i => result.allPts[i]).filter(p => p.y === 0);
    assert.ok(onRoad[0].x > 0 && onRoad.at(-1).x < 1600);
});

test('grade changes and vertically separated junctions are preserved', () => {
    const graph = planner.buildGraphFromSegments([]);
    planner.addRoadsToGraph(graph, [road([[0, 0, 64], [16, 0, 64], [32, 0, 80]]),
        road([[16, 0, 80], [16, 32, 80]])]);
    assert.ok(graph.pts.some(p => p.x === 16 && p.y === 0 && p.h === 64));
    assert.ok(graph.pts.some(p => p.x === 16 && p.y === 0 && p.h === 80));
});

test('malformed geometry cannot create an accelerated shortcut', () => {
    const result = plan([road([[0, 0, 64], [null, 'invalid'], [100, 0, 64]])], [0, 0, 64], [100, 0, 64]);
    close(result.travelCost, 100);
});

test('direct walking remains valid at floating point and zero distance boundaries', () => {
    for (const end of [[1, 1, 1], [0, 0, 0], [3.5789, 12.785, 6.578]]) {
        const result = plan([], [0, 0, 0], end);
        close(result.travelCost, Math.hypot(...end));
        assert.equal(result.jumps, 0);
    }
});

test('spatially pruned routing matches a dense reference solver', () => {
    let seed = 152;
    const random = () => ((seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0) / 2 ** 32);
    for (let trial = 0; trial < 100; trial++) {
        const pts = Array.from({length: 12}, () => ({x: random() * 800, y: random() * 800, h: random() * 100}));
        const pairs = [1, 0, 3, 2], costs = [20, 200, 50, 100];
        const roadEdges = Array.from({length: 7}, (_, i) => [i + 4, i + 5, 1 + random() * 8]);
        const start = [random() * 800, random() * 800, random() * 100];
        const end = [random() * 800, random() * 800, random() * 100];
        const result = planner.computeRouteCore(pts, pairs, start, end, costs, {roadAware: true, roadEdges});
        const nodes = pts.concat([{x: start[0], y: start[1], h: start[2]}, {x: end[0], y: end[1], h: end[2]}]);
        const dist = nodes.map(() => Infinity), visited = new Set();
        dist[pts.length] = 0;
        while (visited.size < nodes.length) {
            let u = -1;
            for (let i = 0; i < nodes.length; i++) if (!visited.has(i) && (u < 0 || dist[i] < dist[u])) u = i;
            visited.add(u);
            for (let v = 0; v < nodes.length; v++) {
                const a = nodes[u], b = nodes[v], distance = Math.hypot(a.x - b.x, a.y - b.y, a.h - b.h);
                let cost = distance;
                if (pairs[u] === v) cost = Math.min(cost, costs[u]);
                for (const [x, y, speed] of roadEdges) if (x === u && y === v || y === u && x === v) cost = Math.min(cost, distance / speed);
                dist[v] = Math.min(dist[v], dist[u] + cost);
            }
        }
        close(result.travelCost, dist.at(-1));
    }
});
