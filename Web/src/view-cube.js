import { Quaternion, Vector2, Vector3 } from 'three';

// Port of swisscam-godot/scripts/ui/view_cube.gd. Both scenes use Y-up.
export const VERTICES = [
  [-1,-1,-1],[1,-1,-1],[1,1,-1],[-1,1,-1],
  [-1,-1,1],[1,-1,1],[1,1,1],[-1,1,1]
].map(v=>new Vector3(...v));
export const FACES = [
  {id:'FRONT',label:'Front',normal:[0,0,1],vertices:[4,5,6,7]},
  {id:'BACK',label:'Back',normal:[0,0,-1],vertices:[1,0,3,2]},
  {id:'TOP',label:'Top',normal:[0,1,0],vertices:[7,6,2,3]},
  {id:'BOTTOM',label:'Bottom',normal:[0,-1,0],vertices:[0,1,5,4]},
  {id:'LEFT',label:'Left',normal:[-1,0,0],vertices:[0,4,7,3]},
  {id:'RIGHT',label:'Right',normal:[1,0,0],vertices:[5,1,2,6]}
];

export function projectCube(cameraQuaternion,width=104,height=104){
  const rotation=cameraQuaternion.clone().invert(),scale=Math.min(width,height)*.215;
  const projected=VERTICES.map(v=>{const p=v.clone().applyQuaternion(rotation);return new Vector2(width/2+p.x*scale,height/2-p.y*scale);});
  const faces=[],corners=[],edges=[],seenCorners=new Set(),seenEdges=new Set();
  for(const definition of FACES){
    const facing=new Vector3(...definition.normal).applyQuaternion(rotation).z;
    if(facing<=.012)continue;
    const polygon=definition.vertices.map(i=>projected[i]);
    faces.push({...definition,kind:'face',polygon,center:polygon.reduce((sum,p)=>sum.add(p),new Vector2()).multiplyScalar(.25),facing,direction:new Vector3(...definition.normal)});
    for(let i=0;i<4;i++){
      const a=definition.vertices[i],b=definition.vertices[(i+1)%4],key=[Math.min(a,b),Math.max(a,b)].join(':');
      if(!seenCorners.has(a)){seenCorners.add(a);corners.push({kind:'corner',id:String(a),point:projected[a],direction:VERTICES[a].clone()});}
      if(!seenEdges.has(key)){seenEdges.add(key);edges.push({kind:'edge',id:key,a:projected[a],b:projected[b],direction:VERTICES[a].clone().add(VERTICES[b]).multiplyScalar(.5)});}
    }
  }
  return {faces,corners,edges};
}

export function hitTestCube(geometry,point){
  let nearest=null,distance=7;
  for(const corner of geometry.corners){const d=point.distanceTo(corner.point);if(d<distance){nearest=corner;distance=d;}}
  if(nearest)return nearest;
  distance=4;
  for(const edge of geometry.edges){const ab=edge.b.clone().sub(edge.a),t=Math.max(0,Math.min(1,point.clone().sub(edge.a).dot(ab)/Math.max(ab.lengthSq(),1e-12))),d=point.distanceTo(edge.a.clone().addScaledVector(ab,t));if(d<distance){nearest=edge;distance=d;}}
  if(nearest)return nearest;
  for(const face of geometry.faces){let inside=false;const p=face.polygon;for(let i=0,j=p.length-1;i<p.length;j=i++){if((p[i].y>point.y)!==(p[j].y>point.y)&&point.x<(p[j].x-p[i].x)*(point.y-p[i].y)/(p[j].y-p[i].y)+p[i].x)inside=!inside;}if(inside)return face;}
  return null;
}

export function createViewCube(container,navigation){
  const ns='http://www.w3.org/2000/svg',svg=document.createElementNS(ns,'svg');
  svg.setAttribute('viewBox','0 0 104 104');svg.setAttribute('tabindex','0');svg.setAttribute('role','group');svg.setAttribute('aria-label','View cube. Click faces, edges or corners; drag to orbit. Arrow keys rotate, F B T D L R select faces, Home restores isometric.');
  const title=document.createElementNS(ns,'title');title.textContent='Click a face, edge or corner. Drag to rotate. Home: isometric.';svg.append(title);
  const layer=document.createElementNS(ns,'g');svg.append(layer);container.replaceChildren(svg);
  const measure=document.createElement('canvas').getContext('2d');
  let geometry={faces:[],corners:[],edges:[]},hover=null,press=null,lastQuaternion=new Quaternion(0,0,0,0),focus=false;
  function node(name,attributes,parent=layer){const el=document.createElementNS(ns,name);for(const [key,value] of Object.entries(attributes))el.setAttribute(key,String(value));parent.append(el);return el;}
  const same=(a,b)=>a?.kind===b?.kind&&a?.id===b?.id;
  function draw(){
    layer.replaceChildren();
    for(const face of geometry.faces){
      const active=same(hover,face),t=face.facing*.65,start=[59,82,105],end=[114,137,158],fill=active?'#468d88':`rgb(${start.map((c,i)=>Math.round(c+(end[i]-c)*t)).join(',')})`;
      const polygon=node('polygon',{points:face.polygon.map(p=>`${p.x},${p.y}`).join(' '),fill,stroke:active?'#a9bfd2':focus?'#5ee0c0':'#8ba2b7','stroke-width':1.2,'data-kind':'face','data-view':face.id});
      const tooltip=node('title',{},polygon);tooltip.textContent=`${face.label} view · click. Drag to reveal other faces.`;
      if(face.facing<.23)continue;
      const intersections=[];for(let i=0;i<4;i++){const a=face.polygon[i],b=face.polygon[(i+1)%4];if(Math.abs(a.y-b.y)>.001&&face.center.y>=Math.min(a.y,b.y)&&face.center.y<=Math.max(a.y,b.y))intersections.push(a.x+(b.x-a.x)*(face.center.y-a.y)/(b.y-a.y));}
      if(intersections.length<2)continue;
      const available=Math.max(...intersections)-Math.min(...intersections)-4;let size=face.facing>.8?10:9;
      while(size>=7){measure.font=`${size}px Inter, sans-serif`;if(measure.measureText(face.label).width<=available)break;size--;}
      if(size>=7)node('text',{x:face.center.x,y:face.center.y,'font-size':size,'text-anchor':'middle','dominant-baseline':'central',fill:'#f5f9fc','pointer-events':'none'}).textContent=face.label;
    }
    // Transparent enlarged edge/corner targets catch the same 4/7px regions as SwissCAM.
    for(const edge of geometry.edges){
      node('line',{x1:edge.a.x,y1:edge.a.y,x2:edge.b.x,y2:edge.b.y,stroke:'transparent','stroke-width':8,'data-kind':'edge'});
      if(same(hover,edge))node('line',{x1:edge.a.x,y1:edge.a.y,x2:edge.b.x,y2:edge.b.y,stroke:'#5ee0c0','stroke-width':4,style:'pointer-events:none'});
    }
    for(const corner of geometry.corners){
      node('circle',{cx:corner.point.x,cy:corner.point.y,r:7,fill:'transparent','data-kind':'corner'});
      if(same(hover,corner))node('circle',{cx:corner.point.x,cy:corner.point.y,r:5,fill:'#5ee0c0',stroke:'#d2fff4',style:'pointer-events:none'});
    }
  }
  function point(e){const r=svg.getBoundingClientRect();return new Vector2((e.clientX-r.left)*104/r.width,(e.clientY-r.top)*104/r.height);}
  function end(e,cancel=false){if(!press||e.pointerId!==press.id)return;const click=!press.dragging&&!cancel;press=null;if(svg.hasPointerCapture(e.pointerId))svg.releasePointerCapture(e.pointerId);if(click){const hit=hitTestCube(geometry,point(e));if(hit){if(hit.kind==='face')navigation.preset(hit.direction);else navigation.direction(hit.direction);}}hover=null;draw();}
  svg.addEventListener('pointerdown',e=>{if(e.button!==0)return;const p=point(e);if(!hitTestCube(geometry,p))return;e.preventDefault();e.stopPropagation();svg.focus({preventScroll:true});press={id:e.pointerId,point:p,angles:navigation.angles(),dragging:false};svg.setPointerCapture(e.pointerId);});
  svg.addEventListener('pointermove',e=>{const p=point(e);if(press){const delta=p.clone().sub(press.point);press.dragging ||= delta.length()>4;if(press.dragging)navigation.orbit(press.angles.yaw-delta.x*.014,Math.max(-Math.PI/2,Math.min(Math.PI/2,press.angles.pitch+delta.y*.014)));}const next=press?.dragging?null:hitTestCube(geometry,p);if(!same(hover,next)){hover=next;draw();}});
  svg.addEventListener('pointerup',e=>end(e));svg.addEventListener('pointercancel',e=>end(e,true));svg.addEventListener('lostpointercapture',()=>{press=null;});svg.addEventListener('pointerleave',()=>{hover=null;draw();});
  svg.addEventListener('focus',()=>{focus=true;draw();});svg.addEventListener('blur',()=>{focus=false;press=null;draw();});
  svg.addEventListener('keydown',e=>{
    if(e.altKey||e.ctrlKey||e.metaKey)return;
    const presets={KeyF:'FRONT',KeyB:'BACK',KeyT:'TOP',KeyD:'BOTTOM',KeyL:'LEFT',KeyR:'RIGHT'};
    if(presets[e.code])navigation.preset(new Vector3(...FACES.find(f=>f.id===presets[e.code]).normal));
    else if(['Home','Enter','NumpadEnter'].includes(e.code))navigation.preset(new Vector3(-1,1,1));
    else if(e.code.startsWith('Arrow')){const a=navigation.angles(),step=Math.PI/4;navigation.orbit(a.yaw+(e.code==='ArrowLeft'?-step:e.code==='ArrowRight'?step:0),Math.max(-Math.PI/2,Math.min(Math.PI/2,a.pitch+(e.code==='ArrowUp'?step:e.code==='ArrowDown'?-step:0))));}
    else return; // Space/Escape retain the robot's stop interlocks.
    e.preventDefault();e.stopPropagation();
  });
  return {update(quaternion){if(1-Math.abs(lastQuaternion.dot(quaternion))<1e-10)return;lastQuaternion.copy(quaternion);geometry=projectCube(quaternion);hover=null;draw();}};
}
