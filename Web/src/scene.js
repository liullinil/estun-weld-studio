import * as T from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { TransformControls } from 'three/addons/controls/TransformControls.js';
import { RGBELoader } from 'three/addons/loaders/RGBELoader.js';
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js';
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js';
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js';
import { SSAOPass } from 'three/addons/postprocessing/SSAOPass.js';
import { HOME, TOOL, jointMatrix } from './kinematics.js';
import { createCameraNavigation } from './camera-navigation.js';
import { selectedSeamOverlay } from './seam-highlight.js';
import { plannedPathSegments } from './planned-path.js';

export function createStudio(container,onTransform,onSeam){
  const scene=new T.Scene();scene.background=new T.Color('#242b33');scene.fog=new T.FogExp2('#343d47',.025);
  const renderer=new T.WebGLRenderer({antialias:true,alpha:false,preserveDrawingBuffer:true,powerPreference:'high-performance'});
  renderer.setPixelRatio(Math.min(devicePixelRatio,2));renderer.shadowMap.enabled=true;renderer.shadowMap.type=T.PCFSoftShadowMap;
  renderer.toneMapping=T.ACESFilmicToneMapping;renderer.toneMappingExposure=.92;container.append(renderer.domElement);
  const camera=new T.PerspectiveCamera(38,1,.06,50),orbit=new OrbitControls(camera,renderer.domElement);orbit.enableDamping=true;orbit.dampingFactor=.12;orbit.minDistance=.5;orbit.maxDistance=15;orbit.maxPolarAngle=Math.PI;orbit.mouseButtons={LEFT:T.MOUSE.ROTATE,MIDDLE:T.MOUSE.PAN,RIGHT:T.MOUSE.ROTATE};
  const navigation=createCameraNavigation(camera,orbit);
  const composer=new EffectComposer(renderer,new T.WebGLRenderTarget(1,1,{type:T.HalfFloatType,samples:4}));composer.addPass(new RenderPass(scene,camera));
  const ao=new SSAOPass(scene,camera,1,1);ao.kernelRadius=.18;ao.minDistance=.00002;ao.maxDistance=.0036;composer.addPass(ao);
  const bloom=new UnrealBloomPass(new T.Vector2(1,1),.20,.25,1.3);composer.addPass(bloom);composer.addPass(new OutputPass());
  const mat=(color,metalness=0,roughness=.45)=>new T.MeshStandardMaterial({color,metalness,roughness});
  const steel=mat('#87919a',.86,.34),dark=mat('#333b42',0,.52),black=mat('#151b20',0,.55),wall=mat('#41484f',0,.69);
  function mesh(geometry,material,parent=scene,pos=[0,0,0]){const m=new T.Mesh(geometry,material);m.position.set(...pos);m.castShadow=true;m.receiveShadow=true;parent.add(m);return m;}
  const box=(size,pos,material=dark,parent=scene)=>mesh(new T.BoxGeometry(...size),material,parent,pos);
  const cylinder=(r,h,pos,material=steel,parent=scene,n=64)=>mesh(new T.CylinderGeometry(r,r,h,n),material,parent,pos);
  const glow=(color,power=1)=>new T.MeshStandardMaterial({color,emissive:color,emissiveIntensity:power,roughness:.4});
  // Fixed room, machined mounting platform and lighting follow StudioWorld.cs.
  const floor=box([30,.15,30],[0,-.18,0],mat('#454c53',0,.82));
  const grid=new T.GridHelper(12,12,'#505b64','#505b64');grid.position.y=-.103;grid.material.opacity=.45;grid.material.transparent=true;scene.add(grid);
  for(let i=-8;i<=8;i++)for(const z of [-5.6,5.6])box([1.38,5,.13],[i*1.42,2.38,z],wall);
  for(const z of [-5.25,5.25]){box([26,.11,.22],[0,.12,z],black);box([26,.022,.025],[0,.2,z+(z<0?.13:-.13)],glow('#bacbd7',1.3));}
  const plinth=new T.Group();plinth.scale.set(.7,1,.7);scene.add(plinth);
  cylinder(1,.13,[0,-.025,0],black,plinth);cylinder(.91,.055,[0,.067,0],steel,plinth);cylinder(.83,.016,[0,.103,0],dark,plinth);
  const ring=mesh(new T.TorusGeometry(.915,.006,8,160),glow('#f4b64d',.8),plinth,[0,.071,0]);ring.rotation.x=Math.PI/2;
  for(let i=0;i<24;i++){const a=i*Math.PI/12;cylinder(.012,.01,[Math.cos(a)*.865,.102,Math.sin(a)*.865],black,plinth,6);}
  for(const x of [-1.4,1.4])for(const z of [-1.4,1.4]){box([.42,.002,.022],[x,-.099,z],mat('#be914b',.25,.44));box([.022,.002,.42],[x,-.099,z],mat('#be914b',.25,.44));}
  box([.9,1.65,.64],[-2.6,.725,-2.9]);box([.78,1.5,.018],[-2.6,.75,-2.566],black);
  for(let i=0;i<12;i++)box([.58,.012,.024],[-2.6,.24+i*.033,-2.55],steel);
  box([.54,.15,.024],[-2.6,1.24,-2.55],mat('#293e40',.25,.2));
  scene.add(new T.HemisphereLight('#b9c6d0','#39352d',.65));
  const lights=[];
  function spot(pos,target,color,intensity,shadow){const l=new T.SpotLight(color,intensity,14,T.MathUtils.degToRad(56),.6,1.3);l.position.set(...pos);l.target.position.set(...target);l.castShadow=shadow;l.shadow.mapSize.set(2048,2048);l.shadow.bias=-.0002;l.shadow.normalBias=.004;l.shadow.camera.near=.1;scene.add(l,l.target);lights.push(l);}
  spot([1.6,5,3.2],[.6,1,0],'#f4e7d2',32,true);spot([-1,3.8,-3.2],[.7,1.4,0],'#a0c7fa',40,true);spot([4,2.8,1.5],[.8,1,0],'#d8e8f3',14,false);spot([-3.2,2.5,1.2],[0,1,0],'#fff0d3',18,false);
  const collisionMeshes=[],collisionMaterial=new T.MeshPhysicalMaterial({color:'#ff263f',emissive:'#ff102b',emissiveIntensity:.5,metalness:0,roughness:.38,clearcoat:.1});
  function registerCollisionMeshes(parent,link){parent.traverse(object=>{if(object.isMesh){object.userData.collisionLink=link;object.userData.originalMaterial=object.material;collisionMeshes.push(object);}});}
  function showCollisions(links){const set=new Set(links);for(const object of collisionMeshes)object.material=set.has(object.userData.collisionLink)?collisionMaterial:object.userData.originalMaterial;}
  const mount=new T.Group();mount.position.y=.12;scene.add(mount);const joints=[];let parent=mount;
  for(let i=0;i<6;i++){const j=new T.Group();j.matrixAutoUpdate=false;j.matrix.copy(jointMatrix(i,HOME[i]));parent.add(j);joints.push(j);parent=j;}
  const tcp=new T.Group();tcp.matrixAutoUpdate=false;tcp.matrix.copy(TOOL);joints[5].add(tcp);
  const axes=new T.AxesHelper(.16);axes.material.depthTest=false;axes.renderOrder=20;tcp.add(axes);
  const torch=new T.Group();joints[5].add(torch);
  const torchSteel=mat('#8295a3',.85,.23),graphite=mat('#17242d',.3,.36),copper=mat('#bc754a',.78,.29),ceramic=mat('#d3c6a8',.06,.48),blue=mat('#237c92',.4,.32);
  function segment(a,b,r,m,parent=torch){a=new T.Vector3(...a);b=new T.Vector3(...b);const o=cylinder(r,a.distanceTo(b),a.clone().add(b).multiplyScalar(.5).toArray(),m,parent,32);o.quaternion.setFromUnitVectors(new T.Vector3(0,1,0),b.sub(a).normalize());return o;}
  segment([0,0,0],[0,-.012,0],.048,torchSteel);segment([0,-.012,0],[0,-.064,0],.032,graphite);segment([0,-.020,0],[0,-.034,0],.034,blue);
  for(let i=0;i<6;i++){const a=i*Math.PI/3;segment([Math.cos(a)*.04,0,Math.sin(a)*.04],[Math.cos(a)*.04,-.015,Math.sin(a)*.04],.004,graphite);}
  const neck=[[0,-.056,0],[0,-.09,0],[.01,-.114,0],[.035,-.144,0],[.054,-.174,0]];for(let i=1;i<neck.length;i++){segment(neck[i-1],neck[i],.013,torchSteel);mesh(new T.SphereGeometry(.013,16,8),torchSteel,torch,neck[i]);}
  segment([.046,-.160,0],[.063,-.189,0],.02,graphite);segment([.06,-.184,0],[.07,-.201,0],.017,ceramic);segment([.066,-.194,0],[.087,-.231,0],.014,copper);segment([.087,-.231,0],[.091,-.238,0],.006,graphite);segment([.089,-.234,0],[.1,-.2532,0],.0007,copper);
  for(const s of [-1,1]){segment([s*.024,-.018,.019],[s*.024,.016,.026],.005,graphite);segment([s*.024,-.023,.019],[s*.024,-.031,.019],.006,s===1?blue:copper);}registerCollisionMeshes(torch,7);
  const part=new T.Group();scene.add(part);let seamGroup=new T.Group();part.add(seamGroup);let partMesh,selection=null;
  const transform=new TransformControls(camera,renderer.domElement);scene.add(transform.getHelper());transform.setSize(.65);
  transform.addEventListener('dragging-changed',e=>{orbit.enabled=!e.value;});transform.addEventListener('objectChange',()=>onTransform(part));
  const raycaster=new T.Raycaster();raycaster.params.Line.threshold=.008;let down;
  renderer.domElement.addEventListener('pointerdown',e=>{down=[e.clientX,e.clientY];});renderer.domElement.addEventListener('pointerup',e=>{if(!down||Math.hypot(e.clientX-down[0],e.clientY-down[1])>4||transform.dragging)return;const r=renderer.domElement.getBoundingClientRect();raycaster.setFromCamera(new T.Vector2((e.clientX-r.left)/r.width*2-1,-(e.clientY-r.top)/r.height*2+1),camera);const hit=raycaster.intersectObjects(seamGroup.children)[0];if(hit)onSeam(hit.object.userData.id,{x:e.clientX,y:e.clientY});});
  const plannedPath=new T.Group();scene.add(plannedPath);
  function showProgram(program){
    for(const child of [...plannedPath.children]){child.geometry.dispose();child.material.dispose();plannedPath.remove(child);}
    const segments=plannedPathSegments(program);
    for(const kind of ['travel','weld']){if(!segments[kind].length)continue;const geometry=new T.BufferGeometry().setAttribute('position',new T.Float32BufferAttribute(segments[kind],3));const material=kind==='travel'?new T.LineDashedMaterial({color:'#f1b965',dashSize:.024,gapSize:.016,depthTest:false,transparent:true,opacity:.7,toneMapped:false}):new T.LineBasicMaterial({color:'#59f4cf',depthTest:false,transparent:true,opacity:.95,toneMapped:false});const line=new T.LineSegments(geometry,material);line.computeLineDistances();line.renderOrder=5;plannedPath.add(line);}
  }
  const beadGroup=new T.Group();scene.add(beadGroup);let lastBead=null,beads=[];
  const arcLight=new T.PointLight('#85cfff',0,.9,1);scene.add(arcLight);const arc=mesh(new T.SphereGeometry(.007,12,8),new T.MeshBasicMaterial({color:'#d7f7ff'}));arc.visible=false;arc.castShadow=false;
  const particleGeo=new T.BufferGeometry(),pp=new Float32Array(160*3);particleGeo.setAttribute('position',new T.BufferAttribute(pp,3));const sparks=new T.Points(particleGeo,new T.PointsMaterial({color:'#ffd090',size:.006,transparent:true,opacity:.85,blending:T.AdditiveBlending,depthWrite:false}));scene.add(sparks);sparks.visible=false;
  // Small radial sprites are smoke/effect masks, not decorative page artwork.
  const cv=document.createElement('canvas');cv.width=cv.height=64;const ctx=cv.getContext('2d'),g=ctx.createRadialGradient(32,32,0,32,32,32);g.addColorStop(0,'rgba(190,205,215,.5)');g.addColorStop(1,'rgba(190,205,215,0)');ctx.fillStyle=g;ctx.fillRect(0,0,64,64);const smokeTexture=new T.CanvasTexture(cv);const smoke=[];for(let i=0;i<14;i++){const s=new T.Sprite(new T.SpriteMaterial({map:smokeTexture,transparent:true,opacity:0,depthWrite:false}));scene.add(s);smoke.push(s);}
  let arcOn=false,time=0,settings={sparks:true,smoke:true,beads:true,trace:false},trail=[];const trailGeo=new T.BufferGeometry();let trailLine=new T.Line(trailGeo,new T.LineBasicMaterial({color:'#58c8be'}));scene.add(trailLine);
  function fit(view='home'){
    orbit.target.set(.4,.7,-.1);const d=3.25,yaw=view==='front'?0:view==='right'?Math.PI/2:2.35,pitch=view==='top'?1.569:view==='front'?.08:.22;
    camera.position.copy(orbit.target).add(new T.Vector3(Math.sin(yaw)*Math.cos(pitch),Math.sin(pitch),Math.cos(yaw)*Math.cos(pitch)).multiplyScalar(d));orbit.update();
  }fit();
  function resize(){const w=container.clientWidth,h=container.clientHeight;renderer.setSize(w,h);composer.setPixelRatio(renderer.getPixelRatio());composer.setSize(w,h);camera.aspect=w/h;camera.updateProjectionMatrix();}new ResizeObserver(resize).observe(container);resize();
  const hdrReady=new RGBELoader().loadAsync(`${import.meta.env.BASE_URL}assets/studio.hdr`).then(t=>{const pmrem=new T.PMREMGenerator(renderer);const env=pmrem.fromEquirectangular(t);scene.environment=env.texture;scene.environmentIntensity=.65;t.dispose();pmrem.dispose();});
  async function loadRobot(progress){const response=await fetch(`${import.meta.env.BASE_URL}assets/robot.meshbin`);if(!response.ok)throw Error('Robot CAD could not be loaded');const b=await response.arrayBuffer(),v=new DataView(b);let o=4;const int=()=>{const n=v.getInt32(o,true);o+=4;return n;},float=()=>{const n=v.getFloat32(o,true);o+=4;return n;};if(new TextDecoder().decode(b.slice(0,4))!=='ESI1')throw Error('Invalid robot model');const count=int();let triangles=0;
    for(let g=0;g<count;g++){const link=int(),rgba=[float(),float(),float(),float()],n=int(),indexCount=int(),p=new Float32Array(n*3),norm=new Float32Array(n*3);for(let i=0;i<n;i++){for(let k=0;k<3;k++)p[i*3+k]=float();for(let k=0;k<3;k++)norm[i*3+k]=float();}const indices=new Uint32Array(indexCount);for(let i=0;i<indexCount;i+=3){indices[i]=int();indices[i+2]=int();indices[i+1]=int();}const geo=new T.BufferGeometry();geo.setAttribute('position',new T.BufferAttribute(p,3));geo.setAttribute('normal',new T.BufferAttribute(norm,3));geo.setIndex(new T.BufferAttribute(indices,1));const color=new T.Color().setRGB(...rgba.slice(0,3),T.SRGBColorSpace),m=new T.MeshPhysicalMaterial({color,metalness:0,roughness:rgba[0]<.36?.48:.36,clearcoat:rgba[0]<.36?.02:.08,clearcoatRoughness:.4});registerCollisionMeshes(mesh(geo,m,link?joints[link-1]:mount),link);triangles+=indexCount/3;progress?.((g+1)/count);}await hdrReady;return triangles;
  }
  function loadPart(doc){transform.detach();if(partMesh){part.remove(partMesh);partMesh.geometry.dispose();partMesh.material.dispose();}const geo=new T.BufferGeometry();geo.setAttribute('position',new T.Float32BufferAttribute(doc.vertices,3));geo.setAttribute('normal',new T.Float32BufferAttribute(doc.normals,3));const indices=doc.indices.slice();for(let i=0;i<indices.length;i+=3)[indices[i+1],indices[i+2]]=[indices[i+2],indices[i+1]];geo.setIndex(indices);geo.computeBoundingBox();partMesh=mesh(geo,mat('#98abb9',.65,.32),part);const center=geo.boundingBox.getCenter(new T.Vector3());part.position.set(.85-center.x,.27-geo.boundingBox.min.y,-center.z);part.rotation.set(0,0,0,'YXZ');transform.attach(part);clearEffects();return part;}
  function seams(list,results,selected,included=new Set()){selection=selected;for(const c of [...seamGroup.children]){c.geometry.dispose();c.material.dispose();seamGroup.remove(c);}for(const s of list){const chosen=included.has(s.id),result=results?.find(r=>r.id===s.id),color=chosen?'#00ffff':result?(result.state==='Ready'?'#62dda0':'#ee7478'):'#e8b661';const geo=new T.BufferGeometry().setAttribute('position',new T.Float32BufferAttribute(s.points,3)),line=new T.Line(geo,new T.LineBasicMaterial({color,depthTest:false,transparent:true,opacity:chosen?1:.68,toneMapped:false}));line.renderOrder=8;line.userData.id=s.id;seamGroup.add(line);if(chosen)for(const overlay of selectedSeamOverlay(s.points,s.id))seamGroup.add(overlay);}}
  function clearEffects(){for(const b of [...beadGroup.children]){b.geometry.dispose();b.material.dispose();beadGroup.remove(b);}beads=[];lastBead=null;arcOn=false;trail=[];trailLine.geometry.dispose();trailLine.geometry=new T.BufferGeometry();}
  function render(dt,moving){time+=dt;orbit.update();const center=document.querySelector('.center-ui'),cr=container.getBoundingClientRect(),vr=center.getBoundingClientRect();if(!document.querySelector('.app').classList.contains('cinema'))camera.setViewOffset(cr.width,cr.height,cr.width/2-(vr.left-cr.left+vr.width/2),0,cr.width,cr.height);else camera.clearViewOffset();scene.updateMatrixWorld();const p=tcp.getWorldPosition(new T.Vector3());arc.position.copy(p);arcLight.position.copy(p);arc.visible=arcOn;arcLight.intensity=arcOn?3+Math.sin(time*137)*.8:0;sparks.visible=arcOn&&settings.sparks;
    if(arcOn&&settings.beads&&(!lastBead||lastBead.distanceTo(p)>.0025)){const b=mesh(new T.SphereGeometry(.0028,8,6),new T.MeshStandardMaterial({color:'#89939a',metalness:.85,roughness:.3,emissive:'#ff4e14',emissiveIntensity:2}),beadGroup,p.toArray());b.scale.set(1,.55,1);beads.push({mesh:b,time});lastBead=p.clone();}if(!arcOn)lastBead=null;
    for(const b of beads)b.mesh.material.emissiveIntensity=Math.max(0,2-(time-b.time)*.65);
    for(let i=0;i<160;i++){const age=(time*1.5+i*.037)%1,angle=i*2.399,spread=.02+((i*13)%37)*.003;pp[i*3]=p.x+Math.cos(angle)*age*spread;pp[i*3+1]=p.y+age*.24-age*age*.36;pp[i*3+2]=p.z+Math.sin(angle)*age*spread;}particleGeo.attributes.position.needsUpdate=true;
    smoke.forEach((s,i)=>{const age=(time*.65+i/14)%1;s.position.copy(p).add(new T.Vector3(Math.sin(i*12)*age*.03,age*.27,Math.cos(i*12)*age*.03));s.scale.setScalar(.02+age*.14);s.material.opacity=arcOn&&settings.smoke?.22*Math.sin(age*Math.PI):0;});
    if(settings.trace&&moving){if(!trail.length||trail.at(-1).distanceTo(p)>.006){trail.push(p);if(trail.length>1600)trail.shift();trailLine.geometry.dispose();trailLine.geometry=new T.BufferGeometry().setFromPoints(trail);}}trailLine.visible=settings.trace;composer.render();
  }
  return {renderer,scene,camera,orbit,navigation,part,transform,tcp,loadRobot,loadPart,seams,render,fit,clearEffects,showProgram,showCollisions,
    setAngles(a){joints.forEach((j,i)=>{j.matrix.copy(jointMatrix(i,a[i]));j.matrixWorldNeedsUpdate=true;});},
    setArc(on){arcOn=on;},setAxes(on){axes.visible=on;},setTrace(on){settings.trace=on;},
    setMode(mode){transform.setMode(mode);},setGizmo(on){transform.enabled=on;transform.getHelper().visible=on;},
    applySettings(s){settings={...settings,...s};if(s.shadows!==undefined)renderer.shadowMap.enabled=s.shadows;if(s.ao!==undefined)ao.enabled=s.ao;if(s.bloom!==undefined)bloom.enabled=s.bloom;if(s.exposure!==undefined)renderer.toneMappingExposure=s.exposure;if(s.resolution!==undefined){renderer.setPixelRatio(Math.min(devicePixelRatio,2)*s.resolution);resize();}},
    capture(){return renderer.domElement.toDataURL('image/png');}
  };
}
