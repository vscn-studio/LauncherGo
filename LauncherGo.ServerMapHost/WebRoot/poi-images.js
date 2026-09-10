(() => {
  const $=id=>document.getElementById(id),maxBytes=5*1024*1024;
  let enabled=false,changed=false,selected=null,epoch=0,previewUrl=null,language=()=> 'zh',api='api/v1';
  const messages={zh:{poiImagesEnabled:'允许地点标记添加图片',poiImageAdd:'添加图片',poiImageRemove:'移除图片',poiImageHelp:'最大尺寸 2560 × 2560 px，最大文件 5 MB',poi_image_size:'图片最大为 5 MB。',poi_image_dimensions:'图片宽、高均不能超过 2560 px。',poi_image_format:'请选择有效的 JPG、PNG、WebP 或 BMP 图片，不支持 GIF。',poi_image_animation:'不支持动画图片。',poi_images_disabled:'管理员已关闭地点图片功能。',poi_image_encode:'图片处理失败，请重试。',poiImageView:'点击放大图片',poiImageClose:'关闭图片',poiImageLoading:'正在检查图片…',poiImageFailed:'图片加载失败'},en:{poiImagesEnabled:'Allow images on place markers',poiImageAdd:'Add image',poiImageRemove:'Remove image',poiImageHelp:'Maximum dimensions 2560 × 2560 px, maximum file size 5 MB',poi_image_size:'Images must be at most 5 MB.',poi_image_dimensions:'Image width and height must not exceed 2560 px.',poi_image_format:'Choose a valid JPG, PNG, WebP or BMP image. GIF is not supported.',poi_image_animation:'Animated images are not supported.',poi_images_disabled:'Place images are disabled by the administrator.',poi_image_encode:'Could not process the image. Please retry.',poiImageView:'Enlarge image',poiImageClose:'Close image',poiImageLoading:'Checking image…',poiImageFailed:'Image failed to load'}};
  const t=key=>messages[language()]?.[key]||messages.en[key]||key;
  const url=(id,key,size)=>`${api}/poi-image?id=${encodeURIComponent(id)}&key=${encodeURIComponent(key)}&size=${size}`;
  function clearPreview(){if(previewUrl)URL.revokeObjectURL(previewUrl);previewUrl=null;$('poiImagePreview').removeAttribute('src');$('poiImagePreview').hidden=true;$('poiImagePreviewFrame').hidden=true;}
  function reset(feature){epoch++;selected=null;changed=false;$('poiImageInput').value='';$('poiImageError').textContent='';clearPreview();const key=feature?.properties?.imageKey;if(enabled&&/^[a-f0-9]{32}$/.test(key||'')){$('poiImagePreview').src=url(feature.id,key,480);$('poiImagePreview').hidden=false;$('poiImagePreviewFrame').hidden=false;}$('poiImageRemove').hidden=$('poiImagePreview').hidden;}
  async function validate(file){
    if(!file.size||file.size>maxBytes)throw Error(t('poi_image_size'));
    const bytes=new Uint8Array(await file.arrayBuffer()),view=new DataView(bytes.buffer),tag=(at,n)=>String.fromCharCode(...bytes.slice(at,at+n));
    const png=tag(1,3)==='PNG',webp=tag(0,4)==='RIFF'&&tag(8,4)==='WEBP',jpeg=bytes[0]===255&&bytes[1]===216,bmp=tag(0,2)==='BM';
    if(!png&&!webp&&!jpeg&&!bmp)throw Error(t('poi_image_format'));
    if(png)for(let i=8;i+12<=bytes.length;){if(tag(i+4,4)==='acTL')throw Error(t('poi_image_animation'));i+=12+view.getUint32(i);}
    if(webp)for(let i=12;i+8<=bytes.length;){if(['ANIM','ANMF'].includes(tag(i,4)))throw Error(t('poi_image_animation'));const n=view.getUint32(i+4,true);i+=8+n+(n&1);}
    let image;try{image=await createImageBitmap(file);}catch{throw Error(t('poi_image_format'));}
    try{if(image.width>2560||image.height>2560)throw Error(t('poi_image_dimensions'));}finally{image.close();}
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
    $('poiImageViewer').addEventListener('click',e=>{if(e.target===$('poiImageViewer'))$('poiImageViewer').close();});
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
    button.onclick=()=>{if(!enabled)return;$('poiImageViewerError').textContent='';$('poiImageFull').alt=feature.properties.name||'';$('poiImageFull').src=url(feature.id,key,1280);$('poiImageViewer').showModal();};return button;
  }
  window.ServerMapPoiImages={init,reset,setting,payload,thumbnail,messages,error:key=>messages[language()]?.[key]};
})();
