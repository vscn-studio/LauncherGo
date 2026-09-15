const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../LauncherGo.ServerMapHost/WebRoot');
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=', 'base64');
const streams = new Set();
let empty = false, truncated = false, fail = false, delayed = false;
const requests = [];
const catalog = [{code:'cassiterite',zh:'锡石',en:'Cassiterite'},{code:'copper',zh:'铜',en:'Copper'}];
const metadata = { spawn:{x:512000,z:512000}, center:{x:512000,z:512000}, maxZoom:12,maxZoomOut:12, updatedAt:'2026-09-15T00:00:00Z',serverName:'Mineral heatmap test',serverMapVersion:'0.3.9',tileVersion:'test' };
const cell = (selected) => ({type:'Feature',id:'ore-16000-16000',geometry:{type:'Polygon',coordinates:[[[512000,512000],[512032,512000],[512032,512032],[512000,512032],[512000,512000]]]},properties:{density:selected==='cassiterite'?1:7,sampledAt:'2026-09-15T00:00:00Z',ores:selected==='cassiterite'?[{code:'cassiterite',density:1,partsPerThousand:.01}]:[{code:'copper',density:7,partsPerThousand:4.2},{code:'cassiterite',density:1,partsPerThousand:.01}]}});

async function main(){
  const server = http.createServer(async (req,res)=>{
    const url = new URL(req.url,'http://localhost'), pathname=url.pathname.replace(/^\/servermap/, '');
    if(pathname.startsWith('/api/v1/')){
      const route=pathname.slice(8);
      if(route==='events'){res.writeHead(200,{'Content-Type':'text/event-stream'});res.write(':ready\n\n');streams.add(res);req.on('close',()=>streams.delete(res));return;}
      if(route.startsWith('tiles/')){res.writeHead(200,{'Content-Type':'image/png'});res.end(png);return;}
      let data={type:'FeatureCollection',features:[]};
      if(route==='map/metadata')data=metadata;
      if(route==='auth/me')data={authenticated:false};
      if(route==='announcement')data={html:'',management:{fogEnabled:false}};
      if(['hidden-regions','waypoints','tracks','area-markers','allies'].includes(route))data=[];
      if(route==='layers/manifest')data={layers:[{id:'mineral-heatmap',visible:false}]};
      if(route==='layers/mineral-heatmap'){
        requests.push({ore:url.searchParams.get('ore'),bbox:url.searchParams.get('bbox')});
        if(fail){res.writeHead(500);res.end();return;}
        data={type:'FeatureCollection',ores:catalog,features:empty?[]:[cell(url.searchParams.get('ore'))],truncated};
        if(delayed)await new Promise(resolve=>setTimeout(resolve,250));
      }
      res.writeHead(200,{'Content-Type':'application/json'});res.end(JSON.stringify(data));return;
    }
    const file=path.resolve(root,pathname.replace(/^\//,'')||'index.html');
    if(!file.startsWith(root+path.sep)){res.writeHead(403);res.end();return;}
    try{const body=await fs.readFile(file);res.writeHead(200,{'Content-Type':{'.html':'text/html; charset=utf-8','.js':'text/javascript','.css':'text/css'}[path.extname(file)]||'application/octet-stream'});res.end(body);}catch{res.writeHead(404);res.end();}
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const browser=await chromium.launch({headless:true});
  try{
    for(const width of [1440,390]){
      empty=truncated=fail=delayed=false;
      const page=await browser.newPage({viewport:{width,height:820}}), errors=[];
      page.on('pageerror',e=>{errors.push(e.message);console.error(e.message);});
      await page.addInitScript(()=>localStorage.setItem('servermap-language','zh'));
      await page.goto(`http://127.0.0.1:${server.address().port}${width===390?'/servermap/':'/'}`);
      const toggle=page.locator('[data-layer="mineral-heatmap"] input');
      await toggle.waitFor({state:'attached'});assert.equal(await page.locator('[data-layer="mineral-heatmap"] label').textContent(),'矿物热力图');
      await page.locator('#home').click();
      await page.waitForFunction(()=>document.querySelector('#zoom').textContent.includes('级别 12'));
      assert.equal(await page.locator('#oreHeatmapControls').isVisible(),false);
      if(width===390)await page.locator('#mobileMenu').click();
      await toggle.check();
      await page.waitForFunction(()=>document.querySelectorAll('#oreHeatmapSelect option').length===3);
      assert.equal(await page.locator('.ore-legend span').count(),15);
      assert.equal(await page.locator('.leaflet-overlay-pane canvas').count(),1);
      const map=await page.locator('#map').boundingBox();
      await page.locator('#sidebar').evaluate(el=>el.style.visibility='hidden');
      await page.locator('#sidebarShade').evaluate(el=>el.style.pointerEvents='none');
      await page.locator('#oreHeatmapControls').evaluate(el=>el.style.pointerEvents='none');
      await page.mouse.click(map.width/2+16,map.height/2+16);
      await page.locator('.leaflet-popup-content').waitFor();
      assert.match(await page.locator('.leaflet-popup-content').innerText(),/铜.*4.20‰/);
      assert.doesNotMatch(await page.locator('.leaflet-popup-content').innerText(),/采样[：时间]|Sampled/);
      await page.locator('.leaflet-popup-close-button').click();
      await page.waitForFunction(()=>!document.querySelector('.leaflet-popup-content'));
      await page.locator('#sidebar').evaluate(el=>el.style.visibility='');
      const filtered=page.waitForResponse(r=>r.url().includes('ore=cassiterite'));
      await page.locator('#oreHeatmapSelect').selectOption('cassiterite');
      await filtered;
      assert.equal(requests.at(-1).ore,'cassiterite');assert.equal(requests.at(-1).bbox.split(',').length,4);
      await page.locator('#sidebar').evaluate(el=>el.style.visibility='hidden');
      await page.locator('#sidebarShade').evaluate(el=>el.style.pointerEvents='none');
      await page.mouse.click(map.width/2+16,map.height/2+16);
      await page.locator('.leaflet-popup-content').waitFor();
      assert.match(await page.locator('.leaflet-popup-content').innerText(),/微量/);
      assert.doesNotMatch(await page.locator('.leaflet-popup-content').innerText(),/铜/);
      await page.locator('.leaflet-popup-close-button').click();
      await page.waitForFunction(()=>!document.querySelector('.leaflet-popup-content'));
      await page.locator('#sidebar').evaluate(el=>el.style.visibility='');
      const refresh = async () => {
        const pending=page.waitForResponse(r=>r.url().includes('/layers/mineral-heatmap'));
        for(const stream of streams)stream.write('event: layer\ndata: {"layer":"mineral-heatmap"}\n\n');
        await pending;
      };
      // Empty/error responses must clear the previously interactive density area.
      truncated=true;await refresh();
      truncated=false;empty=true;await refresh();
      await page.locator('#sidebar').evaluate(el=>el.style.visibility='hidden');
      await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
      await page.mouse.click(map.width/2+16,map.height/2+16);
      assert.equal(await page.locator('.leaflet-popup-content').count(),0);
      fail=true;await refresh();await page.waitForFunction(()=>document.querySelectorAll('#oreHeatmapSelect option').length<=2);
      await page.locator('#sidebar').evaluate(el=>el.style.visibility='');
      fail=false;empty=false;await refresh();
      await page.locator('#language').selectOption('en',{force:true});
      await page.waitForFunction(()=>document.querySelector('#oreHeatmapSelect option:checked').textContent==='Cassiterite');
      assert.equal(await page.locator('#oreHeatmapSelect').inputValue(),'cassiterite');
      assert.equal(await page.locator('[data-layer="mineral-heatmap"] label').innerText(),'Mineral heatmap');
      delayed=true;
      const pending=page.waitForRequest(r=>r.url().includes('/layers/mineral-heatmap'));
      for(const stream of streams)stream.write('event: layer\ndata: {"layer":"mineral-heatmap"}\n\n');
      await pending;await toggle.uncheck();assert.equal(await page.locator('#oreHeatmapControls').isVisible(),false);
      delayed=false;await toggle.check();
      await page.waitForFunction(()=>document.querySelector('#oreHeatmapControls').hidden===false);
      for(const stream of streams)stream.write('event: visibility\ndata: {"changed":true}\n\n');
      await page.waitForFunction(()=>document.querySelector('#oreHeatmapSelect').value==='');
      const policy = rule => {for(const stream of streams)stream.write('event: settings\ndata: '+JSON.stringify({management:{fogEnabled:false,layers:{'mineral-heatmap':rule}}})+'\n\n');};
      policy({forbidden:true});
      await page.waitForFunction(()=>document.querySelector('[data-layer="mineral-heatmap"] input').disabled&&document.querySelector('#oreHeatmapControls').hidden);
      assert.equal(await page.locator('#oreHeatmapSelect option').count(),1,'forbidden policy must clear cached catalog');
      policy({forced:true});
      await page.waitForFunction(()=>document.querySelector('[data-layer="mineral-heatmap"] input').checked&&!document.querySelector('#oreHeatmapControls').hidden);
      assert.equal(await toggle.isDisabled(),true);
      await page.waitForFunction(()=>document.querySelectorAll('#oreHeatmapSelect option').length===3);
      assert.deepEqual(errors,[]);
      await page.close();
      console.log(`PASS: heatmap ${width}px: layer, canvas, popup, filtering, SSE, empty/error states, language, toggle race, privacy reset and forced/forbidden policy`);
    }
  }finally{await browser.close();for(const stream of streams)stream.end();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
}
main().catch(error=>{console.error(error);process.exitCode=1;});
