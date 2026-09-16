import test from 'node:test';
import assert from 'node:assert/strict';
import { Raycaster, Vector3 } from 'three';
import { selectedSeamOverlay, SELECTED_SEAM_COLOR } from '../src/seam-highlight.js';

test('selected seam has a bright screen-space core and halo without intercepting CAD picks',()=>{
  const layers=selectedSeamOverlay([0,0,0,.21,0,0],'S001');
  assert.equal(SELECTED_SEAM_COLOR,'#00ffff');assert.equal(layers.length,2);
  assert.deepEqual(layers.map(x=>x.material.linewidth),[11,5]);
  for(const line of layers){assert.equal(line.material.worldUnits,false);assert.equal(line.material.depthTest,false);assert.equal(line.material.toneMapped,false);assert.equal(line.userData.id,'S001');assert.deepEqual(new Raycaster(new Vector3(0,0,1),new Vector3(0,0,-1)).intersectObject(line),[]);line.geometry.dispose();line.material.dispose();}
});
