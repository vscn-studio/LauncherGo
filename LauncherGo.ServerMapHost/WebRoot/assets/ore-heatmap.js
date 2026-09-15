/* Both sources are server observations. Node slices describe search volumes, not exact ore Y. */
window.createOreHeatmap = function ({ gameLatLng, relativePoint, language, changed }) {
  const panel = document.querySelector('#oreHeatmapControls');
  const select = document.querySelector('#oreHeatmapSelect'), modeSelect = document.querySelector('#oreHeatmapMode');
  const minY = document.querySelector('#oreMinY'), maxY = document.querySelector('#oreMaxY');
  const legend = panel.querySelector('.ore-legend');
  const colors = ['#8c929b', '#5555b5', '#6260ae', '#79709c', '#a58972', '#c78250', '#c0523e', '#a82224'];
  const nodeColors = ['#8c929b','#687790','#568ea2','#55a7a3','#a7b755','#db9640','#e06535'];
  const densityLabels = {zh:['无读数','微量','极贫','贫','中等','高','很高','极高'],en:['No reading','Trace','Very poor','Poor','Decent','High','Very high','Ultra high']};
  const nodeLabels = {zh:['未检出','微量 1–9','少量 10–19','中量 20–39','大量 40–79','很多 80–159','巨量 ≥160'],en:['Not detected','Trace 1–9','Small 10–19','Medium 20–39','Large 40–79','Very large 80–159','Huge ≥160']};
  let catalog = [], lastData, failed = false;
  const zh = () => language() === 'zh', labels = () => densityLabels[zh()?'zh':'en'];
  const name = code => { const ore = catalog.find(item => item.code === code); return ore?.[zh()?'zh':'en'] || ore?.en || code; };
  const time = value => new Date(value).toLocaleString(zh()?'zh-CN':'en-US');
  const nameMode = mode => mode === 'node' ? (zh()?'矿脉搜索':'Node search') : (zh()?'密度勘探':'Density');
  const levels = ore => Math.max(0, Math.min(6, Number(ore?.amountLevel) || 0));
  function translate() {
    const selected = select.value;
    select.replaceChildren(new Option(zh()?'全部矿物':'All ores',''));
    for (const ore of catalog) select.add(new Option(name(ore.code),ore.code));
    if (selected && !catalog.some(ore => ore.code === selected)) select.add(new Option(selected,selected));
    select.value = selected;
    panel.querySelector('label[for="oreHeatmapSelect"]').textContent = zh()?'矿物筛选':'Filter ore';
    panel.querySelector('label[for="oreHeatmapMode"]').textContent = zh()?'探矿模式':'Prospecting mode';
    ['全部模式','密度勘探','矿脉搜索'].forEach((label,i) => modeSelect.options[i].textContent = zh()?label:['All modes','Density','Node search'][i]);
    panel.querySelector('.ore-depth-label').textContent = zh()?'矿脉搜索 Y 范围（留空为全部）':'Node search Y range (blank = all)';
    minY.setAttribute('aria-label',zh()?'最低 Y':'Minimum Y'); maxY.setAttribute('aria-label',zh()?'最高 Y':'Maximum Y');
    minY.disabled = maxY.disabled = modeSelect.value === 'density';
    legend.replaceChildren();
    function legendItems(items,palette) {
      items.forEach((label,i) => { const entry=document.createElement('span'), swatch=document.createElement('i'); swatch.style.backgroundColor=palette[i]; entry.append(swatch,document.createTextNode(label)); legend.append(entry); });
    }
    if (modeSelect.value !== 'node') legendItems(labels(),colors);
    if (modeSelect.value !== 'density') legendItems(nodeLabels[zh()?'zh':'en'],nodeColors);
  }
  function update() { if (!minY.checkValidity() || !maxY.checkValidity() || minY.value && maxY.value && +minY.value > +maxY.value) return; translate(); changed(); }
  select.onchange = modeSelect.onchange = minY.onchange = maxY.onchange = update;
  function popup(samples, code) {
    const box=document.createElement('div'); box.className='ore-reading-popup';
    const title=document.createElement('b'); title.textContent=(code?name(code)+' · ':'')+nameMode(samples[0].mode); box.append(title);
    for (const sample of samples) {
      const ores=(sample.ores||[]).filter(ore=>!code||ore.code===code);
      // Empty intervals clear that depth segment but are intentionally omitted
      // from the popup; the popup contains positive observations only.
      if (!ores.length) continue;
      const row=document.createElement('section');
      if (sample.mode==='node') {
        const local=relativePoint({x:sample.sampleX,z:sample.sampleZ});
        const range=document.createElement('div'); range.className='ore-reading-range'; range.textContent='Y '+(sample.sampleY-sample.radius)+'–'+(sample.sampleY+sample.radius)+' · O ('+Math.round(local.x)+', '+sample.sampleY+', '+Math.round(local.z)+') · R '+sample.radius; row.append(range);
      }
      for (const ore of ores) {
        const line=document.createElement('div'); line.textContent=name(ore.code)+' · '+(sample.mode==='node'
          ? ore.blocks+' '+(zh()?'个矿块':'blocks') : (labels()[ore.density]||'')+' · '+Number(ore.partsPerThousand).toFixed(2)+'‰'); row.append(line);
      }
      const date=document.createElement('small'); date.className='ore-reading-time'; date.textContent=(zh()?'采样：':'Sampled: ')+time(sample.sampledAt); row.append(date); box.append(row);
    }
    return box;
  }
  function normalizeNodeSamples(samples) {
    if (samples.length < 2) return samples;
    const ranges=samples.map(sample=>({sample,min:sample.sampleY-sample.radius,max:sample.sampleY+sample.radius}));
    const boundaries=[...new Set(ranges.flatMap(range=>[range.min,range.max]))].sort((a,b)=>a-b);
    const result=[];
    for(let i=0;i<boundaries.length-1;i++) {
      const min=boundaries[i], max=boundaries[i+1];
      if(max<=min) continue;
      const covered=ranges.filter(range=>range.min<=min&&range.max>=max)
        .sort((a,b)=>new Date(b.sample.sampledAt)-new Date(a.sample.sampledAt));
      if(!covered.length) continue;
      const source=covered[0].sample;
      result.push({...source,sampleY:(min+max)/2,radius:(max-min)/2});
    }
    return result;
  }
  function feature(item) {
    const p=item.properties||{}, samples=p.samples||[p], node=p.mode==='node';
    const effectiveSamples=node?normalizeNodeSamples(samples):samples;
    const density=Math.max(0,Math.min(7,Number(p.density)||0));
    const level=node?Math.max(0,...effectiveSamples.flatMap(s=>(s.ores||[]).map(levels))):density;
    const color=(node?nodeColors:colors)[level], coords=item.geometry.coordinates[0];
    const polygon=L.geoJSON(item,{coordsToLatLng:c=>gameLatLng(c[0],c[1]),style:{color,fillColor:color,fillOpacity:level?.38:.08,weight:level?.5:1,dashArray:node?'3 3':null},
      onEachFeature:(_,layer)=>layer.bindPopup(popup(effectiveSamples))});
    const group=L.layerGroup([polygon]), codes=[...new Set(effectiveSamples.flatMap(s=>(s.ores||[]).map(o=>o.code)))];
    // Density readings remain a colored area only.  The 3D columns are reserved
    // for node (ore-vein) searches, where each segment represents a Y search band.
    if (!node) {
      group.on('add', () => polygon.bringToBack());
      return group;
    }
    if (!codes.length) { group.on('add',()=>polygon.bringToBack()); return group; }
    const root=document.createElement('div'); root.className='ore-columns-inner'; root.dataset.mode=node?'node':'density';
    let pinned=null;
    function focus(code) { root.classList.toggle('has-focus',!!code); root.querySelectorAll('.ore-column').forEach(column=>column.classList.toggle('selected',column.dataset.ore===code)); }
    const ordered=[...effectiveSamples].sort((a,b)=>(b.sampleY||0)-(a.sampleY||0));
    const minDepth=node?Math.min(...ordered.map(sample=>sample.sampleY-sample.radius)):0;
    const maxDepth=node?Math.max(...ordered.map(sample=>sample.sampleY+sample.radius)):1;
    const depthSpan=Math.max(1,maxDepth-minDepth);
    const rowCount=node?Math.max(1,Math.ceil(depthSpan/16)):1;
    const columnsHeight=node?Math.min(192,Math.max(48,rowCount*24)):84;
    for (const code of codes) {
      const column=document.createElement('button'); column.type='button'; column.className='ore-column'; column.dataset.ore=code;
      column.setAttribute('aria-label',name(code)); column.setAttribute('aria-pressed','false');
      column.style.height=columnsHeight+'px';
      const title=document.createElement('b'); title.textContent=name(code); column.append(title);
      ordered.forEach(sample=>{
        const ore=(sample.ores||[]).find(o=>o.code===code), segment=document.createElement('span'); segment.className='ore-column-segment';
        const amount=node?levels(ore):ore?.density||0;
        const bottom=node?((sample.sampleY-sample.radius-minDepth)/depthSpan*100):0;
        const height=node?((sample.radius*2)/depthSpan*100):Math.max(3,amount/7*100);
        const hasOre=!!ore && amount>0;
        segment.style.cssText='bottom:'+bottom+'%;height:'+height+'%;background:'+(hasOre?nodeColors[amount]:'transparent')+';opacity:'+(hasOre?1:0)+';pointer-events:'+(hasOre?'auto':'none');
        if (hasOre) segment.title=name(code)+' · Y '+(sample.sampleY-sample.radius)+'–'+(sample.sampleY+sample.radius)+' · '+ore.blocks+' '+(zh()?'个矿块':'blocks')+' · '+time(sample.sampledAt);
        segment.dataset.sampleY=sample.sampleY??''; column.append(segment);
      });
      column.onmouseenter=()=>focus(code); column.onmouseleave=()=>focus(pinned);
      column.onfocus=()=>focus(code); column.onblur=()=>focus(pinned);
      column.onclick=event=>{event.stopPropagation(); if(pinned===code){ pinned=null; focus(null); root.querySelectorAll('button').forEach(b=>b.setAttribute('aria-pressed','false')); marker.closePopup(); return; } pinned=code; focus(pinned); root.querySelectorAll('button').forEach(b=>b.setAttribute('aria-pressed',String(b.dataset.ore===pinned))); marker.setPopupContent(popup(ordered,code)); marker.openPopup();};
      root.append(column);
    }
    const width=Math.max(40,codes.length*28), iconHeight=columnsHeight;
    // A feature represents one chunk rectangle. Keep one immutable geographic
    // anchor for the whole column group; never average search centers from
    // different depth records, which made the marker drift as new probes arrived.
    const xs=coords.map(point=>point[0]), zs=coords.map(point=>point[1]);
    const center=gameLatLng((Math.min(...xs)+Math.max(...xs))/2,(Math.min(...zs)+Math.max(...zs))/2);
    // The fixed chunk coordinate is the bottom-centre of the actual column
    // box. The ore name is absolutely positioned overflow and is excluded from
    // iconHeight, so it cannot move the geographic anchor.
    const marker=L.marker(center,{icon:L.divIcon({className:'ore-columns',html:root,iconSize:[width,iconHeight],iconAnchor:[width/2,iconHeight]}),keyboard:false}).bindPopup(popup(ordered));
    marker.on('popupclose',()=>{ pinned=null; focus(null); root.querySelectorAll('button').forEach(button=>button.setAttribute('aria-pressed','false')); });
    let activeMap;
    function zoom() {
      const a=activeMap.latLngToLayerPoint(gameLatLng(coords[0][0],coords[0][1])), b=activeMap.latLngToLayerPoint(gameLatLng(coords[2][0],coords[2][1]));
      const visible=Math.abs(b.x-a.x)>=Math.max(96,width+8);
      if(visible&&!group.hasLayer(marker))group.addLayer(marker);
      if(!visible&&group.hasLayer(marker))group.removeLayer(marker);
    }
    group.on('add',()=>{activeMap=polygon._map;polygon.bringToBack();activeMap.on('zoomend',zoom);zoom();});
    group.on('remove',()=>{activeMap?.off('zoomend',zoom);});
    return group;
  }
  function accept(data) {
    catalog=data.ores||[]; failed=false;
    // One node column group per horizontal chunk; independent depth observations remain separate slices.
    const nodes=new Map(), features=[];
    for(const item of data.features||[]) {
      if(item.properties?.mode!=='node') {features.push(item);continue;}
      const key=JSON.stringify(item.geometry.coordinates), prior=nodes.get(key);
      if(prior)prior.properties.samples.push(item.properties);
      else {const grouped={...item,id:'node-columns-'+item.id,properties:{...item.properties,samples:[item.properties]}};nodes.set(key,grouped);features.push(grouped);}
    }
    data.features=features;lastData=data;translate();
  }
  translate();
  return {selected:()=>select.value,mode:()=>modeSelect.value,depth:()=>modeSelect.value==='density'?'':(minY.value?'&minY='+encodeURIComponent(minY.value):'')+(maxY.value?'&maxY='+encodeURIComponent(maxY.value):''),
    visible:value=>{panel.hidden=!value;},accept,error:()=>{failed=true;catalog=[];translate();},
    reset:()=>{catalog=[];lastData=undefined;failed=false;select.value='';modeSelect.value='';minY.value=maxY.value='';translate();},translate,feature};
};
