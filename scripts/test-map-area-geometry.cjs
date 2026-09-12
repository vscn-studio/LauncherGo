const assert=require('node:assert/strict');
const {geometry:g}=require('../LauncherGo.ServerMapHost/WebRoot/area-markers.js');
const {higherLimit,rangeOverlaps,validRange}=require('../LauncherGo.ServerMapHost/WebRoot/area-markers.js');
assert.deepEqual(g.pixelRect({x:1.1,z:2.1},{x:1.2,z:2.2}),[1,2,2,3]);
assert.deepEqual(g.pixelRect({x:-.2,z:-.2},{x:-.1,z:-.1}),[-1,-1,0,0]);
assert.equal(higherLimit([{minZoom:10},{minZoom:12}]),9);
assert.equal(higherLimit([{minZoom:7},{minZoom:12}]),6);
assert.equal(higherLimit([{minZoom:4},{minZoom:12}]),3);
assert.ok(rangeOverlaps({minZoom:7,maxZoom:10},{minZoom:10,maxZoom:13}));
assert.ok(!rangeOverlaps({minZoom:7,maxZoom:10},{minZoom:11,maxZoom:13}));
assert.ok(validRange(4,4)&&validRange(4,15)&&!validRange(10,9)&&!validRange(4.5,15));
const pixels=rects=>{const result=new Set();for(const [a,b,c,d] of rects)for(let x=a;x<c;x++)for(let z=b;z<d;z++){const k=x+','+z;assert.ok(!result.has(k),'No double-painted pixel');result.add(k);}return result;};
let seed=12345;const rnd=()=>{seed=(Math.imul(seed,1664525)+1013904223)>>>0;return seed;};
let rects=[],expected=new Set();const occupied=[[0,0,4,4]],old=pixels(occupied);
for(let step=0;step<150;step++){
 const x=rnd()%20-10,z=rnd()%20-10,r=[x,z,x+1+rnd()%6,z+1+rnd()%6],erase=!!(rnd()%2);
 for(const k of pixels([r])){if(erase)expected.delete(k);else if(!old.has(k))expected.add(k);}
 rects=g.paint(rects,r,erase,occupied);assert.deepEqual(pixels(rects),expected);
}
assert.deepEqual(g.compact([[0,0,5,10],[5,0,10,10]]),[[0,0,10,10]]);
assert.equal(g.boundary([[0,0,5,10],[5,0,10,10]]).filter(e=>e[0]===5&&e[2]===5).length,0);
const hole=g.paint([[0,0,10,10]],[3,3,7,7],true);assert.equal(pixels(hole).size,84);assert.ok(g.boundary(hole).some(e=>e[0]===3&&e[2]===3));
assert.deepEqual(g.paint([],[-1000000,-1000000,1000000,1000000],false),[[-1000000,-1000000,1000000,1000000]]);
assert.deepEqual(g.largestRectangle([[0,0,10,4],[2,4,8,10]]),[2,0,8,10],'The best rectangle spans multiple fragments');
assert.deepEqual(g.largestRectangle([[0,0,5,10],[5,0,10,10]]),[0,0,10,10]);
assert.deepEqual(g.largestRectangle([[-1000000,-1000000,1000000,0],[-1000000,0,1000000,1000000]]),[-1000000,-1000000,1000000,1000000]);
assert.equal(g.largestRectangle([]),null);
const fogFragments=Array.from({length:2048},(_,i)=>[i*64,i*64,i*64+32,i*64+32]);
fogFragments.push([-1000,-1000,-936,-936]);
assert.deepEqual(g.largestRectangle(fogFragments),[-1000,-1000,-936,-936],'Highly fragmented fog uses a bounded label calculation inside one explored fragment');
function checkLargest(rects){
 const occupied=pixels(rects),xs=rects.flatMap(r=>[r[0],r[2]]),zs=rects.flatMap(r=>[r[1],r[3]]),minX=Math.min(...xs),maxX=Math.max(...xs),minZ=Math.min(...zs),maxZ=Math.max(...zs);let best=0;
 for(let x=minX;x<maxX;x++)for(let z=minZ;z<maxZ;z++)for(let x2=x+1;x2<=maxX;x2++)for(let z2=z+1;z2<=maxZ;z2++){const area=(x2-x)*(z2-z);if(area<=best)continue;let valid=true;for(let xx=x;xx<x2&&valid;xx++)for(let zz=z;zz<z2;zz++)if(!occupied.has(xx+','+zz)){valid=false;break;}if(valid)best=area;}
 const r=g.largestRectangle(rects);assert.equal((r[2]-r[0])*(r[3]-r[1]),best);for(const p of pixels([r]))assert.ok(occupied.has(p),'Label rectangle must never cross a hole');
}
checkLargest(hole);checkLargest([[0,0,2,2],[5,5,8,8]]);
for(let i=0;i<35;i++){let rects=[];for(let j=0;j<8;j++){const x=rnd()%7-3,z=rnd()%7-3;rects=g.paint(rects,[x,z,x+1+rnd()%3,z+1+rnd()%3],false);}checkLargest(rects);}
console.log('Area geometry: randomized paint/erase, neighbour protection, holes, seam cancellation and sparse large areas passed.');
