import { Spherical, Vector3 } from 'three';

export function createCameraNavigation(camera,orbit,fitTarget=new Vector3(.4,.7,-.1),fitDistance=3.25){
  function angles(){const p=camera.position.clone().sub(orbit.target);return {yaw:Math.atan2(p.x,p.z),pitch:Math.atan2(p.y,Math.hypot(p.x,p.z))};}
  function rotate(yaw,pitch){
    // Consume pending OrbitControls inertia before snapping; otherwise it rotates
    // the newly selected principal view during the next render frames.
    const damping=orbit.enableDamping;orbit.enableDamping=false;orbit.update();
    const radius=camera.position.distanceTo(orbit.target);
    const spherical=new Spherical(radius,Math.PI/2-pitch,yaw).makeSafe();
    camera.position.copy(orbit.target).add(new Vector3().setFromSpherical(spherical));
    orbit.update();orbit.enableDamping=damping;
  }
  function direction(value){const d=value.clone().normalize();rotate(Math.atan2(d.x,d.z),Math.asin(d.y));}
  return {angles,orbit:rotate,direction,preset(value){direction(value);orbit.target.copy(fitTarget);camera.position.copy(fitTarget).add(value.clone().normalize().multiplyScalar(fitDistance));direction(value);}};
}
