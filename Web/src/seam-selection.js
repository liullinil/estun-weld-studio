export function seamLength(seam){let length=0;for(let i=3;i<seam.points.length;i+=3)length+=Math.hypot(seam.points[i]-seam.points[i-3],seam.points[i+1]-seam.points[i-2],seam.points[i+2]-seam.points[i-1]);return length;}

export function potentialSeams(seams,{minimumAngle=85,maximumAngle=95,minimumLength=0,maximumLength=Infinity,concaveOnly=false}={}){
  return seams.filter(s=>s.minAngleDegrees>=minimumAngle-.05&&s.maxAngleDegrees<=maximumAngle+.05&&seamLength(s)*1000>=minimumLength-.001&&seamLength(s)*1000<=maximumLength+.001&&(!concaveOnly||s.concave||s.kind.toLowerCase().includes('contact')));
}

export function connectedSeams(seams,tolerance=.00002){
  const graph=new Map(seams.map(s=>[s.id,new Set()])),buckets=new Map();
  for(const seam of seams){
    if(seam.points.length<6)continue;
    for(const point of [seam.points.slice(0,3),seam.points.slice(-3)]){
      const cell=point.map(v=>Math.floor(v/tolerance));
      for(let dx=-1;dx<=1;dx++)for(let dy=-1;dy<=1;dy++)for(let dz=-1;dz<=1;dz++){
        const neighbors=buckets.get([cell[0]+dx,cell[1]+dy,cell[2]+dz].join(','))||[];
        for(const other of neighbors)if(other.id!==seam.id&&Math.hypot(...point.map((v,i)=>v-other.point[i]))<=tolerance){graph.get(seam.id).add(other.id);graph.get(other.id).add(seam.id);}
      }
      const key=cell.join(',');if(!buckets.has(key))buckets.set(key,[]);buckets.get(key).push({id:seam.id,point});
    }
  }
  return graph;
}

export class SeamSelection {
  constructor(){this.selected=new Set();this.scopes=new Map();this.graph=new Map();this.transaction=null;}
  setCandidates(seams){this.finish();this.graph=connectedSeams(seams);for(const id of this.selected)if(!this.graph.has(id))this.selected.delete(id);}
  reset(){this.selected.clear();this.scopes.clear();this.transaction=null;this.graph.clear();}
  component(id){if(!this.graph.has(id))return [];const indices=new Map([...this.graph.keys()].map((key,i)=>[key,i])),seen=new Set([id]),order=[id];let frontier=[id];while(frontier.length){const next=[...new Set(frontier.flatMap(key=>[...this.graph.get(key)]).filter(key=>!seen.has(key)))].sort((a,b)=>indices.get(a)-indices.get(b));for(const key of next){seen.add(key);order.push(key);}frontier=next;}return order;}
  idsFor(id,scope){const order=this.component(id);return new Set(order.slice(0,Math.min(order.length,Math.max(1,Math.round(scope)+1))));}
  begin(id){
    this.finish();if(!this.graph.has(id))return null;
    const adding=!this.selected.has(id),order=this.component(id),scope=Math.min(this.scopes.get(id)??0,order.length-1);
    this.transaction={id,adding,scope,order,before:new Set(this.selected),beforeScopes:new Map(this.scopes)};
    this.applyScope(scope);return this.transaction;
  }
  applyScope(scope){
    const t=this.transaction;if(!t)return;scope=Math.max(0,Math.min(t.order.length-1,Math.round(scope)));t.scope=scope;
    this.selected=new Set(t.before);this.scopes=new Map(t.beforeScopes);const affected=new Set(t.order.slice(0,scope+1));
    for(const id of affected){if(t.adding){this.selected.add(id);if(!t.before.has(id))this.scopes.set(id,scope);}else this.selected.delete(id);}
    // Remember the add scope for removals and later additions, even if the popup
    // is opened from a propagated neighbor rather than its original seed.
    if(t.adding)this.scopes.set(t.id,scope);t.affected=affected;
  }
  finish(){this.transaction=null;}
}
