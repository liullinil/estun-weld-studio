import test from 'node:test';
import assert from 'node:assert/strict';
import { CollisionMonitor } from '../src/collision-monitor.js';
test('collision scenes are reused by pose; switching off restores materials and ignores pending responses',async()=>{
  const requests=[],applied=[];let release;
  const monitor=new CollisionMonitor(async body=>{requests.push(body);if(requests.length===3)return await new Promise(r=>release=r);return {sceneId:'scene',links:requests.length===1?[2,4]:[],clear:requests.length!==1};},links=>applied.push(links));
  const q=[0,90,0,0,90,0],doc={vertices:[]};await monitor.tick(q,doc,{},500,false);assert.deepEqual(applied.at(-1),[2,4]);
  await monitor.tick(q,doc,{},800,false);assert.equal(requests.length,1);
  await monitor.tick([1,90,0,0,90,0],doc,{},1100,false);assert.deepEqual(requests[1],{sceneId:'scene',startAngles:[1,90,0,0,90,0]});
  const pending=monitor.tick([2,90,0,0,90,0],doc,{},1400,false);monitor.setEnabled(false);release({sceneId:'scene',links:[1,5]});await pending;assert.deepEqual(applied.at(-1),[]);
});
test('workpiece changes reset cached scene and stale results cannot recolor the new scene',async()=>{
  let done;const applied=[];const m=new CollisionMonitor(()=>new Promise(r=>done=r),links=>applied.push(links));const request=m.tick([0,0,0,0,0,0],{}, {},500,false);m.reset();done({sceneId:'old',links:[3]});await request;assert.equal(m.sceneId,null);assert.deepEqual(applied.at(-1),[]);
});
