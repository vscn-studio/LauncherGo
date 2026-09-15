/* Node readings describe complete search volumes, never individual Y slices. */
window.createOreHeatmap = function ({ gameLatLng, relativePoint, language, changed, api, map }) {
  const panel=document.querySelector('#oreHeatmapControls');
  const select=document.querySelector('#oreHeatmapSelect'),modeSelect=document.querySelector('#oreHeatmapMode');
  const minY=document.querySelector('#oreMinY'),maxY=document.querySelector('#oreMaxY'),legend=panel.querySelector('.ore-legend');
  const colors=['#8c929b','#5555b5','#6260ae','#79709c','#a58972','#c78250','#c0523e','#a82224'];
  const nodeColors=['#8c929b','#687790','#568ea2','#55a7a3','#a7b755','#db9640','#e06535'];
  const densityLabels={zh:['无读数','微量','极贫','贫','中等','高','很高','极高'],en:['No reading','Trace','Very poor','Poor','Decent','High','Very high','Ultra high']};
  const nodeLabels={zh:['未检出','微量 1–9','少量 10–19','中量 20–39','大量 40–79','很多 80–159','巨量 ≥160'],en:['Not detected','Trace 1–9','Small 10–19','Medium 20–39','Large 40–79','Very large 80–159','Huge ≥160']};
  let catalog=[],tooltip=null,detailRequest=null,openedPoint=null,actionPopup=null,actionOwner=null;
  // Keep probe actions above fixed map controls while following Leaflet's map offset.
  const actionPane=map.createPane('oreProbeActions',map.getContainer());
  const positionActions=()=>L.DomUtil.setPosition(actionPane,map.containerPointToLayerPoint([0,0]).multiplyBy(-1));
  map.on('move zoom resize',positionActions);positionActions();
  const zh=()=>language()==='zh',tr=(cn,en)=>zh()?cn:en,labels=()=>densityLabels[zh()?'zh':'en'];
  const name=code=>{const ore=catalog.find(item=>item.code===code);return ore?.[zh()?'zh':'en']||ore?.en||code;};
  const level=ore=>Math.max(0,Math.min(6,Number(ore?.amountLevel)||0));
  const el=(tag,cls,text)=>{const node=document.createElement(tag);if(cls)node.className=cls;if(text!==undefined)node.textContent=text;return node;};
  function button(text,action,cls){const b=el('button','notebook-button '+cls,text);b.type='button';b.onclick=action;return b;}
  function translate(){
    const selected=select.value;
    select.replaceChildren(new Option(tr('全部矿物','All ores'),''));
    for(const ore of catalog)select.add(new Option(name(ore.code),ore.code));
    if(selected&&!catalog.some(ore=>ore.code===selected))select.add(new Option(selected,selected));
    select.value=selected;
    panel.querySelector('label[for="oreHeatmapSelect"]').textContent=tr('矿物筛选','Filter ore');
    panel.querySelector('label[for="oreHeatmapMode"]').textContent=tr('探矿模式','Prospecting mode');
    ['全部模式','密度勘探','矿脉搜索'].forEach((label,i)=>modeSelect.options[i].textContent=zh()?label:['All modes','Density','Node search'][i]);
    panel.querySelector('.ore-depth-label').textContent=tr('搜索覆盖 Y 范围（留空为全部）','Search coverage Y range (blank = all)');
    minY.setAttribute('aria-label',tr('最低 Y','Minimum Y'));maxY.setAttribute('aria-label',tr('最高 Y','Maximum Y'));
    minY.disabled=maxY.disabled=modeSelect.value==='density';legend.replaceChildren();
    function items(texts,palette){texts.forEach((text,i)=>{const entry=el('span'),swatch=el('i');swatch.style.backgroundColor=palette[i];entry.append(swatch,document.createTextNode(text));legend.append(entry);});}
    if(modeSelect.value!=='node')items(labels(),colors);
    if(modeSelect.value!=='density')items(nodeLabels[zh()?'zh':'en'],nodeColors);
  }
  function closeDetail(){detailRequest?.abort();detailRequest=null;openedPoint=null;if(tooltip){tooltip.remove();tooltip=null;}}
  function closeActions(){const popup=actionPopup;actionPopup=null;actionOwner=null;popup?.remove();}
  function closeProbeUi(){closeDetail();closeActions();}
  map.on('click',closeProbeUi);
  map.getContainer().addEventListener('keydown',event=>{if(event.key==='Escape')closeProbeUi();});
  function update(){if(!minY.checkValidity()||!maxY.checkValidity()||minY.value&&maxY.value&&+minY.value>+maxY.value)return;closeProbeUi();translate();changed();}
  select.onchange=modeSelect.onchange=minY.onchange=maxY.onchange=update;
  function densityPopup(samples){
    const box=el('div','ore-reading-popup');box.append(el('b','',tr('密度勘探','Density')));
    for(const sample of samples)for(const ore of sample.ores||[])box.append(el('div','',name(ore.code)+' · '+(labels()[ore.density]||'')+' · '+Number(ore.partsPerThousand).toFixed(2)+'‰'));
    return box;
  }
  function heatColor(count,maximum){
    const ratio=count/maximum;
    const from=[255,224,139],to=[213,32,39];
    return 'rgb('+from.map((v,i)=>Math.round(v+(to[i]-v)*ratio)).join(',')+')';
  }
  function chart(data){
    const root=el('div','ore-depth-chart');
    const columns=data.columns||[];
    if(!columns.length){root.append(el('span','ore-depth-status',data.incomplete?tr('旧记录无逐层数据，请重新探矿','No per-level data. Prospect again.'):tr('未检出矿物','No ore detected')));return root;}
    const step=Math.max(1,Number(data.step)||6);
    // Use the same broken Y axis for every ore; never compress away another ore's layer.
    const levels=[...new Set(columns.flatMap(column=>column.layers.filter(layer=>layer.blocks>0).map(layer=>layer.y)))].sort((a,b)=>b-a);
    const rowHeight=14,gapHeight=10;
    const pageSize=Math.max(2,Math.min(10,Math.floor((map.getContainer().clientHeight-210)/(rowHeight+gapHeight))));
    const pages=Math.max(1,Math.ceil(levels.length/pageSize));let page=0;
    const scroller=el('div','ore-depth-scroll'),plot=el('div','ore-depth-plot');
    plot.style.gridTemplateColumns=`42px repeat(${columns.length}, 88px)`;
    scroller.append(plot);root.append(scroller);
    function render(){
      plot.replaceChildren();
      const visible=levels.slice(page*pageSize,(page+1)*pageSize),rows=[],gaps=[];let height=0;
      visible.forEach((y,i)=>{
        if(i&&visible[i-1]-y>1){gaps.push({top:height,minY:y+1,maxY:visible[i-1]-1});height+=gapHeight;}
        rows.push({y,top:height});height+=rowHeight;
      });
      plot.style.setProperty('--plot-height',height+'px');
      const axis=el('div','ore-depth-axis'),axisTitle=el('div','ore-depth-heading','Y'),axisBody=el('div','ore-depth-axis-body');
      for(const row of rows){const tick=el('span','ore-depth-tick',String(row.y));tick.style.top=row.top+'px';tick.dataset.y=row.y;tick.classList.toggle('ore-depth-major',row.y%step===0);axisBody.append(tick);}
      function breaks(target){for(const gap of gaps){const mark=el('span','ore-depth-break','⋯');mark.style.top=gap.top+'px';mark.title=tr('省略 Y ','Omitted Y ')+gap.minY+'–'+gap.maxY;target.append(mark);}}
      breaks(axisBody);
      axis.append(axisTitle,axisBody);plot.append(axis);
      for(const column of columns){
        const item=el('div','ore-depth-column'),head=el('div','ore-depth-heading');
        head.append(el('b','',column[zh()?'zh':'en']||column.en||name(column.code)),el('span','ore-depth-total',column.total+' '+tr('块','blocks')));
        const bar=el('div','ore-depth-bar');bar.dataset.ore=column.code;bar.setAttribute('aria-label',(column[zh()?'zh':'en']||name(column.code))+' '+column.total+' '+tr('块','blocks'));
        const maximum=Math.max(1,...column.layers.map(layer=>layer.blocks));
        const byY=new Map(column.layers.map(layer=>[layer.y,layer]));
        for(const row of rows){
          const layer=byY.get(row.y);
          if(!layer?.blocks){const empty=el('div','ore-depth-coverage');empty.style.top=row.top+'px';empty.style.height=rowHeight+'px';bar.append(empty);continue;}
          const cell=el('div','ore-depth-cell');cell.dataset.y=layer.y;cell.dataset.blocks=layer.blocks;
          cell.style.top=row.top+'px';cell.style.height=rowHeight+'px';cell.style.backgroundColor=heatColor(layer.blocks,maximum);
          cell.title=`Y ${layer.y} · ${layer.blocks} `+tr('块','blocks');bar.append(cell);
        }
        breaks(bar);
        item.append(head,bar);plot.append(item);
      }
    }
    render();
    root.append(el('small','ore-depth-note',tr('仅显示有矿 Y 层 · ⋯ 表示省略区间','Ore-bearing Y levels only · ⋯ marks omitted levels')));
    if(pages>1){
      const pager=el('div','ore-depth-pages'),label=el('span');
      const refresh=()=>{render();label.textContent=(page+1)+' / '+pages;previous.disabled=page===0;next.disabled=page===pages-1;fitTooltip();};
      const previous=button(tr('较高层','Higher'),()=>{page--;refresh();},'ore-depth-page');
      const next=button(tr('较低层','Lower'),()=>{page++;refresh();},'ore-depth-page');
      pager.append(previous,label,next);root.append(pager);refresh();
    }
    if(data.incomplete)root.append(el('small','ore-depth-status',tr('部分记录无逐层数据，请重新探矿','Some samples lack per-level data. Prospect again.')));
    requestAnimationFrame(fitTooltip);
    return root;
  }
  function fitTooltip(){
    if(!tooltip?.isConnected||!openedPoint)return;
    const point=map.latLngToContainerPoint(gameLatLng(openedPoint.x,openedPoint.z));
    const bounds=map.getContainer().getBoundingClientRect(),box=tooltip.getBoundingClientRect();
    const left=Math.max(8,Math.min(bounds.width-box.width-8,point.x-box.width/2));
    let top=point.y-box.height-14;
    const controls=panel.getBoundingClientRect();
    const intersectsControls=!panel.hidden&&left+bounds.left<controls.right&&left+bounds.left+box.width>controls.left
      &&top+bounds.top<controls.bottom&&top+bounds.top+box.height>controls.top;
    if((top<8||intersectsControls)&&point.y+14+box.height<bounds.height-8)top=point.y+14;
    tooltip.style.left=left+'px';tooltip.style.top=Math.max(8,Math.min(bounds.height-box.height-8,top))+'px';
  }
  map.on('move zoom resize',fitTooltip);
  async function openDepth(point,marker){
    if(openedPoint?.x===point.x&&openedPoint?.z===point.z){closeDetail();return;}
    closeDetail();openedPoint=point;
    const tip=tooltip=el('div','leaflet-tooltip ore-depth-tooltip');tip.setAttribute('role','tooltip');
    tip.append(el('span','ore-depth-status',tr('加载中…','Loading…')));map.getContainer().append(tip);
    L.DomEvent.disableClickPropagation(tip);L.DomEvent.disableScrollPropagation(tip);fitTooltip();
    const request=detailRequest=new AbortController();
    try{
      const query=`x=${encodeURIComponent(point.x)}&z=${encodeURIComponent(point.z)}&ore=${encodeURIComponent(select.value)}`
        +(minY.value?'&minY='+encodeURIComponent(minY.value):'')+(maxY.value?'&maxY='+encodeURIComponent(maxY.value):'');
      const response=await fetch(`${api}/ore-probes?${query}`,{signal:request.signal});
      if(!response.ok)throw new Error('HTTP '+response.status);
      const data=await response.json();if(tooltip!==tip||request.signal.aborted)return;
      tip.replaceChildren(chart(data));fitTooltip();
    }catch(error){if(error.name!=='AbortError'&&tooltip===tip){tip.replaceChildren(button(tr('加载失败，点击重试','Load failed. Retry'),()=>{closeDetail();openDepth(point,marker);},'ore-depth-retry'));fitTooltip();}}
  }
  function deletePanel(container,point,admin,done){
    container.replaceChildren(el('p','',admin?tr('删除此探矿点的全部记录？','Delete all records at this probe point?'):tr('删除我在此探矿点的记录？','Delete my records at this probe point?')));
    const status=el('span','ore-delete-status');status.setAttribute('role','status');
    const confirm=button(admin?tr('删除探矿点','Delete probe point'):tr('删除我的记录','Delete my records'),async()=>{
      confirm.disabled=true;status.textContent=tr('正在删除…','Deleting…');
      try{const response=await fetch(`${api}/ore-probes?x=${encodeURIComponent(point.x)}&z=${encodeURIComponent(point.z)}`,{method:'DELETE',headers:{'X-ServerMap-Request':'1'}});if(!response.ok)throw new Error('HTTP '+response.status);done();}
      catch{confirm.disabled=false;status.textContent=tr('删除失败，请确认登录后重试','Delete failed. Check your login and retry.');}
    },'ore-delete-confirm');container.append(confirm,status);
  }
  function feature(item){
    const p=item.properties||{},samples=p.samples||[p];
    if(p.mode!=='node'){
      const color=colors[Math.max(0,Math.min(7,Number(p.density)||0))];
      return L.geoJSON(item,{coordsToLatLng:c=>gameLatLng(c[0],c[1]),style:{color,fillColor:color,fillOpacity:.3,weight:.5},onEachFeature:(_,layer)=>layer.bindPopup(densityPopup(samples))});
    }
    const point={x:p.sampleX,z:p.sampleZ},amount=Math.max(0,...samples.flatMap(s=>(s.ores||[]).map(level)));
    const dot=el('span','ore-probe-dot');dot.style.setProperty('--ore-color',nodeColors[amount]);
    const marker=L.marker(gameLatLng(point.x,point.z),{icon:L.divIcon({className:'ore-probe-marker',html:dot,iconSize:[24,24],iconAnchor:[12,12]}),title:tr('探矿点','Probe point'),keyboard:true,bubblingMouseEvents:false});
    marker.on('click',()=>{if(actionPopup){closeProbeUi();return;}openDepth(point,marker);});
    marker.on('contextmenu',event=>{
      if(event.originalEvent)L.DomEvent.stop(event.originalEvent);
      closeProbeUi();const box=el('div','ore-probe-menu');
      if(samples.some(s=>s.owned||s.adminDelete))deletePanel(box,point,samples.some(s=>s.adminDelete),()=>{closeProbeUi();changed();});
      else box.append(el('p','',tr('只能删除本人采样；旧记录无法确认归属。','Only your own samples can be deleted. Legacy records have no known owner.')));
      // bindPopup installs its own left-click handler; actions must remain right-click only.
      const popup=L.popup({pane:'oreProbeActions',className:'ore-probe-popup',closeOnClick:false})
        .setLatLng(marker.getLatLng()).setContent(box);
      actionPopup=popup;actionOwner=marker;
      popup.on('remove',()=>{if(actionPopup===popup){actionPopup=null;actionOwner=null;}});
      popup.openOn(map);
    });
    marker.on('remove',()=>{if(openedPoint?.x===point.x&&openedPoint?.z===point.z)closeDetail();if(actionOwner===marker)closeActions();});return marker;
  }
  function accept(data){
    catalog=data.ores||[];const nodes=new Map(),features=[];
    for(const item of data.features||[]){
      if(item.properties?.mode!=='node'){features.push(item);continue;}
      const p=item.properties;if(!Number.isFinite(p.sampleX)||!Number.isFinite(p.sampleZ))continue;
      const key=JSON.stringify([p.sampleX,p.sampleZ]),prior=nodes.get(key);
      if(prior)prior.properties.samples.push(p);
      else{const grouped={...item,id:'node-point-'+key,properties:{...p,samples:[p]}};nodes.set(key,grouped);features.push(grouped);}
    }
    data.features=features;translate();
  }
  translate();
  return {selected:()=>select.value,mode:()=>modeSelect.value,depth:()=>modeSelect.value==='density'?'':(minY.value?'&minY='+encodeURIComponent(minY.value):'')+(maxY.value?'&maxY='+encodeURIComponent(maxY.value):''),
    visible:value=>{panel.hidden=!value;if(!value)closeProbeUi();},accept,error:()=>{closeProbeUi();catalog=[];translate();},
    reset:()=>{closeProbeUi();catalog=[];select.value='';modeSelect.value='';minY.value=maxY.value='';translate();},translate,feature};
};
