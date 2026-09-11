const assert=require('node:assert/strict');
const zlib=require('node:zlib');
function crc(bytes){let c=0xffffffff;for(const byte of bytes){c^=byte;for(let i=0;i<8;i++)c=c&1?(c>>>1)^0xedb88320:c>>>1;}return(c^0xffffffff)>>>0;}
function pngChunk(type,data){const b=Buffer.alloc(data.length+12);b.writeUInt32BE(data.length);b.write(type,4);data.copy(b,8);b.writeUInt32BE(crc(b.subarray(4,-4)),b.length-4);return b;}
function apng(){
 const header=Buffer.alloc(13);header.writeUInt32BE(2);header.writeUInt32BE(2,4);header[8]=8;header[9]=6;
 const control=Buffer.alloc(8);control.writeUInt32BE(2);
 const frame=seq=>{const b=Buffer.alloc(26);b.writeUInt32BE(seq);b.writeUInt32BE(2,4);b.writeUInt32BE(2,8);b.writeUInt16BE(1,20);b.writeUInt16BE(10,22);return b;};
 const pixels=zlib.deflateSync(Buffer.alloc(18)),second=Buffer.alloc(4);second.writeUInt32BE(2);
 return Buffer.concat([Buffer.from('89504e470d0a1a0a','hex'),pngChunk('IHDR',header),pngChunk('tEXt',Buffer.from('GPS\0private-location')),pngChunk('acTL',control),pngChunk('fcTL',frame(0)),pngChunk('IDAT',pixels),pngChunk('fcTL',frame(1)),pngChunk('fdAT',Buffer.concat([second,pixels])),pngChunk('IEND',Buffer.alloc(0))]);
}
function gif(){
 const frame=Buffer.from('21f904000a0000002c0000000001000100000202440100','hex');
 return Buffer.concat([Buffer.from('47494638396101000100800000000000ffffff','hex'),Buffer.from('21ff0b4e45545343415045322e300301000000','hex'),Buffer.from([0x21,0xfe,16]),Buffer.from('private-location'),Buffer.from([0]),frame,frame,Buffer.from([0x3b])]);
}
function webpChunk(tag,data){const b=Buffer.alloc(8+data.length+(data.length&1));b.write(tag);b.writeUInt32LE(data.length,4);data.copy(b,8);return b;}
function animatedWebp(still){
 const image=[];for(let i=12;i+8<=still.length;){const n=still.readUInt32LE(i+4),tag=still.toString('ascii',i,i+4);if(['VP8 ','VP8L','ALPH'].includes(tag))image.push(still.subarray(i,i+8+n+(n&1)));i+=8+n+(n&1);}
 const extended=Buffer.alloc(10);extended[0]=0x1a;extended.writeUIntLE(1599,4,3);extended.writeUIntLE(799,7,3);
 const frame=Buffer.alloc(16);frame.writeUIntLE(1599,6,3);frame.writeUIntLE(799,9,3);frame.writeUIntLE(100,12,3);frame[15]=2;
 const payload=Buffer.concat([webpChunk('VP8X',extended),webpChunk('EXIF',Buffer.from('private-location')),webpChunk('ANIM',Buffer.alloc(6)),...Array.from({length:2},()=>webpChunk('ANMF',Buffer.concat([frame,...image])))]);
 const header=Buffer.alloc(12);header.write('RIFF');header.writeUInt32LE(payload.length+4,4);header.write('WEBP',8);return Buffer.concat([header,payload]);
}
async function test(call,cookies,input,saved,still){
 const before=(await call('/announcement',cookies.admin)).json(),policy=before.management;
 const body=bytes=>({...input,id:saved.Id,imageData:bytes.toString('base64')});
 assert.equal((await call('/pois',cookies.alice,body(gif()))).status,400,'GIF excluded by default type policy');
 const updated=await call('/announcement',cookies.admin,{html:before.html,management:{...policy,ImageTypes:['.JPG','png','webp','bmp','GIF']}});
 assert.equal(updated.status,200,updated.bytes.toString());
 for(const [type,bytes,marker] of [['gif',gif(),'NETSCAPE2.0'],['png',apng(),'acTL'],['webp',animatedWebp(still),'ANIM']]){
  const response=await call('/pois',cookies.alice,body(bytes));assert.equal(response.status,200,response.bytes.toString());saved=response.json();
  for(const size of [0,1280]){
   const display=await call(`/poi-image?id=${saved.Id}&key=${saved.ImageKey}&size=${size}`);
   assert.equal(display.status,200);assert.equal(display.headers.get('content-type'),'image/'+type);
   assert.ok(display.bytes.includes(Buffer.from(marker)),'Animation controls retained: '+type);
   assert.equal(display.bytes.includes(Buffer.from('private-location')),false,'Privacy metadata removed: '+type);
   if(type==='png')assert.equal(display.bytes.toString('latin1').split('fcTL').length-1,2);
   if(type==='webp')assert.equal(display.bytes.toString('latin1').split('ANMF').length-1,2);
   if(type==='gif')assert.equal(display.bytes.toString('hex').split('21f904').length-1,2);
   // Re-upload cleaned output to verify that it remains decodable.
   const clean=await call('/pois',cookies.alice,body(display.bytes));assert.equal(clean.status,200,clean.bytes.toString());saved=clean.json();
  }
  const thumbnail=await call(`/poi-image?id=${saved.Id}&key=${saved.ImageKey}&size=480`);
  assert.equal(thumbnail.status,200);assert.equal(thumbnail.headers.get('content-type'),'image/webp');assert.equal(thumbnail.bytes.includes(Buffer.from('ANIM')),false);
 }
 assert.equal((await call('/announcement',cookies.admin,{html:before.html,management:policy})).status,200);
 console.log('PASS animated image types: GIF opt-in, APNG/animated WebP accepted, frames/loop retained, metadata removed, decodable originals and static thumbnails');
 return saved;
}
module.exports={test,gif,apng,animatedWebp};
