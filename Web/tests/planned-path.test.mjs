import test from 'node:test';
import assert from 'node:assert/strict';
import { plannedPathSegments } from '../src/planned-path.js';
import { forward,pose,HOME } from '../src/kinematics.js';
test('planned paths include the start pose and separate rapid from welding interpolation',()=>{
  const program={startJoints:HOME,moves:[{joints:[10,90,0,0,90,0],arc:false},{joints:[12,90,0,0,90,0],arc:true}]};
  const lines=plannedPathSegments(program);assert.equal(lines.travel.length,5*6);assert.equal(lines.weld.length,6);
  const start=pose(forward(HOME)).position;assert.deepEqual(lines.travel.slice(0,3),[start.x,start.y+.12,start.z]);assert.deepEqual(lines.travel.slice(-3),lines.weld.slice(0,3));
});
