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
  const validRange=(min,max)=>Number.isInteger(min)&&Number.isInteger(max)&&min>=4&&max<=15&&max>=min;
  const rangeOverlaps=(a,b)=>a.minZoom<=b.maxZoom&&a.maxZoom>=b.minZoom;
  const higherLimit=markers=>markers.length?Math.min(...markers.map(m=>m.minZoom))-1:3;
  const appearance=m=>({borderOpacity:m?.borderOpacity??.25,fillOpacity:m?.fillOpacity??.08,textOpacity:m?.textOpacity??.58});
  const pixelRect=(a,b)=>{const x1=Math.floor(Math.min(a.x,b.x)),z1=Math.floor(Math.min(a.z,b.z));return [x1,z1,Math.max(x1+1,Math.ceil(Math.max(a.x,b.x))),Math.max(z1+1,Math.ceil(Math.max(a.z,b.z)))];};
  const contains=(r,p)=>p.x>=r[0]&&p.x<r[2]&&p.z>=r[1]&&p.z<r[3];
  const bounds=rects=>[Math.min(...rects.map(r=>r[0])),Math.min(...rects.map(r=>r[1])),Math.max(...rects.map(r=>r[2])),Math.max(...rects.map(r=>r[3]))];
  // Exact maximum-area axis-aligned rectangle in the union, including across
  // selection fragments. Coordinate compression costs O(n²), not world area.
  function largestRectangle(rects){
    if(!rects.length)return null;
    const xs=[...new Set(rects.flatMap(r=>[r[0],r[2]]))].sort((a,b)=>a-b),zs=[...new Set(rects.flatMap(r=>[r[1],r[3]]))].sort((a,b)=>a-b);
    const xi=new Map(xs.map((v,i)=>[v,i])),zi=new Map(zs.map((v,i)=>[v,i])),events=zs.map(()=>[]),diff=new Int32Array(xs.length+1),heights=new Float64Array(xs.length);
    for(const [x1,z1,x2,z2] of rects){events[zi.get(z1)].push([xi.get(x1),xi.get(x2),1]);events[zi.get(z2)].push([xi.get(x1),xi.get(x2),-1]);}
    let best=null,bestArea=0;
    for(let row=0;row<zs.length-1;row++){
      for(const [a,b,delta] of events[row]){diff[a]+=delta;diff[b]-=delta;}
      let coverage=0;for(let x=0;x<xs.length-1;x++){coverage+=diff[x];heights[x]=coverage>0?heights[x]+zs[row+1]-zs[row]:0;}
      const stack=[];
      for(let x=0;x<xs.length;x++){
        const h=heights[x];let start=x;
        while(stack.length&&stack.at(-1).height>h){const bar=stack.pop(),r=[xs[bar.start],zs[row+1]-bar.height,xs[x],zs[row+1]],area=(r[2]-r[0])*bar.height;start=bar.start;
          if(area>bestArea||(area===bestArea&&(!best||r[1]<best[1]||r[1]===best[1]&&(r[0]<best[0]||r[0]===best[0]&&r[2]>best[2])))){best=r;bestArea=area;}}
        if(h>0&&(!stack.length||stack.at(-1).height<h))stack.push({start,height:h});
      }
    }
    return best;
  }
  // Cancel shared edges, including partially shared ones: no internal rectangle seams.
  function boundary(rects){
    const lines=new Map();
    function edge(axis,line,start,end,sign){const key=axis+':'+line;let item=lines.get(key);if(!item)lines.set(key,item={axis,line,events:new Map()});item.events.set(start,(item.events.get(start)||0)+sign);item.events.set(end,(item.events.get(end)||0)-sign);}
    for(const [x1,z1,x2,z2] of rects){edge('h',z1,x1,x2,1);edge('h',z2,x1,x2,-1);edge('v',x1,z1,z2,1);edge('v',x2,z1,z2,-1);}
    const out=[];for(const {axis,line,events} of lines.values()){let sum=0,prev;for(const [p,delta] of [...events].sort((a,b)=>a[0]-b[0])){if(sum&&prev!==undefined&&p>prev)out.push(axis==='h'?[prev,line,p,line]:[line,prev,line,p]);sum+=delta;prev=p;}}return out;
  }
  const words={zh:{layer:'区域标记',add:'添加区域标记',edit:'编辑区域标记',remove:'删除区域标记',manage:'管理区域标记',type:'显示级别',select:'框选',erase:'擦除',pan:'移动地图',undo:'撤销',redo:'恢复',finish:'完成',cancel:'取消',save:'保存',name:'区域名称',color:'颜色',help:'拖拽框选或擦除，边缘对齐底图像素（1 方块）。只修改当前草稿；同类型已有区域自动避让。可放大后精细编辑。',empty:'请先选择未被同类型区域占用的范围。',complex:'选区过于复杂，请减少片段（最多 1024 个）。',error:'操作失败，请重试。',conflict:'区域已被其他管理员更新，请取消后重新打开，避免覆盖新数据。',confirm:'删除这个区域标记？不会改变领地或隐藏区域。',saved:'区域标记已保存',unavailable:'区域标记暂不可用，请确认服务端模组已更新。',changedType:'切换类型会避让该类型的已有区域。',count:'片段',loading:'正在加载…'},en:{layer:'Area markers',add:'Add area marker',edit:'Edit area marker',remove:'Delete area marker',manage:'Manage area markers',type:'Zoom band',select:'Select',erase:'Erase',pan:'Pan map',undo:'Undo',redo:'Redo',finish:'Finish',cancel:'Cancel',save:'Save',name:'Area name',color:'Color',help:'Drag to select or erase, aligned to base-map pixels (1 block). Only the draft changes; existing same-band areas are excluded. Zoom in for precise editing.',empty:'Select an area not occupied in this zoom band.',complex:'Selection is too complex (maximum 1024 rectangles).',error:'Operation failed. Please retry.',conflict:'Another admin updated areas. Cancel and reopen to avoid overwriting new data.',confirm:'Delete this area marker? Claims and hidden regions are unaffected.',saved:'Area marker saved',unavailable:'Area markers unavailable. Check the server mod version.',changedType:'Changing bands excludes areas already occupied in that band.',count:'pieces',loading:'Loading…'}};
  function create({map,api,gameLatLng,gamePoint,getAuth,getLanguage,getMetadata,layerVisibility,cancelOtherTools}){
    Object.assign(words.zh,{merge:'合并添加新区域',clearSelection:'清除多选',selected:'已选区域',pick:'选择区域',unpick:'取消选择',multiHelp:'Ctrl＋左键切换多选；已选区域跨级别保持显示。只能合并为比所有来源更高的类别，原区域保留。',noHigher:'已选区域没有可用的更高类别。',borderTransparency:'边缘透明度',fillTransparency:'区域背景透明度',textTransparency:'文字透明度',transparencyHelp:'0% 不透明，100% 全透明。',search:'搜索区域名称或级别',noResults:'没有匹配的区域'});
    Object.assign(words.en,{merge:'Merge into new area',clearSelection:'Clear selection',selected:'Selected areas',pick:'Select area',unpick:'Deselect',multiHelp:'Ctrl-click to toggle selection. Selected areas stay visible across zooms. Merge only into a higher level than every source; originals are retained.',noHigher:'No higher band is available for this selection.',borderTransparency:'Border transparency',fillTransparency:'Fill transparency',textTransparency:'Text transparency',transparencyHelp:'0% opaque, 100% transparent.',search:'Search area name or zoom band',noResults:'No matching areas'});
    Object.assign(words.zh,{empty:'请先选择未被相交显示区间的已有区域占用的范围。',type:'显示级别区间',applyRange:'应用区间',minLevel:'最低显示级别',maxLevel:'最高显示级别',invalidRange:'请输入 4～15 的整数区间，最高级别不得小于最低级别；合并区间须低于所有来源区间。',help:'按 1 方块精度框选或擦除。任何显示级别有交集的已有区域都会避让，擦除只修改当前草稿。',changedType:'修改区间后，点击“应用区间”预览避让结果。',multiHelp:'Ctrl＋左键跨区间多选；已选区域持续显示。合并新区间的最高级别须小于所有来源的最低级别，原区域保留。'});
    Object.assign(words.en,{empty:'Select pixels not occupied by areas sharing a display level.',type:'Display level range',applyRange:'Apply range',minLevel:'Minimum display level',maxLevel:'Maximum display level',invalidRange:'Use integers 4–15 with maximum at least minimum; merged ranges must be below every source range.',help:'Select or erase at one-block precision. Existing areas sharing any display level are excluded. Erasing only changes the draft.',changedType:'After changing levels, apply the range to preview excluded overlaps.',multiHelp:'Ctrl-click across ranges; selections remain visible. The merged maximum must be below every source minimum. Originals are preserved.'});
    const t=key=>words[getLanguage()==='zh'?'zh':'en'][key]||key,el=(tag,props={})=>Object.assign(document.createElement(tag),props);
    const button=(key,action)=>{const b=el('button',{type:'button',className:'notebook-button',textContent:t(key)});b.dataset.areaAction=key;b.onclick=action;return b;};
    const svgEl=(name,attrs={})=>{const node=document.createElementNS('http://www.w3.org/2000/svg',name);for(const [key,value] of Object.entries(attrs))node.setAttribute(key,String(value));return node;};
    const section=el('section',{className:'notebook-section',id:'areaMarkerSection'}),heading=el('div',{className:'notebook-heading layer-row'}),toggle=el('input',{type:'checkbox',id:'areaMarkerToggle',checked:layerVisibility.get('area-markers',false)}),label=el('label',{htmlFor:toggle.id});
    heading.append(toggle,label);section.append(heading);document.querySelector('#sidebar [data-i18n="onlinePlayers"]').before(section);
    const manage=button('manage',()=>manageDialog());manage.id='areaManageButton';manage.classList.add('area-manage','notebook-icon-button');manage.textContent='';
    const manageIcon=svgEl('svg',{class:'icon icon-tabler icon-tabler-chart-area-line',viewBox:'0 0 24 24',fill:'none',stroke:'currentColor','stroke-width':2,'stroke-linecap':'round','stroke-linejoin':'round','aria-hidden':true,focusable:false});
    for(const d of ['M4 19l4 -6l4 2l4 -5l4 4l0 5l-16 0','M4 12l3 -4l4 2l5 -6l4 4'])manageIcon.append(svgEl('path',{d}));
    manage.append(manageIcon);manage.setAttribute('aria-haspopup','dialog');manage.setAttribute('aria-controls','areaMarkerModal');document.querySelector('#mapActions').insertBefore(manage,document.getElementById('languageButton')||document.getElementById('language'));
    const context=document.querySelector('#contextMenu'),add=button('add',()=>start()),edit=button('edit',()=>start(contextMarker)),remove=button('remove',()=>removeMarker(contextMarker));context.append(add,edit,remove);
    const toolbar=el('div',{id:'areaMarkerToolbar',hidden:true}),modal=el('div',{className:'modal-backdrop',id:'areaMarkerModal',hidden:true}),notice=el('div',{id:'areaMarkerNotice',hidden:true});notice.setAttribute('role','status');document.body.append(toolbar,modal,notice);
    map.createPane('areaMarkers');map.getPane('areaMarkers').style.zIndex='410';map.getPane('areaMarkers').style.pointerEvents='none';
    map.createPane('areaMarkerEdges');map.getPane('areaMarkerEdges').style.zIndex='415';map.getPane('areaMarkerEdges').style.pointerEvents='none';
    map.createPane('areaMarkerDraft');map.getPane('areaMarkerDraft').style.zIndex='640';map.getPane('areaMarkerDraft').style.pointerEvents='none';
    const group=L.layerGroup().addTo(map),draftGroup=L.layerGroup().addTo(map);
    const edgeRenderer=L.svg({pane:'areaMarkerEdges'}),draftEdgeRenderer=L.svg({pane:'areaMarkerDraft'}),geometryCache=new WeakMap();
    function geometry(rects){let cached=geometryCache.get(rects);if(!cached){cached={edges:boundary(rects),labelRect:largestRectangle(rects)};geometryCache.set(rects,cached);}return cached;}
    let data={revision:0,markers:[]},loaded=false,ready=false,epoch=0,pending=false,controller,draft=null,contextMarker,tool='select',undo=[],redo=[],pointer=null,rubber,noticeTimer,busy=false,frame=0,savedHandlers;
    const selected=new Set();let mergeMin=4,mergeMax=6;
    const selection=()=>data.markers.filter(m=>selected.has(m.id));
    function tell(key){notice.textContent=t(key);notice.hidden=false;clearTimeout(noticeTimer);noticeTimer=setTimeout(()=>notice.hidden=true,6500);}
    async function request(body,method=body?'POST':'GET'){
      const abort=new AbortController(),timer=setTimeout(()=>abort.abort(),15000);if(method==='GET')controller=abort;
      try{const response=await fetch(api+'/area-markers',{method,cache:'no-store',signal:abort.signal,headers:body?{'Content-Type':'application/json','X-ServerMap-Request':'1'}:{},body:body?JSON.stringify(body):undefined});if(!response.ok){const e=Error(response.status===409?'conflict':method==='GET'?'unavailable':'error');e.status=response.status;throw e;}return await response.json();}finally{clearTimeout(timer);if(controller===abort)controller=null;}
    }
    async function refresh(){
      if(!ready||document.hidden||pending)return;pending=true;const captured=epoch;
      try{const next=await request();if(captured!==epoch)return;if(!Array.isArray(next.markers)||!Number.isSafeInteger(next.revision))throw Error('unavailable');loaded=true;if(JSON.stringify(next)!==JSON.stringify(data)){data=next;for(const id of selected)if(!data.markers.some(m=>m.id===id))selected.delete(id);render();renderToolbar();}}
      catch{if(captured===epoch&&!loaded){data={revision:0,markers:[]};render();}}finally{pending=false;}
    }
    function rangeInputs(prefix,min,max,limit=15){
      const wrapper=el('div',{className:'zoom-range'}),low=el('input',{id:prefix+'Min',type:'number',min:'4',max:String(limit),step:'1',required:true,value:String(min)}),high=el('input',{id:prefix+'Max',type:'number',min:'4',max:String(limit),step:'1',required:true,value:String(max)});
      low.setAttribute('aria-label',t('minLevel'));high.setAttribute('aria-label',t('maxLevel'));wrapper.append(low,el('span',{textContent:'～'}),high);
      const read=()=>({minZoom:Number(low.value),maxZoom:Number(high.value)}),valid=()=>{const r=read();return low.value!==''&&high.value!==''&&validRange(r.minZoom,r.maxZoom)&&r.maxZoom<=limit;};
      const validate=()=>high.setCustomValidity(valid()?'':t('invalidRange'));low.oninput=high.oninput=validate;validate();
      return {wrapper,read,valid};
    }
    function applyRange(range){
      if(!validRange(range.minZoom,range.maxZoom)||draft.sourceIds&&range.maxZoom>higherLimit(selection()))throw Error('invalidRange');
      if(range.minZoom===draft.minZoom&&range.maxZoom===draft.maxZoom)return;
      let source=draft.rects;if(draft.sourceIds){source=[];for(const r of selection().flatMap(m=>m.rects))source=paint(source,r,false);}
      const rects=cut(source,data.markers.filter(m=>m.id!==draft.id&&rangeOverlaps(m,range)).flatMap(m=>m.rects));
      undo.push({minZoom:draft.minZoom,maxZoom:draft.maxZoom,rects:draft.rects});if(undo.length>32)undo.shift();redo=[];draft={...draft,...range,rects};render();
    }
    function visible(marker){return map.getZoom()>=marker.minZoom&&map.getZoom()<=marker.maxZoom;}
    function overlay(marker,target,isDraft=false){
      if(!marker.rects.length)return;
      const b=bounds(marker.rects),view=map.getBounds();if(!view.intersects(L.latLngBounds(gameLatLng(b[0],b[1]),gameLatLng(b[2],b[3]))))return;
      const style=appearance(marker),chosen=selected.has(marker.id),svg=svgEl('svg',{viewBox:`${b[0]} ${b[1]} ${b[2]-b[0]} ${b[3]-b[1]}`,preserveAspectRatio:'none','aria-label':marker.name||t('add'),role:'img'});svg.dataset.areaId=marker.id||'draft';svg.dataset.selected=String(chosen);
      const shape=marker.rects.map(([x1,z1,x2,z2])=>`M${x1} ${z1}H${x2}V${z2}H${x1}Z`).join('');
      svg.append(svgEl('path',{d:shape,fill:marker.color,'fill-opacity':isDraft?Math.max(.12,style.fillOpacity):style.fillOpacity,stroke:'none'}));
      const geo=geometry(marker.rects),segments=geo.edges.map(([x1,z1,x2,z2])=>[gameLatLng(x1,z1),gameLatLng(x2,z2)]),edgeOptions={pane:isDraft?'areaMarkerDraft':'areaMarkerEdges',renderer:isDraft?draftEdgeRenderer:edgeRenderer,weight:2,lineCap:'round',lineJoin:'round',smoothFactor:0,interactive:false,bubblingMouseEvents:false,areaMarkerId:marker.id||'draft'};
      // Native Leaflet paths use screen-space projection/strokes like claim areas.
      // They are not resized or half-clipped at the world-sized SVG's outer edge.
      L.polyline(segments,{...edgeOptions,className:'area-marker-boundary',color:marker.color,opacity:isDraft?.85:style.borderOpacity}).addTo(target);
      if(chosen)L.polyline(segments,{...edgeOptions,className:'area-marker-selection',color:'#ffe092',opacity:.95,dashArray:'5 3'}).addTo(target);
      // Fit the full diagonal of the largest rectangle in the union, not the
      // largest stored fragment. No fixed screen-size or angle limit.
      if(marker.name&&!isDraft){
        const r=geo.labelRect,w=r[2]-r[0],h=r[3]-r[1],angle=Math.atan2(h,w);svg.dataset.labelRect=r.join(',');
        const defs=svgEl('defs'),clip=svgEl('clipPath',{id:'area-clip-'+marker.id});clip.append(svgEl('path',{d:shape}));defs.append(clip);svg.append(defs);
        const container=svgEl('g',{'clip-path':`url(#area-clip-${marker.id})`}),g=svgEl('g',{transform:`translate(${(r[0]+r[2])/2} ${(r[1]+r[3])/2}) rotate(${-angle*180/Math.PI})`}),text=svgEl('text',{'font-size':100,'text-anchor':'middle',opacity:style.textOpacity,fill:marker.color,stroke:'#20252b','stroke-width':1.5,'paint-order':'stroke fill'});text.textContent=marker.name;g.append(text);container.append(g);svg.append(container);
        L.svgOverlay(svg,[gameLatLng(b[0],b[1]),gameLatLng(b[2],b[3])],{pane:'areaMarkers',className:'area-marker-overlay',interactive:false}).addTo(target);
        const box=text.getBBox(),scale=.82*Math.min(w/(Math.max(1,box.width)*Math.cos(angle)+Math.max(1,box.height)*Math.sin(angle)),h/(Math.max(1,box.width)*Math.sin(angle)+Math.max(1,box.height)*Math.cos(angle)));
        text.setAttribute('transform',`scale(${scale}) translate(${-box.x-box.width/2} ${-box.y-box.height/2})`);return;
      }
      L.svgOverlay(svg,[gameLatLng(b[0],b[1]),gameLatLng(b[2],b[3])],{pane:isDraft?'areaMarkerDraft':'areaMarkers',className:'area-marker-overlay',interactive:false}).addTo(target);
    }
    function render(){
      group.clearLayers();for(const m of data.markers.slice().sort((a,b)=>Number(selected.has(a.id))-Number(selected.has(b.id))))if(m.id!==draft?.id&&(selected.has(m.id)||((toggle.checked||draft)&&(draft?rangeOverlaps(m,draft):visible(m)))))overlay(m,group);
      renderDraft();
    }
    function renderDraft(){draftGroup.clearLayers();rubber=null;if(draft)overlay(draft,draftGroup,true);}
    function schedule(){if(!frame)frame=requestAnimationFrame(()=>{frame=0;render();});}
    function toggleSelected(marker){
      if(!getAuth().admin||draft)return;
      if(selected.has(marker.id))selected.delete(marker.id);else selected.add(marker.id);
      map.closePopup();render();renderToolbar();
    }
    function pickAt(p){
      if(!getAuth().admin||draft)return;
      const candidates=data.markers.filter(m=>(selected.has(m.id)||(toggle.checked&&visible(m)))&&m.rects.some(r=>contains(r,p)));
      if(candidates.length===1){toggleSelected(candidates[0]);return;}
      if(candidates.length>1){const popup=el('div',{className:'notebook-popup'});for(const m of candidates){const b=button(selected.has(m.id)?'unpick':'pick',()=>toggleSelected(m));b.textContent=`${selected.has(m.id)?'☑':'☐'} ${m.name} · ${m.minZoom}–${m.maxZoom}`;popup.append(b);}L.popup().setLatLng(gameLatLng(p.x,p.z)).setContent(popup).openOn(map);}
    }
    function toolbarHelp(text){
      const details=el('details',{className:'area-toolbar-help'}),summary=el('summary'),label=getLanguage()==='zh'?'操作说明':'Instructions';summary.title=label;summary.setAttribute('aria-label',label);
      summary.innerHTML='<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" aria-hidden="true"><circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7v.01"/></svg>';
      details.append(summary,el('div',{className:'area-help',textContent:text}));return details;
    }
    function renderSelectionToolbar(){
      toolbar.replaceChildren();if(!selected.size)return;
      const chosen=selection(),limit=higherLimit(chosen),actions=el('div',{className:'notebook-toolbar-actions'});
      mergeMax=Math.min(Math.max(4,mergeMax),Math.max(4,limit));mergeMin=Math.min(mergeMin,mergeMax);
      const range=rangeInputs('areaMerge',mergeMin,mergeMax,Math.max(4,limit)),merge=button('merge',()=>{if(!range.valid()){tell('invalidRange');return;}const r=range.read();mergeMin=r.minZoom;mergeMax=r.maxZoom;void startMerge();});merge.disabled=chosen.length<2||limit<4;
      const rangeLabel=el('label',{textContent:t('type')});rangeLabel.append(range.wrapper);
      actions.append(el('span',{className:'area-selection-count',textContent:`${t('selected')} (${chosen.length})`}),rangeLabel,merge,button('clearSelection',()=>cancel()),toolbarHelp(t(limit>=4?'multiHelp':'noHigher')));toolbar.append(actions);
      const list=el('div',{className:'area-selected-list'});for(const m of chosen){const b=button('unpick',()=>toggleSelected(m));b.textContent=`${m.name} · ${m.minZoom}–${m.maxZoom} ×`;b.title=b.textContent;b.setAttribute('aria-label',`${t('unpick')}: ${m.name} · ${m.minZoom}–${m.maxZoom}`);list.append(b);}toolbar.append(list);
    }
    async function startMerge(){
      if(!getAuth().admin||busy||draft)return;await refresh();
      if(!data.zoomRanges){tell('unavailable');return;}
      const sources=selection(),range={minZoom:mergeMin,maxZoom:mergeMax};if(sources.length<2||!validRange(mergeMin,mergeMax)||mergeMax>higherLimit(sources)){tell('noHigher');return;}
      try{
        let rects=[];for(const r of sources.flatMap(m=>m.rects))rects=paint(rects,r,false);
        rects=cut(rects,data.markers.filter(m=>rangeOverlaps(m,range)).flatMap(m=>m.rects));if(!rects.length){tell('empty');return;}
        const ids=sources.map(m=>m.id);cancelOtherTools();cancel();for(const id of ids)selected.add(id);
        draft={...appearance(),id:'',name:'',color:'#9cbdc7',...range,sourceIds:ids,rects,revision:data.revision};
        savedHandlers=Object.fromEntries(['dragging','doubleClickZoom','boxZoom','touchZoom'].map(k=>[k,!!map[k]?.enabled()]));map.doubleClickZoom.disable();setTool('pan');render();finishDialog();
      }catch(e){tell(e.message);}
    }
    function setTool(value){tool=value;if(draft){map.dragging[value==='pan'?'enable':'disable']();map.getContainer().classList.toggle('area-marker-drawing',value!=='pan');}renderToolbar();}
    function renderToolbar(){
      toolbar.hidden=!draft&&!selected.size;if(!draft){renderSelectionToolbar();return;}toolbar.replaceChildren();const actions=el('div',{className:'notebook-toolbar-actions'}),typeLabel=el('label',{textContent:t('type')}),range=rangeInputs('areaMarker',draft.minZoom,draft.maxZoom,draft.sourceIds?higherLimit(selection()):15);
      typeLabel.append(range.wrapper);actions.append(typeLabel,button('applyRange',()=>{if(!range.valid()){tell('invalidRange');return;}try{applyRange(range.read());renderToolbar();}catch(e){tell(e.message);}}));
      for(const key of (draft.sourceIds?['pan']:['select','erase','pan'])){const b=button(key,()=>setTool(key));b.setAttribute('aria-pressed',String(tool===key));actions.append(b);}
      for(const [key,source,target] of [['undo',undo,redo],['redo',redo,undo]]){const b=button(key,()=>{const previous=source.pop();if(!previous)return;target.push({minZoom:draft.minZoom,maxZoom:draft.maxZoom,rects:draft.rects});draft={...draft,...previous};render();renderToolbar();});b.disabled=!source.length||!!draft.sourceIds;actions.append(b);}
      const finish=button('finish',()=>{if(!range.valid()){tell('invalidRange');return;}try{applyRange(range.read());finishDialog();}catch(e){tell(e.message);}});finish.disabled=!draft.rects.length;actions.append(finish,button('cancel',cancel),el('span',{className:'area-selection-count',textContent:`${draft.rects.length} ${t('count')}`}),toolbarHelp(`${t('help')} ${t('changedType')}`));toolbar.append(actions);
    }
    function cancel(clearSelection=true){
      if(clearSelection)selected.clear();
      if(pointer){try{map.getContainer().releasePointerCapture(pointer.id);}catch{}pointer=null;}
      if(savedHandlers){for(const [key,enabled] of Object.entries(savedHandlers))map[key]?.[enabled?'enable':'disable']();savedHandlers=null;}
      draft=null;undo=[];redo=[];toolbar.hidden=true;modal.hidden=true;map.getContainer().classList.remove('area-marker-drawing');render();
    }
    async function start(marker){
      context.classList.remove('show');if(!getAuth().admin||busy)return;await refresh();if(!loaded||!data.zoomRanges){tell('unavailable');return;}if(!getAuth().admin)return;
      cancelOtherTools();cancel();map.closePopup();marker=marker?data.markers.find(m=>m.id===marker.id):null;
      const band=marker?[marker.minZoom,marker.maxZoom]:BANDS.find(b=>map.getZoom()>=b[0]&&map.getZoom()<=b[1])||BANDS[3];
      draft={...appearance(marker),id:marker?.id||'',name:marker?.name||'',color:marker?.color||'#9cbdc7',minZoom:band[0],maxZoom:band[1],rects:marker?.rects.map(r=>r.slice())||[],revision:data.revision};
      if(marker){const b=bounds(marker.rects);map.fitBounds([gameLatLng(b[0],b[1]),gameLatLng(b[2],b[3])],{padding:[50,100],maxZoom:15,animate:false});}
      savedHandlers=Object.fromEntries(['dragging','doubleClickZoom','boxZoom','touchZoom'].map(k=>[k,!!map[k]?.enabled()]));map.doubleClickZoom.disable();map.boxZoom.disable();map.touchZoom?.disable();setTool('select');render();
    }
    function finishDialog(){
      if(!draft?.rects.length){tell('empty');return;}const form=el('form',{className:'modal'}),name=el('input',{id:'areaMarkerName',value:draft.name,maxLength:80,required:true}),color=el('input',{id:'areaMarkerColor',type:'color',value:draft.color}),error=el('div',{className:'error'}),actions=el('div',{className:'modal-actions'}),save=button('save',()=>{});save.type='submit';
      form.append(el('h2',{textContent:t(draft.id?'edit':'add')}));for(const [key,input] of [['name',name],['color',color]])form.append(el('label',{textContent:t(key),htmlFor:input.id}),input);actions.append(button('cancel',()=>modal.hidden=true),save);form.append(error,actions);modal.replaceChildren(form);modal.hidden=false;name.focus();
      for(const [key,word] of [['borderOpacity','borderTransparency'],['fillOpacity','fillTransparency'],['textOpacity','textTransparency']]){
        const wrapper=el('div',{className:'area-opacity-field'}),input=el('input',{id:`area-${key}`,type:'range',min:'0',max:'100',step:'1',value:String(Math.round((1-appearance(draft)[key])*100))}),output=el('output',{textContent:input.value+'%'}),label=el('label',{textContent:t(word),htmlFor:input.id});
        input.oninput=()=>{output.textContent=input.value+'%';draft[key]=Math.round(100-Number(input.value))/100;};wrapper.append(label,input,output);form.insertBefore(wrapper,error);
      }
      const range=rangeInputs('areaDialog',draft.minZoom,draft.maxZoom,draft.sourceIds?higherLimit(selection()):15);form.insertBefore(el('label',{textContent:t('type')}),error);form.insertBefore(range.wrapper,error);
      form.insertBefore(el('p',{className:'area-help',textContent:t('transparencyHelp')}),error);
      form.onsubmit=async event=>{event.preventDefault();if(busy||!getAuth().admin||!draft)return;busy=true;save.disabled=true;const captured=epoch;try{if(!range.valid())throw Error('invalidRange');applyRange(range.read());if(!draft.rects.length)throw Error('empty');await request({...draft,name:name.value.trim(),color:color.value});if(captured!==epoch)return;cancel();toggle.checked=true;layerVisibility.set('area-markers',true);await refresh();tell('saved');}catch(e){if(captured===epoch)error.textContent=t(e.message);}finally{busy=false;save.disabled=false;}};
    }
    async function removeMarker(marker){
      context.classList.remove('show');if(!marker||!getAuth().admin||busy||!confirm(t('confirm')))return;busy=true;const captured=epoch;
      try{await request({id:marker.id,revision:data.revision},'DELETE');if(captured!==epoch)return;modal.hidden=true;map.closePopup();await refresh();}catch(e){tell(e.message);}finally{busy=false;}
    }
    function manageDialog(){
      if(!getAuth().admin)return;const form=el('div',{className:'modal'}),search=el('input',{type:'search',id:'areaMarkerSearch',placeholder:t('search')}),list=el('div');search.setAttribute('aria-label',t('search'));form.append(el('h2',{textContent:t('manage')}),search,list);
      const show=()=>{list.replaceChildren();const query=search.value.trim().toLocaleLowerCase().replace(/[–—]/g,'-'),matches=data.markers.filter(m=>`${m.name} ${m.minZoom}-${m.maxZoom}`.toLocaleLowerCase().replace(/[–—]/g,'-').includes(query));for(const m of matches){const row=el('div',{className:'area-manager-row'}),pick=button(selected.has(m.id)?'unpick':'pick',()=>{toggleSelected(m);show();});pick.disabled=!!draft;row.append(el('span',{textContent:`${m.name} · ${m.minZoom}–${m.maxZoom}`}),pick,button('edit',()=>start(m)),button('remove',()=>removeMarker(m)));list.append(row);}if(!matches.length)list.append(el('p',{textContent:t('noResults')}));};
      search.oninput=show;show();const actions=el('div',{className:'modal-actions area-manager-actions'});actions.append(button('add',()=>start()),button('cancel',()=>modal.hidden=true));form.append(actions);modal.replaceChildren(form);modal.hidden=false;search.focus();
    }
    function lang(){label.textContent=t('layer');for(const [key,b] of [['add',add],['edit',edit],['remove',remove]])b.textContent=t(key);manage.title=t('manage');manage.setAttribute('aria-label',t('manage'));add.hidden=manage.hidden=!getAuth().admin;edit.hidden=remove.hidden=true;renderToolbar();render();}
    function authChanged(){epoch++;controller?.abort();loaded=false;data={revision:0,markers:[]};cancel();lang();void refresh();}
    // Pointer capture allows an erase/selection to finish outside the map. Other map
    // tools never receive these drawing gestures; pan mode restores normal navigation.
    const mapEl=map.getContainer();
    mapEl.addEventListener('click',e=>{if(e.ctrlKey&&e.button===0&&getAuth().admin&&!draft&&modal.hidden&&!e.target.closest('.leaflet-control,.leaflet-popup')){stopEvent(e);pickAt(point(e));}},true);
    function stopEvent(e){e.preventDefault();e.stopImmediatePropagation();}
    function point(e){return gamePoint(map.mouseEventToLatLng(e));}
    const rect=pixelRect;
    mapEl.addEventListener('pointerdown',e=>{if(!draft||tool==='pan'||e.button!==0||e.target.closest('.leaflet-control,.leaflet-popup')||!modal.hidden)return;stopEvent(e);if(pointer)return;pointer={id:e.pointerId,start:point(e)};mapEl.setPointerCapture(e.pointerId);},true);
    mapEl.addEventListener('pointermove',e=>{if(!pointer||e.pointerId!==pointer.id)return;stopEvent(e);const r=rect(pointer.start,point(e)),latLngs=[gameLatLng(r[0],r[1]),gameLatLng(r[2],r[3])];if(!rubber)rubber=L.rectangle(latLngs,{pane:'areaMarkerDraft',renderer:L.svg({pane:'areaMarkerDraft'}),color:tool==='erase'?'#ef8989':'#fff',weight:1,fillOpacity:.14,interactive:false}).addTo(draftGroup);else rubber.setBounds(latLngs);},true);
    mapEl.addEventListener('pointerup',e=>{if(!pointer||e.pointerId!==pointer.id)return;stopEvent(e);const r=rect(pointer.start,point(e));pointer=null;try{mapEl.releasePointerCapture(e.pointerId);}catch{}try{if(r.some(v=>Math.abs(v)>32000000))throw Error('complex');const occupied=data.markers.filter(m=>m.id!==draft.id&&rangeOverlaps(m,draft)).flatMap(m=>m.rects),next=paint(draft.rects,r,tool==='erase',occupied);if(JSON.stringify(next)!==JSON.stringify(draft.rects)){undo.push({minZoom:draft.minZoom,maxZoom:draft.maxZoom,rects:draft.rects});if(undo.length>32)undo.shift();redo=[];draft.rects=next;}}catch(error){tell(error.message);}renderDraft();renderToolbar();},true);
    mapEl.addEventListener('pointercancel',()=>{pointer=null;renderDraft();});
    for(const event of ['click','dblclick','contextmenu'])mapEl.addEventListener(event,e=>{if(draft&&tool!=='pan'&&!e.target.closest('.leaflet-control,.leaflet-popup'))stopEvent(e);},true);
    map.on('moveend zoomend resize',schedule);
    map.on('click',e=>{if(!draft&&getAuth().admin&&modal.hidden&&e.originalEvent?.ctrlKey)pickAt(gamePoint(e.latlng));});
    map.on('contextmenu',e=>{const p=gamePoint(e.latlng);contextMarker=toggle.checked?data.markers.find(m=>visible(m)&&m.rects.some(r=>contains(r,p))):null;add.hidden=!getAuth().admin;edit.hidden=remove.hidden=!getAuth().admin||!contextMarker;context.style.top=`${Math.max(5,Math.min(parseFloat(context.style.top)||5,innerHeight-context.offsetHeight-5))}px`;});
    toggle.onchange=()=>{layerVisibility.set('area-markers',toggle.checked);render();};
    document.addEventListener('keydown',e=>{if(e.key==='Escape'&&(!modal.hidden||draft||selected.size)){e.preventDefault();e.stopImmediatePropagation();if(!modal.hidden){modal.hidden=true;return;}cancel();}},true);
    document.addEventListener('servermap-notebook-cancel',()=>{if(draft||selected.size)cancel();});
    document.querySelector('#measure').addEventListener('click',()=>{if(draft)cancel();});
    context.addEventListener('click',e=>{if(draft&&e.target.closest('button')&&!e.target.closest('[data-area-action]'))cancel();},true);
    document.addEventListener('visibilitychange',()=>{if(!document.hidden)void refresh();});setInterval(refresh,10000);lang();
    return {ready:async()=>{ready=true;await refresh();},authChanged,languageChanged:lang,refresh,privacyChanged:()=>{epoch++;controller?.abort();loaded=false;data={revision:0,markers:[]};cancel();void refresh();},cancel};
  }
  const exports={create,geometry:{subtract,paint,cut,compact,boundary,overlaps,pixelRect,largestRectangle},validRange,rangeOverlaps,higherLimit,bands:BANDS};
  if(typeof window!=='undefined')window.ServerMapAreas=exports;
  if(typeof module!=='undefined')module.exports=exports;
})();
