const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const {chromium} = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const webRoot = path.resolve(__dirname, '../LauncherGo.ServerMapHost/WebRoot');
async function main() {
  const browser = await chromium.launch({headless:true});
  try {
    for (const owner of ['', 'admin', 'player']) for (const width of [1280, 390]) {
      const page = await browser.newPage({viewport:{width,height:844},locale:'zh-CN'});
      const errors=[]; page.on('pageerror', e=>errors.push(e.message));
      let cost=owner==='admin'?0:2, available=3, posts=0, failure=false, quoteBody, enabled=false;
      const defaults={itemCode:'game:gear-temporal',itemsPerJump:1,effectsEnabled:false,stabilityLossPercent:0,hungerLoss:0,healthLoss:0};
      let policy={...defaults};
      await page.addInitScript(()=>{window.EventSource=class extends EventTarget{constructor(){super();window.testEvents=this;}close(){}};});
      await page.route('http://servermap.test/**',async route=>{
        const url=new URL(route.request().url()), name=url.pathname;
        const json=(value,status=200)=>route.fulfill({json:value,status});
        if(name.endsWith('/auth/me'))return json({authenticated:!!owner,admin:owner==='admin',name:owner});
        if(name.endsWith('/map/metadata'))return json({maxZoom:12,maxZoomOut:8,spawn:{x:500000,z:500000},colormapReady:true,tileVersion:'test'});
        if(name.endsWith('/layers/manifest'))return json({layers:[{id:'players',visible:true}]});
        if(name.includes('/layers/'))return json({features:[]});
        if(name.endsWith('/announcement')){if(route.request().method()==='POST'){assert.equal(owner,'admin');const body=route.request().postDataJSON();enabled=body.playerGearTeleportEnabled;policy=body.playerTeleport;}return json({html:'',playerGearTeleportEnabled:enabled,playerTeleport:policy});}
        if(name.endsWith('/teleport/quote')){
          quoteBody=route.request().postDataJSON();assert.equal(route.request().headers()['x-servermap-request'],'1');
          const reason=owner!=='admin'&&cost===0?'teleport_zero_jumps':cost>available?'teleport_gears':null;
          return json({quoteId:'server-quote',x:quoteBody.x+.5,y:111,z:quoteBody.z+.5,cost,jumps:cost/policy.itemsPerJump,itemCode:policy.itemCode,settings:policy,available,admin:owner==='admin',allowed:!reason,reason});
        }
        if(name.endsWith('/teleport')){
          posts++;assert.deepEqual(route.request().postDataJSON(),{quoteId:'server-quote'});
          await new Promise(resolve=>setTimeout(resolve,150));
          return failure?json({error:'teleport_changed'},409):json({ok:true,x:quoteBody.x+.5,y:111,z:quoteBody.z+.5,consumed:cost});
        }
        if(name.startsWith('/api/'))return json([]);
        const file=path.join(webRoot,name==='/'?'index.html':name.slice(1));
        return route.fulfill({body:await fs.readFile(file),contentType:{'.html':'text/html','.js':'text/javascript','.css':'text/css','.png':'image/png','.svg':'image/svg+xml'}[path.extname(file)]});
      });
      await page.goto('http://servermap.test/');
      await page.waitForFunction(()=>document.querySelector('#layers li'));
      if(width<700)await page.locator('#mobilePoint').click();
      else await page.locator('#map').click({button:'right',position:{x:width/2,y:500}});
      const button=page.locator('#contextMenu [data-action="teleport"]');
      if(!owner){assert.equal(await button.isVisible(),false);await page.close();continue;}
      if(owner==='player'){
        assert.equal(await button.isVisible(),false,'Player teleport must be hidden by default');
        enabled=true;await page.evaluate(()=>window.testEvents.dispatchEvent(new MessageEvent('settings',{data:JSON.stringify({playerGearTeleportEnabled:true})})));
      }
      await button.click();await page.waitForFunction(()=>!document.querySelector('#teleportRefresh').disabled);
      assert.ok(quoteBody.x>490000&&quoteBody.z>490000,'Relative coordinates were not converted to world coordinates');
      assert.equal(await page.locator('#teleportSubmit').isEnabled(),true);
      if(owner==='player'){
        assert.match(await page.locator('#teleportDetails').textContent(),/本次消耗: 2/);
        policy={itemCode:'game:gear-rusty',itemsPerJump:3,effectsEnabled:true,stabilityLossPercent:25,hungerLoss:100,healthLoss:2};cost=6;available=9;
        await page.evaluate(policy=>window.testEvents.dispatchEvent(new MessageEvent('settings',{data:JSON.stringify({playerGearTeleportEnabled:true,playerTeleport:policy})})),policy);
        assert.equal(await page.locator('#teleportSubmit').isEnabled(),false,'Policy update must invalidate confirmation');
        await page.locator('#teleportRefresh').click();await page.waitForFunction(()=>!document.querySelector('#teleportSubmit').disabled);
        assert.match(await page.locator('#teleportDetails').textContent(),/本次消耗: 6 game:gear-rusty/);
        assert.match(await page.locator('#teleportDetails').textContent(),/生命值扣减 2/);
        policy={...defaults};cost=2;available=3;
        await page.evaluate(policy=>window.testEvents.dispatchEvent(new MessageEvent('settings',{data:JSON.stringify({playerGearTeleportEnabled:true,playerTeleport:policy})})),policy);
        enabled=false;await page.evaluate(()=>window.testEvents.dispatchEvent(new MessageEvent('settings',{data:JSON.stringify({playerGearTeleportEnabled:false})})));
        assert.equal(await page.locator('#teleportSubmit').isEnabled(),false);assert.match(await page.locator('#teleportError').textContent(),/已关闭/);
        await page.locator('#teleportForm').evaluate(form=>form.dispatchEvent(new Event('submit',{cancelable:true})));assert.equal(posts,0,'Disabled confirmation must not submit');
        enabled=true;await page.evaluate(()=>window.testEvents.dispatchEvent(new MessageEvent('settings',{data:JSON.stringify({playerGearTeleportEnabled:true})})));
        cost=0;await page.locator('#teleportRefresh').click();await page.waitForFunction(()=>!document.querySelector('#teleportRefresh').disabled);
        assert.equal(await page.locator('#teleportSubmit').isEnabled(),false);assert.match(await page.locator('#teleportError').textContent(),/0 次跃迁/);
        cost=4;await page.locator('#teleportRefresh').click();await page.waitForFunction(()=>!document.querySelector('#teleportRefresh').disabled);
        assert.equal(await page.locator('#teleportSubmit').isEnabled(),false);assert.match(await page.locator('#teleportError').textContent(),/不足/);
        cost=2;await page.locator('#teleportRefresh').click();await page.waitForFunction(()=>!document.querySelector('#teleportSubmit').disabled);
        failure=true;await page.locator('#teleportSubmit').click();await page.waitForFunction(()=>!document.querySelector('#teleportRefresh').disabled);
        assert.match(await page.locator('#teleportError').textContent(),/位置或权限/);assert.equal(await page.locator('#teleportSubmit').isEnabled(),false);
        failure=false;await page.locator('#teleportRefresh').click();await page.waitForFunction(()=>!document.querySelector('#teleportSubmit').disabled);
      }else assert.match(await page.locator('#teleportDetails').textContent(),/不消耗物品/);
      const before=posts;await page.locator('#teleportSubmit').click();
      await page.locator('#teleportForm').evaluate(form=>form.dispatchEvent(new Event('submit',{cancelable:true})));
      await page.waitForFunction(()=>document.querySelector('#teleportDetails').textContent.includes('传送完成'));
      assert.equal(posts,before+1,'Duplicate submit sent a second payment request');assert.deepEqual(errors,[]);
      if(owner==='admin'){
        await page.locator('#teleportModal [data-close-modal]').click();
        if(width<700)await page.locator('#mobileMenu').click();
        await page.locator('#manageButton').click();
        assert.equal(await page.locator('#playerGearTeleportInput').isChecked(),false);
        assert.equal(await page.locator('#teleportItemCodeInput').inputValue(),'game:gear-temporal');
        assert.equal(await page.locator('#teleportItemsPerJumpInput').inputValue(),'1');
        assert.equal(await page.locator('#teleportEffectsInput').isChecked(),false);
        assert.equal(await page.locator('#teleportHealthInput').isEnabled(),false);
        await page.locator('#teleportItemCodeInput').fill('game:gear-rusty');
        await page.locator('#teleportItemsPerJumpInput').fill('3');
        await page.locator('#teleportEffectsInput').check();
        await page.locator('#teleportStabilityInput').fill('25');await page.locator('#teleportHungerInput').fill('100');await page.locator('#teleportHealthInput').fill('2');
        await page.locator('#playerGearTeleportInput').check();await page.locator('#manageForm button[type="submit"]').click();
        await page.waitForFunction(()=>document.querySelector('#manageModal').hidden);assert.equal(enabled,true);
        assert.deepEqual(policy,{itemCode:'game:gear-rusty',itemsPerJump:3,effectsEnabled:true,stabilityLossPercent:25,hungerLoss:100,healthLoss:2});
        await page.locator('#manageButton').click();assert.equal(await page.locator('#playerGearTeleportInput').isChecked(),true);
        assert.equal(await page.locator('#teleportItemCodeInput').inputValue(),'game:gear-rusty');assert.equal(await page.locator('#teleportHealthInput').inputValue(),'2');
        await page.locator('#playerGearTeleportInput').uncheck();await page.locator('#manageForm button[type="submit"]').click();
        await page.waitForFunction(()=>document.querySelector('#manageModal').hidden);assert.equal(enabled,false);
      }
      console.log(`PASS ${owner} ${width}: context action, authoritative quote, cost/eligibility and single submission`);await page.close();
    }
  }finally{await browser.close();}
}
main().catch(error=>{console.error(error);process.exitCode=1;});
