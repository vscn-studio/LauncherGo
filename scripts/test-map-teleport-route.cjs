const route = require('../LauncherGo.ServerMapHost/WebRoot/vendor/webcartographer-route.js');
let input = '';
process.stdin.on('data', value => input += value);
process.stdin.on('end', () => {
  const results = JSON.parse(input).map(test => {
    const graph = route.buildGraphFromSegments(test.links.map(p => [[p.X,p.Z],[p.TargetX,p.TargetZ]]));
    const costs = [];
    test.links.forEach((p,i) => {
      graph.pts[i*2].h=p.Y; graph.pts[i*2+1].h=p.TargetY;
      costs[i*2]=p.Y<64?320:200; costs[i*2+1]=p.TargetY<64?320:200;
    });
    // The browser may return null for a direct-only route at a floating-point
    // distance boundary; such a route cannot authorize a paid teleport either.
    return route.computeRouteCore(graph.pts,graph.pairs,test.start,test.end,costs)?.jumps ?? 0;
  });
  process.stdout.write(JSON.stringify(results));
});
