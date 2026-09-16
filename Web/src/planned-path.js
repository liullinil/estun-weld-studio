import { Vector3 } from 'three';
import { forward, pose } from './kinematics.js';

// Follow the same joint interpolation used by playback, not straight chords
// between requested TCP targets. The model's mounting plane is 120mm high.
export function plannedPathSegments(program){
  const travel=[],weld=[];if(!program?.moves?.length)return {travel,weld};
  let start=program.startJoints.slice(),previous=pose(forward(start)).position.add(new Vector3(0,.12,0));
  for(const move of program.moves){
    const summed=start.reduce((sum,v,i)=>sum+Math.abs(move.joints[i]-v),0),steps=Math.max(1,Math.ceil(summed/2));
    for(let step=1;step<=steps;step++){
      const q=start.map((v,i)=>v+(move.joints[i]-v)*step/steps),p=pose(forward(q)).position.add(new Vector3(0,.12,0));
      (move.arc?weld:travel).push(...previous.toArray(),...p.toArray());previous=p;
    }
    start=move.joints;
  }
  return {travel,weld};
}
