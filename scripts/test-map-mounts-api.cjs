const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
module.exports=async function(call,cookies){
  const features=async cookie=>(await call('/layers/mounts',cookie)).json().features;
  let list=[];
  for(let i=0;i<40;i++){list=await features(cookies.admin);if(list.length===2)break;await new Promise(r=>setTimeout(r,100));}
  assert.equal(list.length,2,'Valid PNGs published; invalid PNG discarded');
  assert.equal((await features()).length,0);assert.equal((await features(cookies.alice)).length,1);assert.equal((await features(cookies.bob)).length,2);
  const boat=list.find(f=>f.id==='9001'),elk=list.find(f=>f.id==='9002');
  assert.ok(boat&&elk);assert.equal(boat.properties.worldSize,8);assert.equal(boat.properties.centerX,0);assert.equal(boat.properties.directions,16);assert.equal(boat.properties.displayScale,2);assert.equal(elk.properties.displayScale,1);assert.ok(Math.abs(boat.properties.yaw-Math.PI/2)<.0001);
  assert.doesNotMatch(JSON.stringify(list),/alice|bob|Alice|Bob(?! elk)/,'No passenger identities in feature payload');
  assert.equal((await call('/layers/mounts?bbox=0,0,1,1',cookies.admin)).json().features.length,0);
  const url=f=>`/mount-image?id=${f.id}&key=${f.properties.imageKey}`;
  for(const cookie of [cookies.alice,cookies.bob,cookies.admin]){
    const result=await call(url(boat),cookie);assert.equal(result.status,200);assert.equal(result.headers.get('content-type'),'image/png');assert.equal(result.headers.get('cache-control'),'no-store');assert.equal(result.headers.get('vary'),'Cookie');assert.equal(result.bytes.readInt32BE(16),1024);assert.equal(result.bytes.readInt32BE(20),1024);
  }
  assert.equal((await call(url(boat))).status,404);assert.equal((await call(url(elk),cookies.alice)).status,404);
  assert.equal((await call(url(boat).replace(/key=.*/, 'key=forged'),cookies.alice)).status,404);
  assert.equal((await call(url(boat),cookies.alice,{},'POST')).status,405);
  // Origin and riders are outside this fog, but the enlarged atlas footprint crosses it.
  const region=await call('/hidden-regions',cookies.admin,{name:'Mount footprint test',minX:2005,minZ:1999,maxX:2006,maxZ:2000});
  assert.equal(region.status,200);const fog=region.json();
  try{
    assert.equal((await features(cookies.alice)).length,0);assert.equal((await call(url(boat),cookies.alice)).status,404);
    assert.equal((await features(cookies.admin)).length,2);assert.equal((await call(url(boat),cookies.admin)).status,200);
  }finally{assert.equal((await call('/hidden-regions?id='+fog.id,cookies.admin,undefined,'DELETE',{'X-ServerMap-Request':'1'})).status,200);}
  assert.equal((await features(cookies.alice)).length,1);
  await fs.writeFile(path.join(path.dirname(process.env.MAP_TEST_CONTROL),'dismount.test'),'1');
  for(let i=0;i<40;i++){if(!(await features(cookies.admin)).length)break;await new Promise(r=>setTimeout(r,100));}
  assert.equal((await features(cookies.admin)).length,0);assert.equal((await call(url(boat),cookies.admin)).status,404,'Old image URL unavailable after dismount');
  console.log('PASS real mount API: decoder, riders/admin/guest permissions, deduplication, no identity leaks, enlarged atlas footprint fog, bbox, no-store, dismount revocation');
};
