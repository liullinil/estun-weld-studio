import { Line2 } from 'three/addons/lines/Line2.js';
import { LineGeometry } from 'three/addons/lines/LineGeometry.js';
import { LineMaterial } from 'three/addons/lines/LineMaterial.js';

export const SELECTED_SEAM_COLOR = '#00ffff';
export function selectedSeamOverlay(points,id){
  const layers=[];
  for(const [width,opacity,order] of [[11,.22,30],[5,1,31]]){
    const geometry=new LineGeometry().setPositions(points);
    const material=new LineMaterial({color:SELECTED_SEAM_COLOR,linewidth:width,worldUnits:false,transparent:true,opacity,depthTest:false,depthWrite:false,toneMapped:false});
    const line=new Line2(geometry,material);line.renderOrder=order;line.userData.id=id;
    // The original one-pixel candidate remains the picking surface. Decoration
    // must not cover or steal clicks from adjacent candidate seams.
    line.raycast=()=>{};layers.push(line);
  }
  return layers;
}
