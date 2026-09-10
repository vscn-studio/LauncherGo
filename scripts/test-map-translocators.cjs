const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const webRoot=path.resolve(__dirname,'../LauncherGo.ServerMapHost/WebRoot');
const link=(id,a,b,properties={})=>({id,type:'Feature',geometry:{type:'LineString',coordinates:[a,b]},properties:{name:id,...properties}});
async function main(){
  const browser=await chromium.launch({headless:true});
  try{for(const [mobile,svg] of [[false,false],[false,true],[true,false]]){
    const page=await browser.newPage({viewport:mobile?{width:390,height:844}:{width:1280,height:800},hasTouch:mobile,isMobile:mobile});
    const errors=[];page.on('pageerror',e=>errors.push(e.message));
    let fixtures=[link('A',[-90,0],[90,0]),link('B',[-90,100],[90,100]),link('Hidden',[-90,-100],[-90,-100],{targetVisible:false,lineVisible:false})];
    await page.addInitScript(()=>{localStorage.setItem('servermap-pinned','unpinned');window.EventSource=class extends EventTarget{constructor(){super();window.events=this;}};});
    await page.route('http://servermap.test/**',async route=>{
      const url=new URL(route.request().url()),json=value=>route.fulfill({json:value});
      if(url.pathname.endsWith('/map/metadata'))return json({maxZoom:12,maxZoomOut:12,spawn:{x:0,z:0},tileVersion:'test',colormapReady:true});
      if(url.pathname.endsWith('/layers/manifest'))return json({layers:[{id:'translocators',visible:true}]});
      if(url.pathname.endsWith('/layers/translocators'))return json({features:fixtures});
      if(url.pathname.endsWith('/auth/me'))return json({authenticated:false});
      if(url.pathname.endsWith('/announcement'))return json({html:'<span></span>'});
      if(url.pathname.endsWith('/render-progress'))return json({phase:'idle'});
      if(url.pathname.includes('/tiles/'))return route.fulfill({path:path.join(webRoot,'assets/sky.png'),contentType:'image/png'});
      if(url.pathname.startsWith('/api/'))return json([]);
      const asset=url.pathname==='/'?'index.html':url.pathname.slice(1),file=path.resolve(webRoot,asset);assert.ok(file.startsWith(webRoot+path.sep));
      let body=await fs.readFile(file);if(asset==='index.html'){body=body.toString().replace('const map=L.map','const map=window.testMap=L.map');if(svg)body=body.replace('preferCanvas:true','preferCanvas:false');}
      return route.fulfill({body,contentType:{'.html':'text/html','.js':'text/javascript','.css':'text/css','.svg':'image/svg+xml','.png':'image/png'}[path.extname(file)]||'text/plain'});
    });
    await page.goto('http://servermap.test/');await page.waitForFunction(()=>document.querySelectorAll('.translocator-icon').length===5);
    const icons=id=>page.locator(`.translocator-marker[title="${id}"]`);
    const check=async(id,active)=>{
      await page.waitForFunction(({id,color})=>[...document.querySelectorAll('.translocator-marker')].filter(e=>e.title===id).every(e=>getComputedStyle(e.querySelector('.translocator-icon')).backgroundColor===color),{id,color:active?'rgb(230, 190, 255)':'rgb(188, 132, 229)'});
      const state=await page.evaluate(id=>{
        const markers=Object.values(window.testMap._layers).filter(l=>l instanceof L.Marker&&l.options.title===id),origin=markers[0].getLatLng();
        const line=Object.values(window.testMap._layers).find(l=>l.options.className==='translocator-line'&&l.getLatLngs()[0].equals(origin));
        return {line:line?{weight:line.options.weight,color:line.options.color,opacity:line.options.opacity,stroke:line.getElement()?getComputedStyle(line.getElement()).stroke:null,width:line.getElement()?parseFloat(getComputedStyle(line.getElement()).strokeWidth):null}:null,
          icons:markers.map(m=>{const icon=m.getElement().querySelector('.translocator-icon'),s=getComputedStyle(icon),parent=getComputedStyle(m.getElement());return{color:s.backgroundColor,opacity:Number(s.opacity),mask:s.maskImage,shadow:parent.boxShadow,border:parent.borderTopWidth};})};
      },id);
      const color=active?'rgb(230, 190, 255)':'rgb(188, 132, 229)',opacity=active?1:.85;
      for(const icon of state.icons){assert.equal(icon.color,color);assert.equal(icon.opacity,opacity);assert.match(icon.mask,/spiral\.svg/);assert.equal(icon.shadow,'none','No icon ring');assert.equal(icon.border,'0px');}
      if(state.line){assert.equal(state.line.color,active?'#e6beff':'#bc84e5');assert.equal(state.line.opacity,opacity);assert.equal(state.line.weight,1.5);if(svg){assert.equal(state.line.stroke,color);assert.equal(state.line.width,1.5);}}
    };
    await check('A',false);await check('B',false);await check('Hidden',false);
    if(!mobile){
      await icons('A').first().hover();await check('A',true);await check('B',false);
      await page.mouse.move(10,200);await check('A',false);
      // Leaflet Canvas throttles hover hit-testing; allow the previous move to settle.
      await page.waitForTimeout(50);
      const center=await page.evaluate(()=>window.testMap.latLngToContainerPoint([0,0]));await page.mouse.move(center.x,center.y);
      await check('A',true);
      await page.waitForTimeout(50);await page.mouse.move(10,200);await check('A',false);
    }
    if(mobile)await icons('A').first().tap();else await icons('A').first().click();
    await page.mouse.move(10,200);await check('A',true);
    // Export must include the colored spiral mask, not only the connecting line.
    const coloredPixels=await page.evaluate(async()=>{
      const map=window.testMap,marker=Object.values(map._layers).find(l=>l instanceof L.Marker&&l.options.title==='A'&&l.options.translocatorEndpoint===0),p=map.latLngToContainerPoint(marker.getLatLng());
      const bounds=L.latLngBounds(map.containerPointToLatLng([p.x-12,p.y-12]),map.containerPointToLatLng([p.x+12,p.y+12])),tileLayer=Object.values(map._layers).find(l=>l instanceof L.TileLayer);
      const {blob}=await ServerMapScreenshot.capture({map,bounds,tileLayer,nativeZoom:12,signal:new AbortController().signal});
      const image=await createImageBitmap(blob),canvas=document.createElement('canvas');canvas.width=image.width;canvas.height=image.height;const ctx=canvas.getContext('2d');ctx.drawImage(image,0,0);image.close();
      const data=ctx.getImageData(0,0,canvas.width,canvas.height).data,ratio=canvas.width/24;let count=0;
      for(let y=0;y<canvas.height;y++)for(let x=0;x<canvas.width;x++){if(Math.abs(y-canvas.height/2)<4*ratio)continue;const i=(y*canvas.width+x)*4;if(Math.abs(data[i]-230)<4&&Math.abs(data[i+1]-190)<4&&data[i+2]>250&&data[i+3]>250)count++;}return count;
    });assert.ok(coloredPixels>10,'Colored spiral pixels survive PNG export away from the connection');
    if(mobile)await icons('B').first().tap();else await icons('B').first().click();await page.mouse.move(10,200);await check('A',false);await check('B',true);
    await page.evaluate(()=>window.testMap.closePopup());await check('B',false);
    // Real dblclick dispatch: no map double-click zoom, and the opposite popup owns selection.
    await icons('A').first().dblclick();
    let destination=await page.evaluate(()=>{const map=window.testMap,selected=Object.values(map._layers).find(l=>l instanceof L.Marker&&l.isPopupOpen());return{zoom:map.getZoom(),x:map.getCenter().lng*4096,z:map.getCenter().lat*4096,endpoint:selected.options.translocatorEndpoint,name:selected.options.title};});
    assert.deepEqual(destination,{zoom:12,x:90,z:0,endpoint:1,name:'A'});await check('A',true);
    await icons('A').last().dblclick();destination=await page.evaluate(()=>{const map=window.testMap,selected=Object.values(map._layers).find(l=>l instanceof L.Marker&&l.isPopupOpen());return{zoom:map.getZoom(),x:map.getCenter().lng*4096,endpoint:selected.options.translocatorEndpoint};});assert.deepEqual(destination,{zoom:12,x:-90,endpoint:0});
    await page.evaluate(()=>{window.testMap.closePopup();window.testMap.setView([0,0],12,{animate:false});});
    const before=await page.evaluate(()=>({center:window.testMap.getCenter(),zoom:window.testMap.getZoom()}));
    await icons('Hidden').dblclick();assert.deepEqual(await page.evaluate(()=>({center:window.testMap.getCenter(),zoom:window.testMap.getZoom()})),before,'Hidden destination is never revealed or used for navigation');
    await page.mouse.move(10,200);await page.evaluate(()=>window.testMap.closePopup());
    if(mobile)await page.locator('#mobileMenu').tap();
    await page.locator('#layers input').uncheck({force:true});assert.equal(await page.locator('.translocator-icon').count(),0);
    await page.locator('#layers input').check({force:true});await page.waitForFunction(()=>document.querySelectorAll('.translocator-icon').length===5);await check('A',false);await check('B',false);
    assert.deepEqual(errors,[]);console.log(`PASS translocators ${mobile?'mobile':'desktop'} ${svg?'SVG':'Canvas'}: matching colors, hover, selection, no rings, bidirectional double-click, hidden endpoint, layer reset, PNG masks`);await page.close();
  }}finally{await browser.close();}
}
main().catch(error=>{console.error(error);process.exitCode=1;});
