import test from 'node:test';
import assert from 'node:assert/strict';
import { createAngleRange } from '../src/angle-range.js';

class Element {
  constructor(name){this.name=name;this.attributes={};this.children=[];this.events={};this.captures=new Set();}
  setAttribute(k,v){this.attributes[k]=String(v);}hasAttribute(k){return Object.hasOwn(this.attributes,k);}
  append(n){this.children=this.children.filter(x=>x!==n);this.children.push(n);}replaceChildren(...nodes){this.children=nodes;}
  addEventListener(type,fn){(this.events[type]??=[]).push(fn);}dispatch(type,extra={}){const event={pointerId:1,button:0,buttons:1,clientX:140,clientY:8,preventDefault(){},stopPropagation(){},...extra};for(const fn of this.events[type]||[])fn(event);}
  setPointerCapture(id){this.captures.add(id);}hasPointerCapture(id){return this.captures.has(id);}releasePointerCapture(id){this.captures.delete(id);}getScreenCTM(){return {inverse(){return {};}};}
}
test('angle drag requires a handle press, keeps capture outside, and ends on release anywhere',()=>{
  const before={document:globalThis.document,window:globalThis.window,DOMPoint:globalThis.DOMPoint};
  globalThis.document={createElementNS:(ns,name)=>new Element(name)};globalThis.window=new Element('window');globalThis.DOMPoint=class{constructor(x,y){this.x=x;this.y=y;}matrixTransform(){return this;}};
  try{
    const container=new Element('div'),minimum={value:85},maximum={value:95};let changes=0;createAngleRange(container,minimum,maximum,()=>changes++);const svg=container.children[0],handle=svg.children.find(x=>x.name==='circle');
    globalThis.window.dispatch('pointermove',{buttons:0,clientX:0});assert.equal(changes,0);
    handle.dispatch('pointerdown',{clientX:140,clientY:8});assert.ok(handle.hasPointerCapture(1));const count=changes;
    globalThis.window.dispatch('pointermove',{clientX:-500,clientY:108});assert.ok(changes>count);assert.equal(+minimum.value,0);assert.ok(handle.hasPointerCapture(1));
    globalThis.window.dispatch('pointerup',{clientX:-500});assert.equal(handle.hasPointerCapture(1),false);const released=changes;
    globalThis.window.dispatch('pointermove',{buttons:0,clientX:140,clientY:8});assert.equal(changes,released);
    handle.dispatch('pointerdown',{button:2,buttons:2});assert.equal(changes,released);
  }finally{for(const [key,value] of Object.entries(before)){if(value===undefined)delete globalThis[key];else globalThis[key]=value;}}
});
