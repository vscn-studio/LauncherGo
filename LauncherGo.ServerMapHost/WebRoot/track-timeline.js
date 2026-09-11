/* A time ruler with activity lanes, a draggable playhead and a zoomable time window. */
(() => {
  'use strict';
  function create({container,onChange,getLanguage}) {
    const node=(tag,props={})=>Object.assign(document.createElement(tag),props);
    const text=(zh,en)=>getLanguage()==='zh'?zh:en;
    const toolbar=node('div',{className:'time-axis-toolbar'}),play=node('button',{type:'button',textContent:'▶'}),duration=node('span',{className:'time-axis-duration'});
    const viewport=node('div',{className:'time-axis',tabIndex:0}),ruler=node('div',{className:'time-axis-ruler'}),activity=node('div',{className:'time-axis-activity'}),mountLane=node('div',{className:'time-axis-mounts'});
    const playhead=node('div',{className:'time-axis-playhead'}),stamp=node('span',{className:'time-axis-stamp'}),hover=node('span',{className:'time-axis-hover',hidden:true});
    viewport.setAttribute('role','slider');viewport.setAttribute('aria-label',text('轨迹时间轴，可拖动或使用方向键','Track timeline: drag or use arrow keys'));viewport.setAttribute('aria-orientation','horizontal');
    activity.setAttribute('aria-hidden','true');mountLane.setAttribute('aria-hidden','true');ruler.setAttribute('aria-hidden','true');playhead.setAttribute('aria-hidden','true');
    playhead.append(stamp);viewport.append(ruler,activity,mountLane,playhead,hover);toolbar.append(play,duration);
    const zoomOut=node('button',{type:'button',textContent:'−'}),zoomIn=node('button',{type:'button',textContent:'+'}),fit=node('button',{type:'button',textContent:text('全程','Fit')});
    for(const [b,label] of [[play,text('播放 / 暂停','Play / pause')],[zoomOut,text('缩小时间轴','Zoom out')],[zoomIn,text('放大时间轴','Zoom in')]]){b.title=label;b.setAttribute('aria-label',label);}
    toolbar.append(node('span',{className:'time-axis-key',textContent:text('移动 · 停留 · 坐骑','Movement · Stops · Mounts')}),zoomOut,zoomIn,fit);container.append(toolbar,viewport);
    let start=0,end=0,value=0,viewStart=0,viewEnd=1,stops=[],mounts=[],gaps=[],playing=false,frame=0,lastFrame=0,dragging=false;
    const clock=ms=>new Date(ms).toLocaleTimeString(getLanguage()==='zh'?'zh-CN':undefined,{hour12:false,hour:'2-digit',minute:'2-digit',second:'2-digit'});
    const percent=time=>100*(time-viewStart)/(viewEnd-viewStart);
    function cursor(){
      const position=percent(value);playhead.hidden=position<0||position>100;playhead.style.left=Math.max(0,Math.min(100,position))+'%';stamp.textContent=clock(value);
      stamp.classList.toggle('align-right',position>80);stamp.classList.toggle('align-left',position<20);
      viewport.setAttribute('aria-valuemin',String(start));viewport.setAttribute('aria-valuemax',String(end));viewport.setAttribute('aria-valuenow',String(value));viewport.setAttribute('aria-valuetext',new Date(value).toLocaleString());
      viewport.dataset.time=String(value);
    }
    function segment(lane,range,className){
      if(range.end<viewStart||range.start>viewEnd)return;
      const el=node('span',{className});el.style.left=Math.max(0,percent(range.start))+'%';el.style.width=Math.max(.3,Math.min(100,percent(range.end))-Math.max(0,percent(range.start)))+'%';
      el.title=range.label||clock(range.start)+' – '+clock(range.end);lane.append(el);
    }
    function render(){
      ruler.replaceChildren();activity.replaceChildren();mountLane.replaceChildren();
      const width=viewport.clientWidth||650,span=viewEnd-viewStart,ideal=span/Math.max(2,Math.floor(width/95));
      const steps=[1000,2000,5000,10000,15000,30000,60000,120000,300000,600000,900000,1800000,3600000,7200000,14400000,28800000,86400000];
      const step=steps.findLast(n=>n<=ideal)||1000;
      for(let time=Math.ceil(viewStart/step)*step;time<=viewEnd;time+=step){
        const tick=node('span',{className:'time-axis-tick',textContent:clock(time)});tick.style.left=percent(time)+'%';ruler.append(tick);
      }
      segment(activity,{start,end},'time-axis-motion');
      for(const gap of gaps)segment(activity,gap,'time-axis-gap');
      for(const stop of stops)segment(activity,stop,'time-axis-stop');
      for(const mount of mounts)segment(mountLane,mount,'time-axis-mounted');
      duration.textContent=text('全程 ','Duration ')+Math.round((end-start)/1000)+' s · '+text('当前视窗 ','View ')+Math.round(span/1000)+' s';
      viewport.style.setProperty('--minor-tick',Math.max(1,step/5/span*width)+'px');cursor();
    }
    function seek(time,notify=true){
      value=Math.round(Math.max(start,Math.min(end,time)));cursor();if(notify)onChange(value);
    }
    function setWindow(from,to){
      const full=Math.max(1000,end-start),span=Math.max(Math.min(1000,full),Math.min(full,to-from));
      viewStart=Math.max(start,Math.min(Math.max(start,end-span),from));viewEnd=viewStart+span;render();
    }
    function zoom(factor,anchor=value){
      const span=viewEnd-viewStart,ratio=Math.max(0,Math.min(1,(anchor-viewStart)/span)),next=span*factor;setWindow(anchor-next*ratio,anchor+next*(1-ratio));
    }
    zoomIn.onclick=()=>zoom(.5);zoomOut.onclick=()=>zoom(2);fit.onclick=()=>setWindow(start,Math.max(end,start+1000));
    const pointerTime=event=>{const box=viewport.getBoundingClientRect();return viewStart+Math.max(0,Math.min(1,(event.clientX-box.left)/box.width))*(viewEnd-viewStart);};
    viewport.addEventListener('pointerdown',event=>{if(event.button!==0)return;event.preventDefault();pause();hover.hidden=true;dragging=true;viewport.setPointerCapture(event.pointerId);viewport.focus({preventScroll:true});seek(pointerTime(event));});
    viewport.addEventListener('pointermove',event=>{
      if(dragging){seek(pointerTime(event));return;}const time=pointerTime(event);hover.textContent=clock(time);hover.style.left=Math.max(5,Math.min(95,percent(time)))+'%';hover.hidden=false;
    });
    const release=event=>{dragging=false;if(viewport.hasPointerCapture(event.pointerId))viewport.releasePointerCapture(event.pointerId);};
    viewport.addEventListener('pointerup',release);viewport.addEventListener('pointercancel',release);viewport.addEventListener('lostpointercapture',()=>dragging=false);viewport.addEventListener('pointerleave',()=>hover.hidden=true);
    viewport.addEventListener('wheel',event=>{event.preventDefault();if(event.shiftKey){const delta=(viewEnd-viewStart)*Math.sign(event.deltaY)*.15;setWindow(viewStart+delta,viewEnd+delta);}else zoom(event.deltaY>0?1.4:1/1.4,pointerTime(event));},{passive:false});
    viewport.addEventListener('keydown',event=>{
      const jump=event.shiftKey?10000:1000;
      if(event.key==='ArrowLeft'||event.key==='ArrowRight'){event.preventDefault();pause();seek(value+(event.key==='ArrowRight'?jump:-jump));if(value<viewStart||value>viewEnd)setWindow(value-(viewEnd-viewStart)/2,value+(viewEnd-viewStart)/2);}
      else if(event.key==='Home'||event.key==='End'){event.preventDefault();pause();seek(event.key==='Home'?start:end);setWindow(value-(viewEnd-viewStart)/2,value+(viewEnd-viewStart)/2);}
      else if(event.key===' '){event.preventDefault();play.click();}
    });
    function pause(){playing=false;cancelAnimationFrame(frame);play.textContent='▶';play.setAttribute('aria-pressed','false');}
    function tick(now){
      if(!playing)return;const next=value+(now-lastFrame);lastFrame=now;seek(next);
      if(value>=end){pause();return;}if(value>viewEnd)setWindow(value,value+(viewEnd-viewStart));frame=requestAnimationFrame(tick);
    }
    play.onclick=()=>{if(playing){pause();return;}if(end<=start)return;if(value>=end)seek(start);playing=true;play.textContent='Ⅱ';play.setAttribute('aria-pressed','true');lastFrame=performance.now();frame=requestAnimationFrame(tick);};
    new ResizeObserver(render).observe(viewport);
    function update(data,reset=false){
      const atEnd=value===end,oldValue=value,fullView=viewStart<=start&&viewEnd>=end;
      start=data.start;end=Math.max(start,data.end);stops=data.stops||[];mounts=data.mounts||[];gaps=data.gaps||[];
      if(reset){pause();viewStart=start;viewEnd=Math.max(start+1000,end);}
      else if(fullView){viewStart=start;viewEnd=Math.max(start+1000,end);}
      seek(reset?start:atEnd?end:oldValue,false);render();onChange(value);
    }
    function clear(){pause();stops=[];mounts=[];gaps=[];start=end=value=0;viewStart=0;viewEnd=1;render();}
    return {update,clear,pause,get value(){return value;},showMounts:visible=>{mountLane.hidden=!visible;viewport.classList.toggle('without-mounts',!visible);}};
  }
  window.ServerMapTrackTimeline={create};
})();
