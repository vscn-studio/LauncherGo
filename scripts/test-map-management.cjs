const assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const root=path.resolve(__dirname,'../LauncherGo.ServerMapHost/WebRoot');
async function main(){
 const browser=await chromium.launch({headless:true});
 try{for(const width of [1280,390,320]){
  const page=await browser.newPage({viewport:{width,height:850},locale:'zh-CN',hasTouch:width<700,isMobile:width<700}),errors=[],writes=[],images=[];
  let policy={ImageMaxMb:10,PoiQuota:10,DailyTeleports:0,ImageTypes:['jpeg','png','webp','bmp'],Layers:{}},admin=true,deleted=false;
  const items=[{id:'photo',name:'湖边营地',text:'宁静的清晨',author:'Alice',imageKey:'a'.repeat(32)}],now=Date.now()-120000;
  const track={Id:'track1',PlayerUid:'alice',PlayerName:'Alice',Started:new Date(now).toISOString(),Ended:new Date(now+90000).toISOString(),Samples:Array.from({length:91},(_,i)=>({Time:new Date(now+i*1000).toISOString(),X:i<65?0:i-65,Y:110,Z:0,Yaw:0,Mount:{Name:'Elk',X:0,Y:110,Z:0,Yaw:0}}))};
  page.on('pageerror',e=>errors.push(e.message));
  await page.addInitScript(()=>{window.EventSource=class extends EventTarget{};localStorage.setItem('servermap-pinned','pinned');});
  await page.route('http://servermap.test/**',async route=>{
   const req=route.request(),u=new URL(req.url()),n=u.pathname,json=value=>route.fulfill({json:value});
   if(n.endsWith('/map/metadata'))return json({maxZoom:12,maxZoomOut:12,spawn:{x:0,z:0},colormapReady:true});
   if(n.endsWith('/layers/manifest'))return json({layers:['players','mounts','spawn','pois'].map(id=>({id,visible:true}))});
   if(n.includes('/layers/'))return json({features:[]});
   if(n.endsWith('/auth/me'))return json({authenticated:admin,admin});
   if(n.endsWith('/auth/logout')){admin=false;return json({authenticated:false});}
   if(n.endsWith('/announcement')){
    if(req.method()==='POST'){writes.push(req.postDataJSON());policy=req.postDataJSON().management;}
    return json({html:'<h3>公告</h3>',poiImagesEnabled:true,management:policy});
   }
   if(n.endsWith('/moments'))return json({total:items.length,items});
   if(n.endsWith('/admin/images')){if(req.method()==='DELETE'){items.length=0;return json({removed:true});}return json({total:items.length,items});}
   if(n.endsWith('/admin/online-players'))return json([{uid:'alice',name:'Alice'}]);
   if(n.endsWith('/admin/tracks')){
    if(req.method()==='DELETE'){assert.equal(req.headers()['x-servermap-request'],'1');deleted=true;return json({removed:true});}
    if(req.method()==='POST'){const p=req.postDataJSON();writes.push(p);return json({...track,id:'track1'});}
    return json(u.searchParams.has('id')?track:deleted?[]:[track]);
   }
   if(n.endsWith('/poi-image')){images.push(u.searchParams.get('size'));return route.fulfill({path:path.join(root,'assets/sky.png'),contentType:'image/png'});}
   if(n.includes('/tiles/'))return route.fulfill({path:path.join(root,'assets/sky.png'),contentType:'image/png'});
   if(n.endsWith('/area-markers'))return json({revision:0,markers:[]});
   if(n.startsWith('/api/'))return json([]);
   const file=path.resolve(root,n==='/'?'index.html':n.slice(1));assert.ok(file.startsWith(root+path.sep));let body=await fs.readFile(file);
   if(n==='/')body=body.toString().replace('const map=L.map','const map=window.testMap=L.map');
   return route.fulfill({body,contentType:{'.html':'text/html','.js':'text/javascript','.css':'text/css','.png':'image/png','.svg':'image/svg+xml'}[path.extname(file)]||'text/plain'});
  });
  await page.goto('http://servermap.test/');await page.waitForFunction(()=>!document.querySelector('#go').disabled);
  await page.locator('#momentsButton').click();await page.locator('.moment-card').waitFor();
  assert.equal(await page.locator('.moments-grid').evaluate(el=>getComputedStyle(el).columnCount),width<700?'2':'3');
  const modalBox=await page.locator('#momentsDialog').boundingBox(),headerBox=await page.locator('#momentsDialog header').boundingBox();
  assert.ok(headerBox.y-modalBox.y<3,'header has no extra top padding');
  const searchBox=await page.locator('.moments-search').boundingBox(),titleBox=await page.locator('#momentsDialog h2').boundingBox();
  assert.ok(searchBox.x>=titleBox.x+titleBox.width,'search is beside title');
  await page.mouse.click(modalBox.x+3,modalBox.y+modalBox.height-3);
  assert.equal(await page.locator('#momentsDialog').evaluate(d=>d.open),true,'internal blank space does not close modal');
  await page.locator('.moments-search button').click();await page.locator('.moment-card').waitFor();
  assert.match(await page.locator('.moment-card img').getAttribute('src'),/size=0/);
  assert.match(await page.locator('.moment-caption').textContent(),/湖边营地.*宁静的清晨.*Alice/);
  await page.locator('.moment-card').hover();await page.waitForFunction(()=>getComputedStyle(document.querySelector('.moment-caption')).opacity==='1');
  await page.locator('.moment-card>button').click();assert.match(await page.locator('#poiImageFull').getAttribute('src'),/size=0/);
  await page.locator('#poiImageViewerClose').click();assert.equal(await page.locator('#momentsDialog').evaluate(d=>d.open),true,'image close preserves gallery');
  if(process.env.MAP_ACTIONS_SCREENSHOTS)await page.screenshot({path:path.join(process.env.MAP_ACTIONS_SCREENSHOTS,'moments-'+width+'.png')});
  await page.locator('#momentsDialog .dialog-close').click();
  await page.locator('#momentsButton').click();await page.mouse.click(2,2);await page.locator('#momentsDialog').waitFor({state:'hidden'});
  if(width<700)await page.locator('#mobileMenu').click();await page.locator('#manageButton').click();
  assert.equal(await page.locator('.management-nav button').count(),6);
  await page.locator('[aria-controls="management-uploads"]').click();
  assert.equal(await page.locator('#imageMaxMb').inputValue(),'10');await page.locator('#imageMaxMb').fill('12');
  await page.locator('#imageTypes').fill('.JPG, PNG webp gif avif');
  await page.locator('[aria-controls="management-quotas"]').click();await page.locator('#poiQuota').fill('20');await page.locator('#dailyTeleports').fill('3');
  await page.locator('[aria-controls="management-layers"]').click();
  assert.equal(await page.locator('.management-grid input[type=number]').count(),3);
  await page.locator('input[aria-label="players scale"]').fill('2');await page.locator('input[aria-label="players forced"]').check();await page.locator('input[aria-label="mounts forbidden"]').check();
  if(process.env.MAP_ACTIONS_SCREENSHOTS)await page.screenshot({path:path.join(process.env.MAP_ACTIONS_SCREENSHOTS,'management-'+width+'.png')});
  await page.locator('#manageForm button[type=submit]').click();await page.locator('#manageModal').waitFor({state:'hidden'});
  assert.equal(writes[0].management.imageMaxMb,12);assert.equal(writes[0].management.poiQuota,20);assert.equal(writes[0].management.dailyTeleports,3);
  assert.deepEqual(writes[0].management.imageTypes,['jpeg','png','webp','gif','avif']);assert.equal(writes[0].management.layers.spawn.scale,1);
  assert.equal(await page.locator('[data-layer="players"] input').isDisabled(),true);assert.equal(await page.locator('[data-layer="mounts"] input').isChecked(),false);
  await page.locator('#trackingButton').click();await page.locator('#trackPlayer option').waitFor({state:'attached'});await page.locator('#trackSeconds').fill('120');
  await page.locator('.tracking-controls button').first().click();await page.waitForFunction(()=>document.querySelector('.track-open'));
  await page.locator('.track-open').click();await page.locator('#trackTimeline').waitFor();
  assert.equal(await page.locator('#trackTimeline input[type=range]').count(),0);
  const axis=page.locator('.time-axis'),box=await axis.boundingBox();
  assert.ok(await page.locator('.time-axis-tick').count()>1);assert.equal(await page.locator('.time-axis-stop').count(),1);
  await page.mouse.move(box.x+10,box.y+55);await page.mouse.down();await page.mouse.move(box.x+box.width*.6,box.y+55,{steps:5});await page.mouse.up();
  const value=Number(await axis.getAttribute('aria-valuenow'));assert.ok(Math.abs(value-(now+54000))<1500,'drag seeks along ruler');
  await axis.press('End');assert.equal(Number(await axis.getAttribute('aria-valuenow')),now+90000);
  await axis.press('Home');await axis.press('ArrowRight');assert.equal(Number(await axis.getAttribute('aria-valuenow')),now+1000);
  const beforeZoom=await page.locator('.time-axis-duration').textContent();await page.locator('[aria-label="放大时间轴"]').click();assert.notEqual(await page.locator('.time-axis-duration').textContent(),beforeZoom);
  await page.locator('.time-axis-toolbar button').last().click();
  await page.locator('[aria-label="播放 / 暂停"]').click();await page.waitForFunction(t=>Number(document.querySelector('.time-axis').getAttribute('aria-valuenow'))>t,now+1100);
  await page.locator('[aria-label="播放 / 暂停"]').click();assert.equal(await page.locator('[aria-label="播放 / 暂停"]').getAttribute('aria-pressed'),'false');
  assert.match(await page.locator('#trackTimeline output').textContent(),/Alice/);
  assert.equal(await page.evaluate(()=>Object.values(testMap._layers).filter(l=>l.getTooltip?.()?.getContent?.()?.textContent?.startsWith('停留 ')).length),1);
  await page.locator('#trackTimeline input[type=checkbox]').uncheck();
  assert.equal(await page.locator('.time-axis-mounts').isVisible(),false);
  assert.equal(await page.evaluate(()=>Object.values(testMap._layers).filter(l=>l.getTooltip?.()?.getContent?.()?.textContent==='Elk').length),0);
  if(process.env.MAP_ACTIONS_SCREENSHOTS)await page.screenshot({path:path.join(process.env.MAP_ACTIONS_SCREENSHOTS,'timeline-'+width+'.png')});
  await page.locator('#planRoute').click();await page.locator('#trackTimeline').waitFor({state:'hidden'});assert.equal(await page.locator('#notebookToolbar').isVisible(),true);
  await page.locator('#trackingButton').click();await page.locator('.track-open').click();await page.locator('#trackTimeline').waitFor();assert.equal(await page.locator('#notebookToolbar').isVisible(),false);
  await page.locator('#trackingButton').click();await page.locator('.track-delete').waitFor();
  page.once('dialog',d=>d.dismiss());await page.locator('.track-delete').click();assert.equal(deleted,false);
  page.once('dialog',d=>d.accept());await page.locator('.track-delete').click();await page.locator('.track-row').waitFor({state:'detached'});
  assert.equal(deleted,true);assert.equal(await page.locator('#trackTimeline').isVisible(),false);
  await page.locator('#trackDialog .dialog-close').click();
  if(width<700)await page.locator('#mobileMenu').click();await page.locator('#manageButton').click();await page.locator('[aria-controls="management-images"]').click();await page.locator('.image-management-row').waitFor();
  page.once('dialog',d=>d.accept());await page.locator('.image-management-row button').click();await page.locator('.image-management-row').waitFor({state:'detached'});
  await page.locator('#manageForm [data-close-modal]').click();
  await page.locator('#logoutButton').click();await page.locator('#trackingButton').waitFor({state:'hidden'});
  assert.deepEqual(errors,[]);assert.ok(images.includes('0'));console.log('PASS management/moments/tracking '+width);await page.close();
 }}finally{await browser.close();}
}
main().catch(e=>{console.error(e);process.exitCode=1;});
