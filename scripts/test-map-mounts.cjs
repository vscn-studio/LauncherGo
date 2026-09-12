const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const webRoot=path.resolve(__dirname,'../LauncherGo.ServerMapHost/WebRoot');
const mount=(id,x,z,size,yaw=0)=>({id:String(id),type:'Feature',geometry:{type:'Point',coordinates:[x,z]},properties:{name:'Synthetic model '+id,code:'fixture:vehicle',imageKey:'a'.repeat(64),worldSize:size,centerX:4,centerZ:-2,yaw,directions:16,displayScale:size<=10?2:1,kind:'mount'}});
async function main(){
  const browser=await chromium.launch({headless:true});
  try{for(const mobile of [false,true]){
    const context=await browser.newContext({viewport:mobile?{width:390,height:844}:{width:1280,height:800},hasTouch:mobile,isMobile:mobile,locale:'zh-CN'});
    const page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
    // Directional pixels are synthetic, not redistributed game assets.
    const image=Buffer.from(await page.evaluate(()=>{const c=document.createElement('canvas');c.width=c.height=1024;const ctx=c.getContext('2d');for(let i=0;i<16;i++){ctx.save();ctx.translate((i%4)*256+128,Math.floor(i/4)*256+128);ctx.rotate(-i*Math.PI/8);ctx.fillStyle='#ff0022';ctx.fillRect(0,-28,110,56);ctx.fillStyle='#0044ff';ctx.fillRect(-110,-28,110,56);ctx.restore();}return c.toDataURL('image/png').split(',')[1];}),'base64');
    let fixtures=[mount(1,0,0,64),mount(2,-70,-50,10,Math.PI/2)],imageRequests=0;
    await page.addInitScript(()=>{localStorage.setItem('servermap-pinned','unpinned');window.EventSource=class extends EventTarget{constructor(){super();window.events=this;}};});
    await page.route('http://servermap.test/**',async route=>{
      const url=new URL(route.request().url()),json=value=>route.fulfill({json:value});
      if(url.pathname.endsWith('/map/metadata'))return json({maxZoom:12,maxZoomOut:12,spawn:{x:0,z:0},tileVersion:'test',colormapReady:true});
      if(url.pathname.endsWith('/layers/manifest'))return json({layers:[{id:'players',visible:false},{id:'mounts',visible:true}]});
      if(url.pathname.endsWith('/layers/mounts'))return json({features:fixtures});
      if(url.pathname.endsWith('/layers/players'))return json({features:[]});
      if(url.pathname.endsWith('/mount-image')){imageRequests++;return route.fulfill({body:image,contentType:'image/png',headers:{'Cache-Control':'no-store'}});}
      if(url.pathname.endsWith('/auth/me'))return json({authenticated:true,admin:false,name:'alice'});
      if(url.pathname.endsWith('/announcement'))return json({html:'<span></span>'});
      if(url.pathname.endsWith('/render-progress'))return json({phase:'idle'});
      if(url.pathname.includes('/tiles/'))return route.fulfill({path:path.join(webRoot,'assets/icons/spawn.png'),contentType:'image/png'});
      if(url.pathname.startsWith('/api/'))return json([]);
      const asset=url.pathname==='/'?'index.html':url.pathname.slice(1),file=path.resolve(webRoot,asset);assert.ok(file.startsWith(webRoot+path.sep));
      let body=await fs.readFile(file);if(asset==='index.html')body=body.toString().replace('const map=L.map','const map=window.testMap=L.map').replace('async function fetchLayer(name){','window.refreshMounts=()=>fetchLayer("mounts");\n    async function fetchLayer(name){');
      return route.fulfill({body,contentType:{'.html':'text/html','.js':'text/javascript','.css':'text/css','.svg':'image/svg+xml','.png':'image/png'}[path.extname(file)]||'text/plain'});
    });
    await page.goto('http://servermap.test/');
    await page.waitForFunction(()=>document.querySelectorAll('.mount-model-image').length===2&&[...document.querySelectorAll('.mount-model-image')].every(i=>i.complete&&i.naturalWidth===1024));
    assert.equal(await page.locator('[data-layer="mounts"] label').textContent(),'坐骑');
    assert.equal(await page.locator('[data-layer="players"] input').isChecked(),false);
    assert.equal(await page.locator('.mount-model-frame').nth(1).evaluate(e=>parseFloat(e.style.width)),20,'Small mount doubles; large stays unchanged');
    assert.deepEqual(await page.evaluate(()=>[0,Math.PI/8,Math.PI/2,Math.PI,Math.PI*2,-Math.PI/8,-Math.PI*2].map(ServerMapMounts.direction)),[0,1,4,8,0,15,0]);
    const state=()=>page.evaluate(()=>{const marker=Object.values(window.testMap._layers).find(m=>m.updateMount&&m.getElement()?.querySelector('img').alt==='Synthetic model 1'),img=marker.getElement().querySelector('img'),frame=img.parentElement,turn=frame.parentElement,r=frame.getBoundingClientRect(),a=window.testMap.latLngToContainerPoint(marker.getLatLng());return {width:parseFloat(frame.style.width),left:parseFloat(turn.style.left),top:parseFloat(turn.style.top),rotation:turn.style.transform,direction:Number(frame.dataset.direction),atlasX:parseFloat(img.style.left),atlasY:parseFloat(img.style.top),shadow:turn.style.filter,x:marker.getLatLng().lng*4096,z:marker.getLatLng().lat*4096,centerX:r.x+r.width/2-a.x,centerZ:r.y+r.height/2-a.y,bg:getComputedStyle(img).backgroundColor};});
    let s=await state();assert.equal(s.width,64);assert.equal(s.left,-28);assert.equal(s.top,-34);assert.equal(s.centerX,4);assert.equal(s.centerZ,-2);assert.equal(s.direction,0);assert.match(s.shadow,/drop-shadow/);assert.equal(s.bg,'rgba(0, 0, 0, 0)');
    await page.evaluate(()=>window.keptMount=document.querySelector('.mount-model-image'));
    const requestsBefore=imageRequests;fixtures=[mount(1,20,10,64,Math.PI/2),fixtures[1]];
    await page.evaluate(()=>window.refreshMounts());
    s=await state();assert.equal(s.x,20);assert.equal(s.z,10);assert.equal(s.centerX,4);assert.equal(s.centerZ,-2);assert.equal(s.rotation,'');assert.equal(s.direction,4);assert.equal(s.atlasX,0);assert.equal(s.atlasY,-64);
    assert.equal(await page.evaluate(()=>window.keptMount===document.querySelector('.mount-model-image')),true);assert.equal(imageRequests,requestsBefore,'Motion must not reload model PNG');
    for(let direction=0;direction<16;direction++){
      fixtures[0]=mount(1,20,10,64,direction*Math.PI/8);await page.evaluate(()=>window.refreshMounts());
      const frame=await state();assert.equal(frame.direction,direction);assert.equal(frame.width,64);assert.equal(frame.centerX,4);assert.equal(frame.centerZ,-2);
    }
    fixtures[0]=mount(1,20,10,64,Math.PI/2);await page.evaluate(()=>window.refreshMounts());
    assert.equal(imageRequests,requestsBefore,'All sixteen headings reuse the same atlas');
    await page.evaluate(()=>window.testMap.setZoom(10,{animate:false}));
    await page.waitForFunction(()=>parseFloat(document.querySelector('.mount-model-frame').style.width)===16);
    s=await state();assert.equal(s.width,16);assert.equal(s.left,-7);assert.ok(Math.abs(s.centerZ+.5)<.01);
    await page.evaluate(()=>window.testMap.setZoom(15,{animate:false}));await page.waitForFunction(()=>parseFloat(document.querySelector('.mount-model-frame').style.width)===512);
    await page.evaluate(()=>window.testMap.setZoom(12,{animate:false}));await page.waitForFunction(()=>parseFloat(document.querySelector('.mount-model-frame').style.width)===64);
    // A clockwise screen error would put the red (+X) end below, not above, the origin.
    const exported=await page.evaluate(async()=>{
      const map=window.testMap,p=map.latLngToContainerPoint([10/4096,20/4096]),bounds=L.latLngBounds(map.containerPointToLatLng([p.x-40,p.y-40]),map.containerPointToLatLng([p.x+40,p.y+40]));
      const tileLayer=Object.values(map._layers).find(l=>l instanceof L.TileLayer),{blob}=await ServerMapScreenshot.capture({map,bounds,tileLayer,nativeZoom:12,signal:new AbortController().signal});
      const bitmap=await createImageBitmap(blob),c=document.createElement('canvas');c.width=bitmap.width;c.height=bitmap.height;const ctx=c.getContext('2d');ctx.drawImage(bitmap,0,0);bitmap.close();
      const pixels=ctx.getImageData(0,0,c.width,c.height).data;let above=0,below=0;
      for(let y=0;y<c.height;y++)for(let x=0;x<c.width;x++){const i=(y*c.width+x)*4;if(pixels[i]>245&&pixels[i+1]<10&&pixels[i+2]<50)(y<c.height/2?above++:below++);}
      return {above,below};
    });assert.ok(exported.above>100,'PNG export retains rotated model pixels');assert.equal(exported.below,0);
    const shadowDifference=await page.evaluate(async()=>{
      const map=window.testMap,p=map.latLngToContainerPoint([10/4096,20/4096]),bounds=L.latLngBounds(map.containerPointToLatLng([p.x-40,p.y-40]),map.containerPointToLatLng([p.x+40,p.y+40]));
      const tileLayer=Object.values(map._layers).find(l=>l instanceof L.TileLayer);
      async function pixels(){const {blob}=await ServerMapScreenshot.capture({map,bounds,tileLayer,nativeZoom:12,signal:new AbortController().signal}),bitmap=await createImageBitmap(blob),c=document.createElement('canvas');c.width=bitmap.width;c.height=bitmap.height;const ctx=c.getContext('2d');ctx.drawImage(bitmap,0,0);bitmap.close();return ctx.getImageData(0,0,c.width,c.height).data;}
      const withShadow=await pixels(),turn=document.querySelector('.mount-model-heading'),filter=turn.style.filter;let withoutShadow;
      try{turn.style.filter='none';withoutShadow=await pixels();}finally{turn.style.filter=filter;}
      let darker=0;for(let i=0;i<withShadow.length;i+=4)if(withoutShadow[i]+withoutShadow[i+1]+withoutShadow[i+2]-withShadow[i]-withShadow[i+1]-withShadow[i+2]>5)darker++;
      return darker;
    });assert.ok(shadowDifference>30,'Silhouette shadow survives PNG export');
    if(mobile)await page.locator('#mobileMenu').tap();
    await page.locator('[data-layer="mounts"] input').uncheck({force:true});assert.equal(await page.locator('.mount-model-image').count(),0);
    await page.reload();await page.waitForSelector('[data-layer="mounts"] input',{state:'attached'});
    assert.equal(await page.locator('[data-layer="mounts"] input').isChecked(),false);assert.equal(await page.locator('.mount-model-image').count(),0);
    if(mobile)await page.locator('#mobileMenu').tap();
    await page.locator('[data-layer="mounts"] input').check({force:true});await page.waitForFunction(()=>document.querySelectorAll('.mount-model-image').length===2);
    // The normal polling loop must update mounts even when players are unchecked.
    fixtures=[fixtures[0]];await page.waitForFunction(()=>document.querySelectorAll('.mount-model-image').length===1,null,{timeout:8000});
    fixtures=[{...mount(3,0,0,10),properties:{name:'no model'}}];await page.evaluate(()=>window.refreshMounts());assert.equal(await page.locator('.mount-model-image').count(),0,'Dismount removes image, unavailable model has no icon fallback');
    assert.deepEqual(errors,[]);console.log(`PASS mounts ${mobile?'mobile':'desktop'}: size boost, 16 fixed-camera headings, offset, in-place motion, independent persistent checkbox, dismount, no placeholder, cropped directional atlas PNG export`);
    await context.close();
  }}finally{await browser.close();}
}
main().catch(error=>{console.error(error);process.exitCode=1;});
