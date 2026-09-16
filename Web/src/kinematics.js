import { Matrix4, Vector3, Quaternion, MathUtils } from 'three';
export const HOME = [0,90,0,0,90,0];
export const LIMITS = [360,360,160,360,360,360];
export const SPEEDS = [110,110,150,180,180,180];
export const REST = [
  [-1,0,0,0, 0,1,0,0, 0,0,-1,0, 0,0,0,1],
  [0,0,1,0, -1,0,0,.216, 0,-1,0,.255, 0,0,0,1],
  [0,0,-1,0, 0,1,0,0, 1,0,0,.850, 0,0,0,1],
  [1,0,0,0, 0,1,0,.203, 0,0,1,.765, 0,0,0,1],
  [1,0,0,0, 0,0,-1,-.167, 0,1,0,0, 0,0,0,1],
  [1,0,0,0, 0,0,1,.1625, 0,-1,0,.1625, 0,0,0,1]
].map(a=>new Matrix4().set(...a));
export const TOOL = new Matrix4().makeRotationZ(Math.PI/6).setPosition(.1,-.2532,0);
export function jointMatrix(i,a){return REST[i].clone().multiply(new Matrix4().makeRotationY(-MathUtils.degToRad(a)));}
export function forward(angles){let m=new Matrix4(); for(let i=0;i<6;i++) m.multiply(jointMatrix(i,angles[i])); return m.multiply(TOOL);}
export function pose(m){return {position:new Vector3().setFromMatrixPosition(m),quaternion:new Quaternion().setFromRotationMatrix(m)};}
function rotationError(a,b){const q=a.clone().multiply(b.clone().invert()).normalize();if(q.w<0){q.x*=-1;q.y*=-1;q.z*=-1;q.w*=-1;}const v=new Vector3(q.x,q.y,q.z),l=v.length();return v.multiplyScalar(l<1e-6?2:2*Math.atan2(l,MathUtils.clamp(q.w,-1,1))/l);}
function linear(a,b){a=a.map(r=>r.slice());b=b.slice();for(let c=0;c<6;c++){let p=c;for(let r=c+1;r<6;r++)if(Math.abs(a[r][c])>Math.abs(a[p][c]))p=r;if(Math.abs(a[p][c])<1e-12)return null;[a[c],a[p]]=[a[p],a[c]];[b[c],b[p]]=[b[p],b[c]];const d=a[c][c];for(let k=c;k<6;k++)a[c][k]/=d;b[c]/=d;for(let r=0;r<6;r++)if(r!==c){const f=a[r][c];for(let k=c;k<6;k++)a[r][k]-=f*a[c][k];b[r]-=f*b[c];}}return b;}
export function solve(target,initial,positionTolerance=.0002,orientationTolerance=.001){
  if(initial.length!==6||initial.some(x=>!Number.isFinite(x))||target.elements.some(x=>!Number.isFinite(x)))return null;
  const desired=pose(target);let a=initial.slice(),damping=.008;
  for(let it=0;it<48;it++){
    const cur=pose(forward(a)),dp=desired.position.clone().sub(cur.position),dr=rotationError(desired.quaternion,cur.quaternion);
    if(dp.length()<=positionTolerance&&dr.length()<=orientationTolerance)return a;
    const error=[...dp.toArray(),...dr.clone().multiplyScalar(.4).toArray()],j=Array.from({length:6},()=>Array(6).fill(0));
    for(let c=0;c<6;c++){const b=a.slice();b[c]+=MathUtils.radToDeg(.001);const p=pose(forward(b)),jp=p.position.sub(cur.position).multiplyScalar(1000),jr=rotationError(p.quaternion,cur.quaternion).multiplyScalar(400);[...jp.toArray(),...jr.toArray()].forEach((v,r)=>j[r][c]=v);}
    let improved=false;const cost=dp.lengthSq()+.16*dr.lengthSq();
    for(let attempt=0;attempt<5;attempt++){
      const s=Array.from({length:6},(_,r)=>Array.from({length:6},(_,c)=>j[r].reduce((sum,v,k)=>sum+v*j[c][k],0)+(r===c?damping*damping:0))),dual=linear(s,error);if(!dual)break;
      const step=Array.from({length:6},(_,c)=>j.reduce((sum,r,k)=>sum+r[c]*dual[k],0)),scale=Math.min(1,.12/Math.max(...step.map(Math.abs))),trial=a.map((v,i)=>MathUtils.clamp(v+MathUtils.radToDeg(step[i]*scale),-LIMITS[i],LIMITS[i])),candidate=pose(forward(trial));
      const next=desired.position.distanceToSquared(candidate.position)+.16*rotationError(desired.quaternion,candidate.quaternion).lengthSq();
      if(Number.isFinite(next)&&next<cost){a=trial;damping=Math.max(.002,damping*.6);improved=true;break;}damping=Math.min(.5,damping*3);
    }if(!improved)break;
  }return null;
}
