/*
 * Translocator route-planner core logic.
 *
 * Pure, dependency-free routing math shared by the browser map
 * (js/features/routing.js) and the Node test-suite. Loads as a global
 * (window.RoutePlanner) in the browser and as a CommonJS module
 * (require('./route.js')) under Node.
 */
(function (root, factory) {
    if (typeof module !== 'undefined' && module.exports) {
        module.exports = factory();
    } else {
        root.RoutePlanner = factory();
    }
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {

    // ----------------------------------------------------------------------
    // Internal helpers: a binary min-heap and a uniform spatial grid.
    //
    // The routing graph is geometrically a *complete* walk graph (you may walk
    // straight between any two points), so a naive Dijkstra is O(N^2) in both
    // the min-extraction and the relaxation. With thousands of translocators
    // that quadratic blows up and freezes the browser. We avoid it by:
    //   * pulling the cheapest frontier node from a binary heap, and
    //   * only relaxing endpoints that lie within the remaining travel budget,
    //     looked up through a spatial grid instead of scanning all N nodes.
    // A finite distance budget makes both bounded, turning the search into work
    // proportional to the *reachable neighbourhood* rather than the whole map.
    // ----------------------------------------------------------------------

    function MinHeap() { this.a = []; }
    MinHeap.prototype.size = function () { return this.a.length; };
    MinHeap.prototype.push = function (cost, node) {
        let a = this.a;
        a.push([cost, node]);
        let i = a.length - 1;
        while (i > 0) {
            let p = (i - 1) >> 1;
            if (a[p][0] <= a[i][0]) break;
            let t = a[p]; a[p] = a[i]; a[i] = t;
            i = p;
        }
    };
    MinHeap.prototype.pop = function () {
        let a = this.a;
        if (a.length === 0) return null;
        let top = a[0];
        let last = a.pop();
        if (a.length) {
            a[0] = last;
            let i = 0, n = a.length;
            for (;;) {
                let l = 2 * i + 1, r = l + 1, s = i;
                if (l < n && a[l][0] < a[s][0]) s = l;
                if (r < n && a[r][0] < a[s][0]) s = r;
                if (s === i) break;
                let t = a[s]; a[s] = a[i]; a[i] = t;
                i = s;
            }
        }
        return top;
    };

    // Bucket point indices into a uniform grid (~1 point per cell on average).
    function buildGrid(pts) {
        let n = pts.length;
        if (n === 0) return null;
        let minx = Infinity, miny = Infinity, maxx = -Infinity, maxy = -Infinity;
        for (let i = 0; i < n; i++) {
            let p = pts[i];
            if (p.x < minx) minx = p.x;
            if (p.x > maxx) maxx = p.x;
            if (p.y < miny) miny = p.y;
            if (p.y > maxy) maxy = p.y;
        }
        let w = Math.max(1e-9, maxx - minx), h = Math.max(1e-9, maxy - miny);
        let cell = Math.max(1, Math.sqrt((w * h) / n));
        let cols = Math.max(1, Math.ceil(w / cell));
        let rows = Math.max(1, Math.ceil(h / cell));
        let buckets = new Map();
        for (let i = 0; i < n; i++) {
            let cx = Math.min(cols - 1, Math.floor((pts[i].x - minx) / cell));
            let cy = Math.min(rows - 1, Math.floor((pts[i].y - miny) / cell));
            let k = cx + cy * cols;
            let b = buckets.get(k);
            if (b) b.push(i); else buckets.set(k, [i]);
        }
        return {minx: minx, miny: miny, cell: cell, cols: cols, rows: rows, buckets: buckets};
    }

    // Invoke fn(i) for every point index whose cell overlaps the square of
    // half-side r around (px, py). Callers still verify the exact distance.
    function gridQuery(grid, px, py, r, fn) {
        if (!grid || !(r >= 0)) return;
        let cell = grid.cell, cols = grid.cols, rows = grid.rows;
        let c0 = Math.floor((px - r - grid.minx) / cell);
        let c1 = Math.floor((px + r - grid.minx) / cell);
        let r0 = Math.floor((py - r - grid.miny) / cell);
        let r1 = Math.floor((py + r - grid.miny) / cell);
        if (c0 < 0) c0 = 0;
        if (r0 < 0) r0 = 0;
        if (c1 > cols - 1) c1 = cols - 1;
        if (r1 > rows - 1) r1 = rows - 1;
        for (let cy = r0; cy <= r1; cy++) {
            let base = cy * cols;
            for (let cx = c0; cx <= c1; cx++) {
                let b = grid.buckets.get(base + cx);
                if (!b) continue;
                for (let j = 0; j < b.length; j++) fn(b[j]);
            }
        }
    }

    // Dijkstra over {translocator endpoints + start + end}.
    // Walking between any two nodes costs Euclidean distance; a translocator
    // jump between paired endpoints costs jumpCost.
    //
    //   pts     : [{x, y}, ...]  translocator endpoints
    //   pairs   : pairs[i] = index of the paired endpoint of i
    //   start   : [x, y, height?]
    //   end     : [x, y, height?]
    //   jumpCost: walking-block cost of one translocator jump. May be a single
    //             number (same for every jump) or an array indexed by endpoint,
    //             where jumpCost[u] is the cost of entering the translocator at
    //             endpoint u (penalty applies on entry only).
    //
    // Walking straight from start to end is always an option, so the optimal
    // route never costs more than that direct distance. We use it as the
    // initial budget, tighten it whenever a cheaper route to the destination is
    // found, and stop as soon as the destination is settled.
    //
    // Returns {path, types, allPts, walkDist, jumps} or null if unreachable.
    function computeRouteCore(pts, pairs, start, end, jumpCost) {
        if (jumpCost === undefined) jumpCost = 0;
        let jc = function (u) { return Array.isArray(jumpCost) ? (jumpCost[u] || 0) : jumpCost; };
        let n = pts.length;
        let allPts = pts.concat([{x: start[0], y: start[1], h: start[2] || 0}, {x: end[0], y: end[1], h: end[2] || 0}]);
        let S = n, E = n + 1, N = n + 2;

        let dist = new Array(N).fill(Infinity);
        let prev = new Array(N).fill(-1);
        let prevType = new Array(N).fill(null); // 'walk' | 'tl'
        let visited = new Array(N).fill(false);
        dist[S] = 0;

        let grid = buildGrid(allPts);
        // Upper bound on the answer: the straight-line walk from start to end.
        let bound = Math.hypot(start[0] - end[0], start[1] - end[1], (start[2] || 0) - (end[2] || 0));
        let heap = new MinHeap();
        heap.push(0, S);

        while (heap.size()) {
            let top = heap.pop();
            let du = top[0], u = top[1];
            if (visited[u]) continue;
            if (u === E) break;          // destination settled: shortest found
            if (du > bound) break;       // everything else is beyond the budget
            visited[u] = true;

            // Translocator jump (only for endpoint nodes)
            if (u < n) {
                let v = pairs[u];
                let nd = du + jc(u);
                if (nd < dist[v] && nd <= bound) {
                    dist[v] = nd;
                    prev[v] = u;
                    prevType[v] = 'tl';
                    if (v === E && nd < bound) bound = nd;
                    heap.push(nd, v);
                }
            }
            // Walk to every node within the remaining budget.
            let pu = allPts[u];
            gridQuery(grid, pu.x, pu.y, bound - du, function (v) {
                if (v === u || visited[v]) return;
                let pv = allPts[v];
                let dx = pu.x - pv.x, dy = pu.y - pv.y, dh = (pu.h || 0) - (pv.h || 0);
                let nd = du + Math.sqrt(dx * dx + dy * dy + dh * dh);
                if (nd < dist[v] && nd <= bound) {
                    dist[v] = nd;
                    prev[v] = u;
                    prevType[v] = 'walk';
                    if (v === E && nd < bound) bound = nd;
                    heap.push(nd, v);
                }
            });
        }

        if (dist[E] === Infinity) return null;

        let path = [], types = [];
        let cur = E;
        while (cur !== -1) {
            path.push(cur);
            if (prev[cur] !== -1) types.push(prevType[cur]);
            cur = prev[cur];
        }
        path.reverse();
        types.reverse();

        let walkDist = 0, jumps = 0;
        for (let i = 1; i < path.length; i++) {
            if (types[i - 1] === 'tl') {
                jumps++;
            } else {
                let a = allPts[path[i - 1]], b = allPts[path[i]];
                walkDist += Math.hypot(a.x - b.x, a.y - b.y, (a.h || 0) - (b.h || 0));
            }
        }
        return {path, types, allPts, walkDist, jumps};
    }

    // Single-source shortest travel distances from `start` to every graph node
    // (all translocator endpoints), allowing free walking between any two nodes
    // and translocator jumps (cost jumpCost) between paired endpoints.
    //
    //   pts     : [{x, y}, ...]  translocator endpoints
    //   pairs   : pairs[i] = index of the paired endpoint of i
    //   start   : [x, y]
    //   jumpCost: walking-block cost of one translocator jump. May be a single
    //             number or an array indexed by endpoint (cost of entering the
    //             translocator at that endpoint).
    //
    // Returns {nodes, dist} where nodes = pts + [start] and dist[i] is the
    // shortest travel cost from start to nodes[i]. Use this to measure travel
    // distance to arbitrary off-graph points (e.g. traders): the cost to reach
    // a point p is min over i of dist[i] + euclidean(nodes[i], p).
    //
    //   opts.maxDist : optional budget. When finite, only nodes reachable
    //                  within this travel cost are explored (others stay
    //                  Infinity). This keeps the search proportional to the
    //                  reachable neighbourhood instead of the whole network -
    //                  essential on large maps. Distances <= maxDist are exact.
    function computeSourceDistances(pts, pairs, start, jumpCost, opts) {
        if (jumpCost === undefined) jumpCost = 0;
        let jc = function (u) { return Array.isArray(jumpCost) ? (jumpCost[u] || 0) : jumpCost; };
        let n = pts.length;
        let nodes = pts.concat([{x: start[0], y: start[1]}]);
        let S = n, N = n + 1;
        let maxDist = (opts && typeof opts.maxDist === 'number') ? opts.maxDist : Infinity;

        let dist = new Array(N).fill(Infinity);
        dist[S] = 0;

        if (!isFinite(maxDist)) {
            // Unbounded: dense O(N^2) solver (kept for small inputs / exactness).
            let visited = new Array(N).fill(false);
            for (let iter = 0; iter < N; iter++) {
                let u = -1, best = Infinity;
                for (let i = 0; i < N; i++) {
                    if (!visited[i] && dist[i] < best) { best = dist[i]; u = i; }
                }
                if (u === -1) break;
                visited[u] = true;
                if (u < n) {
                    let v = pairs[u];
                    let nd = dist[u] + jc(u);
                    if (nd < dist[v]) dist[v] = nd;
                }
                let pu = nodes[u];
                for (let v = 0; v < N; v++) {
                    if (v === u || visited[v]) continue;
                    let pv = nodes[v];
                    let dx = pu.x - pv.x, dy = pu.y - pv.y;
                    let nd = dist[u] + Math.sqrt(dx * dx + dy * dy);
                    if (nd < dist[v]) dist[v] = nd;
                }
            }
            return {nodes: nodes, dist: dist};
        }

        // Bounded: heap + spatial grid, exploring only within the budget.
        let visited = new Array(N).fill(false);
        let grid = buildGrid(nodes);
        let heap = new MinHeap();
        heap.push(0, S);
        while (heap.size()) {
            let top = heap.pop();
            let du = top[0], u = top[1];
            if (du > maxDist) break;
            if (visited[u]) continue;
            visited[u] = true;

            // Translocator jump (only for endpoint nodes)
            if (u < n) {
                let v = pairs[u];
                let nd = du + jc(u);
                if (nd < dist[v] && nd <= maxDist) {
                    dist[v] = nd;
                    heap.push(nd, v);
                }
            }
            // Walk to every node within the remaining budget.
            let pu = nodes[u];
            gridQuery(grid, pu.x, pu.y, maxDist - du, function (v) {
                if (v === u || visited[v]) return;
                let pv = nodes[v];
                let dx = pu.x - pv.x, dy = pu.y - pv.y;
                let nd = du + Math.sqrt(dx * dx + dy * dy);
                if (nd < dist[v] && nd <= maxDist) {
                    dist[v] = nd;
                    heap.push(nd, v);
                }
            });
        }

        return {nodes: nodes, dist: dist};
    }

    // Build the {pts, pairs} graph from raw translocator endpoint pairs.
    //   segments: [[[x0, y0], [x1, y1]], ...]
    function buildGraphFromSegments(segments) {
        let pts = [];
        let pairs = [];
        for (let c of segments) {
            if (!c || c.length < 2) continue;
            let i0 = pts.length;
            pts.push({x: c[0][0], y: c[0][1]});
            pts.push({x: c[1][0], y: c[1][1]});
            pairs[i0] = i0 + 1;
            pairs[i0 + 1] = i0;
        }
        return {pts, pairs};
    }

    // Format a map coordinate as in-game "X, Z" (in-game Z = -mapY).
    function fmtGameCoord(p) {
        return Math.round(p.x) + ', ' + Math.round(-p.y);
    }

    return {
        computeRouteCore: computeRouteCore,
        computeSourceDistances: computeSourceDistances,
        buildGraphFromSegments: buildGraphFromSegments,
        fmtGameCoord: fmtGameCoord
    };
});
