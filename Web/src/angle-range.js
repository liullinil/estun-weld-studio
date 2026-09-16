export function angleFromPoint(x,y){return Math.max(0,Math.min(180,Math.atan2(Math.max(0,108-y),x-140)*180/Math.PI));}
export function createAngleRange(container,minInput,maxInput,onChange){
  const ns='http://www.w3.org/2000/svg',svg=document.createElementNS(ns,'svg');svg.setAttribute('viewBox','0 0 280 127');svg.setAttribute('aria-label','Seam angle range on a 90 degree profile');
  const el=(name,attrs)=>{const n=document.createElementNS(ns,name);for(const [k,v] of Object.entries(attrs))n.setAttribute(k,v);svg.append(n);return n;};
  el('path',{d:'M38 108 A102 102 0 0 1 242 108',class:'protractor-arc'});
  for(let a=0;a<=180;a+=5){const r=a%30===0?91:a%10===0?95:98,rad=a*Math.PI/180;el('line',{x1:140+102*Math.cos(rad),y1:108-102*Math.sin(rad),x2:140+r*Math.cos(rad),y2:108-r*Math.sin(rad),class:'protractor-tick'});if(a%45===0){const text=el('text',{x:140+79*Math.cos(rad),y:111-79*Math.sin(rad),'text-anchor':'middle',class:'protractor-label'});text.textContent=`${a}°`;}}
  el('path',{d:'M73 108 H140 V41',class:'angle-profile'});el('path',{d:'M126 108 V94 H140',class:'right-angle-mark'});
  const band=el('path',{class:'angle-band'}),handles=[];
  for(const [index,label] of [[0,'Minimum angle'],[1,'Maximum angle']]){
    const line=el('line',{x1:140,y1:108,class:`angle-bound angle-${index}`,tabindex:'0',role:'slider','aria-label':label,'aria-valuemin':'0','aria-valuemax':'180'});
    const hit=el('line',{x1:140,y1:108,class:'angle-hit'}),knob=el('circle',{r:6,class:`angle-handle angle-${index}`});handles.push({line,hit,knob});
    let pointer=null;
    const change=e=>{const point=new DOMPoint(e.clientX,e.clientY).matrixTransform(svg.getScreenCTM().inverse()),angle=Math.round(angleFromPoint(point.x,point.y));if(index===0)minInput.value=Math.min(angle,+maxInput.value);else maxInput.value=Math.max(angle,+minInput.value);update();onChange();};
    for(const target of [line,hit,knob]){
      target.addEventListener('pointerdown',e=>{if(container.hasAttribute('inert'))return;e.preventDefault();e.stopPropagation();pointer=e.pointerId;target.setPointerCapture(pointer);change(e);});
      target.addEventListener('pointermove',e=>{if(pointer===e.pointerId)change(e);});
      const release=e=>{if(pointer===e.pointerId)pointer=null;};target.addEventListener('pointerup',release);target.addEventListener('pointercancel',release);target.addEventListener('lostpointercapture',release);
    }
    line.addEventListener('keydown',e=>{let step=e.shiftKey?5:1;if(['ArrowLeft','ArrowDown'].includes(e.key))step=-step;else if(!['ArrowRight','ArrowUp'].includes(e.key))return;e.preventDefault();e.stopPropagation();const input=index?maxInput:minInput;input.value=Math.max(index?+minInput.value:0,Math.min(index?180:+maxInput.value,+input.value+step));update();onChange();});
  }
  function update(){const values=[+minInput.value,+maxInput.value],point=(a,r=100)=>[140+r*Math.cos(a*Math.PI/180),108-r*Math.sin(a*Math.PI/180)];values.forEach((a,i)=>{const [x,y]=point(a),h=handles[i];for(const n of [h.line,h.hit]){n.setAttribute('x2',x);n.setAttribute('y2',y);}const knob=point(a,i===0?80:100);h.knob.setAttribute('cx',knob[0]);h.knob.setAttribute('cy',knob[1]);h.line.setAttribute('aria-valuenow',a);});const a=point(values[0]),b=point(values[1]);band.setAttribute('d',`M140 108 L${a[0]} ${a[1]} A100 100 0 0 0 ${b[0]} ${b[1]} Z`);for(const h of handles)svg.append(h.knob);}
  container.replaceChildren(svg);update();return {update};
}
