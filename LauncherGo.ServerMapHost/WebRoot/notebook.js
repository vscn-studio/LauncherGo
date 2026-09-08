/* Player-owned game waypoints and separate web routes. */
// Link icon geometry adapted from Feather (MIT), Copyright 2013-2023 Cole Bemis.
// See vendor/Feather-LICENSE.txt and THIRD_PARTY_NOTICES.txt.
(() => {
  const words = {
    zh: { myMarkers:'游戏标记', myRoutes:'我的轨迹', hiddenRegions:'隐藏区域', plan:'规划路线', login:'登录后可查看和保存', emptyMarkers:'暂无游戏内标记', emptyRoutes:'暂无已保存轨迹', emptyRegions:'暂无隐藏区域', progress:'地图渲染进度', waiting:'等待游戏就绪', starting:'正在启动', scanning:'正在扫描存档', rendering:'正在渲染', retrying:'等待重试', idle:'当前队列已完成', queued:'排队', active:'处理中', completed:'已完成', failed:'失败', regions:'发现区域', pendingSave:'等待游戏保存', noProgress:'渲染进度暂不可用', sharePoint:'复制坐标链接', shareRoute:'复制轨迹链接', editRoute:'编辑轨迹', deleteRoute:'删除轨迹', hideRegion:'框选隐藏区域', editRegion:'编辑隐藏区域', removeRegion:'移除隐藏区域', undo:'撤销', redo:'恢复', finish:'完成', cancel:'取消', save:'保存', routeName:'轨迹名称', color:'颜色', routeHelp:'左键依次添加折线顶点；支持撤销与恢复。完成后保存。', regionHelp:'左键点击对角位置确定隐藏区域，Esc 取消。', routeMin:'至少需要两个点', routeMax:'最多支持 512 个点', copied:'链接已复制', copyFailed:'复制失败，请允许剪贴板访问后重试', error:'操作失败', routeSaved:'已保存到我的轨迹', sharedRoute:'分享的轨迹', saveShared:'保存到我的轨迹', close:'关闭', confirmDelete:'删除此轨迹？它的分享链接也将失效。', confirmRemove:'解除这个区域对所有玩家的隐藏？', confirmFog:'保存后将向普通玩家隐藏此区域。', unavailableShare:'分享不存在、已撤销，或轨迹经过隐藏区域', loading:'加载中…', waypointUnavailable:'游戏图标未在服务器加载', latest:'最近完成', points:'点', shareWarning:'拥有链接的人可以预览这条轨迹；不会分享你的其他标记。', manageRoute:'轨迹操作', fogName:'区域名称', fogDisabled:'管理员预览：区域文字已关闭，其他玩家仍受隐藏限制' },
    en: { myMarkers:'Game markers', myRoutes:'My routes', hiddenRegions:'Hidden regions', plan:'Plan route', login:'Log in to view and save', emptyMarkers:'No game waypoints', emptyRoutes:'No saved routes', emptyRegions:'No hidden regions', progress:'Map rendering', waiting:'Waiting for game', starting:'Starting', scanning:'Scanning save', rendering:'Rendering', retrying:'Waiting to retry', idle:'Current queue complete', queued:'Queued', active:'Active', completed:'Completed', failed:'Failed', regions:'Regions found', pendingSave:'Awaiting game save', noProgress:'Render progress unavailable', sharePoint:'Copy coordinate link', shareRoute:'Copy route link', editRoute:'Edit route', deleteRoute:'Delete route', hideRegion:'Select hidden region', editRegion:'Edit hidden region', removeRegion:'Remove hidden region', undo:'Undo', redo:'Redo', finish:'Finish', cancel:'Cancel', save:'Save', routeName:'Route name', color:'Color', routeHelp:'Left-click to add polyline vertices. Undo and redo points; finish to save.', regionHelp:'Left-click the opposite corner to hide a rectangle. Esc cancels.', routeMin:'At least two points required', routeMax:'Maximum 512 points', copied:'Link copied', copyFailed:'Copy failed; allow clipboard access and retry', error:'Operation failed', routeSaved:'Saved to My routes', sharedRoute:'Shared route', saveShared:'Save to My routes', close:'Close', confirmDelete:'Delete this route? Its share links will also stop working.', confirmRemove:'Reveal this region to all players?', confirmFog:'Saving hides this region from other players.', unavailableShare:'Share missing, revoked, or crossing a hidden region', loading:'Loading…', waypointUnavailable:'Game icon not loaded on server', latest:'Last completed', points:'points', shareWarning:'Anyone with the link can preview this route, but not your other markers.', manageRoute:'Route actions', fogName:'Region name', fogDisabled:'Admin preview: region labels off; other players remain restricted' }
  };
  Object.assign(words.zh,{addGameMarker:'添加游戏标记',editMarker:'编辑游戏标记',deleteMarker:'删除游戏标记',shareMarker:'复制标记链接',saveMarkerShare:'保存到游戏标记',sharedMarker:'分享的标记',markerName:'名称',markerText:'说明',pinned:'置顶',suggestNames:'建议已保存名称',icon:'图标',markerSaved:'游戏标记已保存',deleteMarkerConfirm:'删除这个游戏标记？其分享链接也会失效。',markerShareWarning:'拥有链接的人可以查看这个标记。',mapDisabled:'服务器已禁用游戏地图'});
  Object.assign(words.en,{addGameMarker:'Add game marker',editMarker:'Edit game marker',deleteMarker:'Delete game marker',shareMarker:'Copy marker link',saveMarkerShare:'Save to Game markers',sharedMarker:'Shared marker',markerName:'Name',markerText:'Description',pinned:'Pinned',suggestNames:'Suggest saved names',icon:'Icon',markerSaved:'Game marker saved',deleteMarkerConfirm:'Delete this game marker? Its share links will stop working.',markerShareWarning:'Anyone with the link can view this marker.',mapDisabled:'Game map is disabled on this server'});
  Object.assign(words.zh,{editPoi:'编辑地点标记',sharePoi:'复制地点链接',shareTranslocator:'复制传送器链接'});
  Object.assign(words.en,{editPoi:'Edit place marker',sharePoi:'Copy place link',shareTranslocator:'Copy translocator link'});
  function create(options) {
    const { map, api, gameLatLng, gamePoint, getAuth, getMetadata, getLanguage, cancelMeasurement, closePanels, invalidatePrivacy } = options;
    const text = key => words[getLanguage() === 'zh' ? 'zh' : 'en'][key] || key;
    const el = (tag, props = {}) => Object.assign(document.createElement(tag), props);
    const button = (label, action) => { const b = el('button', { type:'button', className:'notebook-button', textContent:label }); b.onclick = action; return b; };
    const routeIcons={
      shareRoute:['M10 13a5 5 0 0 0 7 0l3-3a5 5 0 0 0-7-7l-2 2','M14 11a5 5 0 0 0-7 0l-3 3a5 5 0 0 0 7 7l2-2'],
      editRoute:['m16 3 5 5-12 12-6 1 1-6Z','m13 6 5 5'],
      deleteRoute:['M3 6h18M9 6V3h6v3M5 6l1 15h12l1-15M10 10v7M14 10v7'],
      saveShared:['M12 3v12m-5-5 5 5 5-5','M5 16v5h14v-5']
    };
    Object.assign(routeIcons,{sharePoi:routeIcons.shareRoute,shareTranslocator:routeIcons.shareRoute,editPoi:routeIcons.editRoute,shareMarker:routeIcons.shareRoute,editMarker:routeIcons.editRoute,deleteMarker:routeIcons.deleteRoute,saveMarkerShare:routeIcons.saveShared,editRegion:routeIcons.editRoute,removeRegion:routeIcons.deleteRoute});
    function routeIconButton(key,action){
      const b=button('',action),label=text(key),svg=document.createElementNS('http://www.w3.org/2000/svg','svg');
      b.classList.add('notebook-icon-button');b.title=label;b.setAttribute('aria-label',label);b.dataset.icon=key;
      for(const [name,value] of Object.entries({viewBox:'0 0 24 24',fill:'none',stroke:'currentColor','stroke-width':'1.8','stroke-linecap':'round','stroke-linejoin':'round','aria-hidden':'true',focusable:'false'}))svg.setAttribute(name,value);
      for(const d of routeIcons[key]){const path=document.createElementNS('http://www.w3.org/2000/svg','path');path.setAttribute('d',d);svg.append(path);}b.append(svg);return b;
    }
    const markerGroup = L.layerGroup().addTo(map), routeGroup = L.layerGroup().addTo(map), sharedGroup = L.layerGroup().addTo(map), draftGroup = L.layerGroup().addTo(map);
    const sharedMarkerGroup=L.layerGroup().addTo(map);
    let sharedMarker,sharedMarkerId=new URL(location.href).searchParams.get('waypoint'),contextMarker;
    map.createPane('notebookFog'); map.getPane('notebookFog').style.zIndex = '650';
    map.getPane('notebookFog').style.pointerEvents = 'none';
    const fogGroup = L.layerGroup().addTo(map);
    const fogRenderer = L.svg({pane:'notebookFog'});

    const fogPreferenceKey=`servermap-fog-preview:${location.host}${api}`;
    let ready = false, authEpoch = 0, privacyEpoch = 0, markers = [], routes = [], regions = [], selectedRoute, sharedRoute, sharedId = new URL(location.href).searchParams.get('route');
    let mode = null, draft = [], redoDraft = [], editingId = null, selectionStart = null, selectionRect, contextPosition, contextRoute, contextRegion, requestBusy = false;
    let markersSignature = '', fogSignature = '', noticeTimer, preview, lastProgress, writing = false;
    const sections = {};
    for (const key of ['myMarkers','myRoutes','hiddenRegions']) {
      const section = el('section', { className:'notebook-section' }); section.id = `notebook-${key}`;
      const heading = el('div', { className:'notebook-heading layer-row' }), label = el('label'), toggle = el('input', { type:'checkbox', checked:true, id:`notebook-toggle-${key}` }), title = el('span');
      label.htmlFor=toggle.id;label.append(title);heading.append(toggle,label);section.append(heading);
      document.querySelector('#sidebar [data-i18n="onlinePlayers"]').before(section); sections[key] = { section, title, toggle, heading };
    }
    try {sections.hiddenRegions.toggle.checked=localStorage.getItem(fogPreferenceKey)!=='off';}catch{}
    const tooltip=el('div',{id:'notebookTooltip',hidden:true});tooltip.setAttribute('role','tooltip');document.body.append(tooltip);let tooltipOwner;
    function closeTooltip(){tooltip.hidden=true;tooltipOwner?.setAttribute('aria-expanded','false');tooltipOwner?.removeAttribute('aria-describedby');tooltipOwner=null;}
    function showTooltip(anchor,message){
      if(tooltipOwner===anchor){closeTooltip();return;}closeTooltip();tooltipOwner=anchor;tooltip.textContent=message;tooltip.hidden=false;anchor.setAttribute('aria-expanded','true');anchor.setAttribute('aria-describedby',tooltip.id);
      const bounds=anchor.getBoundingClientRect(),width=tooltip.offsetWidth,height=tooltip.offsetHeight;
      tooltip.style.left=`${Math.max(8,Math.min(bounds.left+bounds.width/2-width/2,innerWidth-width-8))}px`;
      tooltip.style.top=`${Math.max(8,bounds.top-height-8>=8?bounds.top-height-8:Math.min(bounds.bottom+8,innerHeight-height-8))}px`;
    }
    const markerInfo=button('ⓘ',()=>showTooltip(markerInfo,getLanguage()==='zh'?'部分图标等待同步：管理员使用新版地图模组进入游戏后会自动同步。':'Some icons await sync: an admin must join the game with the updated map mod.'));
    markerInfo.className='notebook-info';markerInfo.hidden=true;markerInfo.setAttribute('aria-expanded','false');sections.myMarkers.heading.append(markerInfo);
    const progressBox = el('div'); progressBox.id = 'notebookProgress'; document.querySelector('#sidebar .tools').before(progressBox);
    const progressTrack=el('div',{className:'notebook-progress-track'});progressTrack.setAttribute('role','group');progressBox.append(progressTrack);
    progressTrack.addEventListener('click',event=>{if(event.target===progressTrack&&progressTrack.dataset.empty==='true')showTooltip(progressTrack,progressDetail('pending'));});
    progressTrack.addEventListener('keydown',event=>{if(progressTrack.dataset.empty==='true'&&(event.key==='Enter'||event.key===' ')){event.preventDefault();showTooltip(progressTrack,progressDetail('pending'));}});
    const progressSegments={};for(const key of ['failed','pending','completed']){const segment=button('',()=>showTooltip(segment,progressDetail(key)));segment.className=`notebook-progress-segment ${key}`;segment.setAttribute('aria-expanded','false');progressSegments[key]=segment;progressTrack.append(segment);}
    const planButton = button('', () => startRoute()); planButton.id = 'planRoute'; document.querySelector('#sidebar .tools').prepend(planButton);
    const toolbar = el('div', { hidden:true }); toolbar.id = 'notebookToolbar'; toolbar.setAttribute('aria-live','polite'); document.body.append(toolbar);
    const noticeBox = el('div', { hidden:true }); noticeBox.id = 'notebookNotice'; noticeBox.setAttribute('role','status'); document.body.append(noticeBox);
    const modal = el('div', { className:'modal-backdrop', hidden:true }); modal.id = 'notebookModal'; document.body.append(modal);
    const contextMenu = document.querySelector('#contextMenu'), contextButtons = {};
    for (const [key, handler] of Object.entries({addGameMarker:()=>markerDialog(),shareMarker:()=>shareMarker(contextMarker),editMarker:()=>markerDialog(contextMarker),deleteMarker:()=>removeMarker(contextMarker),sharePoint:sharePoint,shareRoute:() => shareRoute(contextRoute),editRoute:() => startRoute(contextRoute),deleteRoute:() => removeRoute(contextRoute),hideRegion:selectRegion,editRegion:() => regionDialog(contextRegion),removeRegion:() => removeRegion(contextRegion)})) {
      const b = button('', () => { contextMenu.classList.remove('show'); handler(); }); contextMenu.append(b); contextButtons[key] = b;
    }
    function notice(message) { clearTimeout(noticeTimer); noticeBox.textContent = message; noticeBox.hidden = false; noticeTimer = setTimeout(() => { noticeBox.hidden = true; }, 6000); }
    async function request(path, body, method = body === undefined ? 'GET' : 'POST') {
      const controller=new AbortController(),timeout=setTimeout(()=>controller.abort(),15000);
      try {
        const response = await fetch(api + path, { method, signal:controller.signal, cache:'no-store', headers:body === undefined && method === 'GET' ? {} : {'Content-Type':'application/json','X-ServerMap-Request':'1'}, body:body === undefined ? undefined : JSON.stringify(body) });
        if (!response.ok) { const error = new Error(`${text('error')} (${response.status})`); error.status = response.status; throw error; }
        return await response.json();
      } finally {clearTimeout(timeout);}
    }
    const safe = action => Promise.resolve().then(action).catch(error => notice(error.message || text('error')));
    async function copyLink(url) {
      try { await navigator.clipboard.writeText(url); }
      catch {
        const input = el('textarea', { value:url }); input.style.cssText = 'position:fixed;opacity:0;'; document.body.append(input); input.select();
        const copied = document.execCommand('copy'); input.remove(); if (!copied) { notice(text('copyFailed')); return; }
      }
      notice(text('copied'));
    }
    function shareUrl(point) {
      const metadata = getMetadata(), url = new URL(location.pathname, location.origin);
      url.searchParams.set('x', Math.round(point.x - metadata.spawn.x)); url.searchParams.set('z', Math.round(point.z - metadata.spawn.z));
      url.searchParams.set('zoom', metadata.maxZoom - map.getZoom()); url.searchParams.set('renderer', new URL(location.href).searchParams.get('renderer') || 'basic'); return url;
    }
    function sharePoint(point) { if (!getMetadata()) return; const url = shareUrl(point || contextPosition || gamePoint(map.getCenter())); url.searchParams.set('point','1'); safe(() => copyLink(url.href)); }
    function shareRoute(route) {
      if (!route) return;
      safe(async () => {
        if (route!==sharedRoute&&!getAuth().authenticated) return;
        let id;
        if (route === sharedRoute) id = sharedId;
        else { const result = await request('/route-shares', { id:route.id }); id = result.id; }
        const url = shareUrl({ x:route.points[0][0], z:route.points[0][1] }); url.searchParams.set('route',id); await copyLink(url.href);
      });
    }
    function waypointIcon(marker, className) {
      const icon = el('span', { className }); icon.style.backgroundColor = /^#[0-9a-f]{6}$/i.test(marker.color) ? marker.color : '#ffffff';
      if (marker.iconAvailable) { const url = `url("${api}/waypoint-icons/${encodeURIComponent(marker.icon)}")`; icon.style.maskImage = url; icon.style.webkitMaskImage = url; }
      else { icon.classList.add('notebook-icon-fallback'); icon.title = `${text('waypointUnavailable')}: ${marker.icon}`; }
      return icon;
    }
    function renderMarkers() {
      markerGroup.clearLayers(); const section = sections.myMarkers;
      section.section.hidden=!getAuth().authenticated;markerInfo.hidden=!getAuth().authenticated||!markers.some(marker=>!marker.iconAvailable);markerInfo.setAttribute('aria-label',text('waypointUnavailable'));
      if(markerInfo.hidden&&tooltipOwner===markerInfo)closeTooltip();
      if (!getAuth().authenticated||!section.toggle.checked) return;
      for (const marker of markers) {
        addMarker(marker,markerGroup);
      }
    }
    function addMarker(marker,group,shared=false){
      const icon=L.divIcon({className:'',html:waypointIcon(marker,'notebook-waypoint'),iconSize:[20,20],iconAnchor:[10,10]});
      const popup=el('div',{className:'notebook-popup'}),origin=getMetadata().spawn,actions=el('div',{className:'notebook-popup-actions'});
      popup.append(el('b',{textContent:marker.name}),el('p',{textContent:`X ${Math.round(marker.x-origin.x)}, Y ${Math.round(marker.y)}, Z ${Math.round(marker.z-origin.z)}`}),el('p',{textContent:marker.text||''}),actions);
      actions.append(routeIconButton('shareMarker',()=>shareMarker(marker)));
      if(getAuth().authenticated){if(shared)actions.append(routeIconButton('saveMarkerShare',importMarker));else actions.append(routeIconButton('editMarker',()=>markerDialog(marker)),routeIconButton('deleteMarker',()=>removeMarker(marker)));}
      const layer=L.marker(gameLatLng(marker.x,marker.z),{icon,title:marker.name,notebookMarkerId:marker.id,notebookShared:shared}).bindPopup(popup).addTo(group);
      layer.on('contextmenu',event=>{if(event.originalEvent)L.DomEvent.stop(event.originalEvent);map.fire('contextmenu',{...event,notebookMarker:marker});});
    }
    function shareMarker(marker){
      if(!marker)return;
      safe(async()=>{const id=marker===sharedMarker?sharedMarkerId:(await request('/waypoint-shares',{id:marker.id})).id;const url=shareUrl(marker);url.searchParams.set('waypoint',id);await copyLink(url.href);});
    }
    function removeMarker(marker){
      if(!marker||!getAuth().authenticated||!confirm(text('deleteMarkerConfirm')))return;
      safe(async()=>{await request(`/my-waypoints?id=${encodeURIComponent(marker.id)}`,undefined,'DELETE');map.closePopup();await refreshPrivate();});
    }
    function importMarker(){
      if(!sharedMarkerId||!getAuth().authenticated||writing)return;
      writing=true;const epoch=authEpoch;
      safe(async()=>{await request('/my-waypoints',{shareId:sharedMarkerId});if(epoch!==authEpoch)return;sharedMarker=null;sharedMarkerId=null;sharedMarkerGroup.clearLayers();const url=new URL(location.href);url.searchParams.delete('waypoint');history.replaceState(null,'',url);sections.myMarkers.toggle.checked=true;await refreshPrivate();toolbar.hidden=true;notice(text('markerSaved'));}).finally(()=>{writing=false;});
    }
    function markerDialog(marker){
      if(!getAuth().authenticated)return;
      const point=marker||contextPosition||gamePoint(map.getCenter()),epoch=authEpoch;
      safe(async()=>{
        const choices=await request('/waypoint-options');if(epoch!==authEpoch)return;if(!choices.enabled)throw Error(text('mapDisabled'));
        const origin=getMetadata().spawn;let icon=marker?.icon||'circle',color=marker?.color||choices.colors[0]||'#ffffff';
        dialog(text(marker?'editMarker':'addGameMarker'),[
          {name:'name',label:text('markerName'),value:marker?.name||''},
          {name:'pinned',label:text('pinned'),type:'checkbox',value:!!marker?.pinned},
          {name:'suggest',label:text('suggestNames'),type:'checkbox',value:true},
          {name:'color',label:text('color'),type:'color',value:color},
          {name:'text',label:text('markerText'),value:marker?.text||'',optional:true,maxLength:1024},
          {name:'x',label:'X',type:'number',value:point.x-origin.x},
          {name:'y',label:'Y',type:'number',value:marker?.y??0},
          {name:'z',label:'Z',type:'number',value:point.z-origin.z}
        ],'',async value=>{
          if(!value.name.trim())throw Error(text('markerName'));
          const epoch=authEpoch;await request('/my-waypoints',{id:marker?.id,name:value.name.trim(),text:value.text,icon,color:value.color,pinned:value.pinned,x:value.x+origin.x,y:value.y,z:value.z+origin.z});
          if(epoch!==authEpoch)return;sections.myMarkers.toggle.checked=true;await refreshPrivate();notice(text('markerSaved'));map.closePopup();
        });
        const form=modal.querySelector('form'),nameInput=form.elements.name,colorInput=form.elements.color;
        const coordinates=el('div',{className:'notebook-coordinate-fields'});form.elements.x.parentElement.before(coordinates);for(const axis of ['x','y','z'])coordinates.append(form.elements[axis].parentElement);
        const suggestions=el('datalist',{id:'waypointNameSuggestions'});for(const name of [...new Set(markers.map(m=>m.name))])suggestions.append(el('option',{value:name}));form.append(suggestions);
        const setSuggestions=()=>{if(form.elements.suggest.checked)nameInput.setAttribute('list',suggestions.id);else nameInput.removeAttribute('list');};form.elements.suggest.onchange=setSuggestions;setSuggestions();
        const colors=el('div',{className:'notebook-color-picker'});colors.setAttribute('aria-label',text('color'));
        for(const value of choices.colors){const b=button('',()=>{colorInput.value=value;updateIcons();});b.style.backgroundColor=value;b.title=value;b.setAttribute('aria-label',value);colors.append(b);}colorInput.parentElement.after(colors);
        const icons=el('div',{className:'notebook-icon-picker'});icons.setAttribute('role','group');icons.setAttribute('aria-label',text('icon'));
        const available=[...choices.icons];if(!available.some(i=>i.name===icon))available.push({name:icon,available:marker?.iconAvailable});
        const updateIcons=()=>{for(const b of icons.children){b.setAttribute('aria-pressed',String(b.dataset.name===icon));b.firstChild.style.backgroundColor=colorInput.value;}};
        for(const entry of available){const b=button('',()=>{icon=entry.name;updateIcons();if(form.elements.suggest.checked&&!nameInput.value){const suggestion=markers.find(m=>m.icon===icon);if(suggestion)nameInput.value=suggestion.name;}});b.dataset.name=entry.name;b.title=entry.name;b.setAttribute('aria-label',entry.name);b.append(waypointIcon({icon:entry.name,iconAvailable:entry.available,color},'notebook-waypoint'));icons.append(b);}
        colorInput.oninput=updateIcons;updateIcons();form.querySelector('.modal-actions').before(el('label',{textContent:text('icon')}),icons);
        if(!marker){const yInput=form.elements.y;let edited=false;yInput.oninput=()=>{edited=true;};request(`/height?x=${point.x}&z=${point.z}`).then(h=>{if(!edited&&form.isConnected&&Number.isFinite(h.y))yInput.value=Math.max(0,h.y);}).catch(()=>{});}
      });
    }
    function addRoute(route, group, shared = false) {
      const latlngs = route.points.map(p => gameLatLng(p[0],p[1]));
      const line = L.polyline(latlngs, { className:'notebook-route', notebookRouteId:route.id, notebookShared:shared, color:route.color||'#ffd000', weight:4, bubblingMouseEvents:false }).addTo(group);
      const popup = el('div',{className:'notebook-popup'}),actions=el('div',{className:'notebook-popup-actions'});popup.append(el('b',{textContent:route.name}),el('p',{textContent:`${route.points.length} ${text('points')}`}),actions);
      actions.append(routeIconButton('shareRoute',()=>shareRoute(route)));
      if (getAuth().authenticated&&!shared) actions.append(routeIconButton('editRoute',()=>startRoute(route)),routeIconButton('deleteRoute',()=>removeRoute(route)));
      else if(getAuth().authenticated) actions.append(routeIconButton('saveShared',importShared));
      line.bindPopup(popup); line.on('contextmenu', event => { if(event.originalEvent)L.DomEvent.stop(event.originalEvent); map.fire('contextmenu',{...event,notebookRoute:route}); });
      for (const p of [latlngs[0],latlngs.at(-1)]) L.circleMarker(p,{radius:5,color:route.color||'#ffd000',fillOpacity:1,interactive:false}).addTo(group);
    }
    function renderRoutes() {
      routeGroup.clearLayers();sharedGroup.clearLayers();sections.myRoutes.section.hidden=!getAuth().authenticated;
      if(getAuth().authenticated&&sections.myRoutes.toggle.checked)
        for(const route of routes)if(mode!=='route'||route.id!==editingId)addRoute(route,routeGroup);
      if(sharedRoute)addRoute(sharedRoute,sharedGroup,true);
    }
    function cancelMode() {
      mode=null;draft=[];redoDraft=[];editingId=null;selectionStart=null;draftGroup.clearLayers();selectionRect=null;preview=null;planButton.classList.remove('active');toolbar.hidden=true;
      map.dragging.enable();map.doubleClickZoom.enable();map.getContainer().style.cursor='';
      renderRoutes();
    }
    function startRoute(route) {
      if (!getAuth().authenticated) { document.querySelector('#loginButton').click(); return; }
      if (!route) {
        dialog(text('plan'),[{name:'name',label:text('routeName'),value:''},{name:'color',label:text('color'),type:'color',value:'#ffd000'}],'',async value=>{
          if (!value.name.trim()) throw new Error(text('routeName'));
          beginRoute({name:value.name.trim(),color:value.color,points:[]});
        },getLanguage()==='zh'?'开始规划':'Start planning');
        return;
      }
      beginRoute(route);
    }
    function beginRoute(route) {
      if (!getAuth().authenticated) return;
      cancelMode();cancelMeasurement();closePanels();map.closePopup();mode='route';draft=route?.points.map(p=>p.slice())||[];editingId=route?.id||null;selectedRoute=route||null;
      renderRoutes();planButton.classList.add('active');map.doubleClickZoom.disable();renderDraft();renderToolbar();
    }
    function renderDraft() {
      draftGroup.clearLayers();preview=null;const points=draft.map(p=>gameLatLng(p[0],p[1]));
      if(points.length) {L.polyline(points,{color:selectedRoute?.color||'#ffd000',weight:3,interactive:false}).addTo(draftGroup);for(const p of points)L.circleMarker(p,{radius:4,color:'#fff',fillColor:'#ffd000',fillOpacity:1,interactive:false}).addTo(draftGroup);}
    }
    function renderToolbar() {
      toolbar.replaceChildren();toolbar.hidden=false;
      if(mode==='route') {
        const undo=button(text('undo'),()=>{if(!draft.length)return;redoDraft.push(draft.pop());renderDraft();renderToolbar();});undo.disabled=!draft.length;
        const redo=button(text('redo'),()=>{if(!redoDraft.length||draft.length>=512)return;draft.push(redoDraft.pop());renderDraft();renderToolbar();});redo.disabled=!redoDraft.length||draft.length>=512;
        toolbar.append(el('div',{className:'notebook-help',textContent:`${selectedRoute?.name || text('plan')} · ${text('routeHelp')} (${draft.length}/512)`}),undo,redo,button(text('finish'),finishRoute),button(text('cancel'),cancelMode));
      }
      else if(mode==='region') toolbar.append(el('div',{className:'notebook-help',textContent:text('regionHelp')}),button(text('cancel'),cancelMode));
      else if(sharedMarker){toolbar.append(el('div',{className:'notebook-help',textContent:`${text('sharedMarker')}: ${sharedMarker.name}`}));if(getAuth().authenticated)toolbar.append(button(text('saveMarkerShare'),importMarker));toolbar.append(button(text('shareMarker'),()=>shareMarker(sharedMarker)),button(text('close'),()=>{toolbar.hidden=true;}));}
      else if(sharedRoute) {toolbar.append(el('div',{className:'notebook-help',textContent:`${text('sharedRoute')}: ${sharedRoute.name}`}));if(getAuth().authenticated)toolbar.append(button(text('saveShared'),importShared));toolbar.append(button(text('shareRoute'),()=>shareRoute(sharedRoute)),button(text('close'),()=>{toolbar.hidden=true;}));}
      else toolbar.hidden=true;
      const actions=el('div',{className:'notebook-toolbar-actions'});actions.append(...toolbar.querySelectorAll(':scope > button'));if(actions.childElementCount)toolbar.append(actions);
    }
    function dialog(title, fields, description, submit, submitLabel = text('save')) {
      const form=el('form',{className:'modal'}),error=el('div',{className:'error'});form.append(el('h2',{textContent:title}));if(description)form.append(el('p',{textContent:description}));
      const inputs={};for(const field of fields){const label=el('label',{textContent:field.label}),input=el('input',{type:field.type||'text',value:field.value??'',required:field.type!=='checkbox'&&!field.optional});if(field.type==='checkbox')input.checked=!!field.value;input.name=field.name;input.setAttribute('aria-label',field.label);if(field.type==='number'){input.step='any';input.min='-32000000';input.max='32000000';}else if(field.type!=='color')input.maxLength=field.maxLength||80;label.append(input);form.append(label);inputs[field.name]=input;}
      const actions=el('div',{className:'modal-actions'}),save=el('button',{type:'submit',textContent:submitLabel});actions.append(button(text('cancel'),()=>{modal.hidden=true;}),save);form.append(error,actions);modal.replaceChildren(form);modal.hidden=false;form.querySelector('input')?.focus();
      form.onsubmit=async event=>{event.preventDefault();if(writing)return;writing=true;save.disabled=true;const epoch=authEpoch;try{await submit(Object.fromEntries(Object.entries(inputs).map(([key,input])=>[key,input.type==='checkbox'?input.checked:input.type==='number'?input.valueAsNumber:input.value])));if(epoch===authEpoch)modal.hidden=true;}catch(ex){error.textContent=ex.message||text('error');}finally{writing=false;save.disabled=false;}};
    }
    function finishRoute() {
      if(draft.length<2){notice(text('routeMin'));return;}
      const points=draft.map(p=>p.slice()),id=editingId;
      dialog(text('plan'),[{name:'name',label:text('routeName'),value:selectedRoute?.name||text('plan')},{name:'color',label:text('color'),type:'color',value:selectedRoute?.color||'#ffd000'}],text('shareWarning'),async value=>{
        const epoch=authEpoch,route=await request('/routes',{...value,id,points});if(epoch!==authEpoch)return;cancelMode();selectedRoute=route;sections.myRoutes.toggle.checked=true;await refreshPrivate();renderRoutes();notice(text('routeSaved'));
      });
    }
    function removeRoute(route) {if(!route||!confirm(text('confirmDelete')))return;safe(async()=>{await request(`/routes?id=${encodeURIComponent(route.id)}`,undefined,'DELETE');if(selectedRoute?.id===route.id)selectedRoute=null;await refreshPrivate();});}
    function importShared() {
      if(!sharedRoute||!sharedId||writing)return;if(!getAuth().authenticated){document.querySelector('#loginButton').click();return;}
      writing=true;const epoch=authEpoch;safe(async()=>{const route=await request('/routes',{shareId:sharedId});if(epoch!==authEpoch)return;selectedRoute=route;sharedRoute=null;sharedId=null;sections.myRoutes.toggle.checked=true;const url=new URL(location.href);url.searchParams.delete('route');history.replaceState(null,'',url);toolbar.hidden=true;await refreshPrivate();renderRoutes();notice(text('routeSaved'));}).finally(()=>{writing=false;});
    }
    function addRegionLabel(region) {
      const width=region.maxX-region.minX,height=region.maxZ-region.minZ;
      if(!(width>0&&height>0))return;
      const ns='http://www.w3.org/2000/svg',svg=document.createElementNS(ns,'svg'),group=document.createElementNS(ns,'g'),label=document.createElementNS(ns,'text');
      svg.setAttribute('viewBox',`0 0 ${width} ${height}`);svg.setAttribute('preserveAspectRatio','none');
      svg.setAttribute('role','img');svg.dataset.regionId=region.id;
      const title=getAuth().admin&&region.name?region.name:text('hiddenRegions');svg.setAttribute('aria-label',title);
      // Bottom-left to top-right, in world units. Leaflet scales the clipped
      // SVG, including font and outline. No screen-space minimum font size.
      const angle=-Math.atan2(height,width),cos=Math.abs(Math.cos(angle)),sin=Math.abs(Math.sin(angle));
      group.setAttribute('transform',`translate(${width/2} ${height/2}) rotate(${angle*180/Math.PI})`);
      label.textContent=title;label.setAttribute('text-anchor','middle');label.setAttribute('font-size','100');label.setAttribute('y','0');
      group.append(label);svg.append(group);
      L.svgOverlay(svg,[gameLatLng(region.minX,region.minZ),gameLatLng(region.maxX,region.maxZ)],{pane:'notebookFog',className:'notebook-region-label',interactive:false}).addTo(fogGroup);
      const box=label.getBBox(),textWidth=Math.max(1,box.width),textHeight=Math.max(1,box.height);
      const scale=.82*Math.min(width/(textWidth*cos+textHeight*sin),height/(textWidth*sin+textHeight*cos));
      // Scale geometry instead of shrinking the font: browser minimum font
      // sizes must not make a small region label spill into adjacent terrain.
      label.setAttribute('stroke-width','2');
      label.setAttribute('transform',`scale(${scale}) translate(${-box.x-box.width/2} ${-box.y-box.height/2})`);
    }

    function renderFog() {
      fogGroup.clearLayers();const admin=getAuth().admin;map.getPane('notebookFog').style.pointerEvents=admin?'auto':'none';sections.hiddenRegions.section.hidden=!admin;
      for(const region of regions){
        if(admin&&!sections.hiddenRegions.toggle.checked)continue;
        const rectangle=L.rectangle([gameLatLng(region.minX,region.minZ),gameLatLng(region.maxX,region.maxZ)],{pane:'notebookFog',renderer:fogRenderer,className:'notebook-fog',stroke:false,fillOpacity:0,interactive:!!admin,bubblingMouseEvents:false}).addTo(fogGroup);
        addRegionLabel(region);
        if(admin){rectangle.on('contextmenu',event=>{if(event.originalEvent)L.DomEvent.stop(event.originalEvent);map.fire('contextmenu',{...event,notebookRegion:region});});const popup=el('div',{className:'notebook-popup'}),actions=el('div',{className:'notebook-popup-actions'});actions.append(routeIconButton('editRegion',()=>regionDialog(region)),routeIconButton('removeRegion',()=>removeRegion(region)));popup.append(el('b',{textContent:region.name}),actions);rectangle.bindPopup(popup);}
      }

    }
    function selectRegion() {
      if(!getAuth().admin)return;cancelMode();cancelMeasurement();closePanels();mode='region';selectionStart=contextPosition||gamePoint(map.getCenter());map.dragging.disable();map.doubleClickZoom.disable();map.getContainer().style.cursor='crosshair';renderToolbar();
    }
    function regionDialog(region) {
      if(!getAuth().admin||!region)return;
      const origin=getMetadata().spawn;
      dialog(text('editRegion'),[{name:'name',label:text('fogName'),value:region.name||text('hiddenRegions')},{name:'hideInGame',label:getLanguage()==='zh'?'是否游戏内隐藏':'Hide on in-game maps',type:'checkbox',value:!!region.hideInGame},...['minX','minZ','maxX','maxZ'].map(name=>({name,label:name,type:'number',value:region[name]-(name.endsWith('X')?origin.x:origin.z)}))],text('confirmFog'),async value=>{
        const payload={...value,id:region.id};for(const key of ['minX','minZ','maxX','maxZ'])payload[key]+=key.endsWith('X')?origin.x:origin.z;
        await request('/hidden-regions',payload);cancelMode();await privacyChanged();
      });
    }
    function removeRegion(region) {if(!region||!getAuth().admin||!confirm(text('confirmRemove')))return;safe(async()=>{await request(`/hidden-regions?id=${encodeURIComponent(region.id)}`,undefined,'DELETE');await privacyChanged();});}
    async function refreshPrivate() {
      if(!getAuth().authenticated||!ready)return;const epoch=authEpoch,visibility=privacyEpoch;
      const [nextMarkers,nextRoutes]=await Promise.all([request('/my-waypoints'),request('/routes')]);if(epoch!==authEpoch||visibility!==privacyEpoch)return;
      const signature=JSON.stringify(nextMarkers);if(signature!==markersSignature){markersSignature=signature;markers=nextMarkers;renderMarkers();}
      if(selectedRoute?.id)selectedRoute=nextRoutes.find(r=>r.id===selectedRoute.id)||null;
      if(JSON.stringify(routes)!==JSON.stringify(nextRoutes)){routes=nextRoutes;renderRoutes();}
    }
    function progressDetail(key){
      const value=lastProgress;if(!value)return text('noProgress');
      if(key==='failed')return `${text('failed')}: ${value.failed||0}`;
      if(key==='completed')return `${text('completed')}: ${value.completed||0}${value.lastCompletedAt?'\n'+text('latest')+': '+new Date(value.lastCompletedAt).toLocaleTimeString():''}`;
      return `${text(value.phase)}\n${text('queued')}: ${value.queued||0} · ${text('active')}: ${value.active||0}\n${text('retrying')}: ${value.retrying||0} · ${text('pendingSave')}: ${value.awaitingSave||0}\n${text('regions')}: ${value.regionsDiscovered||0}`;
    }
    function renderProgress(value) {
      lastProgress=value;progressTrack.setAttribute('aria-label',text('progress'));progressBox.dataset.phase=value?.phase||'unavailable';
      const count=key=>Math.max(0,Number(value?.[key])||0),pending=count('queued')+count('active')+count('retrying')+count('awaitingSave');
      const weights={failed:count('failed'),pending,completed:count('completed')};
      const visible=Object.keys(weights).filter(key=>weights[key]>0),isEmpty=visible.length===0;
      progressTrack.dataset.empty=String(isEmpty);progressTrack.tabIndex=isEmpty?0:-1;progressTrack.setAttribute('role',isEmpty?'button':'group');
      for(const [key,segment] of Object.entries(progressSegments)){
        segment.hidden=weights[key]===0;segment.style.flexGrow=weights[key];segment.classList.toggle('first-visible',key===visible[0]);segment.classList.toggle('last-visible',key===visible.at(-1));segment.setAttribute('aria-label',progressDetail(key));
        if(tooltipOwner===segment){if(segment.hidden)closeTooltip();else tooltip.textContent=progressDetail(key);}
      }
      if(tooltipOwner===progressTrack){if(!isEmpty)closeTooltip();else tooltip.textContent=progressDetail('pending');}
    }
    async function refreshFog() {
      const epoch=authEpoch,next=await request('/hidden-regions');if(epoch!==authEpoch)return;
      const signature=JSON.stringify(next);if(signature!==fogSignature){
        privacyEpoch++;fogSignature=signature;regions=next;renderFog();map.closePopup();
        markerGroup.clearLayers();routeGroup.clearLayers();sharedRoute=null;sharedGroup.clearLayers();sharedMarker=null;sharedMarkerGroup.clearLayers();if(!mode)toolbar.hidden=true;markers=[];routes=[];markersSignature='';renderMarkers();renderRoutes();invalidatePrivacy();
        await refreshPrivate();if(sharedId)await loadShared(false);if(sharedMarkerId)await loadSharedMarker(false);
      }
    }
    async function privacyChanged() {
      routeGroup.clearLayers();sharedGroup.clearLayers();sharedMarkerGroup.clearLayers();markerGroup.clearLayers();markersSignature='';fogSignature='';
      await refreshFog();await refreshPrivate();if(sharedId)await loadShared(false);if(sharedMarkerId)await loadSharedMarker(false);
    }
    async function loadShared(fit=true) {
      if(!sharedId)return;const epoch=authEpoch,visibility=privacyEpoch;
      try {const route=await request(`/route-shares?id=${encodeURIComponent(sharedId)}`);if(epoch!==authEpoch||visibility!==privacyEpoch)return;sharedRoute=route;renderRoutes();if(!mode){if(fit)map.fitBounds(L.latLngBounds(route.points.map(p=>gameLatLng(...p))),{padding:[50,70],maxZoom:getMetadata().maxZoom,animate:false});renderToolbar();}}
      catch {if(epoch!==authEpoch||visibility!==privacyEpoch)return;sharedRoute=null;renderRoutes();if(!mode)toolbar.hidden=true;notice(text('unavailableShare'));}
    }
    async function loadSharedMarker(fit=true){
      if(!sharedMarkerId)return;const epoch=authEpoch,visibility=privacyEpoch;
      try{
        const marker=await request(`/waypoint-shares?id=${encodeURIComponent(sharedMarkerId)}`);if(epoch!==authEpoch||visibility!==privacyEpoch)return;
        const changed=JSON.stringify(marker)!==JSON.stringify(sharedMarker);
        if(changed||!sharedMarkerGroup.getLayers().length){sharedMarker=marker;sharedMarkerGroup.clearLayers();addMarker(marker,sharedMarkerGroup,true);}
        if(!mode){if(fit){map.setView(gameLatLng(marker.x,marker.z),getMetadata().maxZoom);sharedMarkerGroup.getLayers()[0]?.openPopup();}if(changed||fit)renderToolbar();}
      }catch{if(epoch!==authEpoch||visibility!==privacyEpoch)return;sharedMarker=null;sharedMarkerGroup.clearLayers();if(!mode)toolbar.hidden=true;if(fit)notice(text('unavailableShare'));}
    }
    async function poll() {
      if(!ready||document.hidden||requestBusy)return;requestBusy=true;
      try {
        const epoch=authEpoch;
        const current=await request('/auth/me');if(epoch!==authEpoch)return;
        const previous=getAuth();if(!!current.authenticated!==!!previous.authenticated||!!current.admin!==!!previous.admin||current.name!==previous.name){await options.reloadAuth();return;}
        await Promise.all([refreshPrivate().catch(error=>{if(error.status===401)options.reloadAuth();}),refreshFog().catch(()=>{}),request('/render-progress').then(value=>{lastProgress=value;renderProgress(value);}).catch(()=>renderProgress(null))]);
        if(sharedMarkerId)await loadSharedMarker(false);
      }
      catch {renderProgress(null);}
      finally {requestBusy=false;}
    }
    async function focusSearchResult(result) {
      if(!['waypoint','route','hidden-region'].includes(result.kind))return false;
      if(!getAuth().authenticated)return true;
      const epoch=authEpoch;
      await Promise.all([refreshPrivate(),refreshFog()]);
      if(epoch!==authEpoch)return true;
      cancelMode();cancelMeasurement();closePanels();
      if(result.kind==='waypoint'){
        const marker=markers.find(m=>m.id===result.id);if(!marker)return true;
        sections.myMarkers.toggle.checked=true;renderMarkers();map.setView(gameLatLng(marker.x,marker.z),getMetadata().maxZoom);
        markerGroup.getLayers().find(layer=>layer.options.notebookMarkerId===marker.id)?.openPopup();
      }else if(result.kind==='route'){
        const route=routes.find(r=>r.id===result.id);if(!route)return true;
        sections.myRoutes.toggle.checked=true;renderRoutes();
        const layer=routeGroup.getLayers().find(layer=>layer.options.notebookRouteId===route.id);
        if(layer){map.fitBounds(layer.getBounds(),{padding:[40,40],maxZoom:getMetadata().maxZoom});layer.openPopup();}
      }else if(getAuth().admin){
        const region=regions.find(r=>r.id===result.id);if(region)map.fitBounds([gameLatLng(region.minX,region.minZ),gameLatLng(region.maxX,region.maxZ)],{padding:[40,40],maxZoom:getMetadata().maxZoom});
      }
      return true;
    }
    function languageChanged() {
      for(const [key,section] of Object.entries(sections))section.title.textContent=text(key);
      planButton.textContent=text('plan');planButton.hidden=!getAuth().authenticated;contextButtons.addGameMarker.hidden=!getAuth().authenticated;for(const k of ['shareMarker','editMarker','deleteMarker'])contextButtons[k].hidden=true;document.querySelector('#locate').hidden=!getAuth().authenticated;for(const [key,b] of Object.entries(contextButtons))b.textContent=text(key);
      renderMarkers();renderRoutes();renderFog();renderProgress(lastProgress);if(mode||sharedRoute||sharedMarker)renderToolbar();
    }
    function authChanged() {
      authEpoch++;closeTooltip();markers=[];routes=[];markersSignature='';fogSignature='';selectedRoute=null;sharedRoute=null;sharedMarker=null;sharedMarkerGroup.clearLayers();routeGroup.clearLayers();sharedGroup.clearLayers();modal.hidden=true;cancelMode();languageChanged();
      if(ready){invalidatePrivacy();safe(privacyChanged);}
    }
    sections.myMarkers.toggle.onchange=renderMarkers;sections.myRoutes.toggle.onchange=renderRoutes;
    sections.hiddenRegions.toggle.onchange=()=>{if(!getAuth().admin)return;try{localStorage.setItem(fogPreferenceKey,sections.hiddenRegions.toggle.checked?'on':'off');}catch{}renderFog();invalidatePrivacy();};
    map.on('contextmenu',event=>{
      contextMarker=event.notebookMarker||null;contextPosition=gamePoint(event.latlng);contextRoute=event.notebookRoute||null;contextRegion=event.notebookRegion||(getAuth().admin?regions.find(r=>contextPosition.x>=r.minX&&contextPosition.x<=r.maxX&&contextPosition.z>=r.minZ&&contextPosition.z<=r.maxZ):null);
      contextButtons.shareRoute.hidden=!contextRoute;contextButtons.editRoute.hidden=!getAuth().authenticated||!contextRoute||contextRoute===sharedRoute;contextButtons.deleteRoute.hidden=!getAuth().authenticated||!contextRoute||contextRoute===sharedRoute;
      contextButtons.addGameMarker.hidden=!getAuth().authenticated;contextButtons.shareMarker.hidden=!contextMarker;contextButtons.editMarker.hidden=!getAuth().authenticated||!contextMarker||contextMarker===sharedMarker;contextButtons.deleteMarker.hidden=contextButtons.editMarker.hidden;contextButtons.hideRegion.hidden=!getAuth().admin;contextButtons.editRegion.hidden=!getAuth().admin||!contextRegion;contextButtons.removeRegion.hidden=!getAuth().admin||!contextRegion;
      // Newly added controls can be taller than the base context menu.
      contextMenu.style.top=`${Math.max(5,Math.min(parseFloat(contextMenu.style.top)||5,innerHeight-contextMenu.offsetHeight-5))}px`;
      contextMenu.style.left=`${Math.max(5,Math.min(parseFloat(contextMenu.style.left)||5,innerWidth-contextMenu.offsetWidth-5))}px`;
    });
    map.on('click',event=>{
      if(mode==='route'){if(draft.length>=512){notice(text('routeMax'));return;}const p=gamePoint(event.latlng);draft.push([Math.round(p.x),Math.round(p.z)]);redoDraft=[];renderDraft();renderToolbar();}
      else if(mode==='region'){const p=gamePoint(event.latlng),start=selectionStart;if(Math.abs(p.x-start.x)<1||Math.abs(p.z-start.z)<1)return;const region={name:'',minX:Math.floor(Math.min(start.x,p.x)),minZ:Math.floor(Math.min(start.z,p.z)),maxX:Math.ceil(Math.max(start.x,p.x)),maxZ:Math.ceil(Math.max(start.z,p.z))};cancelMode();regionDialog(region);}
    });
    map.on('mousemove',event=>{
      if(mode==='route'&&draft.length){const last=draft.at(-1);if(!preview)preview=L.polyline([],{color:'#ffd000',dashArray:'5 5',weight:2,interactive:false}).addTo(draftGroup);preview.setLatLngs([gameLatLng(...last),event.latlng]);}
      if(mode==='region'&&selectionStart){if(!selectionRect)selectionRect=L.rectangle([gameLatLng(selectionStart.x,selectionStart.z),event.latlng],{color:'#fff',fillColor:'#fff',fillOpacity:.5,interactive:false}).addTo(draftGroup);else selectionRect.setBounds([gameLatLng(selectionStart.x,selectionStart.z),event.latlng]);}
    });
    document.addEventListener('pointerdown',event=>{if(tooltipOwner&&!tooltip.contains(event.target)&&!tooltipOwner.contains(event.target))closeTooltip();});
    document.addEventListener('keydown',event=>{if(event.key==='Escape'){closeTooltip();modal.hidden=true;cancelMode();}});
    window.addEventListener('resize',closeTooltip);document.querySelector('#sidebar').addEventListener('scroll',closeTooltip);
    map.on('move zoom resize',closeTooltip);

    document.querySelector('#measure').addEventListener('click',()=>{if(mode)cancelMode();});
    document.addEventListener('visibilitychange',()=>{if(!document.hidden)poll();});
    setInterval(poll,5000);languageChanged();
    return { iconButton:routeIconButton,copyPointLink:sharePoint,hideRegions:()=>!getAuth().admin||sections.hiddenRegions.toggle.checked,focusSearchResult,authChanged,languageChanged,privacyChanged,ready:async()=>{ready=true;await poll();await loadShared();await loadSharedMarker();if(new URL(location.href).searchParams.get('point')==='1'){const p=gamePoint(map.getCenter()),origin=getMetadata().spawn;L.circleMarker(map.getCenter(),{radius:7,color:'#fff',fillColor:'#e66c75',fillOpacity:1}).bindPopup(`X ${Math.round(p.x-origin.x)}, Z ${Math.round(p.z-origin.z)}`).addTo(map).openPopup();}} };
  }
  window.ServerMapNotebook={create};
})();
