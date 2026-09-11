const assert=require('node:assert/strict');
module.exports=async(call,cookies)=>{
 const before=(await call('/announcement',cookies.admin)).json(),policy=before.management;
 assert.equal(policy.ImageMaxMb,10);assert.equal(policy.DailyTeleports,0);
 for(const endpoint of ['/admin/images','/admin/tracks','/admin/online-players']){
  assert.equal((await call(endpoint)).status,403);assert.equal((await call(endpoint,cookies.alice)).status,403);assert.equal((await call(endpoint,cookies.admin)).status,200);
 }
 assert.equal((await call('/admin/tracks',cookies.alice,{action:'start',uid:'alice',seconds:10})).status,403);
 assert.equal((await call('/admin/tracks',cookies.admin,{action:'start',uid:'missing',seconds:10})).status,400);
 const history=(await call('/admin/tracks?q=Track%20fixture',cookies.admin)).json();assert.equal(history.length,1);
 const track=(await call('/admin/tracks?id='+history[0].Id,cookies.admin)).json();assert.equal(track.Samples.length,10);
 assert.equal((await call('/admin/tracks?id='+track.Id+'&after='+encodeURIComponent(track.Samples[5].Time),cookies.admin)).json().Samples.length,4);
 const mountImage='/admin/track-mount-image?id='+track.Id+'&key='+track.Samples[0].Mount.ImageKey;
 assert.equal((await call(mountImage)).status,403);assert.equal((await call(mountImage,cookies.alice)).status,403);assert.equal((await call(mountImage,cookies.admin)).status,200);
 assert.equal((await call('/admin/track-mount-image?id=unknown&key='+track.Samples[0].Mount.ImageKey,cookies.admin)).status,404);
 const deleteUrl='/admin/tracks?id='+track.Id,header={'X-ServerMap-Request':'1'};
 assert.equal((await call(deleteUrl,undefined,undefined,'DELETE',header)).status,403);
 assert.equal((await call(deleteUrl,cookies.alice,undefined,'DELETE',header)).status,403);
 assert.equal((await call(deleteUrl,cookies.admin,undefined,'DELETE')).status,403);
 assert.equal((await call('/admin/tracks?id=unknown',cookies.admin,undefined,'DELETE',header)).status,404);
 assert.equal((await call(deleteUrl,cookies.admin,undefined,'DELETE',header)).status,200);
 assert.equal((await call(deleteUrl,cookies.admin)).status,404);
 assert.equal((await call(mountImage,cookies.admin)).status,404);
 assert.equal((await call('/admin/tracks?q=Track%20fixture',cookies.admin)).json().length,0);
 for(const management of [{...policy,ImageMaxMb:51},{...policy,ImageTypes:['../gif']},{...policy,Layers:{players:{Forbidden:true,Forced:true,Scale:1}}},{...policy,Layers:{players:{Scale:11}}}]){
  assert.equal((await call('/announcement',cookies.admin,{html:before.html,management})).status,400);
 }
 const changed={...policy,ImageTypes:['png'],ImageMaxMb:12,PoiQuota:0,DailyTeleports:3,Layers:{players:{Forced:true,DefaultVisible:true,Scale:2},pois:{Forbidden:true,Scale:1},mounts:{DefaultVisible:true,Scale:.1}}};
 assert.equal((await call('/announcement',cookies.alice,{html:before.html,management:changed})).status,403);
 assert.equal((await call('/announcement',cookies.admin,{html:before.html,management:changed})).status,200);
 const manifest=(await call('/layers/manifest')).json().layers;
 assert.equal(manifest.find(l=>l.id==='players').forced,true);assert.equal(manifest.find(l=>l.id==='players').scale,2);
 assert.equal(manifest.find(l=>l.id==='pois').forbidden,true);
 assert.equal((await call('/layers/pois',cookies.admin)).json().features.length,0);
 assert.equal((await call('/pois',cookies.alice,{name:'quota',x:300,z:300})).status,409);
 assert.equal((await call('/announcement',cookies.admin,{html:before.html})).json().management.DailyTeleports,3);
 assert.equal((await call('/announcement',cookies.admin,{html:before.html,management:policy})).status,200);
 console.log('PASS real management API: admin gates, policy validation, forced/blocked layers, scale, quotas, persistence');
};
