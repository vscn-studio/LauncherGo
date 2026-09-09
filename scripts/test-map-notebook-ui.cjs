const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const webRoot=path.resolve(__dirname,'../LauncherGo.ServerMapHost/WebRoot');
const routes=new Map(),shares=new Map();let regions=[],sequence=0;
let playerAvatar=null,playerAvatarState='waiting-model';
let announcement={html:'<p>Test</p>',site:{title:'ServerMap',description:'',keywords:'',faviconUrl:''}};
const gameMarkers=new Map(['alice','bob','admin'].map(owner=>[owner,[{id:owner+'-marker',name:owner+' mine',text:'in-game',icon:'pick',iconAvailable:owner!=='alice',color:'#123456',x:40.25,y:110,z:30.75,pinned:true}]])),markerShares=new Map();
let progress={phase:'rendering',queued:12,active:1,completed:24,failed:2,regionsDiscovered:40,awaitingSave:3};
for(const [id,points] of [['saved-a',[[-200,-100],[-100,-100]]],['saved-b',[[100,100],[200,100]]]])routes.set(id,{id,owner:'alice',name:id,color:'#ffd000',points});
const ownRouteIds=page=>page.evaluate(()=>Object.values(window.testMap._layers).filter(l=>l.options?.className==='notebook-route'&&!l.options.notebookShared).map(l=>l.options.notebookRouteId).sort());
async function checkNotebookRows(page){
 const rows=await page.evaluate(()=>{
  const reference=document.querySelector('#layers .layer-row'),heading=document.querySelector('[data-i18n="onlinePlayers"]');
  const style=row=>{const label=row.querySelector('label'),input=row.querySelector('input'),s=getComputedStyle(label),i=getComputedStyle(input);return{font:s.font,color:s.color,height:row.getBoundingClientRect().height,inputWidth:i.width,inputHeight:i.height,inputX:input.getBoundingClientRect().x,accent:i.accentColor};};
  return [...document.querySelectorAll('.notebook-section')].filter(s=>!s.hidden).map(section=>({style:style(section.querySelector('.layer-row')),reference:style(reference),beforePlayers:!!(section.compareDocumentPosition(heading)&Node.DOCUMENT_POSITION_FOLLOWING)}));
 });
 for(const row of rows){assert.equal(row.beforePlayers,true);assert.deepEqual(row.style,row.reference,'Notebook checkboxes must match existing layer controls');}
}
async function checkRoutePopup(page,id,names){
 await page.waitForFunction(id=>Object.values(window.testMap._layers).some(l=>l.options?.notebookRouteId===id),id);
 await page.evaluate(id=>Object.values(window.testMap._layers).find(l=>l.options?.notebookRouteId===id).openPopup(),id);
 const popup=page.locator('.leaflet-popup .notebook-popup');await popup.waitFor();
 const buttons=await popup.locator('button').evaluateAll(bs=>bs.map(b=>({label:b.getAttribute('aria-label'),title:b.title,text:b.textContent,svg:b.querySelectorAll('svg').length,y:b.getBoundingClientRect().y,width:b.getBoundingClientRect().width})));
 assert.deepEqual(buttons.map(b=>b.label),names);assert.equal(new Set(buttons.map(b=>b.y)).size,1,'Popup icon actions must stay on one row');
 for(const b of buttons){assert.equal(b.text,'');assert.equal(b.svg,1);assert.equal(b.title,b.label);assert.ok(b.width>=(page.viewportSize().width<700?44:34));}
}
async function checkToolbarWidth(page){
 const layout=await page.locator('#notebookToolbar').evaluate(bar=>{
  const bounds=bar.getBoundingClientRect(),css=getComputedStyle(bar),help=bar.querySelector('.notebook-help');
  const range=document.createRange();range.selectNodeContents(help);
  const buttons=[...bar.querySelectorAll('button')],actionsWidth=buttons.reduce((sum,b)=>sum+b.getBoundingClientRect().width,0)+Math.max(0,buttons.length-1)*6;
  const padding=parseFloat(css.paddingLeft)+parseFloat(css.paddingRight)+parseFloat(css.borderLeftWidth)+parseFloat(css.borderRightWidth);
  return{width:bounds.width,right:bounds.right,viewport:innerWidth,expected:Math.max(range.getBoundingClientRect().width,actionsWidth)+padding,overflow:bar.scrollWidth>bar.clientWidth};
 });
 assert.ok(layout.right<=layout.viewport&& !layout.overflow,'Toolbar must fit viewport without horizontal scrolling');
 if(layout.viewport>700)assert.ok(Math.abs(layout.width-layout.expected)<2,`Toolbar has redundant width: ${JSON.stringify(layout)}`);
}
async function checkRegionLabels(page){
 assert.equal(await page.locator('.notebook-fog-layer, #notebookFogCanvas, #notebookFogCanvasBack').count(),0,'Cloud canvases must be removed');
 const labels=page.locator('.notebook-region-label');assert.ok(await labels.count()>0);
 const geometry=await labels.evaluateAll(nodes=>nodes.map(svg=>{
  const text=svg.querySelector('text'),bounds=svg.getBoundingClientRect(),rect=text.getBoundingClientRect(),css=getComputedStyle(svg);
  return {bounds:{left:bounds.left,right:bounds.right,top:bounds.top,bottom:bounds.bottom},rect:{left:rect.left,right:rect.right,top:rect.top,bottom:rect.bottom},rotation:svg.querySelector('g').getAttribute('transform'),overflow:css.overflow,clip:css.clipPath,background:css.backgroundColor,animations:svg.getAnimations({subtree:true}).length};
 }));
 for(const item of geometry){
  assert.equal(item.overflow,'hidden');assert.equal(item.clip,'inset(0px)');assert.equal(item.background,'rgba(0, 0, 0, 0)');assert.equal(item.animations,0);assert.match(item.rotation,/rotate\(-/);
  // Chromium rounds tiny SVG glyphs up for rasterization. Their un-clipped
  // DOM bounds are not painted bounds; the viewport clip above is mandatory.
  if(Math.min(item.bounds.right-item.bounds.left,item.bounds.bottom-item.bounds.top)>=10){
   for(const key of ['left','top'])assert.ok(item.rect[key]>=item.bounds[key]-.1,JSON.stringify(item));
   for(const key of ['right','bottom'])assert.ok(item.rect[key]<=item.bounds[key]+.1,JSON.stringify(item));
  }
 }
 const before=await labels.first().evaluate(s=>s.outerHTML);await page.waitForTimeout(300);assert.equal(await labels.first().evaluate(s=>s.outerHTML),before,'Idle labels should not be repainted');
}
async function main(){
 const browser=await chromium.launch({headless:true});
 try{
  async function open(owner,viewport={width:1280,height:800},url='http://servermap.test/'){
   const page=await browser.newPage({viewport,hasTouch:viewport.width<700,isMobile:viewport.width<700,locale:'zh-CN'});const errors=[];page.on('pageerror',e=>errors.push(e.message));page.on('dialog',d=>d.accept());
   await page.addInitScript(()=>{window.EventSource=class extends EventTarget{constructor(){super();window.testEvents=this;}};window.copied=[];Object.defineProperty(navigator,'clipboard',{value:{writeText:async text=>window.copied.push(text)},configurable:true});});
   await page.route('http://servermap.test/**',async route=>{
    const url=new URL(route.request().url()),name=url.pathname,method=route.request().method(),data=route.request().postDataJSON();const json=(value,status=200)=>route.fulfill({json:value,status});
    const visible=points=>owner==='admin'||!regions.some(r=>Math.min(...points.map(p=>p[0]))<=r.maxX&&Math.max(...points.map(p=>p[0]))>=r.minX&&Math.min(...points.map(p=>p[1]))<=r.maxZ&&Math.max(...points.map(p=>p[1]))>=r.minZ);
    if(name.endsWith('/map/metadata'))return json({maxZoom:12,maxZoomOut:12,spawn:{x:0,z:0},center:{x:0,z:0},updatedAt:'2026-09-08',serverMapVersion:'test',tileVersion:'test',colormapReady:true});
    if(name.endsWith('/layers/manifest'))return json({layers:[{id:'spawn',visible:false},{id:'players',visible:true}]});
    if(name.endsWith('/layers/players'))return json({features:[{type:'Feature',id:'avatar-player',geometry:{type:'Point',coordinates:[500,500]},properties:{name:'Avatar player',avatar:playerAvatar,avatarState:playerAvatarState}}]});
    if(name.includes('/avatars/'))return route.fulfill({path:path.join(webRoot,'assets/icons/spawn.png'),contentType:'image/png'});
    if(name.endsWith('/auth/me'))return json({authenticated:!!owner,admin:owner==='admin',name:owner});
    if(name.endsWith('/announcement')){if(method==='POST'){if(owner!=='admin')return json({},403);announcement=data;}return json(announcement);}
    if(name.endsWith('/render-progress'))return json(progress);
    if(name.endsWith('/my-waypoints')){
     if(!owner)return json({},401);const own=gameMarkers.get(owner)||[];
     if(method==='GET')return json(own.filter(m=>visible([[m.x,m.z]])));
     if(method==='DELETE'){gameMarkers.set(owner,own.filter(m=>m.id!==url.searchParams.get('id')));return json({removed:true});}
     const source=data.shareId?markerShares.get(data.shareId):data;if(!source)return json({},404);
     const marker={...source,id:data.shareId?'w'+(++sequence):data.id||'w'+(++sequence),iconAvailable:true};gameMarkers.set(owner,[...own.filter(m=>m.id!==marker.id),marker]);return json(marker);
    }
    if(name.endsWith('/waypoint-options'))return json({enabled:true,icons:[{name:'circle',available:true},{name:'pick',available:true}],colors:['#ffffff','#123456']});
    if(name.endsWith('/waypoint-shares')){if(method==='GET'){const shared=markerShares.get(url.searchParams.get('id'));return shared&&[...gameMarkers.values()].flat().some(m=>m.id===shared.id)?visible([[shared.x,shared.z]])?json(shared):json({},403):json({},404);}const source=(gameMarkers.get(owner)||[]).find(m=>m.id===data.id);if(!source)return json({},404);const id='ms'+(++sequence);markerShares.set(id,structuredClone(source));return json({id});}
    if(name.endsWith('/search')){const q=url.searchParams.get('q').toLowerCase(),found=[];if(owner){if((owner+' mine in-game pick').includes(q)&&visible([[40.25,30.75]]))found.push({kind:'waypoint',id:owner+'-marker',name:owner+' mine',hasLocation:true,x:40.25,z:30.75});for(const r of routes.values())if(r.owner===owner&&r.name.toLowerCase().includes(q)&&visible(r.points))found.push({kind:'route',id:r.id,name:r.name,hasLocation:true,x:r.points[0][0],z:r.points[0][1]});}return json(found);}
    if(name.includes('/waypoint-icons/'))return route.fulfill({contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20"><path fill="white" d="M2 2H18V18H2Z"/></svg>'});
    if(name.endsWith('/routes')){
     if(!owner)return json({},401);
     if(method==='GET')return json([...routes.values()].filter(r=>r.owner===owner&&visible(r.points)));
     if(method==='DELETE'){routes.delete(url.searchParams.get('id'));return json({removed:true});}
     const source=data.shareId?shares.get(data.shareId):data;if(!source)return json({},404);
     const value={...source,id:data.shareId?'r'+(++sequence):data.id||'r'+(++sequence),owner};routes.set(value.id,value);return json(value);
    }
    if(name.endsWith('/route-shares')){if(method==='GET'){const shared=shares.get(url.searchParams.get('id'));return shared?visible(shared.points)?json(shared):json({},403):json({},404);}const id='s'+(++sequence);shares.set(id,structuredClone(routes.get(data.id)));return json({id});}
    if(name.endsWith('/hidden-regions')){if(method==='GET')return json(regions);if(owner!=='admin')return json({},403);if(method==='DELETE'){regions=regions.filter(r=>r.id!==url.searchParams.get('id'));return json({removed:true});}const value={...data,id:data.id||'fog'+(++sequence)};regions=regions.filter(r=>r.id!==value.id).concat(value);return json({id:value.id});}
    if(name.includes('/tiles/'))return route.fulfill({path:path.join(webRoot,'assets/sky.png'),contentType:'image/png'});
    if(name.startsWith('/api/'))return json({});
    const file=path.resolve(webRoot,name==='/'?'index.html':name.slice(1));assert.ok(file.startsWith(webRoot+path.sep));let body=await fs.readFile(file);
    if(name==='/')body=body.toString().replace('  (() => {','  const makeMap=L.map;L.map=(...args)=>(window.testMap=makeMap(...args));\n  (() => {');
    return route.fulfill({body,contentType:{'.html':'text/html','.css':'text/css','.js':'text/javascript','.svg':'image/svg+xml','.png':'image/png'}[path.extname(file)]||'text/plain'});
   });
   await page.goto(url);await page.waitForFunction(()=>document.querySelector('#notebookProgress').dataset.phase==='rendering');
   const assetNotice=page.locator('#contributors [data-i18n="gameAssetNotice"]');
   assert.match(await assetNotice.textContent(),/不随 LauncherGo 安装包、携带版或模组 ZIP 分发/);
   await page.locator('#contributors .right').click();
   assert.equal(await assetNotice.isVisible(),true);
   await page.locator('#contributors .right').click();
   return {page,errors};
  }
  const {page:alice,errors:aliceErrors}=await open('alice');
  await alice.waitForFunction(()=>document.querySelector('#playerList img')?.dataset.avatarState==='waiting-model');
  assert.match(await alice.locator('#playerList img').getAttribute('title'),/采集头部模型/);
  playerAvatarState='capture-failed';
  await alice.waitForFunction(()=>document.querySelector('#playerList img')?.dataset.avatarState==='capture-failed');
  assert.match(await alice.locator('#playerList img').getAttribute('title'),/采集失败/);
  playerAvatar='api/v1/avatars/'+'a'.repeat(64)+'.png';
  await alice.waitForFunction(()=>document.querySelector('#playerList img')?.dataset.avatarState==='ready');
  assert.ok(await alice.locator('#playerList img').evaluate(img=>img.complete&&img.naturalWidth>0&&img.getAttribute('src').startsWith('/api/v1/avatars/')));
  await alice.locator('.leaflet-marker-icon[title="alice mine"]').waitFor();
  await checkNotebookRows(alice);
  await checkRoutePopup(alice,'saved-a',['复制轨迹链接','编辑轨迹','删除轨迹']);
  await alice.locator('.leaflet-popup').getByRole('button',{name:'编辑轨迹',exact:true}).click();
  await alice.locator('#notebookToolbar').getByRole('button',{name:'取消',exact:true}).click();
  assert.equal(await alice.locator('.notebook-section ul').count(),0,'Notebook sections must contain checkboxes, not lists');
  assert.deepEqual(await ownRouteIds(alice),['saved-a','saved-b'],'All saved routes must appear without selecting a list entry');
  await alice.locator('#notebook-myRoutes input').uncheck();assert.deepEqual(await ownRouteIds(alice),[]);
  await alice.locator('#notebook-myRoutes input').check();assert.deepEqual(await ownRouteIds(alice),['saved-a','saved-b']);
  assert.equal(await alice.locator('.notebook-waypoint').evaluate(el=>el.style.backgroundColor),'rgb(18, 52, 86)');
  assert.doesNotMatch(await alice.locator('#notebook-myMarkers').textContent(),/等待同步/);
  await alice.locator('.notebook-info').click();assert.match(await alice.locator('#notebookTooltip').textContent(),/管理员使用新版/);
  await alice.keyboard.press('Escape');assert.equal(await alice.locator('#notebookTooltip').isVisible(),false);
  assert.equal((await alice.locator('#notebookProgress').textContent()).trim(),'');
  for(const [key,detail] of [['failed','失败: 2'],['pending','排队: 12'],['completed','已完成: 24']]){
   await alice.locator('.notebook-progress-segment.'+key).click();assert.ok((await alice.locator('#notebookTooltip').textContent()).includes(detail));
  }
  await alice.keyboard.press('Escape');
  await alice.locator('.notebook-progress-segment.failed').click();
  progress={phase:'idle',queued:0,active:0,retrying:0,awaitingSave:0,failed:0,completed:11,regionsDiscovered:31,lastCompletedAt:'2026-09-08T20:46:29+08:00'};
  await alice.waitForFunction(()=>document.querySelector('#notebookProgress').dataset.phase==='idle');
  for(const key of ['failed','pending'])assert.equal(await alice.locator('.notebook-progress-segment.'+key).evaluate(el=>el.getBoundingClientRect().width),0,'Zero segment must occupy no space');
  assert.equal(await alice.locator('#notebookTooltip').isVisible(),false,'Hidden segment must close its tooltip');
  assert.equal(await alice.locator('.notebook-progress-segment.completed').evaluate(el=>el.offsetWidth===el.parentElement.offsetWidth),true,'Only completed must fill the track');
  await alice.locator('.notebook-progress-segment.completed').click();assert.match(await alice.locator('#notebookTooltip').textContent(),/已完成: 11/);
  progress={phase:'waiting-colormap',reason:'rebuild',pending:4,rebuilding:true,surfaceExtraction:17,coloring:3,parents:2,indexing:17};
  await alice.waitForFunction(()=>document.querySelector('#notebookProgress').dataset.phase==='waiting-colormap');
  await alice.locator('.notebook-progress-segment.pending').click();
  assert.match(await alice.locator('#notebookTooltip').textContent(),/等待管理员客户端色表/);
  assert.match(await alice.locator('#notebookTooltip').textContent(),/地表提取: 17/);
  assert.equal(await alice.locator('.notebook-progress-segment.pending').evaluate(el=>el.style.flexGrow),'4','Persisted waiting jobs disappeared from progress');
  progress={phase:'waiting'};
  await alice.waitForFunction(()=>document.querySelector('.notebook-progress-track').dataset.empty==='true');
  assert.equal(await alice.locator('.notebook-progress-segment:visible').count(),0,'Empty progress must not fabricate a colored segment');
  await alice.locator('.notebook-progress-track').focus();await alice.keyboard.press('Enter');assert.match(await alice.locator('#notebookTooltip').textContent(),/等待游戏就绪/);
  progress={phase:'rendering',queued:12,active:1,completed:24,failed:2,regionsDiscovered:40,awaitingSave:3};
  await alice.waitForFunction(()=>document.querySelector('#notebookProgress').dataset.phase==='rendering');
  assert.equal(await alice.locator('#notebookTooltip').isVisible(),false);
  await alice.locator('#planRoute').click();
  await alice.locator('#notebookModal').waitFor();
  assert.equal(await alice.locator('#notebookToolbar').isVisible(),false);
  await alice.locator('#notebookModal input[name="name"]').fill('Mine trip');
  await alice.locator('#notebookModal').getByRole('button',{name:'开始规划'}).click();
  await alice.locator('#notebookModal').waitFor({state:'hidden'});
  await checkToolbarWidth(alice);
  assert.equal(await alice.locator('#notebookToolbar').getByRole('button',{name:'撤销',exact:true}).isDisabled(),true);
  assert.equal(await alice.locator('#notebookToolbar').getByRole('button',{name:'恢复',exact:true}).isDisabled(),true);
  assert.equal(await alice.locator('#notebookToolbar').getByRole('button',{name:'撤销一点',exact:true}).count(),0);
  for(const [x,z] of [[-90,0],[0,50],[70,-50]]){
   const point=await alice.evaluate(([x,z])=>{const p=window.testMap.latLngToContainerPoint([z/4096,x/4096]);return {x:p.x,y:p.y};},[x,z]);await alice.mouse.click(point.x,point.y);
  }
  await alice.locator('#notebookToolbar').getByRole('button',{name:'撤销',exact:true}).click();
  assert.match(await alice.locator('#notebookToolbar').textContent(),/\(2\/512\)/);
  await alice.locator('#notebookToolbar').getByRole('button',{name:'恢复',exact:true}).click();assert.match(await alice.locator('#notebookToolbar').textContent(),/\(3\/512\)/);
  assert.equal(await alice.locator('#notebookToolbar').getByRole('button',{name:'恢复',exact:true}).isDisabled(),true);
  for(let i=0;i<2;i++)await alice.locator('#notebookToolbar').getByRole('button',{name:'撤销',exact:true}).click();
  assert.match(await alice.locator('#notebookToolbar').textContent(),/\(1\/512\)/);
  await alice.locator('#notebookToolbar').getByRole('button',{name:'恢复',exact:true}).click();
  await alice.evaluate(()=>window.testMap.fire('click',{latlng:L.latLng(80/4096,90/4096)}));
  assert.equal(await alice.locator('#notebookToolbar').getByRole('button',{name:'恢复',exact:true}).isDisabled(),true,'Adding a new point must discard redo history');
  await alice.locator('#notebookToolbar').getByRole('button',{name:'撤销',exact:true}).click();assert.match(await alice.locator('#notebookToolbar').textContent(),/\(2\/512\)/);
  await alice.locator('#notebookToolbar').getByRole('button',{name:'完成',exact:true}).click();
  assert.equal(await alice.locator('#notebookModal input[name="name"]').inputValue(),'Mine trip');
  await alice.locator('#notebookModal button[type="submit"]').click();await alice.locator('#notebookModal').waitFor({state:'hidden'});
  const original=[...routes.values()].find(r=>r.owner==='alice'&&r.name==='Mine trip');assert.deepEqual(original.points,[[-90,0],[0,50]]);
  assert.deepEqual(await ownRouteIds(alice),[original.id,'saved-a','saved-b'].sort());
  await alice.locator('#notebook-myRoutes input').uncheck();
  await alice.locator('#searchInput').fill('Mine trip');
  await alice.locator('#searchResults li').filter({hasText:'Mine trip'}).click();
  await alice.locator('.leaflet-popup .notebook-popup').waitFor();
  assert.equal(await alice.locator('#notebook-myRoutes input').isChecked(),true);
  await alice.locator('#notebook-myMarkers input').uncheck();
  await alice.locator('#searchInput').fill('pick');
  await alice.locator('#searchResults li').filter({hasText:'alice mine'}).press('Enter');
  await alice.waitForFunction(()=>!!document.querySelector('.notebook-waypoint'));
  assert.equal(await alice.locator('#notebook-myMarkers input').isChecked(),true);
  await alice.evaluate(id=>{const route=Object.values(window.testMap._layers).find(l=>l.options?.notebookRouteId===id);route.fire('contextmenu',{latlng:route.getLatLngs()[0],originalEvent:new MouseEvent('contextmenu',{clientX:500,clientY:300})});},original.id);
  await alice.locator('#contextMenu').getByRole('button',{name:'复制轨迹链接',exact:true}).click();
  await alice.waitForFunction(()=>window.copied.length);const link=await alice.evaluate(()=>window.copied.at(-1));assert.ok(new URL(link).searchParams.get('route'));
  await alice.evaluate(()=>window.testMap.fire('contextmenu',{latlng:L.latLng(0,0),originalEvent:new MouseEvent('contextmenu',{clientX:500,clientY:300})}));
  await alice.locator('#contextMenu').getByRole('button',{name:'复制坐标链接',exact:true}).click();
  await alice.waitForFunction(()=>window.copied.length===2);assert.equal(new URL(await alice.evaluate(()=>window.copied.at(-1))).searchParams.get('point'),'1');
  await alice.goto(link);await alice.locator('#notebookToolbar').getByRole('button',{name:'保存到我的轨迹'}).waitFor();
  assert.deepEqual(await ownRouteIds(alice),[original.id,'saved-a','saved-b'].sort(),'Shared preview must coexist with all own routes');
  await alice.locator('#notebook-myRoutes input').uncheck();assert.deepEqual(await ownRouteIds(alice),[]);
  assert.equal(await alice.evaluate(()=>Object.values(window.testMap._layers).filter(l=>l.options?.notebookShared).length),1,'Own routes checkbox must not hide shared preview');
  await alice.locator('#notebook-myRoutes input').check();assert.deepEqual(await ownRouteIds(alice),[original.id,'saved-a','saved-b'].sort());
  const {page:bob,errors:bobErrors}=await open('bob',{width:390,height:844},link);
  await bob.locator('#notebookToolbar').getByRole('button',{name:'保存到我的轨迹'}).waitFor();
  await checkToolbarWidth(bob);
  assert.equal([...routes.values()].filter(r=>r.owner==='bob').length,0,'Link visit must not automatically save');
  await bob.evaluate(()=>window.testMap.panBy([20,10],{animate:false}));assert.ok(new URL(bob.url()).searchParams.get('route'),'Map movement lost share URL');
  await bob.locator('#notebookToolbar').getByRole('button',{name:'保存到我的轨迹'}).tap();
  await bob.waitForFunction(()=>document.querySelector('#notebookToolbar').hidden);
  const imported=[...routes.values()].find(r=>r.owner==='bob');assert.ok(imported);assert.notEqual(imported.id,original.id);assert.deepEqual(imported.points,original.points);assert.equal(new URL(bob.url()).searchParams.has('route'),false);
  await checkRoutePopup(bob,imported.id,['复制轨迹链接','编辑轨迹','删除轨迹']);await bob.evaluate(()=>window.testMap.closePopup());
  await bob.locator('#mobileMenu').tap();assert.deepEqual(await ownRouteIds(bob),[imported.id]);
  await checkNotebookRows(bob);
  await bob.locator('#notebook-myRoutes input').uncheck();assert.deepEqual(await ownRouteIds(bob),[]);
  await bob.locator('#notebook-myRoutes input').check();assert.deepEqual(await ownRouteIds(bob),[imported.id]);
  assert.equal(await bob.locator('.notebook-section ul').count(),0);
  progress={phase:'idle',completed:11,failed:0,queued:0,active:0,retrying:0,awaitingSave:0};
  await bob.waitForFunction(()=>document.querySelector('#notebookProgress').dataset.phase==='idle');
  for(const key of ['failed','pending'])assert.equal(await bob.locator('.notebook-progress-segment.'+key).evaluate(el=>el.getBoundingClientRect().width),0,'Mobile minimum touch width must not expose zero segments');
  await bob.locator('.notebook-progress-segment.completed').tap();assert.match(await bob.locator('#notebookTooltip').textContent(),/已完成: 11/);
  await bob.keyboard.press('Escape');progress={phase:'rendering',queued:12,completed:24};
  assert.equal(await bob.locator('#notebook-hiddenRegions').isVisible(),false);
  const {page:admin,errors:adminErrors}=await open('admin');
  await admin.locator('#manageButton').click();
  await admin.locator('#siteTitleInput').fill('社区地图 <test>');
  await admin.locator('#siteDescriptionInput').fill('世界地图 & 玩家路线');
  await admin.locator('#siteKeywordsInput').fill('地图,路线');
  await admin.locator('#siteFaviconInput').fill('https://example.com/favicon.ico');
  await admin.locator('#manageForm button[type="submit"]').click();
  await admin.waitForFunction(()=>document.title==='社区地图 <test>');
  assert.equal(await admin.locator('meta[name="description"]').getAttribute('content'),'世界地图 & 玩家路线');
  assert.equal(await admin.locator('meta[name="keywords"]').getAttribute('content'),'地图,路线');
  assert.equal(await admin.locator('#siteFavicon').getAttribute('href'),'https://example.com/favicon.ico');
  assert.equal(announcement.site.title,'社区地图 <test>');
  await checkNotebookRows(admin);
  await admin.evaluate(()=>window.testMap.fire('contextmenu',{latlng:L.latLng(20/4096,20/4096),originalEvent:new MouseEvent('contextmenu',{clientX:500,clientY:300})}));
  const menuFonts=await admin.locator('#contextMenu button:visible').evaluateAll(bs=>bs.map(b=>getComputedStyle(b).font));assert.equal(new Set(menuFonts).size,1,'Every context menu action must use the same font');
  await admin.locator('#contextMenu').getByRole('button',{name:'框选隐藏区域',exact:true}).click();
  await checkToolbarWidth(admin);
  const corner=await admin.evaluate(()=>{const p=window.testMap.latLngToContainerPoint([80/4096,80/4096]);return{x:p.x,y:p.y};});await admin.mouse.click(corner.x,corner.y);
  await admin.locator('#notebookModal input[name="name"]').fill('Secret area');
  assert.equal(await admin.getByRole('checkbox',{name:'是否游戏内隐藏'}).isChecked(),false);await admin.getByRole('checkbox',{name:'是否游戏内隐藏'}).check();
  await admin.locator('#notebookModal button[type="submit"]').click();await admin.waitForTimeout(500);assert.equal(await admin.locator('#notebookModal .error').textContent(),'');await admin.locator('#notebookModal').waitFor({state:'hidden'});
  assert.equal(regions[0].hideInGame,true);
  await admin.evaluate(()=>Object.values(window.testMap._layers).find(l=>l.options?.className==='notebook-fog').openPopup());
  const regionButtons=await admin.locator('.leaflet-popup .notebook-popup-actions button').evaluateAll(bs=>bs.map(b=>({label:b.getAttribute('aria-label'),title:b.title,text:b.textContent,icons:b.querySelectorAll('svg').length,y:b.getBoundingClientRect().y})));
  assert.deepEqual(regionButtons.map(b=>b.label),['编辑隐藏区域','移除隐藏区域']);assert.equal(new Set(regionButtons.map(b=>b.y)).size,1);
  for(const b of regionButtons){assert.equal(b.label,b.title);assert.equal(b.text,'');assert.equal(b.icons,1);}
  await admin.locator('.leaflet-popup').getByRole('button',{name:'编辑隐藏区域'}).click();
  assert.equal(await admin.getByRole('checkbox',{name:'是否游戏内隐藏'}).isChecked(),true);await admin.getByRole('checkbox',{name:'是否游戏内隐藏'}).uncheck();
  await admin.locator('#notebookModal button[type="submit"]').click();await admin.locator('#notebookModal').waitFor({state:'hidden'});assert.equal(regions[0].hideInGame,false);
  assert.equal(regions.length,1);assert.equal(regions[0].minX,20);assert.equal(regions[0].maxX,80);
  assert.equal(await admin.locator('.notebook-fog').count(),1);
  assert.equal(await admin.locator('.notebook-region-label text').textContent(),'Secret area');
  let prior;
  for(const zoom of [12,11,10,9,6,12]){
   await admin.evaluate(zoom=>window.testMap.setZoom(zoom,{animate:false}),zoom);await admin.waitForTimeout(100);await checkRegionLabels(admin);
   const size=await admin.locator('.notebook-region-label').evaluate(s=>({viewport:s.getBoundingClientRect().width,text:s.querySelector('text').getBoundingClientRect().width}));
   if(prior){
    const factor=Math.pow(2,zoom-prior.zoom);
    assert.ok(Math.abs(size.viewport-prior.viewport*factor)<=1+factor,'Region clip must scale with world zoom (pixel rounding allowed)');
    if(size.viewport>=10&&prior.viewport>=10)assert.ok(Math.abs(size.text/prior.text-Math.pow(2,zoom-prior.zoom))<.01,`Text must scale with world zoom: ${JSON.stringify({size,prior,zoom})}`);
   }
   prior={...size,zoom};
  }
  assert.equal(await admin.locator('.notebook-fog').getAttribute('fill-opacity'),'0','No region background fill');
  await bob.evaluate(()=>window.testEvents.dispatchEvent(new MessageEvent('visibility')));
  await bob.waitForFunction(()=>!document.querySelector('.notebook-waypoint'));
  assert.doesNotMatch(await bob.locator('#notebook-myMarkers').textContent(),/bob mine/);
  await admin.locator('#notebook-hiddenRegions input').uncheck();assert.equal(await admin.locator('.notebook-fog').count(),0);assert.equal(regions.length,1,'Admin fog checkbox modified server rules');
  assert.equal(await admin.evaluate(()=>{const layer=Object.values(window.testMap._layers).find(l=>l instanceof L.TileLayer);return new URL(layer.getTileUrl({x:0,y:0,z:12}),location.href).searchParams.get('hideRegions');}),'0');
  assert.equal(await admin.locator('.notebook-region-label').count(),0);
  assert.equal(await admin.evaluate(()=>Object.values(window.testMap._layers).filter(layer=>layer instanceof L.TileLayer).length),1,'Stale tile layers survived preview toggle');
  await admin.reload();await admin.waitForFunction(()=>document.querySelector('#notebookProgress').dataset.phase==='rendering');
  assert.equal(await admin.locator('#notebook-hiddenRegions input').isChecked(),false);assert.equal(await admin.locator('.notebook-region-label').count(),0,'Admin preview preference lost on reload');
  await admin.locator('#notebook-hiddenRegions input').check();
  assert.equal(await admin.evaluate(()=>{const layer=Object.values(window.testMap._layers).find(l=>l instanceof L.TileLayer);return new URL(layer.getTileUrl({x:0,y:0,z:12}),location.href).searchParams.get('hideRegions');}),'1');
  await admin.evaluate(()=>{let region;window.testMap.eachLayer(l=>{if(l.options?.className==='notebook-fog')region=l;});region.fire('contextmenu',{latlng:region.getBounds().getCenter(),originalEvent:new MouseEvent('contextmenu',{clientX:500,clientY:300})});});
  await admin.locator('#contextMenu').getByRole('button',{name:'编辑隐藏区域',exact:true}).click();await admin.locator('#notebookModal input[name="maxX"]').fill('90');await admin.locator('#notebookModal button[type="submit"]').click();await admin.locator('#notebookModal').waitFor({state:'hidden'});assert.equal(regions[0].maxX,90);
  await admin.evaluate(()=>{let region;window.testMap.eachLayer(l=>{if(l.options?.className==='notebook-fog')region=l;});region.fire('contextmenu',{latlng:region.getBounds().getCenter(),originalEvent:new MouseEvent('contextmenu',{clientX:500,clientY:300})});});
  await admin.locator('#contextMenu').getByRole('button',{name:'移除隐藏区域',exact:true}).click();await admin.waitForFunction(()=>document.querySelectorAll('.notebook-fog').length===0);assert.equal(regions.length,0);
  const {page:guest,errors:guestErrors}=await open(null,{width:390,height:844},link);
  for(const selector of ['#planRoute','#locate','#notebook-myMarkers','#notebook-myRoutes','#notebook-hiddenRegions'])assert.equal(await guest.locator(selector).isVisible(),false);
  assert.equal(await guest.locator('#notebookToolbar').getByRole('button',{name:'保存到我的轨迹'}).count(),0);
  assert.equal(await guest.locator('#notebookToolbar').getByRole('button',{name:'登录后可查看和保存'}).count(),0);
  await checkToolbarWidth(guest);
  await guest.locator('#notebookToolbar').getByRole('button',{name:'复制轨迹链接'}).tap();await guest.waitForFunction(()=>window.copied.length===1);
  assert.equal(await guest.locator('#authModal').isVisible(),false,'Public re-sharing must not require login');
  if(process.env.MAP_SCREENSHOTS){await fs.mkdir(process.env.MAP_SCREENSHOTS,{recursive:true});await guest.screenshot({path:path.join(process.env.MAP_SCREENSHOTS,'notebook-guest-share.png')});}
  await guest.waitForFunction(()=>Object.values(window.testMap._layers).some(layer=>layer.options?.className==='notebook-route'));
  // No SSE here: missed notifications must still invalidate an open shared route on polling.
  regions=[{id:'poll-fog',name:'Private',minX:-100,minZ:-10,maxX:10,maxZ:60}];
  await guest.waitForFunction(()=>document.querySelector('.notebook-fog')&&!Object.values(window.testMap._layers).some(layer=>layer.options?.className==='notebook-route'),{},{timeout:15000});
  await checkRegionLabels(guest);assert.equal(await guest.locator('.notebook-region-label text').textContent(),'隐藏区域');
  assert.equal(await guest.locator('#notebookToolbar').isVisible(),false);
  assert.deepEqual(guestErrors,[]);regions=[];
  for(const errors of [aliceErrors,bobErrors,adminErrors])assert.deepEqual(errors,[]);
  const {page:markerOwner,errors:markerErrors}=await open('alice');
  await markerOwner.evaluate(()=>window.testMap.fire('contextmenu',{latlng:L.latLng(30/4096,40/4096),originalEvent:new MouseEvent('contextmenu',{clientX:500,clientY:300})}));
  await markerOwner.locator('#contextMenu').getByRole('button',{name:'添加游戏标记',exact:true}).click();
  await markerOwner.locator('#notebookModal input[name="name"]').fill('Web fruit');
  await markerOwner.locator('#notebookModal input[name="pinned"]').check();
  await markerOwner.locator('#notebookModal input[name="text"]').fill('Shared apple orchard');
  await markerOwner.locator('#notebookModal input[name="y"]').fill('110');
  await markerOwner.locator('.notebook-icon-picker').getByRole('button',{name:'pick',exact:true}).click();
  assert.equal(await markerOwner.locator('.notebook-icon-picker [aria-pressed="true"]').getAttribute('aria-label'),'pick');
  if(process.env.MAP_SCREENSHOTS)await markerOwner.screenshot({path:path.join(process.env.MAP_SCREENSHOTS,'notebook-add-marker.png')});
  await markerOwner.locator('#notebookModal button[type="submit"]').click();await markerOwner.locator('#notebookModal').waitFor({state:'hidden'});
  const createdMarker=gameMarkers.get('alice').find(m=>m.name==='Web fruit');assert.ok(createdMarker);assert.equal(createdMarker.icon,'pick');assert.equal(createdMarker.pinned,true);
  const showMarker=async(page,id,shared=false)=>{await page.waitForFunction(({id,shared})=>Object.values(window.testMap._layers).some(l=>l.options?.notebookMarkerId===id&&!!l.options.notebookShared===shared),{id,shared});await page.evaluate(({id,shared})=>Object.values(window.testMap._layers).find(l=>l.options?.notebookMarkerId===id&&!!l.options.notebookShared===shared).openPopup(),{id,shared});};
  await showMarker(markerOwner,createdMarker.id);
  await markerOwner.locator('.leaflet-popup').getByRole('button',{name:'复制标记链接',exact:true}).click();await markerOwner.waitForFunction(()=>window.copied.length);
  const markerLink=await markerOwner.evaluate(()=>window.copied.at(-1));assert.ok(new URL(markerLink).searchParams.get('waypoint'));
  await markerOwner.locator('.leaflet-popup').getByRole('button',{name:'编辑游戏标记',exact:true}).click();await markerOwner.locator('#notebookModal input[name="name"]').fill('Updated fruit');await markerOwner.locator('#notebookModal button[type="submit"]').click();await markerOwner.locator('#notebookModal').waitFor({state:'hidden'});
  const {page:markerGuest,errors:markerGuestErrors}=await open(null,{width:390,height:844},markerLink);
  await markerGuest.locator('#notebookToolbar').getByRole('button',{name:'复制标记链接',exact:true}).waitFor();
  assert.equal(await markerGuest.locator('#notebookToolbar').getByRole('button',{name:'保存到游戏标记'}).count(),0);
  assert.match(await markerGuest.locator('#notebookToolbar').textContent(),/Web fruit/);
  await markerGuest.waitForTimeout(5200);await showMarker(markerGuest,createdMarker.id,true);
  await markerGuest.locator('.leaflet-popup').getByRole('button',{name:'复制标记链接',exact:true}).tap();await markerGuest.waitForFunction(()=>window.copied.length===1);
  const {page:markerBob,errors:markerBobErrors}=await open('bob',{width:390,height:844},markerLink);
  await markerBob.locator('#notebookToolbar').getByRole('button',{name:'保存到游戏标记',exact:true}).tap();await markerBob.waitForFunction(()=>!new URL(location.href).searchParams.has('waypoint'));
  const copiedMarker=gameMarkers.get('bob').find(m=>m.name==='Web fruit');assert.ok(copiedMarker);assert.notEqual(copiedMarker.id,createdMarker.id);
  await showMarker(markerOwner,createdMarker.id);await markerOwner.locator('.leaflet-popup').getByRole('button',{name:'删除游戏标记',exact:true}).click();
  await markerOwner.waitForFunction(id=>!Object.values(window.testMap._layers).some(l=>l.options?.notebookMarkerId===id),createdMarker.id);
  await markerGuest.waitForFunction(()=>!Object.values(window.testMap._layers).some(l=>l.options?.notebookMarkerId),{},{timeout:15000});
  assert.ok(gameMarkers.get('bob').some(m=>m.id===copiedMarker.id));
  for(const errors of [markerErrors,markerGuestErrors,markerBobErrors])assert.deepEqual(errors,[]);
  regions=[{id:'wide-label',name:'A long hidden region name',minX:-300,minZ:-100,maxX:300,maxZ:-80},{id:'tall-label',name:'隐藏区域',minX:-10,minZ:-60,maxX:10,maxZ:240}];
  await admin.evaluate(()=>{window.testMap.setView([0,0],12,{animate:false});window.testEvents.dispatchEvent(new MessageEvent('visibility'));});
  await admin.waitForFunction(()=>document.querySelector('.notebook-region-label[data-region-id="tall-label"]'));
  await checkRegionLabels(admin);
  if(process.env.MAP_SCREENSHOTS){
   await fs.mkdir(process.env.MAP_SCREENSHOTS,{recursive:true});await bob.screenshot({path:path.join(process.env.MAP_SCREENSHOTS,'notebook-mobile.png')});
   regions=[{id:'label-style',name:'隐藏区域',minX:-250,minZ:-180,maxX:250,maxZ:180}];
   await admin.evaluate(()=>{window.testMap.setView([0,0],12);window.testEvents.dispatchEvent(new MessageEvent('visibility'));});
   await admin.waitForFunction(()=>document.querySelector('.notebook-fog'));await admin.waitForTimeout(250);
   await admin.screenshot({path:path.join(process.env.MAP_SCREENSHOTS,'notebook-admin.png')});
   await admin.setViewportSize({width:390,height:844});await checkRegionLabels(admin);
   await admin.screenshot({path:path.join(process.env.MAP_SCREENSHOTS,'notebook-region-mobile.png')});
  }
  console.log('PASS notebook: click tooltips, compact tri-color progress, anonymous controls, shared routes, clipped diagonal region labels with zoom-proportional text, persistent admin preview, privacy polling and desktop/mobile workflows');
 }finally{await browser.close();}
}
main().catch(error=>{console.error(error);process.exitCode=1;});
