export class CollisionMonitor {
  constructor(request,apply){this.request=request;this.apply=apply;this.enabled=true;this.sceneId=null;this.revision=0;this.pending=false;this.lastKey='';this.lastAt=0;this.unavailable=false;}
  reset(){this.revision++;this.sceneId=null;this.lastKey='';this.apply([],null);}
  setEnabled(enabled){this.enabled=enabled;this.revision++;this.lastKey='';if(!enabled)this.apply([],null);}
  async tick(angles,document,transform,now,busy){
    if(!this.enabled||busy||this.pending||now-this.lastAt<60)return;
    const key=angles.map(v=>v.toFixed(4)).join(',');if(key===this.lastKey&&this.sceneId)return;
    this.lastAt=now;this.pending=true;const revision=this.revision;
    try{
      const body=this.sceneId?{sceneId:this.sceneId,startAngles:angles.slice()}:{document,partTransform:transform,startAngles:angles.slice()};
      const result=await this.request(body);if(revision!==this.revision||!this.enabled)return;
      this.sceneId=result.sceneId;this.lastKey=key;this.unavailable=false;this.apply(result.links||[],result);
    }catch(error){if(revision!==this.revision)return;this.sceneId=null;this.lastKey='';this.lastAt=now+1800;this.unavailable=true;this.apply([], {unavailable:true,reason:error.message});}
    finally{this.pending=false;}
  }
}
