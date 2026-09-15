const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../LauncherGo.ServerMapHost/WebRoot');
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=', 'base64');
const ores = [{code:'silver',zh:'白银发晶',en:'Silver quartz'}];
const sample = (id,y,owned=true,x=512016) => ({id,kind:'mineral-heatmap',mode:'node',sampleX:x,sampleZ:512012,sampleY:y,radius:6,sampledAt:'2026-09-16T01:47:12Z',ownerName:owned?'Tester':'Other',owned,ores:[{code:'silver',amountLevel:1,blocks:3}]});
const histogram=()=>({minY:107,maxY:120,step:6,incomplete:legacyOnly,coverage:[{minY:107,maxY:120}],columns:legacyOnly?[]:[
  {code:'silver',zh:'白银发晶',en:'Silver quartz',total:6,layers:[{y:107,blocks:1},{y:113,blocks:2},{y:114,blocks:3}]},
  {code:'copper',zh:'铜',en:'Copper',total:4,layers:[{y:108,blocks:3},{y:112,blocks:1}]}
]});
let samples,failDetail=false,failDelete=false,delayDetail=false,deleteCalls=[],admin=false,legacyOnly=false,detailOverride=null;
const streams=new Set();
async function main(){
  const artifacts=await fs.mkdtemp(path.join(os.tmpdir(),'ore-probes-ui-'));
  const server=http.createServer(async(req,res)=>{
    const url=new URL(req.url,'http://localhost'),pathname=url.pathname.replace(/^\/servermap/,'');
    if(pathname.startsWith('/api/v1/')){
      const route=pathname.slice(8);
      if(route==='events'){res.writeHead(200,{'Content-Type':'text/event-stream'});res.write(':ready\n\n');streams.add(res);req.on('close',()=>streams.delete(res));return;}
      if(route.startsWith('tiles/')){res.writeHead(200,{'Content-Type':'image/png'});res.end(png);return;}
      let data={type:'FeatureCollection',features:[]};
      if(route==='map/metadata')data={spawn:{x:512000,z:512000},center:{x:512000,z:512000},maxZoom:12,maxZoomOut:12,serverName:'Probe test',tileVersion:'test'};
      if(route==='auth/me')data={authenticated:true,name:admin?'Admin':'Tester',admin};
      if(route==='announcement')data={html:'',management:{fogEnabled:false}};
      if(['hidden-regions','waypoints','my-waypoints','routes','tracks','allies'].includes(route))data=[];
      if(route==='area-markers')data={revision:0,markers:[]};
      if(route==='layers/manifest')data={layers:[{id:'mineral-heatmap',visible:true}]};
      if(route==='layers/mineral-heatmap')data={type:'FeatureCollection',ores,features:samples.map(s=>({type:'Feature',id:s.id,geometry:{type:'Point',coordinates:[s.sampleX,s.sampleZ]},properties:{...s,adminDelete:admin}}))};
      if(route==='ore-probes'){
        if(req.method==='DELETE'){
          deleteCalls.push({x:url.searchParams.get('x'),z:url.searchParams.get('z'),header:req.headers['x-servermap-request']});
          if(failDelete){res.writeHead(500);res.end();return;}
          samples=samples.filter(s=>(!admin&&!s.owned)||s.sampleX!==+url.searchParams.get('x')||s.sampleZ!==+url.searchParams.get('z'));
          data={removed:2};
        }else{
          if(delayDetail)await new Promise(resolve=>setTimeout(resolve,350));
          if(failDetail){res.writeHead(500);res.end();return;}
          data=detailOverride||histogram();if(url.searchParams.get('ore'))data.columns=data.columns.filter(c=>c.code===url.searchParams.get('ore'));
        }
      }
      res.writeHead(200,{'Content-Type':'application/json'});res.end(JSON.stringify(data));return;
    }
    const file=path.resolve(root,pathname.replace(/^\//,'')||'index.html');
    if(!file.startsWith(root+path.sep)){res.writeHead(403);res.end();return;}
    try{res.writeHead(200,{'Content-Type':{'.html':'text/html; charset=utf-8','.js':'text/javascript','.css':'text/css'}[path.extname(file)]||'application/octet-stream'});res.end(await fs.readFile(file));}catch{res.end();}
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const browser=await chromium.launch({headless:true});
  try{
    for(const width of [1440,390]){
      samples=[sample('mine114',114),sample('mine113',113),sample('other114',114,false),sample('legacy',112,false),sample('neighbor',114,true,512040)];
      failDetail=failDelete=delayDetail=admin=legacyOnly=false;deleteCalls=[];
      const page=await browser.newPage({viewport:{width,height:900}}),errors=[];
      page.on('pageerror',error=>errors.push(error.message));
      await page.addInitScript(()=>localStorage.setItem('servermap-language','zh'));
      await page.goto(`http://127.0.0.1:${server.address().port}${width===390?'/servermap/':'/'}`);
      await page.locator('#home').evaluate(el=>el.click());
      await page.waitForFunction(()=>document.querySelectorAll('.ore-probe-marker').length===2);
      await page.locator('#sidebar').evaluate(el=>el.style.visibility='hidden');
      await page.locator('#sidebarShade').evaluate(el=>el.style.pointerEvents='none');
      const markers=page.locator('.ore-probe-marker');
      await markers.first().hover();
      assert.equal(await page.locator('.ore-depth-tooltip').count(),0,'chart opened without a click');
      await markers.first().click();
      await page.locator('.ore-depth-column').first().waitFor();
      assert.equal(await page.locator('.ore-section-dialog,.ore-section-svg,.ore-column').count(),0,'removed modal/section remains');
      assert.equal(await page.locator('.ore-depth-column').count(),2,'ore types should have independent bars');
      assert.deepEqual(await page.locator('.ore-depth-total').allTextContents(),['6 块','4 块']);
      assert.equal(await page.locator('.ore-depth-cell').count(),5,'one cell per positive real ore Y');
      assert.equal(await page.locator('[data-ore="silver"] [data-y="114"]').evaluate(el=>el.style.backgroundColor),'rgb(213, 32, 39)');
      assert.notEqual(await page.locator('[data-ore="silver"] [data-y="107"]').evaluate(el=>el.style.backgroundColor),'rgb(213, 32, 39)');
      const ticks=await page.locator('.ore-depth-tick').allTextContents();
      assert.deepEqual(ticks,['114','113','112','108','107'],'axis must show exact occupied Y levels for all ores');
      assert.deepEqual(await page.locator('.ore-depth-major').allTextContents(),['114','108'],'major ticks must align to radius multiples');
      assert.ok(await page.locator('.ore-depth-cell').evaluateAll(cells=>cells.every(cell=>cell.getBoundingClientRect().height>=14)),'ore layers too thin');
      assert.doesNotMatch(await page.locator('.ore-depth-tooltip').innerText(),/采样时间|Tester|剖面|半径/);
      const tooltipBox=await page.locator('.ore-depth-tooltip').boundingBox();
      assert.ok(tooltipBox.x>=0&&tooltipBox.x+tooltipBox.width<=width&&tooltipBox.y>=0&&tooltipBox.y+tooltipBox.height<=900,'tooltip is clipped');
      assert.ok(await page.locator('.ore-depth-tooltip').evaluate(el=>{
        const r=el.getBoundingClientRect();return el.contains(document.elementFromPoint(r.x+r.width/2,r.y+r.height/2));
      }),'map controls cover the tooltip');
      await page.screenshot({path:path.join(artifacts,`depth-${width}.png`)});
      await markers.first().click();await page.waitForFunction(()=>!document.querySelector('.ore-depth-tooltip'));
      // A deep sparse shaft must keep real Y values and quantities without vertical scrolling.
      detailOverride={minY:0,maxY:1023,step:6,columns:[{code:'silver',total:6,layers:[{y:3,blocks:1},{y:500,blocks:2},{y:1000,blocks:3}]}]};
      await markers.first().click();await page.locator('.ore-depth-cell').first().waitFor();
      assert.deepEqual(await page.locator('.ore-depth-tick').allTextContents(),['1000','500','3']);
      assert.equal(await page.locator('.ore-depth-axis-body .ore-depth-break').count(),2);
      assert.ok(await page.locator('.ore-depth-scroll').evaluate(el=>el.scrollHeight<=el.clientHeight),'sparse shaft requires vertical scroll');
      assert.ok((await page.locator('.ore-depth-tooltip').boundingBox()).height<200,'sparse shaft not compact');
      await page.screenshot({path:path.join(artifacts,`sparse-${width}.png`)});
      await markers.first().click();
      detailOverride={minY:0,maxY:39,step:6,columns:[{code:'silver',total:820,layers:Array.from({length:40},(_,y)=>({y,blocks:y+1}))}]};
      await markers.first().click();await page.locator('.ore-depth-cell').first().waitFor();
      const seen=[];
      for(let i=0;i<4;i++){
        seen.push(...await page.locator('.ore-depth-tick').allTextContents());
        assert.equal(await page.locator('.ore-depth-total').innerText(),'820 块');
        assert.ok(await page.locator('.ore-depth-scroll').evaluate(el=>el.scrollHeight<=el.clientHeight),'dense shaft requires vertical scroll');
        if(i<3)await page.getByRole('button',{name:'较低层',exact:true}).click();
      }
      assert.deepEqual(seen,Array.from({length:40},(_,i)=>String(39-i)),'paging skipped or duplicated real layers');
      assert.equal(await page.getByRole('button',{name:'较低层',exact:true}).isDisabled(),true);
      await page.getByRole('button',{name:'较高层',exact:true}).click();
      assert.equal(await page.locator('.ore-depth-tick').first().innerText(),'19');
      await page.screenshot({path:path.join(artifacts,`dense-${width}.png`)});
      await markers.first().click();
      await page.setViewportSize({width,height:400});
      // Fixed controls can cover the point in a short viewport; isolate tooltip layout here.
      await markers.first().dispatchEvent('click');await page.locator('.ore-depth-cell').first().waitFor();
      const shortBox=await page.locator('.ore-depth-tooltip').boundingBox();
      assert.ok(shortBox.y>=0&&shortBox.y+shortBox.height<=400,'short viewport clips compact chart');
      assert.ok(await page.locator('.ore-depth-scroll').evaluate(el=>el.scrollHeight<=el.clientHeight),'short viewport requires vertical scroll');
      await page.keyboard.press('Escape');await page.setViewportSize({width,height:900});detailOverride=null;
      legacyOnly=true;await markers.first().click();
      await page.getByText('旧记录无逐层数据，请重新探矿',{exact:true}).waitFor();
      assert.equal(await page.locator('.ore-depth-cell').count(),0,'legacy totals fabricated actual layers');
      await markers.first().click();await page.waitForFunction(()=>!document.querySelector('.ore-depth-tooltip')); legacyOnly=false;
      failDetail=true;await markers.first().click();
      await page.getByRole('button',{name:'加载失败，点击重试',exact:true}).waitFor();
      await page.screenshot({path:path.join(artifacts,`retry-${width}.png`)});
      failDetail=false;await page.getByRole('button',{name:'加载失败，点击重试',exact:true}).click();await page.locator('.ore-depth-column').first().waitFor();
      await markers.first().click();await page.waitForFunction(()=>!document.querySelector('.ore-depth-tooltip'));
      delayDetail=true;await markers.first().click();await markers.first().click();
      await page.waitForTimeout(450);assert.equal(await page.locator('.ore-depth-tooltip').count(),0,'closed request reopened tooltip');delayDetail=false;
      // Right-click actions must not leave a permanent left-click popup binding behind.
      await markers.first().click({button:'right'});
      await page.locator('.leaflet-popup-close-button').click();
      await page.waitForFunction(()=>!document.querySelector('.ore-probe-menu'));
      await markers.first().click();await page.locator('.ore-depth-column').first().waitFor();
      assert.equal(await page.locator('.ore-probe-menu').count(),0,'left click reopened the right-click menu');
      await markers.first().click();
      assert.equal(await page.locator('.ore-depth-tooltip,.ore-probe-menu').count(),0,'second left click did not dismiss probe details');
      await markers.first().click({button:'right'});await markers.first().click();
      await page.waitForFunction(()=>!document.querySelector('.ore-probe-menu'));
      assert.equal(await page.locator('.ore-depth-tooltip,.ore-probe-menu').count(),0,'dismissing actions opened the depth tooltip');
      await markers.first().click({button:'right'});
      await page.locator('#map').click({position:{x:30,y:600}});
      await page.waitForFunction(()=>!document.querySelector('.ore-probe-menu'));
      assert.equal(await page.locator('.ore-depth-tooltip,.ore-probe-menu').count(),0,'map click did not dismiss actions');
      await markers.first().click({button:'right'});await page.keyboard.press('Escape');
      await page.waitForFunction(()=>!document.querySelector('.ore-probe-menu'));
      assert.equal(await page.locator('.ore-depth-tooltip,.ore-probe-menu').count(),0,'Escape did not dismiss actions');
      await markers.first().click({button:'right'});
      assert.equal(await page.locator('#contextMenu.show').count(),0,'probe right-click bubbled to map');
      await page.getByRole('button',{name:'删除我的记录',exact:true}).click({trial:true});
      assert.ok(await page.locator('.ore-probe-menu').evaluate(el=>{
        const r=el.getBoundingClientRect();return el.contains(document.elementFromPoint(r.x+r.width/2,r.y+r.height/2));
      }),'map controls cover the deletion menu');
      await page.screenshot({path:path.join(artifacts,`delete-${width}.png`)});
      failDelete=true;await page.getByRole('button',{name:'删除我的记录',exact:true}).click();
      await page.waitForFunction(()=>document.querySelector('.ore-delete-status')?.textContent.includes('删除失败'));
      failDelete=false;await page.getByRole('button',{name:'删除我的记录',exact:true}).click();
      await page.waitForFunction(()=>!document.querySelector('.ore-probe-menu'));
      assert.deepEqual(deleteCalls.at(-1),{x:'512016',z:'512012',header:'1'},'deletion used relative rather than absolute coordinates');
      assert.equal(samples.length,3,'owner deleted another player or neighbor');
      await markers.first().click({button:'right'});assert.equal(await page.locator('.ore-delete-confirm').count(),0,'ordinary player may delete others');
      await page.locator('.leaflet-popup-close-button').click();
      // Admin policy arrives in the layer response, not an unchecked client query flag.
      admin=true;
      const refreshed=page.waitForResponse(response=>response.url().includes('/layers/mineral-heatmap'));
      for(const stream of streams)stream.write('event: layer\ndata: {"layer":"mineral-heatmap"}\n\n');
      await refreshed;
      await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
      await markers.first().click({button:'right'});
      await page.getByRole('button',{name:'删除探矿点',exact:true}).click();
      await page.waitForFunction(()=>document.querySelectorAll('.ore-probe-marker').length===1);
      assert.equal(samples.length,1,'admin should delete all owners at one point only');
      assert.equal(samples[0].sampleX,512040);
      await page.locator('#language').selectOption('en',{force:true});
      await page.waitForFunction(()=>document.querySelector('#oreHeatmapSelect option').textContent==='All ores');
      await markers.first().click();await page.locator('.ore-depth-column').first().waitFor();
      assert.match(await page.locator('.ore-depth-tooltip').innerText(),/Silver quartz/);
      assert.deepEqual(errors,[]);
      await page.close();console.log(`PASS ${width}px: real Y bars, quantities, hottest layer, radius ticks, tooltip bounds, legacy, owner/admin deletion and cancellation`);
    }
    console.log('Screenshots: '+artifacts);
  }finally{await browser.close();for(const stream of streams)stream.end();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
}
main().catch(error=>{console.error(error);process.exitCode=1;});
