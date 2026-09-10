const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const webRoot=path.resolve(__dirname,'../LauncherGo.ServerMapHost/WebRoot');
async function checkIcon(page,id){
  const state=await page.locator('#'+id).evaluate(el=>{const s=getComputedStyle(el);return{svg:el.querySelectorAll('svg').length,text:el.textContent,label:el.getAttribute('aria-label'),title:el.title,background:s.backgroundColor,border:s.borderTopWidth,shadow:s.boxShadow};});
  assert.equal(state.svg,1);assert.equal(state.text,'');assert.ok(state.label);assert.equal(state.title,state.label);assert.equal(state.background,'rgba(0, 0, 0, 0)');assert.equal(state.border,'0px');assert.equal(state.shadow,'none');
}
async function checkRemovePosition(page){
  await page.waitForFunction(()=>{const image=document.getElementById('poiImagePreview');return image.complete&&image.naturalWidth>0;});
  const state=await page.evaluate(()=>{const image=document.getElementById('poiImagePreview').getBoundingClientRect(),button=document.getElementById('poiImageRemove').getBoundingClientRect();return{right:button.right-image.right,top:button.top-image.top};});
  assert.ok(Math.abs(state.right)<1,JSON.stringify(state));assert.ok(Math.abs(state.top)<1,JSON.stringify(state));await checkIcon(page,'poiImageRemove');
}
async function checkThumbnailRatios(page){
  for(const [width,height] of [[400,100],[100,400],[150,150]]){
    await page.locator('.poi-image-button img').evaluate((img,{width,height})=>{const canvas=document.createElement('canvas');canvas.width=width;canvas.height=height;canvas.getContext('2d').fillRect(0,0,width,height);img.src=canvas.toDataURL();},{width,height});
    await page.waitForFunction(width=>document.querySelector('.poi-image-button img').naturalWidth===width,width);
    const state=await page.locator('.poi-image-button').evaluate(button=>{const img=button.querySelector('img'),image=img.getBoundingClientRect(),rect=button.getBoundingClientRect();return{ratio:image.width/image.height,width:rect.width-image.width,height:rect.height-image.height,background:getComputedStyle(button).backgroundColor};});
    assert.ok(Math.abs(state.ratio-width/height)<.01,JSON.stringify(state));assert.ok(Math.abs(state.width)<1);assert.ok(Math.abs(state.height)<1);assert.equal(state.background,'rgba(0, 0, 0, 0)');
  }
}
async function main(){const browser=await chromium.launch({headless:true});try{for(const mobile of [false,true]){
  const page=await browser.newPage({viewport:mobile?{width:390,height:844}:{width:1280,height:800},hasTouch:mobile,isMobile:mobile,locale:'zh-CN'}),errors=[],saves=[];page.on('pageerror',e=>errors.push(e.message));
  let enabled=false,features=[{id:'own',type:'Feature',geometry:{type:'Point',coordinates:[0,0]},properties:{name:'Own',text:'Photo description',color:'#123456',rotation:0,editable:true,imageKey:'a'.repeat(32)}}];
  await page.addInitScript(()=>{localStorage.setItem('servermap-pinned','pinned');window.EventSource=class extends EventTarget{constructor(){super();window.testEvents=this;}};});
  await page.route('http://servermap.test/**',async route=>{
    const req=route.request(),url=new URL(req.url()),json=value=>route.fulfill({json:value});
    if(url.pathname.endsWith('/map/metadata'))return json({maxZoom:12,maxZoomOut:12,spawn:{x:0,z:0},tileVersion:'test',colormapReady:true});
    if(url.pathname.endsWith('/layers/manifest'))return json({layers:[{id:'pois',visible:true}]});
    if(url.pathname.endsWith('/layers/pois'))return json({features});
    if(url.pathname.endsWith('/auth/me'))return json({authenticated:true,name:'admin',admin:true});
    if(url.pathname.endsWith('/announcement')){if(req.method()==='POST')enabled=req.postDataJSON().poiImagesEnabled;return json({html:'<span></span>',poiImagesEnabled:enabled});}
    if(url.pathname.endsWith('/pois')&&req.method()==='POST'){const data=req.postDataJSON();saves.push(data);if('imageData'in data)features[0].properties.imageKey=data.imageData?'b'.repeat(32):null;return json({Id:'own'});}
    if(url.pathname.endsWith('/poi-image')||url.pathname.includes('/tiles/'))return route.fulfill({path:path.join(webRoot,'assets/sky.png'),contentType:'image/png'});
    if(url.pathname.endsWith('/render-progress'))return json({phase:'idle'});
    if(url.pathname.startsWith('/api/'))return json([]);
    const file=path.resolve(webRoot,url.pathname==='/'?'index.html':url.pathname.slice(1));assert.ok(file.startsWith(webRoot+path.sep));let body=await fs.readFile(file);if(file.endsWith('index.html'))body=body.toString().replace('const map=L.map','const map=window.testMap=L.map');return route.fulfill({body,contentType:{'.html':'text/html','.js':'text/javascript','.css':'text/css','.svg':'image/svg+xml','.png':'image/png'}[path.extname(file)]||'text/plain'});
  });
  await page.goto('http://servermap.test/');const label=page.locator('.poi-label-anchor[title="Own"] .poi-label');await label.waitFor();
  assert.equal(await page.locator('#contextMenu [data-action="poi"]').textContent(),'添加地点标记');
  await page.evaluate(()=>Object.values(testMap._layers).find(l=>l.options.title==='Own').openPopup());assert.equal(await page.locator('.poi-image-button').count(),0);
  await page.locator('.edit-poi').click();await page.locator('#poiModal').waitFor({state:'visible'});assert.equal(await page.locator('#poiImageField').isVisible(),false);await page.locator('#poiModal [data-close-modal]').click();
  if(mobile)await page.locator('#mobileMenu').click();await page.locator('#manageButton').click();await page.locator('#manageModal').waitFor({state:'visible'});assert.equal(await page.locator('#poiImagesEnabledInput').isChecked(),false);await page.locator('#poiImagesEnabledInput').check();await page.locator('#manageForm button[type="submit"]').click();await page.locator('#manageModal').waitFor({state:'hidden'});assert.equal(enabled,true);if(mobile)await page.locator('#mobileMenu').click();
  await page.evaluate(()=>Object.values(testMap._layers).find(l=>l.options.title==='Own').openPopup());await page.locator('.poi-image-button').waitFor();
  const order=await page.locator('.leaflet-popup .notebook-popup').evaluate(el=>[...el.children].slice(0,4).map(x=>x.tagName));assert.deepEqual(order,['B','P','BUTTON','P']);
  assert.match(await page.locator('.poi-image-button img').getAttribute('src'),/size=480/);await page.locator('.poi-image-button').click();await page.locator('#poiImageViewer').waitFor({state:'visible'});assert.match(await page.locator('#poiImageFull').getAttribute('src'),/size=1280/);await checkIcon(page,'poiImageViewerClose');await page.locator('#poiImageViewerClose').click();assert.equal(await page.locator('#poiImageViewer').isVisible(),false);await page.locator('.poi-image-button').click();await page.keyboard.press('Escape');assert.equal(await page.locator('#poiImageViewer').isVisible(),false);
  await checkThumbnailRatios(page);await page.locator('#language').evaluate(el=>el.click());assert.equal(await page.locator('#poiImageViewerClose').getAttribute('aria-label'),'Close image');assert.equal(await page.locator('#poiImageRemove svg').count(),1);await page.locator('#language').evaluate(el=>el.click());
  await page.locator('.edit-poi').click();await page.locator('#poiImageField').waitFor({state:'visible'});await checkRemovePosition(page);
  const image=await page.evaluate(async()=>{const c=document.createElement('canvas');c.width=800;c.height=400;c.getContext('2d').fillRect(0,0,800,400);return c.toDataURL('image/png').split(',')[1];});
  const upload=buffer=>page.locator('#poiImageInput').setInputFiles({name:'photo.png',mimeType:'image/png',buffer});
  await upload(Buffer.from('GIF89a'));await page.waitForFunction(()=>document.getElementById('poiImageError').textContent.includes('GIF'));assert.equal(saves.length,0);
  await upload(Buffer.alloc(5*1024*1024+1));await page.waitForFunction(()=>document.getElementById('poiImageError').textContent.includes('5 MB'));
  const large=await page.evaluate(()=>{const c=document.createElement('canvas');c.width=2561;c.height=1;return c.toDataURL().split(',')[1];});await upload(Buffer.from(large,'base64'));await page.waitForFunction(()=>document.getElementById('poiImageError').textContent.includes('2560'));
  await upload(Buffer.from(image,'base64'));await page.locator('#poiImagePreview').waitFor({state:'visible'});await page.waitForFunction(()=>document.getElementById('poiImageError').textContent==='');await checkRemovePosition(page);await page.locator('#poiForm button[type="submit"]').click();await page.locator('#poiModal').waitFor({state:'hidden'});assert.equal(saves.length,1);assert.equal(saves[0].imageData,image);assert.equal(saves[0].id,'own');
  await page.evaluate(()=>Object.values(testMap._layers).find(l=>l.options.title==='Own').openPopup());await page.locator('.edit-poi').click();await page.locator('#poiImageRemove').click();assert.equal(await page.locator('#poiImagePreviewFrame').isVisible(),false);await page.locator('#poiForm button[type="submit"]').click();await page.locator('#poiModal').waitFor({state:'hidden'});assert.equal(saves[1].imageData,null);
  enabled=false;await page.evaluate(()=>testEvents.dispatchEvent(new MessageEvent('settings',{data:JSON.stringify({poiImagesEnabled:false})})));assert.equal(await page.locator('#poiImageField').isVisible(),false);await page.reload();await label.waitFor();assert.equal(await page.locator('#poiImagesEnabledInput').isChecked(),false);
  assert.deepEqual(errors,[]);console.log(`PASS POI images UI ${mobile?'mobile':'desktop'}: default-off switch, menu text, upload validation/preview/save/remove, coordinate-image-description ordering, 480/1280 viewer, icon-only transparent buttons, top-right remove, natural image ratios, localization and Escape`);await page.close();
}}finally{await browser.close();}}
main().catch(error=>{console.error(error);process.exitCode=1;});
