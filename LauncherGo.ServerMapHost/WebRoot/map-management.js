/* Moments and categorized administration. */
(() => {
  'use strict';
  const normalize = value => Array.isArray(value) ? value.map(normalize) : value && typeof value === 'object'
    ? Object.fromEntries(Object.entries(value).map(([key,v])=>[key[0].toLowerCase()+key.slice(1),normalize(v)])) : value;
  function create({api,map,gameLatLng,getAuth,getLanguage,getMetadata,refreshPois,closePanels,showSearch,invalidateFog}) {
    const $=id=>document.getElementById(id), text=(zh,en)=>getLanguage()==='zh'?zh:en;
    const node=(tag,props={})=>Object.assign(document.createElement(tag),props);
    const action=(label,fn)=>{const b=node('button',{type:'button',textContent:label});b.onclick=fn;return b;};
    const request=async(path,body,method=body===undefined?'GET':'POST')=>{
      const response=await fetch(api+path,{method,cache:'no-store',signal:AbortSignal.timeout(15000),headers:body===undefined&&method==='GET'?{}:{'Content-Type':'application/json','X-ServerMap-Request':'1'},body:body===undefined?undefined:JSON.stringify(body)});
      const result=await response.json();if(!response.ok){if(response.status===403&&path.startsWith('/admin/')){imageList.replaceChildren();$('manageModal').hidden=true;}throw Error(result.error||response.status);}return normalize(result);
    };
    const report=(target,fn)=>async()=>{target.textContent='';try{await fn();}catch(error){target.textContent=error.message;}};
    const iconButton=(id,label,paths,fn)=>{
      const b=action('',fn);b.id=id;b.title=label;b.setAttribute('aria-label',label);
      const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');
      for(const [k,v] of Object.entries({viewBox:'0 0 24 24',fill:'none',stroke:'currentColor','stroke-width':2,'stroke-linecap':'round','stroke-linejoin':'round','aria-hidden':true}))svg.setAttribute(k,v);
      for(const d of paths){const p=document.createElementNS(svg.namespaceURI,'path');p.setAttribute('d',d);svg.append(p);}b.append(svg);$('mapActions').insertBefore(b,$('languageButton')||$('language'));return b;
    };
    const dialog=(id,title)=>{
      const d=node('dialog',{id,className:'map-dialog'}),head=node('header'),heading=node('h2',{textContent:title}),close=action('×',e=>{e.stopPropagation();d.close();});
      close.className='dialog-close';close.setAttribute('aria-label',text('关闭','Close'));head.append(heading,close);
      d.body=node('div',{className:'map-dialog-body'});d.append(head,d.body);d.setAttribute('aria-label',title);document.body.append(d);
      const outside=e=>{const r=d.getBoundingClientRect();return e.target===d&&(e.clientX<r.left||e.clientX>r.right||e.clientY<r.top||e.clientY>r.bottom);};
      let backdropDown=false;d.addEventListener('pointerdown',e=>backdropDown=outside(e));
      d.addEventListener('click',e=>{if(backdropDown&&outside(e))d.close();backdropDown=false;});return d;
    };
    const languageSelect=$('language'),languageLabel=document.querySelector('label[for="language"]');languageLabel.remove();languageSelect.hidden=true;
    const languageMenu=node('div',{id:'languageMenu',hidden:true,role:'menu'});
    for(const option of languageSelect.options){const item=action(option.textContent,()=>{languageSelect.value=option.value;languageSelect.dispatchEvent(new Event('change',{bubbles:true}));languageMenu.hidden=true;languageButton.setAttribute('aria-expanded','false');});item.setAttribute('role','menuitem');languageMenu.append(item);}
    document.body.append(languageMenu);
    const languageButton=iconButton('languageButton',text('语言','Language'),['M4 5h7','M9 3v2','M5 5c0 4 2 7 6 9','M4 14c4 -2 6 -5 6 -9','M12 20l4 -9l4 9','M19.1 18h-6.2'],()=>{closePanels();const open=languageMenu.hidden;languageMenu.hidden=!open;languageButton.setAttribute('aria-expanded',String(open));if(open){const rect=languageButton.getBoundingClientRect();languageMenu.style.top=rect.bottom+5+'px';languageMenu.style.left=Math.max(8,rect.right-languageMenu.offsetWidth)+'px';}});
    languageButton.dataset.i18nLabel='languageName';languageButton.setAttribute('aria-haspopup','menu');languageButton.setAttribute('aria-controls','languageMenu');languageButton.setAttribute('aria-expanded','false');languageButton.querySelector('svg').setAttribute('class','icon icon-tabler icon-tabler-language');
    languageButton.addEventListener('click',event=>{event.preventDefault();event.stopPropagation();const open=languageMenu.hidden;languageMenu.hidden=!open;languageButton.setAttribute('aria-expanded',String(open));if(open){const rect=languageButton.getBoundingClientRect();languageMenu.style.display='block';languageMenu.style.top=`${rect.bottom+5}px`;languageMenu.style.left=`${Math.max(8,rect.right-180)}px`;}else languageMenu.style.display='none';});
    document.addEventListener('pointerdown',event=>{if(!languageMenu.hidden&&!languageMenu.contains(event.target)&&!languageButton.contains(event.target)){languageMenu.hidden=true;languageButton.setAttribute('aria-expanded','false');}});
    // Keep alliances inline with search, not in a modal or an online-player picker.
    const allies=node('section',{id:'alliesPanel',hidden:true}),alliesForm=node('form',{id:'alliesForm'}),alliesInput=node('input',{id:'allyMapId',type:'text',required:true,maxLength:64,autocomplete:'off',spellcheck:false});
    const alliesJoin=node('button',{id:'allyJoin',type:'submit'}),alliesStatus=node('p',{id:'alliesStatus',role:'status'}),alliesList=node('div',{className:'allies-list'}),ownIdRow=node('div',{className:'ally-own-id'}),ownId=node('input',{id:'ownMapId',readOnly:true}),copyId=action('',async()=>{
      if(!ownId.value)return;
      try{if(navigator.clipboard?.writeText)await navigator.clipboard.writeText(ownId.value);else{ownId.select();if(!document.execCommand('copy'))throw Error();}alliesStatus.textContent=text('地图 ID 已复制','Map ID copied');}
      catch{ownId.focus();ownId.select();alliesStatus.textContent=text('请复制选中的地图 ID','Copy the selected map ID');}
    });
    alliesForm.append(alliesInput,alliesJoin);ownIdRow.append(ownId,copyId);allies.append(alliesForm,ownIdRow,alliesStatus,alliesList);$('searchForm').after(allies);
    let alliesEpoch=0,alliesAuthEpoch=0,alliesBusy=false,alliesData=null;
    const alliesButton=iconButton('alliesButton',text('结盟','Allies'),['M16 21v-2a4 4 0 0 0 -4 -4H6a4 4 0 0 0 -4 4v2','M9 11a4 4 0 1 0 0 -8a4 4 0 0 0 0 8','M22 21v-2a4 4 0 0 0 -3 -3.87','M16 3.13a4 4 0 0 1 0 7.75'],event=>{
      event.stopPropagation();if(!getAuth().authenticated)return;if(!allies.hidden){closeAllies();return;}
      closePanels();showSearch?.();$('searchResults').replaceChildren();allies.hidden=false;alliesButton.setAttribute('aria-expanded','true');void loadAllies().then(()=>{if(!allies.hidden&&!alliesInput.disabled)alliesInput.focus();});
    });
    alliesButton.dataset.i18nLabel='allies';alliesButton.querySelector('svg').setAttribute('class','icon icon-tabler icon-tabler-users-group');
    alliesButton.setAttribute('aria-controls',allies.id);alliesButton.setAttribute('aria-expanded','false');
    function closeAllies(){allies.hidden=true;alliesEpoch++;alliesButton.setAttribute('aria-expanded','false');}
    function allyError(error){const zh={'Invalid player map ID':'请输入有效的玩家地图 ID','Player map ID not found':'没有找到此玩家地图 ID','Cannot add yourself':'不能加入自己','Player already joined':'该玩家已加入','Player request pending':'已发送申请，请等待对方允许','Player request not found':'没有找到待处理的结盟申请','Player alliance limit reached (64)':'自己或对方已达到 64 位盟友上限','Exploration sharing is disabled':'服务器未开启探索共享','Player is not joined':'该玩家已被移除'};return getLanguage()==='zh'?(zh[error.message]||error.message):error.message;}
    function updateAllyControls(){alliesJoin.disabled=alliesBusy||!alliesData?.enabled;alliesInput.disabled=alliesBusy||!alliesData?.enabled;for(const b of alliesList.querySelectorAll('button'))b.disabled=alliesBusy;copyId.disabled=!ownId.value;}
    function renderAllies(){
      allies.setAttribute('aria-label',text('地图探索盟友','Map exploration allies'));alliesInput.placeholder=text('玩家地图唯一 ID','Player map ID');alliesInput.setAttribute('aria-label',alliesInput.placeholder);
      alliesJoin.textContent=text('申请','Request');ownId.setAttribute('aria-label',text('自己的玩家地图唯一 ID','Your player map ID'));copyId.textContent=text('复制','Copy');
      ownId.value=alliesData?.mapId||'';alliesList.replaceChildren();
      if(alliesData){
        alliesStatus.textContent=alliesData.enabled?text('双方共享探索（添加盟友）','Shared exploration (add allies)'):text('服务器未开启探索共享；仍可删除已加入的玩家。','Exploration sharing is disabled. Joined players can still be removed.');
        for(const member of alliesData.members||[]){
          const item=node('div',{className:'ally-row'}),img=node('img',{alt:'',width:32,height:32,loading:'lazy'}),name=node('span',{className:'ally-name',textContent:member.name});
          img.src=member.avatar?.startsWith('api/v1/avatars/')?api.replace(/\/api\/v1\/?$/,'')+'/'+member.avatar:'assets/icons/player.svg';img.onerror=()=>{img.onerror=null;img.src='assets/icons/player.svg';};
          name.title=member.name;
          const pending=member.pending,control=pending==='incoming'?action(text('允许','Allow'),()=>changeAllies({action:'accept',uid:member.uid})):pending==='outgoing'?action(text('等待','Waiting'),()=>{}):action(text('删除','Remove'),()=>changeAllies({action:'remove',uid:member.uid}));
          control.dataset.pending=pending||'';
          if(pending==='outgoing')control.title=text('等待对方允许','Waiting for the other player to allow');
          control.setAttribute('aria-label',pending==='incoming'?text('允许 ','Allow ')+member.name:pending==='outgoing'?text('等待 ','Waiting for ')+member.name:text('删除 ','Remove ')+member.name);
          control.className=pending?'ally-pending-control':'';item.append(img,name,control);alliesList.append(item);
        }
        if(!alliesList.childElementCount)alliesList.append(node('p',{className:'ally-empty',textContent:text('暂无已加入的玩家','No players joined')}));
      }
      updateAllyControls();
    }
    async function loadAllies(){
      if(alliesBusy||allies.hidden||!getAuth().authenticated)return;
      const epoch=++alliesEpoch;if(!alliesData)alliesStatus.textContent=text('加载中…','Loading…');
      try{const data=await request('/allies');if(epoch!==alliesEpoch||!getAuth().authenticated)return;alliesData=data;renderAllies();}
      catch(error){if(epoch===alliesEpoch){alliesStatus.textContent=allyError(error);updateAllyControls();}}
    }
    async function changeAllies(body){
      if(alliesBusy||!getAuth().authenticated)return;
      const authEpoch=alliesAuthEpoch,epoch=++alliesEpoch;alliesBusy=true;updateAllyControls();
      try{
        const data=await request('/allies',body);if(authEpoch!==alliesAuthEpoch)return;
        if(epoch===alliesEpoch){alliesData=data;if(body.action==='add')alliesInput.value='';renderAllies();}
        if(body.action!=='add')invalidateFog?.();
      }catch(error){if(epoch===alliesEpoch)alliesStatus.textContent=allyError(error);}
      finally{if(authEpoch===alliesAuthEpoch){alliesBusy=false;updateAllyControls();}}
    }
    alliesForm.onsubmit=event=>{event.preventDefault();const mapId=alliesInput.value.trim();if(mapId)void changeAllies({action:'add',mapId});};
    $('searchInput').addEventListener('focus',closeAllies);
    document.addEventListener('keydown',event=>{if(event.key==='Escape'&&!allies.hidden){closeAllies();alliesButton.focus();}});
    setInterval(()=>{if(!document.hidden)void loadAllies();},5000);renderAllies();
    const spotlight=dialog('spotlightDialog',text('聚焦','Spotlight'));
    const spotlightSearch=node('input',{id:'spotlightSearch',type:'search',autocomplete:'off'});
    const spotlightSort=node('select',{id:'spotlightSort',className:'spotlight-sort'});
    spotlightSort.append(node('option',{value:'latest'}),node('option',{value:'likes'}));
    const spotlightTools=node('form',{className:'spotlight-tools'});
    const spotlightSubmit=node('button',{type:'submit'}),spotlightStatus=node('p',{className:'spotlight-status',role:'status'});
    const spotlightList=node('div',{className:'spotlight-list'}),spotlightMore=action('',()=>loadSpotlight(false));
    spotlightMore.className='spotlight-more';spotlightMore.hidden=true;
    spotlightTools.append(spotlightSearch,spotlightSubmit,spotlightSort);
    spotlight.querySelector('header').after(spotlightTools);
    spotlight.querySelector('.dialog-close').innerHTML='<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M6 6l12 12M18 6L6 18"/></svg>';
    spotlight.body.append(spotlightStatus,spotlightList,spotlightMore);
    const spotlightButton=iconButton('spotlightButton',text('聚焦','Spotlight'),['M3 12a9 9 0 1 0 18 0a9 9 0 1 0 -18 0','M3.6 15h10.55','M6.551 4.938l3.26 10.034','M17.032 4.636l-8.535 6.201','M20.559 14.51l-8.535 -6.201','M12.257 20.916l3.261 -10.034'],()=>{
      closePanels();spotlight.showModal();loadSpotlight();
    });
    spotlightButton.dataset.i18nLabel='spotlight';spotlightButton.setAttribute('aria-haspopup','dialog');spotlightButton.setAttribute('aria-controls','spotlightDialog');
    let spotlightEpoch=0,spotlightOffset=0,spotlightBusy=false,spotlightDebounce;
    const avatarUrl=value=>/^api\/v1\/avatars\/[a-f0-9]{64}\.png$/.test(value||'')?api.replace(/\/api\/v1\/?$/,'')+'/'+value:'assets/icons/player.svg';
    const formatMomentTime=value=>{
      const date=new Date(value);if(!value||!Number.isFinite(date.getTime()))return '';
      return date.toLocaleString(getLanguage()==='zh'?'zh-CN':getLanguage(),{year:'numeric',month:'short',day:'numeric',hour:'2-digit',minute:'2-digit'});
    };
    const heartSvg='<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M20.8 8.8c0 5.5-8.8 10.2-8.8 10.2S3.2 14.3 3.2 8.8A4.8 4.8 0 0 1 12 6.3a4.8 4.8 0 0 1 8.8 2.5Z"/></svg>';
    function spotlightLanguage(){
      const title=text('聚焦','Spotlight');
      spotlight.querySelector('header h2').textContent=title;spotlight.setAttribute('aria-label',title);
      spotlight.querySelector('.dialog-close').setAttribute('aria-label',text('关闭聚焦','Close Spotlight'));spotlight.querySelector('.dialog-close').title=text('关闭聚焦','Close Spotlight');
      spotlightButton.title=title;spotlightButton.setAttribute('aria-label',title);
      spotlightSearch.placeholder=text('搜索玩家、地点或描述','Search players, places or descriptions');
      spotlightSearch.setAttribute('aria-label',spotlightSearch.placeholder);
      spotlightSubmit.textContent=text('搜索','Search');spotlightSort.setAttribute('aria-label',text('排序','Sort'));
      spotlightSort.options[0].textContent=text('最新','Latest');spotlightSort.options[1].textContent=text('点赞','Likes');
      spotlightMore.textContent=text('加载更多','Load more');
    }
    function renderSpotlightCard(item){
      const card=node('article',{className:'spotlight-card'});
      card.dataset.id=item.id;
      const avatar=node('img',{className:'spotlight-avatar',alt:'',width:44,height:44,loading:'lazy',src:avatarUrl(item.avatar||item.playerAvatar)});
      avatar.onerror=()=>{avatar.onerror=null;avatar.src='assets/icons/player.svg';};
      const content=node('div',{className:'spotlight-content'}),identity=node('div',{className:'spotlight-identity'});
      const author=node('strong',{textContent:item.author||item.playerName||text('未知玩家','Unknown player')});
      const time=node('time',{textContent:formatMomentTime(item.updatedAt)});if(time.textContent)time.dateTime=item.updatedAt;
      identity.append(author,time);
      const title=node('h3',{className:'spotlight-title',textContent:item.title||item.name||''});
      const description=node('p',{className:'spotlight-description',textContent:item.description??item.text??''});
      content.append(identity,title);if(description.textContent)content.append(description);
      const image=action('',()=>ServerMapPoiImages.view(item)),img=node('img',{alt:item.title||item.name||'',loading:'lazy',decoding:'async'});
      image.className='spotlight-image';image.setAttribute('aria-label',text('查看图片','View image'));
      img.onerror=()=>{image.textContent=text('图片加载失败','Image failed to load');image.disabled=true;};
      img.src=ServerMapPoiImages.url(item.id,item.imageKey,0);image.append(img);content.append(image);
      const location=node('div',{className:'spotlight-location'}),pin=node('span',{className:'spotlight-location-icon'});
      pin.setAttribute('aria-hidden','true');location.append(pin,node('span',{textContent:item.location||item.name||text('地点标记','Place marker')}));
      const actions=node('div',{className:'spotlight-actions'}),feedback=node('span',{className:'spotlight-feedback',role:'status'});
      const count=node('span',{className:'spotlight-like-count'}),label=node('span'),like=action('',async()=>{
        if(!getAuth().authenticated){feedback.textContent=text('登录后即可点赞','Log in to like this post');return;}
        if(like.disabled)return;
        const epoch=spotlightEpoch;like.disabled=true;feedback.textContent='';
        try{
          const result=await request('/moments/like',{id:item.id,liked:!item.liked});
          if(epoch!==spotlightEpoch||!card.isConnected)return;
          item.liked=result.liked;item.likes=result.likes;updateLike();
        }catch(error){
          if(epoch===spotlightEpoch&&card.isConnected)feedback.textContent=text('点赞失败，请重试','Could not update the like. Please retry.');
        }finally{if(epoch===spotlightEpoch&&card.isConnected)like.disabled=false;}
      });
      like.className='spotlight-like';like.innerHTML=heartSvg;like.append(label,count);
      function updateLike(){
        like.classList.toggle('liked',!!item.liked);like.setAttribute('aria-pressed',String(!!item.liked));
        label.textContent=item.liked?text('已赞','Liked'):text('赞','Like');
        like.setAttribute('aria-label',item.liked?text('取消点赞','Unlike'):text('点赞','Like'));count.textContent=String(item.likes||0);
      }
      updateLike();actions.append(feedback,like);content.append(location,actions);card.append(avatar,content);return card;
    }
    async function loadSpotlight(reset=true){
      if(!spotlight.open||spotlightBusy&&!reset)return;
      const epoch=++spotlightEpoch;spotlightBusy=true;
      if(reset){spotlightOffset=0;spotlightList.replaceChildren();spotlight.body.scrollTop=0;spotlightMore.hidden=true;}
      if(!ServerMapPoiImages.isEnabled()){spotlightStatus.textContent=text('地点图片功能未启用','Place images are disabled');spotlightMore.hidden=true;spotlightBusy=false;spotlightList.setAttribute('aria-busy','false');return;}
      spotlightList.setAttribute('aria-busy','true');spotlightStatus.textContent=text('加载中…','Loading…');spotlightMore.disabled=true;
      try{
        const query=new URLSearchParams({q:spotlightSearch.value.trim(),sort:spotlightSort.value,skip:String(spotlightOffset)});
        const data=await request('/moments?'+query);
        if(epoch!==spotlightEpoch||!spotlight.open)return;
        const items=data.items||[],existing=new Set([...spotlightList.children].map(card=>card.dataset.id));
        for(const item of items)if(!existing.has(item.id))spotlightList.append(renderSpotlightCard(item));
        spotlightOffset+=items.length;spotlightMore.hidden=!items.length||spotlightOffset>=data.total;
        spotlightStatus.textContent=spotlightOffset?'':spotlightSearch.value.trim()?text('没有匹配的内容','No matching posts'):text('暂无地点图片','No place photos yet');
      }catch(error){
        if(epoch===spotlightEpoch){spotlightStatus.textContent=text('加载失败，请重试','Could not load posts. Please retry.');spotlightMore.hidden=false;}
      }finally{
        if(epoch===spotlightEpoch){spotlightBusy=false;spotlightMore.disabled=false;spotlightList.setAttribute('aria-busy','false');}
      }
    }
    spotlightTools.onsubmit=event=>{event.preventDefault();clearTimeout(spotlightDebounce);loadSpotlight();};
    spotlightSort.onchange=()=>{clearTimeout(spotlightDebounce);loadSpotlight();};
    spotlightSearch.oninput=()=>{clearTimeout(spotlightDebounce);spotlightDebounce=setTimeout(()=>loadSpotlight(),250);};
    spotlight.addEventListener('close',()=>{clearTimeout(spotlightDebounce);spotlightEpoch++;spotlightBusy=false;spotlightList.replaceChildren();});
    document.addEventListener('servermap-poi-images-changed',event=>{if(event.detail?.enabled){if(spotlight.open)loadSpotlight();}else{spotlightEpoch++;spotlightList.replaceChildren();spotlightStatus.textContent=text('地点图片功能未启用','Place images are disabled');spotlightMore.hidden=true;}});
    spotlightLanguage();
    // Move existing controls, preserving their listeners and the legacy save flow.
    const form=$('manageForm'),layout=node('div',{className:'management-layout'}),nav=node('nav',{className:'management-nav'}),content=node('div',{className:'management-content'}),sections={};
    nav.setAttribute('aria-label',text('管理分类','Administration categories'));layout.append(nav,content);form.prepend(layout);
    function section(id,label){const s=node('section',{hidden:true}),b=action(label,()=>selectSection(id));b.setAttribute('role','tab');b.setAttribute('aria-controls','management-'+id);s.id='management-'+id;s.setAttribute('role','tabpanel');nav.append(b);content.append(s);sections[id]={s,b};return s;}
    function selectSection(id){for(const [key,{s,b}] of Object.entries(sections)){s.hidden=key!==id;b.setAttribute('aria-selected',String(key===id));}if(id==='images')loadImages();}
    nav.setAttribute('role','tablist');
    const site=section('site',text('网站与公告','Website & news')),teleport=section('teleport',text('传送配置','Teleport')),uploads=section('uploads',text('图片上传配置','Image uploads')),images=section('images',text('图片管理','Manage images')),layerSettings=section('layers',text('图层与样式','Layers & styles')),quotas=section('quotas',text('玩家额度','Player quotas')),fogSettings=section('fog',text('战争迷雾','Fog of war'));
    function move(id,target){const input=$(id);if(!input)return;const wrapper=input.closest('label');if(wrapper){target.append(wrapper);return;}const label=form.querySelector('label[for="'+id+'"]');if(label)target.append(label);target.append(input);}
    for(const id of ['announcementInput','websiteInput','siteTitleInput','siteDescriptionInput','siteKeywordsInput','siteFaviconInput'])move(id,site);
    for(const id of ['mountedTeleportInput','playerGearTeleportInput','teleportItemCodeInput','teleportItemsPerJumpInput','teleportEffectsInput'])move(id,teleport);
    teleport.append($('teleportEffectInputs'));const hint=form.querySelector('[data-i18n="teleportEffectsHint"]');if(hint)teleport.append(hint);
    move('poiImagesEnabledInput',uploads);
    const imageTypesInput=node('input',{id:'imageTypes',type:'text',required:true,placeholder:'jpeg, png, webp, bmp'});
    uploads.append(node('label',{htmlFor:'imageTypes',textContent:text('允许上传的图片类型','Allowed image types')}),imageTypesInput,node('p',{className:'management-note',textContent:text('用逗号或空格分隔，例如 jpeg, png, webp, bmp；添加 gif 即允许 GIF。实际文件须能被服务器图片解码器识别。','Separate with commas or spaces, e.g. jpeg, png, webp, bmp; add gif to allow GIF. Files must be recognized by the server image decoder.')}));
    imageTypesInput.oninput=()=>imageTypesInput.setCustomValidity('');
    function numberField(target,id,label,min,max,value){const l=node('label',{htmlFor:id,textContent:label}),input=node('input',{id,type:'number',min:String(min),max:String(max),value:String(value),required:true});target.append(l,input);return input;}
    const maxMb=numberField(uploads,'imageMaxMb',text('上传大小上限（MB）','Upload limit (MB)'),1,50,10);
    uploads.append(node('p',{className:'management-note',textContent:text('去除隐私元数据，保留全分辨率展示图与动画，生成静态 WebP 缩略图。最长边 8192 像素、最多 3200 万像素；是否允许上传仅按配置类型判断。旧图片保留。','Privacy metadata is stripped; full-resolution images and animations are preserved with static WebP thumbnails. Maximum 8192 pixels per side / 32 MP. Allowed formats follow this list. Legacy images are preserved.')}));
    const poiQuota=numberField(quotas,'poiQuota',text('每位玩家的地点标记额度','Place markers per player'),0,10000,10),daily=numberField(quotas,'dailyTeleports',text('每日传送次数（0 = 无限）','Daily teleports (0 = unlimited)'),0,100000,0);
    quotas.append(node('p',{className:'management-note',textContent:text('按服务器本地日期统计；成功传送计次，管理员不计次，携带坐骑时每位普通玩家各计一次。降低标记额度不会删除旧标记。','Uses the server local date. Successful teleports count once per non-admin rider. Lowering a marker quota never deletes existing markers.')}));
    const fogEnabled=node('input',{id:'fogEnabled',type:'checkbox'}),shareExploration=node('input',{id:'shareExploration',type:'checkbox'}),adminsBypassFog=node('input',{id:'adminsBypassFog',type:'checkbox'});
    const fogEnabledLabel=node('label',{htmlFor:'fogEnabled',className:'setting-checkbox'}),shareLabel=node('label',{htmlFor:'shareExploration',className:'setting-checkbox'}),adminFogLabel=node('label',{htmlFor:'adminsBypassFog',className:'setting-checkbox'}),fogNote=node('p',{className:'management-note'});
    fogSettings.append(fogEnabledLabel,shareLabel,adminFogLabel,fogNote);
    function fogLanguage(){
      sections.fog.b.textContent=text('战争迷雾','Fog of war');
      fogEnabledLabel.replaceChildren(fogEnabled,document.createTextNode(text('启用战争迷雾','Enable fog of war')));
      shareLabel.replaceChildren(shareExploration,document.createTextNode(text('允许同阵营与盟友共享探索','Share exploration with faction members and allies')));
      adminFogLabel.replaceChildren(adminsBypassFog,document.createTextNode(text('管理员无视战争迷雾','Administrators bypass fog of war')));
      fogNote.textContent=text('开启后仅显示已探索的地图与区域标记，未登录用户无法查看。关闭后所有人不受迷雾限制，隐藏区域仍受保护。探索记录永久保存，重新开启不会丢失；开启共享后同阵营玩家和地图 ID 盟友共享探索。','When enabled, only explored terrain and area markers are shown; anonymous visitors see none. Disabling fog reveals the map to everyone but keeps hidden regions protected. Exploration is retained when fog is re-enabled. Sharing includes faction members and map-ID allies.');
    }
    fogLanguage();
    const layerRows={},table=node('table',{className:'management-grid'}),head=node('tr');
    for(const label of [text('图层','Layer'),text('默认','Default'),text('禁止','Block'),text('强制','Force'),text('倍率','Scale')])head.append(node('th',{textContent:label}));table.append(head);
    for(const [id,zh,en] of [['players','玩家','Players'],['mounts','坐骑','Mounts'],['spawn','出生点','Spawn'],['claims','领地文字','Claim labels'],['claim-areas','领地区域','Claim areas'],['chunks','已生成区域','Regions'],['translocators','传送器','Translocators'],['pois','地点标记','Places']]){
      const row=node('tr');row.append(node('td',{textContent:text(zh,en)}));const fields={};
      for(const key of ['defaultVisible','forbidden','forced','scale']){
        if(key==='scale'&&!['players','mounts','translocators'].includes(id)){row.append(node('td',{className:'no-layer-scale',textContent:'—'}));continue;}
        const cell=node('td'),input=node('input',{type:key==='scale'?'number':'checkbox'});input.setAttribute('aria-label',id+' '+key);
        cell.dataset.label={defaultVisible:text('默认','Default'),forbidden:text('禁止','Block'),forced:text('强制','Force'),scale:text('倍率','Scale')}[key];
        if(key==='scale'){input.min='.1';input.max='10';input.step='.1';input.value='1';input.required=true;}fields[key]=input;cell.append(input);row.append(cell);
      }
      fields.forbidden.onchange=()=>{if(fields.forbidden.checked)fields.forced.checked=false;};fields.forced.onchange=()=>{if(fields.forced.checked)fields.forbidden.checked=false;};
      layerRows[id]=fields;table.append(row);
    }
    layerSettings.append(table,node('p',{className:'management-note',textContent:text('默认勾选用于尚未保存个人偏好的图层；禁止与强制开启不能同时启用。倍率范围 0.1–10，默认 1。','Defaults apply when no personal preference exists. Blocked and forced cannot both be selected. Scale range: 0.1–10, default 1.')}));
    const imageSearch=node('input',{type:'search',placeholder:text('搜索添加者、地点标记名称、描述','Search author, place name, description')}),imageList=node('div'),imageStatus=node('p',{role:'status'}),imageMore=action(text('加载更多','Load more'),()=>loadImages(false));images.append(imageSearch,imageStatus,imageList,imageMore);
    let imageEpoch=0,imageOffset=0;
    async function loadImages(reset=true){
      const epoch=++imageEpoch;if(reset){imageOffset=0;imageList.replaceChildren();}imageStatus.textContent=text('加载中…','Loading…');
      try{
        const data=await request('/admin/images?q='+encodeURIComponent(imageSearch.value)+'&skip='+imageOffset);if(epoch!==imageEpoch||!getAuth().admin)return;
        for(const item of data.items||[]){
          const row=node('div',{className:'image-management-row'}),img=node('img',{src:ServerMapPoiImages.url(item.id,item.imageKey,480),alt:item.name,loading:'lazy'}),details=node('div',{className:'image-details'});
          details.append(node('strong',{textContent:item.name}),node('small',{textContent:item.author}),node('small',{textContent:item.text}));
          const remove=action(text('删除图片','Remove image'),report(imageStatus,async()=>{if(!confirm(text('仅移除此标记的图片，保留标记和可恢复的图片文件？','Remove the image association? Keep the marker and recoverable image files?')))return;await request('/admin/images?id='+encodeURIComponent(item.id)+'&key='+item.imageKey,undefined,'DELETE');await loadImages();refreshPois();}));
          row.append(img,details,remove);imageList.append(row);
        }
        imageOffset+=(data.items||[]).length;imageMore.hidden=imageOffset>=data.total;imageStatus.textContent=data.total?String(data.total):text('暂无图片','No images');
      }catch(error){if(epoch===imageEpoch)imageStatus.textContent=error.message;}
    }
    let imageDebounce;imageSearch.oninput=()=>{clearTimeout(imageDebounce);imageDebounce=setTimeout(()=>loadImages(),250);};
    form.addEventListener('invalid',event=>{const entry=Object.entries(sections).find(([,v])=>v.s.contains(event.target));if(entry)selectSection(entry[0]);},true);
    function settings(value={}){
      value=normalize(value);maxMb.value=value.imageMaxMb??10;poiQuota.value=value.poiQuota??10;daily.value=value.dailyTeleports??0;
      imageTypesInput.value=(value.imageTypes||['jpeg','png','webp','bmp']).join(', ');imageTypesInput.setCustomValidity('');
      fogEnabled.checked=value.fogEnabled??true;shareExploration.checked=!!value.shareExploration;adminsBypassFog.checked=value.adminsBypassFog??true;alliesButton.hidden=!getAuth().authenticated;
      for(const [id,fields] of Object.entries(layerRows)){const rule=value.layers?.[id]||{};fields.defaultVisible.checked=rule.defaultVisible??['players','mounts','spawn','pois'].includes(id);fields.forbidden.checked=!!rule.forbidden;fields.forced.checked=!!rule.forced;if(fields.scale)fields.scale.value=rule.scale??1;}
    }
    function payload(){const imageTypes=[...new Set(imageTypesInput.value.toLowerCase().split(/[\s,;，；]+/).filter(Boolean).map(t=>t.replace(/^\./,'').replace(/^jpg$/,'jpeg').replace(/^apng$/,'png')))];if(!imageTypes.length||imageTypes.length>32||imageTypes.some(t=>!/^[a-z][a-z0-9]{0,15}$/.test(t))){selectSection('uploads');const message=text('请输入图片类型名称，用逗号或空格分隔','Enter image format names separated with commas or spaces');imageTypesInput.setCustomValidity(message);imageTypesInput.reportValidity();throw Error(message);}return {imageTypes,imageMaxMb:Number(maxMb.value),poiQuota:Number(poiQuota.value),dailyTeleports:Number(daily.value),fogEnabled:fogEnabled.checked,shareExploration:shareExploration.checked,adminsBypassFog:adminsBypassFog.checked,layers:Object.fromEntries(Object.entries(layerRows).map(([id,r])=>[id,{defaultVisible:r.defaultVisible.checked,forbidden:r.forbidden.checked,forced:r.forced.checked,scale:r.scale?Number(r.scale.value):1}]))};}
    settings();selectSection('site');

    const tracking=dialog('trackDialog',text('玩家轨迹跟踪','Player tracking')),controls=node('div',{className:'tracking-controls'}),playerSelect=node('select',{id:'trackPlayer'}),duration=node('input',{id:'trackSeconds',type:'number',min:'1',max:'86400',value:'3600'}),trackStatus=node('p',{role:'status'}),historySearch=node('input',{type:'search',placeholder:text('搜索玩家或日期','Search player or date')}),history=node('div',{className:'track-history'});
    for(const [label,input] of [[text('在线玩家','Online player'),playerSelect],[text('最长跟踪时间（s）','Maximum duration (s)'),duration]]){const l=node('label',{textContent:label});l.append(input);controls.append(l);}
    let selectedTrack=null,activeId=null,trackEpoch=0,historyEpoch=0;
    const start=action(text('开始','Start'),report(trackStatus,async()=>{if(!duration.reportValidity()||!playerSelect.value)return;start.disabled=true;try{const result=await request('/admin/tracks',{action:'start',uid:playerSelect.value,seconds:Number(duration.value)});activeId=result.id;await refreshHistory();}finally{start.disabled=false;}}));
    const end=action(text('结束','Stop'),report(trackStatus,async()=>{if(!activeId)return;await request('/admin/tracks',{action:'stop',id:activeId});activeId=null;await refreshHistory();}));
    controls.append(start,end);tracking.body.append(controls,node('p',{className:'management-note',textContent:text('服务端每秒采样；最长 86400 s，最多同时跟踪 16 位玩家。离线、离开主世界或管理员权限失效时结束。连续停留至少 60 s（半径 2 格）会标出停留点。','Server sampling: once per second, up to 86400 s / 16 concurrent players. Stops on logout, dimension change or revoked admin rights. Stops lasting at least 60 s within two blocks are marked.')}),trackStatus,historySearch,history);
    const timeline=node('div',{id:'trackTimeline',hidden:true}),timelineHead=node('div',{className:'timeline-head'}),timeOutput=node('output'),mountLabel=node('label'),showMounts=node('input',{type:'checkbox',checked:true});
    mountLabel.append(showMounts,document.createTextNode(text(' 显示坐骑',' Show mount')));
    const traceGroup=L.layerGroup().addTo(map),cursorGroup=L.layerGroup().addTo(map),mountGroup=L.layerGroup().addTo(map);
    function clearTrack(){trackEpoch++;selectedTrack=null;axis.clear();timeline.hidden=true;traceGroup.clearLayers();cursorGroup.clearLayers();mountGroup.clearLayers();}
    const closeTimeline=action('×',clearTrack);closeTimeline.setAttribute('aria-label',text('关闭轨迹时间轴','Close track timeline'));
    timelineHead.append(timeOutput,mountLabel,closeTimeline);timeline.append(timelineHead);document.body.append(timeline);
    const axis=ServerMapTrackTimeline.create({container:timeline,onChange:renderCursor,getLanguage});
    let cursorSample=null;
    function renderCursor(){
      if(!selectedTrack?.samples?.length)return;const samples=selectedTrack.samples,target=axis.value;let lo=0,hi=samples.length-1;
      while(lo<hi){const mid=Math.ceil((lo+hi)/2);if(Date.parse(samples[mid].time)<=target)lo=mid;else hi=mid-1;}const sample=samples[lo];
      timeOutput.textContent=selectedTrack.playerName+' · '+new Date(target).toLocaleString();
      if(cursorSample===sample)return;cursorSample=sample;cursorGroup.clearLayers();mountGroup.clearLayers();
      const marker=L.marker(gameLatLng(sample.x,sample.z),{icon:L.icon({iconUrl:'assets/icons/player.svg',iconSize:[24,24],iconAnchor:[12,12]}),title:selectedTrack.playerName});marker.addTo(cursorGroup);
      if(showMounts.checked&&sample.mount){
        const m=sample.mount,feature={id:'0',type:'Feature',geometry:{type:'Point',coordinates:[m.x,m.z]},properties:{...m,directions:16}};
        const model=ServerMapMounts.create(feature,{map,api,gameLatLng,nativeZoom:()=>getMetadata()?.maxZoom??12,imageUrl:()=>api+'/admin/track-mount-image?id='+encodeURIComponent(selectedTrack.id)+'&key='+encodeURIComponent(m.imageKey)});
        if(model)model.addTo(mountGroup);
        else L.circleMarker(gameLatLng(m.x,m.z),{radius:8,color:'#d4ab72',fillOpacity:.6}).bindTooltip(node('span',{textContent:m.name})).addTo(mountGroup);
      }
    }
    showMounts.onchange=()=>{cursorSample=null;axis.showMounts(showMounts.checked);renderCursor();};
    function drawTrack(track,follow=true){
      selectedTrack=track;cursorSample=null;traceGroup.clearLayers();const samples=track.samples||[],stops=[],mounts=[],gaps=[];
      if(!samples.length){axis.clear();cursorGroup.clearLayers();mountGroup.clearLayers();timeline.hidden=true;return;}
      const points=samples.map(s=>gameLatLng(s.x,s.z));L.polyline(points,{color:'#8edcf1',weight:3}).addTo(traceGroup);
      // A stop requires >=60 seconds within two blocks and no sampling gap >5s.
      for(let startIndex=0,i=1;i<=samples.length;i++){
        const anchor=samples[startIndex],last=samples[i-1],current=samples[i];
        if(current&&Math.hypot(current.x-anchor.x,current.z-anchor.z)<=2&&Date.parse(current.time)-Date.parse(last.time)<=5000)continue;
        const seconds=(Date.parse(last.time)-Date.parse(anchor.time))/1000;
        if(seconds>=60){const label=text('停留 ','Stopped ')+Math.round(seconds)+' s · '+new Date(anchor.time).toLocaleString();stops.push({start:Date.parse(anchor.time),end:Date.parse(last.time),label});L.circleMarker(gameLatLng(anchor.x,anchor.z),{radius:7,color:'#f0cc78',fillOpacity:.8}).bindTooltip(node('span',{textContent:label})).addTo(traceGroup);}
        startIndex=i;
      }
      for(let i=0;i<samples.length-1;i++){const s=samples[i],from=Date.parse(s.time),to=Date.parse(samples[i+1].time);if(to-from>5000){gaps.push({start:from,end:to});continue;}if(s.mount){const last=mounts.at(-1);if(last?.end===from&&last.label===s.mount.name)last.end=to;else mounts.push({start:from,end:to,label:s.mount.name});}}
      timeline.hidden=false;axis.update({start:Date.parse(samples[0].time),end:Date.parse(samples.at(-1).time),stops,mounts,gaps},follow);
      if(follow)map.fitBounds(L.latLngBounds(points),{padding:[45,100],maxZoom:getMetadata()?.maxZoom??12});
    }
    async function openTrack(id){const epoch=++trackEpoch;const track=await request('/admin/tracks?id='+encodeURIComponent(id));if(epoch!==trackEpoch||!getAuth().admin)return;beforeTrack?.();drawTrack(track);tracking.close();closePanels();}
    // Editing tools share the bottom-center space. Leave history saved when switching tools.
    const toolObserver=new MutationObserver(()=>{if(selectedTrack&&['notebookToolbar','areaMarkerToolbar','routeInfo'].some(id=>$(id)&&!$(id).hidden))clearTrack();});
    for(const id of ['notebookToolbar','areaMarkerToolbar','routeInfo'])if($(id))toolObserver.observe($(id),{attributes:true,attributeFilter:['hidden']});
    async function refreshHistory(){
      const epoch=++historyEpoch;const rows=await request('/admin/tracks?q='+encodeURIComponent(historySearch.value));if(epoch!==historyEpoch||!getAuth().admin)return;history.replaceChildren();
      activeId=rows.find(r=>!r.ended&&r.playerUid===playerSelect.value)?.id||null;end.disabled=!activeId;
      for(const track of rows){const row=node('div',{className:'track-row'}),b=action(track.playerName+' · '+new Date(track.started).toLocaleString()+' · '+(track.ended?text('已结束','Ended'):text('跟踪中','Recording')),report(trackStatus,()=>openTrack(track.id)));b.className='track-open';row.append(b);if(!track.ended)row.append(action(text('结束','Stop'),report(trackStatus,async()=>{await request('/admin/tracks',{action:'stop',id:track.id});await refreshHistory();})));
        const remove=action(text('删除','Delete'),report(trackStatus,async()=>{if(!confirm(text('永久删除此轨迹记录？进行中的跟踪也会结束，此操作不可撤销。','Permanently delete this track? Active recording will also stop. This cannot be undone.')))return;await request('/admin/tracks?id='+encodeURIComponent(track.id),undefined,'DELETE');if(selectedTrack?.id===track.id)clearTrack();await refreshHistory();}));remove.className='track-delete';remove.setAttribute('aria-label',text('删除轨迹：','Delete track: ')+track.playerName);row.append(remove);history.append(row);}
    }
    let historyDebounce;historySearch.oninput=()=>{clearTimeout(historyDebounce);historyDebounce=setTimeout(report(trackStatus,refreshHistory),250);};playerSelect.onchange=report(trackStatus,refreshHistory);
    const trackingButton=iconButton('trackingButton',text('玩家轨迹跟踪','Player tracking'),['M3 5a2 2 0 1 0 4 0a2 2 0 1 0 -4 0','M7 5h9.5a3.5 3.5 0 0 1 0 7h-9a3.5 3.5 0 0 0 0 7h13.5','M18 16l3 3l-3 3'],report(trackStatus,async()=>{
      if(!getAuth().admin)return;closePanels();tracking.showModal();playerSelect.replaceChildren();
      const players=await request('/admin/online-players');if(!getAuth().admin)return;
      for(const p of players)playerSelect.append(node('option',{value:p.uid,textContent:p.name}));start.disabled=!players.length;await refreshHistory();
    }));
    trackingButton.hidden=true;
    let polling=false;
    setInterval(async()=>{
      if(polling||!getAuth().admin||document.hidden)return;polling=true;
      try{
        if(tracking.open)await refreshHistory();
        if(selectedTrack?.ended)await request('/admin/online-players'); // Recheck admin access without reloading a large history.
        else if(selectedTrack){const id=selectedTrack.id,epoch=trackEpoch,after=selectedTrack.samples?.at(-1)?.time,track=await request('/admin/tracks?id='+encodeURIComponent(id)+(after?'&after='+encodeURIComponent(after):''));if(epoch===trackEpoch&&selectedTrack?.id===id)drawTrack({...track,samples:[...selectedTrack.samples,...track.samples]},false);}
      }catch(error){trackStatus.textContent=error.message;}finally{polling=false;}
    },3000);
    function authChanged(){
      alliesAuthEpoch++;closeAllies();alliesBusy=false;alliesData=null;alliesInput.value='';ownId.value='';alliesStatus.textContent='';renderAllies();
      trackingButton.hidden=true;alliesButton.hidden=!getAuth().authenticated;imageEpoch++;historyEpoch++;trackEpoch++;spotlightEpoch++;
      if(spotlight.open)loadSpotlight();
      if(!getAuth().admin){tracking.close();clearTrack();history.replaceChildren();playerSelect.replaceChildren();imageList.replaceChildren();$('manageModal').hidden=true;}
    }
    function privacyChanged(){imageEpoch++;$('poiImageViewer').close();void loadAllies();if(spotlight.open)loadSpotlight();}
    function languageChanged(){
      renderAllies();fogLanguage();
      spotlightLanguage();
      for(const [b,zh,en] of [[trackingButton,'玩家轨迹跟踪','Player tracking'],[alliesButton,'结盟','Allies']]){b.title=text(zh,en);b.setAttribute('aria-label',b.title);}
    }
    return {settings,payload,authChanged,privacyChanged,languageChanged};
  }
  window.ServerMapManagement={create,normalize};
})();
