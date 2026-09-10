const assert=require('node:assert/strict');
const {geometry:g}=require('../LauncherGo.ServerMapHost/WebRoot/area-markers.js');
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
console.log('Area geometry: randomized paint/erase, neighbour protection, holes, seam cancellation and sparse large areas passed.');
