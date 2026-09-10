/* Cartographic areas: sparse, integer base-pixel rectangles, never a world-sized bitmap. */
(() => {
  const MAX_RECTS=1024, BANDS=[[4,6],[7,9],[10,11],[12,13]];
  const overlaps=(a,b)=>a[0]<b[2]&&a[2]>b[0]&&a[1]<b[3]&&a[3]>b[1];
  function subtract(a,b){
    if(!overlaps(a,b))return [a];
    const x1=Math.max(a[0],b[0]),z1=Math.max(a[1],b[1]),x2=Math.min(a[2],b[2]),z2=Math.min(a[3],b[3]),out=[];
    if(a[1]<z1)out.push([a[0],a[1],a[2],z1]);if(z2<a[3])out.push([a[0],z2,a[2],a[3]]);
    if(a[0]<x1)out.push([a[0],z1,x1,z2]);if(x2<a[2])out.push([x2,z1,a[2],z2]);return out;
  }
  function cut(rects,cuts){
    let result=rects,budget=2000000;
    for(const c of cuts){const next=[];for(const r of result){if(--budget<0)throw Error('complex');next.push(...subtract(r,c));if(next.length>MAX_RECTS)throw Error('complex');}result=next;}return result;
  }
  // Merge full shared edges to keep repeated brush strokes compact.
  function compact(rects){
    let result=rects;
    for(let pass=0;pass<4;pass++)for(const axis of [0,1]){
      const other=1-axis,sorted=result.slice().sort((a,b)=>a[other]-b[other]||a[other+2]-b[other+2]||a[axis]-b[axis]),out=[];
      for(const r of sorted){const last=out.at(-1);if(last&&last[other]===r[other]&&last[other+2]===r[other+2]&&last[axis+2]===r[axis])last[axis+2]=r[axis+2];else out.push(r.slice());}result=out;
    }
    if(result.length>MAX_RECTS)throw Error('complex');return result;
  }
  function paint(rects,rect,erase,occupied=[]){return compact(erase?cut(rects,[rect]):rects.concat(cut(cut([rect],occupied),rects)));}
  const contains=(r,p)=>p.x>=r[0]&&p.x<r[2]&&p.z>=r[1]&&p.z<r[3];
  const bounds=rects=>[Math.min(...rects.map(r=>r[0])),Math.min(...rects.map(r=>r[1])),Math.max(...rects.map(r=>r[2])),Math.max(...rects.map(r=>r[3]))];
  // Cancel shared edges, including partially shared ones: no internal rectangle seams.
  function boundary(rects){
    const lines=new Map();
    function edge(axis,line,start,end,sign){const key=axis+':'+line;let item=lines.get(key);if(!item)lines.set(key,item={axis,line,events:new Map()});item.events.set(start,(item.events.get(start)||0)+sign);item.events.set(end,(item.events.get(end)||0)-sign);}
    for(const [x1,z1,x2,z2] of rects){edge('h',z1,x1,x2,1);edge('h',z2,x1,x2,-1);edge('v',x1,z1,z2,1);edge('v',x2,z1,z2,-1);}
    const out=[];for(const {axis,line,events} of lines.values()){let sum=0,prev;for(const [p,delta] of [...events].sort((a,b)=>a[0]-b[0])){if(sum&&prev!==undefined&&p>prev)out.push(axis==='h'?[prev,line,p,line]:[line,prev,line,p]);sum+=delta;prev=p;}}return out;
  }
  const words={zh:{layer:'区域标记',add:'添加区域标记',edit:'编辑区域标记',remove:'删除区域标记',manage:'管理区域标记',type:'显示级别',select:'框选',erase:'擦除',pan:'移动地图',undo:'撤销',redo:'恢复',finish:'完成',cancel:'取消',save:'保存',name:'区域名称',color:'颜色',help:'拖拽框选或擦除，边缘对齐底图像素（1 方块）。只修改当前草稿；同类型已有区域自动避让。可放大后精细编辑。',empty:'请先选择未被同类型区域占用的范围。',complex:'选区过于复杂，请减少片段（最多 1024 个）。',error:'操作失败，请重试。',conflict:'区域已被其他管理员更新，请取消后重新打开，避免覆盖新数据。',confirm:'删除这个区域标记？不会改变领地或隐藏区域。',saved:'区域标记已保存',unavailable:'区域标记暂不可用，请确认服务端模组已更新。',changedType:'切换类型会避让该类型的已有区域。',count:'片段',loading:'正在加载…'},en:{layer:'Area markers',add:'Add area marker',edit:'Edit area marker',remove:'Delete area marker',manage:'Manage area markers',type:'Zoom band',select:'Select',erase:'Erase',pan:'Pan map',undo:'Undo',redo:'Redo',finish:'Finish',cancel:'Cancel',save:'Save',name:'Area name',color:'Color',help:'Drag to select or erase, aligned to base-map pixels (1 block). Only the draft changes; existing same-band areas are excluded. Zoom in for precise editing.',empty:'Select an area not occupied in this zoom band.',complex:'Selection is too complex (maximum 1024 rectangles).',error:'Operation failed. Please retry.',conflict:'Another admin updated areas. Cancel and reopen to avoid overwriting new data.',confirm:'Delete this area marker? Claims and hidden regions are unaffected.',saved:'Area marker saved',unavailable:'Area markers unavailable. Check the server mod version.',changedType:'Changing bands excludes areas already occupied in that band.',count:'pieces',loading:'Loading…'}};
  function create({map,api,gameLatLng,gamePoint,getAuth,getLanguage,getMetadata,layerVisibility,cancelOtherTools}){
    const t=key=>words[getLanguage()==='zh'?'zh':'en'][key]||key,el=(tag,props={})=>Object.assign(document.createElement(tag),props);
    const button=(key,action)=>{const b=el('button',{type:'button',className:'notebook-button',textContent:t(key)});b.dataset.areaAction=key;b.onclick=action;return b;};
    const svgEl=(name,attrs={})=>{const node=document.createElementNS('http://www.w3.org/2000/svg',name);for(const [key,value] of Object.entries(attrs))node.setAttribute(key,String(value));return node;};
    const section=el('section',{className:'notebook-section',id:'areaMarkerSection'}),heading=el('div',{className:'notebook-heading layer-row'}),toggle=el('input',{type:'checkbox',id:'areaMarkerToggle',checked:layerVisibility.get('area-markers',true)}),label=el('label',{htmlFor:toggle.id});
    heading.append(toggle,label);section.append(heading);document.querySelector('#sidebar [data-i18n="onlinePlayers"]').before(section);
    const manage=button('manage',()=>manageDialog());manage.classList.add('area-manage');section.append(manage);
    const context=document.querySelector('#contextMenu'),add=button('add',()=>start()),edit=button('edit',()=>start(contextMarker)),remove=button('remove',()=>removeMarker(contextMarker));context.append(add,edit,remove);
    const toolbar=el('div',{id:'areaMarkerToolbar',hidden:true}),modal=el('div',{className:'modal-backdrop',id:'areaMarkerModal',hidden:true}),notice=el('div',{id:'areaMarkerNotice',hidden:true});notice.setAttribute('role','status');document.body.append(toolbar,modal,notice);
    map.createPane('areaMarkers');map.getPane('areaMarkers').style.zIndex='410';map.getPane('areaMarkers').style.pointerEvents='none';
    map.createPane('areaMarkerDraft');map.getPane('areaMarkerDraft').style.zIndex='640';map.getPane('areaMarkerDraft').style.pointerEvents='none';
    const group=L.layerGroup().addTo(map),draftGroup=L.layerGroup().addTo(map);
    let data={revision:0,markers:[]},loaded=false,ready=false,epoch=0,pending=false,controller,draft=null,contextMarker,tool='select',undo=[],redo=[],pointer=null,rubber,noticeTimer,busy=false,frame=0,savedHandlers;
    function tell(key){notice.textContent=t(key);notice.hidden=false;clearTimeout(noticeTimer);noticeTimer=setTimeout(()=>notice.hidden=true,6500);}
    async function request(body,method=body?'POST':'GET'){
      const abort=new AbortController(),timer=setTimeout(()=>abort.abort(),15000);if(method==='GET')controller=abort;
      try{const response=await fetch(api+'/area-markers',{method,cache:'no-store',signal:abort.signal,headers:body?{'Content-Type':'application/json','X-ServerMap-Request':'1'}:{},body:body?JSON.stringify(body):undefined});if(!response.ok){const e=Error(response.status===409?'conflict':method==='GET'?'unavailable':'error');e.status=response.status;throw e;}return await response.json();}finally{clearTimeout(timer);if(controller===abort)controller=null;}
    }
    async function refresh(){
      if(!ready||document.hidden||pending)return;pending=true;const captured=epoch;
      try{const next=await request();if(captured!==epoch)return;if(!Array.isArray(next.markers)||!Number.isSafeInteger(next.revision))throw Error('unavailable');loaded=true;if(JSON.stringify(next)!==JSON.stringify(data)){data=next;render();}}
      catch{if(captured===epoch&&!loaded){data={revision:0,markers:[]};render();}}finally{pending=false;}
    }
    function visible(marker){return map.getZoom()>=marker.minZoom&&map.getZoom()<=marker.maxZoom;}
    function overlay(marker,target,isDraft=false){
      if(!marker.rects.length)return;
      const b=bounds(marker.rects),view=map.getBounds();if(!view.intersects(L.latLngBounds(gameLatLng(b[0],b[1]),gameLatLng(b[2],b[3]))))return;
      const svg=svgEl('svg',{viewBox:`${b[0]} ${b[1]} ${b[2]-b[0]} ${b[3]-b[1]}`,preserveAspectRatio:'none','aria-label':marker.name||t('add'),role:'img'});svg.dataset.areaId=marker.id||'draft';
      const shape=marker.rects.map(([x1,z1,x2,z2])=>`M${x1} ${z1}H${x2}V${z2}H${x1}Z`).join('');
      svg.append(svgEl('path',{d:shape,fill:marker.color,'fill-opacity':isDraft?.2:.08,stroke:'none'}));
      svg.append(svgEl('path',{d:boundary(marker.rects).map(([x1,z1,x2,z2])=>`M${x1} ${z1}L${x2} ${z2}`).join(''),fill:'none',stroke:marker.color,'stroke-opacity':isDraft?.85:.32,'stroke-width':isDraft?1.5:1,'vector-effect':'non-scaling-stroke'}));
      // Fit a single, subtly diagonal name inside a selected rectangle, never over a hole.
      if(marker.name&&!isDraft){
        const r=marker.rects.reduce((best,item)=>(item[2]-item[0])*(item[3]-item[1])>(best[2]-best[0])*(best[3]-best[1])?item:best),w=r[2]-r[0],h=r[3]-r[1];
        const defs=svgEl('defs'),clip=svgEl('clipPath',{id:'area-clip-'+marker.id});clip.append(svgEl('path',{d:shape}));defs.append(clip);svg.append(defs);
        const container=svgEl('g',{'clip-path':`url(#area-clip-${marker.id})`}),g=svgEl('g',{transform:`translate(${(r[0]+r[2])/2} ${(r[1]+r[3])/2}) rotate(${-Math.min(25,Math.atan2(h,w)*180/Math.PI)})`}),text=svgEl('text',{'font-size':100,'text-anchor':'middle',opacity:.58,fill:marker.color,stroke:'#20252b','stroke-width':1.5,'paint-order':'stroke fill'});text.textContent=marker.name;g.append(text);container.append(g);svg.append(container);
        L.svgOverlay(svg,[gameLatLng(b[0],b[1]),gameLatLng(b[2],b[3])],{pane:'areaMarkers',className:'area-marker-overlay',interactive:false}).addTo(target);
        const box=text.getBBox(),angle=Math.min(25,Math.atan2(h,w)*180/Math.PI)*Math.PI/180,pxPerBlock=Math.pow(2,map.getZoom()-getMetadata().maxZoom),scale=Math.min(.78*w/(Math.max(1,box.width)*Math.cos(angle)+Math.max(1,box.height)*Math.sin(angle)),.78*h/(Math.max(1,box.width)*Math.sin(angle)+Math.max(1,box.height)*Math.cos(angle)),28/(100*pxPerBlock));
        text.setAttribute('transform',`scale(${scale}) translate(${-box.x-box.width/2} ${-box.y-box.height/2})`);return;
      }
      L.svgOverlay(svg,[gameLatLng(b[0],b[1]),gameLatLng(b[2],b[3])],{pane:isDraft?'areaMarkerDraft':'areaMarkers',className:'area-marker-overlay',interactive:false}).addTo(target);
    }
    function render(){
      group.clearLayers();if(toggle.checked||draft)for(const m of data.markers)if(m.id!==draft?.id&&(draft?m.minZoom===draft.minZoom:visible(m)))overlay(m,group);
      renderDraft();
    }
    function renderDraft(){draftGroup.clearLayers();rubber=null;if(draft)overlay(draft,draftGroup,true);}
    function schedule(){if(!frame)frame=requestAnimationFrame(()=>{frame=0;render();});}
    function setTool(value){tool=value;if(draft){map.dragging[value==='pan'?'enable':'disable']();map.getContainer().classList.toggle('area-marker-drawing',value!=='pan');}renderToolbar();}
    function renderToolbar(){
      toolbar.hidden=!draft;if(!draft)return;toolbar.replaceChildren();const actions=el('div',{className:'notebook-toolbar-actions'}),typeLabel=el('label',{textContent:t('type')}),select=el('select',{id:'areaMarkerBand'});
      for(const [min,max] of BANDS)select.append(el('option',{value:String(min),textContent:`${min}–${max}`,selected:min===draft.minZoom}));
      select.onchange=()=>{try{const minZoom=Number(select.value),rects=cut(draft.rects,data.markers.filter(m=>m.id!==draft.id&&m.minZoom===minZoom).flatMap(m=>m.rects));undo.push({minZoom:draft.minZoom,rects:draft.rects});if(undo.length>32)undo.shift();redo=[];draft={...draft,minZoom,maxZoom:BANDS.find(b=>b[0]===minZoom)[1],rects};render();renderToolbar();}catch(e){select.value=String(draft.minZoom);tell(e.message);}};
      typeLabel.append(select);actions.append(typeLabel);
      for(const key of ['select','erase','pan']){const b=button(key,()=>setTool(key));b.setAttribute('aria-pressed',String(tool===key));actions.append(b);}
      for(const [key,source,target] of [['undo',undo,redo],['redo',redo,undo]]){const b=button(key,()=>{const previous=source.pop();if(!previous)return;target.push({minZoom:draft.minZoom,rects:draft.rects});draft={...draft,...previous,maxZoom:BANDS.find(b=>b[0]===previous.minZoom)[1]};render();renderToolbar();});b.disabled=!source.length;actions.append(b);}
      const finish=button('finish',finishDialog);finish.disabled=!draft.rects.length;actions.append(finish,button('cancel',cancel));toolbar.append(actions,el('div',{className:'area-help',textContent:t('help')}),el('div',{className:'area-help',textContent:`${draft.rects.length} ${t('count')} · ${t('changedType')}`}));
    }
    function cancel(){
      if(pointer){try{map.getContainer().releasePointerCapture(pointer.id);}catch{}pointer=null;}
      if(savedHandlers){for(const [key,enabled] of Object.entries(savedHandlers))map[key]?.[enabled?'enable':'disable']();savedHandlers=null;}
      draft=null;undo=[];redo=[];toolbar.hidden=true;modal.hidden=true;map.getContainer().classList.remove('area-marker-drawing');render();
    }
    async function start(marker){
      context.classList.remove('show');if(!getAuth().admin||busy)return;await refresh();if(!loaded){tell('unavailable');return;}if(!getAuth().admin)return;
      cancelOtherTools();cancel();map.closePopup();marker=marker?data.markers.find(m=>m.id===marker.id):null;
      const band=marker?BANDS.find(b=>b[0]===marker.minZoom):BANDS.find(b=>map.getZoom()>=b[0]&&map.getZoom()<=b[1])||BANDS[3];
      draft={id:marker?.id||'',name:marker?.name||'',color:marker?.color||'#9cbdc7',minZoom:band[0],maxZoom:band[1],rects:marker?.rects.map(r=>r.slice())||[],revision:data.revision};
      if(marker){const b=bounds(marker.rects);map.fitBounds([gameLatLng(b[0],b[1]),gameLatLng(b[2],b[3])],{padding:[50,100],maxZoom:15,animate:false});}
      savedHandlers=Object.fromEntries(['dragging','doubleClickZoom','boxZoom','touchZoom'].map(k=>[k,!!map[k]?.enabled()]));map.doubleClickZoom.disable();map.boxZoom.disable();map.touchZoom?.disable();setTool('select');render();
    }
    function finishDialog(){
      if(!draft?.rects.length){tell('empty');return;}const form=el('form',{className:'modal'}),name=el('input',{id:'areaMarkerName',value:draft.name,maxLength:80,required:true}),color=el('input',{id:'areaMarkerColor',type:'color',value:draft.color}),error=el('div',{className:'error'}),actions=el('div',{className:'modal-actions'}),save=button('save',()=>{});save.type='submit';
      form.append(el('h2',{textContent:t(draft.id?'edit':'add')}));for(const [key,input] of [['name',name],['color',color]])form.append(el('label',{textContent:t(key),htmlFor:input.id}),input);actions.append(button('cancel',()=>modal.hidden=true),save);form.append(error,actions);modal.replaceChildren(form);modal.hidden=false;name.focus();
      form.onsubmit=async event=>{event.preventDefault();if(busy||!getAuth().admin||!draft)return;busy=true;save.disabled=true;const captured=epoch;try{await request({...draft,name:name.value.trim(),color:color.value});if(captured!==epoch)return;cancel();toggle.checked=true;layerVisibility.set('area-markers',true);await refresh();tell('saved');}catch(e){if(captured===epoch)error.textContent=t(e.message);}finally{busy=false;save.disabled=false;}};
    }
    async function removeMarker(marker){
      context.classList.remove('show');if(!marker||!getAuth().admin||busy||!confirm(t('confirm')))return;busy=true;const captured=epoch;
      try{await request({id:marker.id,revision:data.revision},'DELETE');if(captured!==epoch)return;modal.hidden=true;map.closePopup();await refresh();}catch(e){tell(e.message);}finally{busy=false;}
    }
    function manageDialog(){
      if(!getAuth().admin)return;const form=el('div',{className:'modal'});form.append(el('h2',{textContent:t('manage')}));
      for(const m of data.markers){const row=el('div',{className:'area-manager-row'});row.append(el('span',{textContent:`${m.name} · ${m.minZoom}–${m.maxZoom}`}),button('edit',()=>start(m)),button('remove',()=>removeMarker(m)));form.append(row);}form.append(button('add',()=>start()),button('cancel',()=>modal.hidden=true));modal.replaceChildren(form);modal.hidden=false;
    }
    function lang(){label.textContent=t('layer');for(const [key,b] of [['add',add],['edit',edit],['remove',remove],['manage',manage]])b.textContent=t(key);add.hidden=manage.hidden=!getAuth().admin;edit.hidden=remove.hidden=true;renderToolbar();render();}
    function authChanged(){epoch++;controller?.abort();loaded=false;data={revision:0,markers:[]};cancel();lang();void refresh();}
    // Pointer capture allows an erase/selection to finish outside the map. Other map
    // tools never receive these drawing gestures; pan mode restores normal navigation.
    const mapEl=map.getContainer();
    function stopEvent(e){e.preventDefault();e.stopImmediatePropagation();}
    function point(e){return gamePoint(map.mouseEventToLatLng(e));}
    function rect(a,b){const x1=Math.floor(Math.min(a.x,b.x)),z1=Math.floor(Math.min(a.z,b.z));return [x1,z1,Math.max(x1+1,Math.ceil(Math.max(a.x,b.x))),Math.max(z1+1,Math.ceil(Math.max(a.z,b.z)))];}
    mapEl.addEventListener('pointerdown',e=>{if(!draft||tool==='pan'||e.button!==0||e.target.closest('.leaflet-control,.leaflet-popup')||!modal.hidden)return;stopEvent(e);if(pointer)return;pointer={id:e.pointerId,start:point(e)};mapEl.setPointerCapture(e.pointerId);},true);
    mapEl.addEventListener('pointermove',e=>{if(!pointer||e.pointerId!==pointer.id)return;stopEvent(e);const r=rect(pointer.start,point(e)),latLngs=[gameLatLng(r[0],r[1]),gameLatLng(r[2],r[3])];if(!rubber)rubber=L.rectangle(latLngs,{pane:'areaMarkerDraft',renderer:L.svg({pane:'areaMarkerDraft'}),color:tool==='erase'?'#ef8989':'#fff',weight:1,fillOpacity:.14,interactive:false}).addTo(draftGroup);else rubber.setBounds(latLngs);},true);
    mapEl.addEventListener('pointerup',e=>{if(!pointer||e.pointerId!==pointer.id)return;stopEvent(e);const r=rect(pointer.start,point(e));pointer=null;try{mapEl.releasePointerCapture(e.pointerId);}catch{}try{if(r.some(v=>Math.abs(v)>32000000))throw Error('complex');const occupied=data.markers.filter(m=>m.id!==draft.id&&m.minZoom===draft.minZoom).flatMap(m=>m.rects),next=paint(draft.rects,r,tool==='erase',occupied);if(JSON.stringify(next)!==JSON.stringify(draft.rects)){undo.push({minZoom:draft.minZoom,rects:draft.rects});if(undo.length>32)undo.shift();redo=[];draft.rects=next;}}catch(error){tell(error.message);}renderDraft();renderToolbar();},true);
    mapEl.addEventListener('pointercancel',()=>{pointer=null;renderDraft();});
    for(const event of ['click','dblclick','contextmenu'])mapEl.addEventListener(event,e=>{if(draft&&tool!=='pan'&&!e.target.closest('.leaflet-control,.leaflet-popup'))stopEvent(e);},true);
    map.on('moveend zoomend resize',schedule);
    map.on('click',e=>{if(draft||!getAuth().admin||!toggle.checked)return;const p=gamePoint(e.latlng),marker=data.markers.find(m=>visible(m)&&m.rects.some(r=>contains(r,p)));if(!marker)return;const popup=el('div',{className:'notebook-popup'});popup.append(el('b',{textContent:marker.name}),button('edit',()=>start(marker)),button('remove',()=>removeMarker(marker)));L.popup().setLatLng(e.latlng).setContent(popup).openOn(map);});
    map.on('contextmenu',e=>{const p=gamePoint(e.latlng);contextMarker=toggle.checked?data.markers.find(m=>visible(m)&&m.rects.some(r=>contains(r,p))):null;add.hidden=!getAuth().admin;edit.hidden=remove.hidden=!getAuth().admin||!contextMarker;context.style.top=`${Math.max(5,Math.min(parseFloat(context.style.top)||5,innerHeight-context.offsetHeight-5))}px`;});
    toggle.onchange=()=>{layerVisibility.set('area-markers',toggle.checked);render();};
    document.addEventListener('keydown',e=>{if(e.key==='Escape'&&(!modal.hidden||draft)){e.preventDefault();e.stopImmediatePropagation();if(!modal.hidden){modal.hidden=true;return;}cancel();}},true);
    document.addEventListener('servermap-notebook-cancel',()=>{if(draft)cancel();});
    document.querySelector('#measure').addEventListener('click',()=>{if(draft)cancel();});
    context.addEventListener('click',e=>{if(draft&&e.target.closest('button')&&!e.target.closest('[data-area-action]'))cancel();},true);
    document.addEventListener('visibilitychange',()=>{if(!document.hidden)void refresh();});setInterval(refresh,10000);lang();
    return {ready:async()=>{ready=true;await refresh();},authChanged,languageChanged:lang,refresh,privacyChanged:()=>{epoch++;controller?.abort();loaded=false;data={revision:0,markers:[]};cancel();void refresh();},cancel};
  }
  const exports={create,geometry:{subtract,paint,cut,compact,boundary,overlaps},bands:BANDS};
  if(typeof window!=='undefined')window.ServerMapAreas=exports;
  if(typeof module!=='undefined')module.exports=exports;
})();
