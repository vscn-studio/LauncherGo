const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const zlib=require('node:zlib');
function crc(bytes){let c=0xffffffff;for(const byte of bytes){c^=byte;for(let i=0;i<8;i++)c=c&1?(c>>>1)^0xedb88320:c>>>1;}return (c^0xffffffff)>>>0;}
function chunk(type,data){const b=Buffer.alloc(data.length+12);b.writeUInt32BE(data.length);b.write(type,4);data.copy(b,8);b.writeUInt32BE(crc(b.subarray(4,-4)),b.length-4);return b;}
function png(w,h,extra=[]){const header=Buffer.alloc(13);header.writeUInt32BE(w);header.writeUInt32BE(h,4);header[8]=8;header[9]=6;return Buffer.concat([Buffer.from('89504e470d0a1a0a','hex'),chunk('IHDR',header),...extra,chunk('IDAT',zlib.deflateSync(Buffer.alloc(h*(w*4+1)))),chunk('IEND',Buffer.alloc(0))]);}
function dimensions(bytes){assert.equal(bytes.toString('ascii',0,4),'RIFF');assert.equal(bytes.toString('ascii',8,12),'WEBP');let size;for(let i=12;i+8<=bytes.length;){const type=bytes.toString('ascii',i,i+4),n=bytes.readUInt32LE(i+4),data=bytes.subarray(i+8,i+8+n);assert.ok(!['EXIF','XMP ','ICCP','ANIM','ANMF'].includes(type),'No metadata or animation: '+type);if(type==='VP8 ')size=[data.readUInt16LE(6)&16383,data.readUInt16LE(8)&16383];if(type==='VP8X')size=[data.readUIntLE(4,3)+1,data.readUIntLE(7,3)+1];if(type==='VP8L'){const bits=data.readUInt32LE(1);size=[(bits&16383)+1,((bits>>>14)&16383)+1];}i+=8+n+(n&1);}assert.ok(size);return size;}
module.exports=async function(call,cookies){
  const root=path.dirname(process.env.MAP_TEST_CONTROL),imagesDir=path.join(root,'poi-images'),input={name:'Image test',text:'Description',type:'text',x:300,z:300,color:'#123456',rotation:0};
  const settings=(await call('/announcement')).json();assert.equal(settings.poiImagesEnabled,false);
  const source=await fs.readFile(path.join(root,'poi-test.png')),body={...input,imageData:source.toString('base64')};
  assert.equal((await call('/pois',cookies.alice,body)).status,403);
  assert.equal((await call('/announcement',cookies.alice,{html:settings.html,poiImagesEnabled:true})).status,403);
  assert.equal((await call('/announcement',cookies.admin,{html:settings.html,poiImagesEnabled:true})).status,200);
  assert.equal((await call('/announcement',cookies.admin,{html:settings.html})).json().poiImagesEnabled,true,'Legacy saves preserve switch');
  const photo=await call('/pois',cookies.alice,body);assert.equal(photo.status,200,photo.bytes.toString());let saved=photo.json();assert.match(saved.ImageKey,/^[a-f0-9]{32}$/);
  const photoUrl=(size,key=saved.ImageKey)=>`/poi-image?id=${saved.Id}&key=${key}&size=${size}`;
  for(const size of [480,1280]){const image=await call(photoUrl(size));assert.equal(image.status,200);assert.equal(image.headers.get('content-type'),'image/webp');assert.equal(image.headers.get('cache-control'),'no-store');assert.deepEqual(dimensions(image.bytes),[size,size/2]);}
  assert.deepEqual((await fs.readdir(imagesDir)).sort(),[480,1280].map(size=>`${saved.ImageKey}-${size}.webp`).sort(),'No original stored');
  assert.equal((await call('/pois',cookies.bob,{...body,id:saved.Id})).status,403);
  assert.equal((await call(photoUrl(2560))).status,404);assert.equal((await call(photoUrl(480,'../bad'))).status,404);
  assert.equal((await call('/pois',cookies.alice,{...input,id:saved.Id,name:'Text edit'})).json().ImageKey,saved.ImageKey);
  assert.equal((await call('/pois',cookies.alice,{...input,id:saved.Id,rotation:30})).status,200);
  const tiff=Buffer.alloc(56);tiff.write('II');tiff.writeUInt16LE(42,2);tiff.writeUInt32LE(8,4);tiff.writeUInt16LE(2,8);tiff.writeUInt16LE(0x112,10);tiff.writeUInt16LE(3,12);tiff.writeUInt32LE(1,14);tiff.writeUInt16LE(6,18);tiff.writeUInt16LE(0x8825,22);tiff.writeUInt16LE(4,24);tiff.writeUInt32LE(1,26);tiff.writeUInt32LE(38,30);tiff.writeUInt16LE(1,38);tiff.writeUInt16LE(1,42);tiff.writeUInt32LE(4,44);tiff.set([2,3,0,0],48);
  const exif=Buffer.concat([Buffer.from('Exif\0\0'),tiff]),app1=Buffer.alloc(exif.length+4);app1[0]=255;app1[1]=225;app1.writeUInt16BE(exif.length+2,2);exif.copy(app1,4);
  const jpeg=await fs.readFile(path.join(root,'poi-test.jpeg')),oriented=Buffer.concat([jpeg.subarray(0,2),app1,jpeg.subarray(2)]);
  const oldKey=saved.ImageKey;const replaced=await call('/pois',cookies.alice,{...input,id:saved.Id,imageData:oriented.toString('base64')});assert.equal(replaced.status,200,replaced.bytes.toString());saved=replaced.json();
  assert.equal((await call(photoUrl(480,oldKey))).status,404);
  assert.deepEqual(dimensions((await call(photoUrl(480))).bytes),[240,480]);assert.deepEqual(dimensions((await call(photoUrl(1280))).bytes),[640,1280]);
  assert.equal((await fs.readdir(imagesDir)).length,2,'Replacement removes old thumbnails');
  // Validate format and dimensions using actual bytes, not extension or MIME claims.
  const bmp=Buffer.alloc(54+32*16*3);bmp.write('BM');bmp.writeUInt32LE(bmp.length,2);bmp.writeUInt32LE(54,10);bmp.writeUInt32LE(40,14);bmp.writeInt32LE(32,18);bmp.writeInt32LE(16,22);bmp.writeUInt16LE(1,26);bmp.writeUInt16LE(24,28);
  for(const bytes of [await fs.readFile(path.join(root,'poi-test.webp')),bmp,png(2560,2560),png(40,20,[chunk('tEXt',Buffer.from('GPS\0private-location')),chunk('eXIf',tiff)])]){
    const response=await call('/pois',cookies.alice,{...input,id:saved.Id,imageData:bytes.toString('base64')});assert.equal(response.status,200,response.bytes.toString());saved=response.json();const output=(await call(photoUrl(480))).bytes;dimensions(output);assert.equal(output.includes(Buffer.from('private-location')),false);
  }
  assert.deepEqual(dimensions((await call(photoUrl(1280))).bytes),[40,20],'Small images are not upscaled');
  const animation=Buffer.alloc(8);animation.writeUInt32BE(2);
  for(const bytes of [Buffer.from('GIF89a'),Buffer.from('<svg xmlns="http://www.w3.org/2000/svg"/>'),png(2561,1),png(1,2561),Buffer.concat([source,Buffer.alloc(5*1024*1024)]),png(2,2,[chunk('acTL',animation)]),Buffer.from('invalid')]){
    const response=await call('/pois',cookies.alice,{...input,id:saved.Id,imageData:bytes.toString('base64')});assert.equal(response.status,400,response.bytes.toString());assert.equal((await fs.readdir(imagesDir)).length,2);
  }
  const region=(await call('/hidden-regions',cookies.admin,{name:'Image privacy',minX:290,minZ:290,maxX:310,maxZ:310})).json();
  assert.equal((await call(photoUrl(480))).status,404);assert.equal((await call(photoUrl(480),cookies.alice)).status,404);assert.equal((await call(photoUrl(480),cookies.admin)).status,200);
  assert.equal((await call('/hidden-regions?id='+region.id,cookies.admin,undefined,'DELETE',{'X-ServerMap-Request':'1'})).status,200);
  assert.equal((await call('/announcement',cookies.admin,{html:settings.html,poiImagesEnabled:false})).status,200);assert.equal((await call(photoUrl(480),cookies.admin)).status,404);assert.equal((await call('/pois',cookies.alice,{...body,id:saved.Id})).status,403);assert.equal((await fs.readdir(imagesDir)).length,2);
  assert.equal((await call('/announcement',cookies.admin,{html:settings.html,poiImagesEnabled:true})).status,200);assert.equal((await call(photoUrl(480))).status,200);
  const cleared=await call('/pois',cookies.alice,{...input,id:saved.Id,imageData:null});assert.equal(cleared.status,200);assert.equal(cleared.json().ImageKey,null);assert.equal((await fs.readdir(imagesDir)).length,0);
  saved=(await call('/pois',cookies.alice,{...body,id:saved.Id})).json();assert.equal((await fs.readdir(imagesDir)).length,2);
  assert.equal((await call('/pois?id='+saved.Id,cookies.alice,undefined,'DELETE',{'X-ServerMap-Request':'1'})).status,200);assert.equal((await fs.readdir(imagesDir)).length,0);
  await call('/announcement',cookies.admin,{html:settings.html,poiImagesEnabled:false});
  console.log('PASS real POI images: default-off/admin switch, JPEG/PNG/WebP/BMP, size/dimension/animation validation, 480/1280 WebP, EXIF orientation and metadata stripping, privacy, replacement/removal cleanup and no originals');
};
