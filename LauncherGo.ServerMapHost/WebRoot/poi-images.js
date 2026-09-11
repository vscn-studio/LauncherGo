(() => {
  const $=id=>document.getElementById(id);let maxBytes=10*1024*1024,allowedTypes=['jpeg','png','webp','bmp'];
  let enabled=false,changed=false,selected=null,epoch=0,previewUrl=null,language=()=> 'zh',api='api/v1';
  const messages={zh:{poiImagesEnabled:'允许地点标记添加图片',poiImageAdd:'添加图片',poiImageRemove:'移除图片',poiImageHelp:'最大尺寸 8192 × 8192 px / 32 MP，最大文件 10 MB',poi_image_size:'图片最大为 10 MB。',poi_image_dimensions:'图片宽、高均不能超过 8192 px / 32 MP。',poi_image_format:'图片无效、无法解码，或实际格式不在管理员允许的类型列表中。',poi_images_disabled:'管理员已关闭地点图片功能。',poi_image_encode:'图片处理失败，请重试。',poiImageView:'点击放大图片',poiImageClose:'关闭图片',poiImageLoading:'正在检查图片…',poiImageFailed:'图片加载失败'},en:{poiImagesEnabled:'Allow images on place markers',poiImageAdd:'Add image',poiImageRemove:'Remove image',poiImageHelp:'Maximum dimensions 8192 × 8192 px / 32 MP, maximum file size 10 MB',poi_image_size:'Images must be at most 10 MB.',poi_image_dimensions:'Image width and height must not exceed 8192 px / 32 MP.',poi_image_format:'Invalid or undecodable image, or its actual format is not in the allowed list.',poi_images_disabled:'Place images are disabled by the administrator.',poi_image_encode:'Could not process the image. Please retry.',poiImageView:'Enlarge image',poiImageClose:'Close image',poiImageLoading:'Checking image…',poiImageFailed:'Image failed to load'}};
  const t=key=>(messages[language()]?.[key]||messages.en[key]||key).replaceAll('10 MB',`${maxBytes/1024/1024} MB`);
  const url=(id,key,size)=>`${api}/poi-image?id=${encodeURIComponent(id)}&key=${encodeURIComponent(key)}&size=${size}`;
  function clearPreview(){if(previewUrl)URL.revokeObjectURL(previewUrl);previewUrl=null;$('poiImagePreview').removeAttribute('src');$('poiImagePreview').hidden=true;$('poiImagePreviewFrame').hidden=true;}
  function reset(feature){epoch++;selected=null;changed=false;$('poiImageInput').value='';$('poiImageError').textContent='';clearPreview();const key=feature?.properties?.imageKey;if(enabled&&/^[a-f0-9]{32}$/.test(key||'')){$('poiImagePreview').src=url(feature.id,key,480);$('poiImagePreview').hidden=false;$('poiImagePreviewFrame').hidden=false;}$('poiImageRemove').hidden=$('poiImagePreview').hidden;}
  async function validate(file){
    if(!file.size||file.size>maxBytes)throw Error(t('poi_image_size'));
    const bytes=new Uint8Array(await file.slice(0,32).arrayBuffer()),tag=(at,n)=>String.fromCharCode(...bytes.slice(at,at+n));
    const type=tag(1,3)==='PNG'?'png':tag(0,4)==='RIFF'&&tag(8,4)==='WEBP'?'webp':bytes[0]===255&&bytes[1]===216?'jpeg':tag(0,2)==='BM'?'bmp':['GIF87a','GIF89a'].includes(tag(0,6))?'gif':null;
    // Unknown formats are checked by the server decoder, so extending the list needs no frontend change.
    if(type&&!allowedTypes.includes(type))throw Error(t('poi_image_format'));
    if(!type)return;
    let image;try{image=await createImageBitmap(file);}catch{throw Error(t('poi_image_format'));}
    try{if(image.width>8192||image.height>8192||image.width*image.height>32000000)throw Error(t('poi_image_dimensions'));}finally{image.close();}
  }
  function init(options){
    language=options.language;api=options.api;
    $('poiImageInput').addEventListener('change',async()=>{
      const file=$('poiImageInput').files[0],current=++epoch;selected=null;changed=false;clearPreview();$('poiImageRemove').hidden=true;if(!file){$('poiImageError').textContent='';return;}
      $('poiImageError').textContent=t('poiImageLoading');
      try{await validate(file);if(current!==epoch||!enabled)return;selected=file;changed=true;previewUrl=URL.createObjectURL(file);$('poiImagePreview').src=previewUrl;$('poiImagePreview').hidden=false;$('poiImagePreviewFrame').hidden=false;$('poiImageRemove').hidden=false;$('poiImageError').textContent='';}
      catch(error){if(current===epoch){$('poiImageInput').value='';$('poiImageError').textContent=error.message;}}
    });
    $('poiImageRemove').onclick=()=>{epoch++;selected=null;changed=true;$('poiImageInput').value='';$('poiImageError').textContent='';clearPreview();$('poiImageRemove').hidden=true;};
    $('poiImageViewerClose').onclick=()=>$('poiImageViewer').close();
    const viewer=$('poiImageViewer'),outside=e=>{const r=viewer.getBoundingClientRect();return e.target===viewer&&(e.clientX<r.left||e.clientX>r.right||e.clientY<r.top||e.clientY>r.bottom);};
    let backdropDown=false;viewer.addEventListener('pointerdown',e=>backdropDown=outside(e));
    viewer.addEventListener('click',e=>{if(backdropDown&&outside(e))viewer.close();backdropDown=false;});
    $('poiImageViewer').addEventListener('close',()=>{$('poiImageFull').removeAttribute('src');});
    $('poiImageFull').onerror=()=>{$('poiImageViewerError').textContent=t('poiImageFailed');};
    new MutationObserver(()=>{if($('poiModal').hidden)reset();}).observe($('poiModal'),{attributes:true,attributeFilter:['hidden']});
  }
  function setting(value){
    enabled=value===true;$('poiImagesEnabledInput').checked=enabled;$('poiImageField').hidden=!enabled;
    if(!enabled){reset();$('poiImageViewer').close();document.querySelectorAll('.poi-image-button').forEach(el=>el.remove());}
  }
  async function payload(){
    if(!enabled||!changed){if(enabled&&$('poiImageInput').files.length)throw Error($('poiImageError').textContent||t('poiImageLoading'));return {};}
    if(!selected)return {imageData:null};
    const file=selected;return {imageData:await new Promise((resolve,reject)=>{const reader=new FileReader();reader.onload=()=>resolve(String(reader.result).split(',')[1]);reader.onerror=()=>reject(Error(t('poi_image_format')));reader.readAsDataURL(file);})};
  }
  function thumbnail(feature){
    const key=feature?.properties?.imageKey;if(!enabled||!/^[a-f0-9]{32}$/.test(key||''))return null;
    const button=document.createElement('button'),image=document.createElement('img');button.type='button';button.className='poi-image-button';button.title=t('poiImageView');button.setAttribute('aria-label',t('poiImageView'));image.src=url(feature.id,key,480);image.alt=feature.properties.name||'';image.loading='lazy';image.decoding='async';button.append(image);
    image.onerror=()=>{button.textContent=t('poiImageFailed');button.disabled=true;};
    button.onclick=()=>{if(enabled)view({id:feature.id,imageKey:key,name:feature.properties.name});};return button;
  }
  function view(item){$('poiImageViewerError').textContent='';$('poiImageFull').alt=item.name||'';$('poiImageFull').src=url(item.id,item.imageKey,0);$('poiImageViewer').showModal();}
  function configure(settings={}){maxBytes=(settings.imageMaxMb??10)*1024*1024;allowedTypes=settings.imageTypes||['jpeg','png','webp','bmp'];$('poiImageInput').accept=allowedTypes.flatMap(t=>t==='jpeg'?['.jpg','.jpeg']:['.'+t]).join(',');const help=document.querySelector('[data-i18n="poiImageHelp"]');if(help)help.textContent=t('poiImageHelp');}
  window.ServerMapPoiImages={init,reset,setting,configure,url,view,payload,thumbnail,messages,error:key=>messages[language()]?.[key]};
})();
