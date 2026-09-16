import test from 'node:test';
import assert from 'node:assert/strict';
import { PerspectiveCamera, Vector2, Vector3 } from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { FACES, projectCube, hitTestCube } from '../src/view-cube.js';
import { createCameraNavigation } from '../src/camera-navigation.js';

function setup(){const camera=new PerspectiveCamera(38,1,.06,50);camera.position.set(2,1,3);const orbit=new OrbitControls(camera,null);orbit.enableDamping=true;orbit.dampingFactor=.12;return {camera,orbit,navigation:createCameraNavigation(camera,orbit)};}
test('all six principal camera directions expose their corresponding clickable face, including bottom',()=>{
  const {camera,navigation}=setup();
  for(const face of FACES){navigation.direction(new Vector3(...face.normal));const cube=projectCube(camera.quaternion);assert.equal(cube.faces.length,1,face.id);assert.equal(cube.faces[0].id,face.id);assert.equal(hitTestCube(cube,cube.faces[0].center).id,face.id);}
});
test('visible face edges and corners make all 26 SwissCAM directions reachable',()=>{
  const {camera,navigation}=setup(),directions=new Set();
  for(const face of FACES){
    navigation.direction(new Vector3(...face.normal));const geometry=projectCube(camera.quaternion);
    const probes=[geometry.faces[0].center,...geometry.corners.map(c=>c.point),...geometry.edges.map(e=>e.a.clone().add(e.b).multiplyScalar(.5))];
    for(const point of probes){const hit=hitTestCube(geometry,point);assert.ok(hit);directions.add(hit.direction.toArray().join(','));navigation.direction(hit.direction);const actual=camera.position.clone().normalize(),expected=hit.direction.clone().normalize();assert.ok(actual.distanceTo(expected)<.000002);}
  }
  assert.equal(directions.size,26);
});
test('empty cube surroundings pass through and principal corners/edges take precedence',()=>{
  const {camera,navigation}=setup();navigation.direction(new Vector3(0,0,1));const cube=projectCube(camera.quaternion);
  assert.equal(hitTestCube(cube,new Vector2(82,10)),null);
  assert.equal(hitTestCube(cube,cube.corners[0].point).kind,'corner');
  assert.equal(hitTestCube(cube,cube.edges[0].a.clone().add(cube.edges[0].b).multiplyScalar(.5)).kind,'edge');
  assert.equal(hitTestCube(cube,new Vector2(52,52)).kind,'face');
});
test('cube remains synchronized across all camera quadrants and both poles',()=>{
  const {camera,navigation}=setup();
  for(let yaw=-Math.PI;yaw<=Math.PI;yaw+=Math.PI/4)for(const pitch of [-Math.PI/2,-.6154797,0,.6154797,Math.PI/2]){
    navigation.orbit(yaw,pitch);const cube=projectCube(camera.quaternion);
    assert.ok(cube.faces.length>=1&&cube.faces.length<=3);
    for(const face of cube.faces)for(const p of face.polygon)assert.ok(p.x>=0&&p.x<=104&&p.y>=0&&p.y<=104);
  }
});
test('snaps clear orbit inertia; edge/corner navigation preserves pan and distance, faces refit',()=>{
  const {camera,orbit,navigation}=setup();orbit.target.set(.2,.3,.4);orbit.update();const target=orbit.target.clone(),distance=camera.position.distanceTo(target);
  orbit._sphericalDelta.theta=.3;orbit._sphericalDelta.phi=.2;
  navigation.direction(new Vector3(1,1,0));const snapped=camera.position.clone();
  for(let i=0;i<20;i++)orbit.update();
  assert.ok(camera.position.distanceTo(snapped)<1e-10);assert.ok(orbit.target.distanceTo(target)<1e-10);assert.ok(Math.abs(camera.position.distanceTo(target)-distance)<1e-10);
  navigation.preset(new Vector3(0,-1,0));assert.ok(orbit.target.distanceTo(new Vector3(.4,.7,-.1))<1e-10);assert.ok(Math.abs(camera.position.distanceTo(orbit.target)-3.25)<1e-10);
});
