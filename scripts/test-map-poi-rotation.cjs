const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const webRoot=path.resolve(__dirname,'../LauncherGo.ServerMapHost/WebRoot');
const point=(id,x,z,rotation,editable)=>({id,type:'Feature',geometry:{type:'Point',coordinates:[x,z]},properties:{name:id,text:'Original description',rotation,color:'#e66c75',editable}});
async function main(){
  const browser=await chromium.launch({headless:true});
  try{
    const page=await browser.newPage({viewport:{width:1280,height:800}}),errors=[],writes=[];
    page.on('pageerror',e=>errors.push(e.message));
    let features=[point('Own',0,0,0,true),point('Other',0,100,30,false),point('Legacy',0,-100,150,true)];
    await page.addInitScript(()=>{localStorage.setItem('servermap-pinned','unpinned');window.EventSource=class extends EventTarget{constructor(){super();window.testEvents=this;}};});
    await page.route('http://servermap.test/**',async route=>{
      const req=route.request(),url=new URL(req.url()),json=value=>route.fulfill({json:value});
      if(url.pathname.endsWith('/map/metadata'))return json({maxZoom:12,maxZoomOut:12,spawn:{x:0,z:0},tileVersion:'test',colormapReady:true});
      if(url.pathname.endsWith('/layers/manifest'))return json({layers:[{id:'pois',visible:true}]});
      if(url.pathname.endsWith('/layers/pois'))return json({features});
      if(url.pathname.endsWith('/pois')&&req.method()!=='GET'){
        const body=req.postDataJSON();writes.push({method:req.method(),body});
        if(req.method()!=='POST')return route.fulfill({status:405,json:{}});
        const feature=features.find(f=>f.id===body.id);feature.properties.rotation=body.rotation;return json({Id:feature.id});
      }
      if(url.pathname.endsWith('/auth/me'))return json({authenticated:true,playerName:'alice',admin:false});
      if(url.pathname.endsWith('/announcement'))return json({html:'<span></span>'});
      if(url.pathname.endsWith('/render-progress'))return json({phase:'idle'});
      if(url.pathname.includes('/tiles/'))return route.fulfill({path:path.join(webRoot,'assets/sky.png'),contentType:'image/png'});
      if(url.pathname.startsWith('/api/'))return json([]);
      const asset=url.pathname==='/'?'index.html':url.pathname.slice(1),file=path.resolve(webRoot,asset);assert.ok(file.startsWith(webRoot+path.sep));
      let body=await fs.readFile(file);if(asset==='index.html')body=body.toString().replace('const map=L.map','const map=window.testMap=L.map');
      return route.fulfill({body,contentType:{'.html':'text/html','.js':'text/javascript','.css':'text/css','.svg':'image/svg+xml','.png':'image/png'}[path.extname(file)]||'text/plain'});
    });
    const label=id=>page.locator(`.poi-label-anchor[title="${id}"] .poi-label`);
    const angle=async(id,expected)=>{await page.waitForFunction(({id,expected})=>document.querySelector(`.poi-label-anchor[title="${id}"] .poi-label`)?.style.transform.includes(`rotate(${expected}deg)`),{id,expected});};
    await page.goto('http://servermap.test/');await label('Own').waitFor();await angle('Legacy',0);await angle('Other',30);
    assert.equal(await page.locator('#poiRotationInput').getAttribute('min'),'-60');assert.equal(await page.locator('#poiRotationInput').getAttribute('max'),'60');
    assert.equal(await page.locator('script[src="poi-rotation.js"],.poi-rotatable,.poi-rotation-hint').count(),0);
    const box=await label('Own').boundingBox(),x=box.x+box.width/2,y=box.y+box.height/2;
    await page.mouse.move(x,y);await page.mouse.down({button:'right'});await page.mouse.move(x+50,y,{steps:5});await page.mouse.up({button:'right'});
    await angle('Own',0);assert.equal(writes.length,0,'Right drag never rotates or saves');
    if(await page.locator('#poiModal').isVisible())await page.locator('#poiModal [data-close-modal]').click();
    await label('Own').click({button:'right'});await page.locator('#poiModal').waitFor({state:'visible'});
    await page.locator('#poiRotationInput').fill('61');await page.locator('#poiForm button[type="submit"]').click();assert.equal(writes.length,0,'Native angle validation rejects out-of-range input');
    await page.locator('#poiRotationInput').fill('-60');await page.locator('#poiForm button[type="submit"]').click();await page.locator('#poiModal').waitFor({state:'hidden'});await angle('Own',-60);
    assert.equal(writes.length,1);assert.equal(writes[0].method,'POST');assert.equal(writes[0].body.rotation,-60);
    await page.reload();await label('Own').waitFor();await angle('Own',-60);await angle('Legacy',0);
    assert.deepEqual(errors,[]);console.log('PASS POI editor rotation: no right-drag rotation or requests, right-click editor preserved, angle bounds, persistence and legacy fallback');
  }finally{await browser.close();}
}
main().catch(error=>{console.error(error);process.exitCode=1;});
