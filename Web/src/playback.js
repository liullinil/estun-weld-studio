export class ProgramPlayback {
  constructor(){this.program=null;this.state='manual';this.index=0;this.elapsed=0;this.start=[];this.angles=[];this.arc=false;this.collision={links:[],reasons:[]};}
  get automatic(){return this.state==='playing'||this.state==='paused';}
  get ready(){return !!this.program?.moves?.length&&this.state==='ready';}
  load(program){this.program=program;this.state=program?.moves?.length?'ready':'manual';this.arc=false;this.collision={links:[],reasons:[]};}
  startFrom(angles){if(!this.ready||Math.max(...angles.map((v,i)=>Math.abs(v-this.program.startJoints[i])))>.3)return false;this.angles=angles.slice();this.start=angles.slice();this.index=0;this.elapsed=0;this.state='playing';this.arc=false;return true;}
  pause(){if(this.state!=='playing')return false;this.state='paused';this.arc=false;return true;}
  resume(){if(this.state!=='paused')return false;this.state='playing';return true;}
  stop(){this.program=null;this.state='manual';this.arc=false;this.collision={links:[],reasons:[]};}
  tick(dt,override){
    if(this.state!=='playing')return null;
    let budget=Math.max(0,Math.min(dt,.05))*override;
    for(let guard=0;this.state==='playing'&&guard<1000&&budget>0;guard++){
      const m=this.program.moves[this.index],duration=Math.max(.001,m.duration),step=Math.min(budget,duration-this.elapsed);this.elapsed+=step;budget-=step;
      const t=Math.min(1,this.elapsed/duration);this.angles=this.start.map((v,i)=>v+(m.joints[i]-v)*t);this.arc=!!m.arc;
      const frames=m.collisionFrames||[];let frame={links:[],reasons:[]};for(const item of frames){if(item.t>t)break;frame=item;}this.collision=frame;
      if(this.elapsed>=duration-1e-8){this.index++;if(this.index>=this.program.moves.length){this.state='complete';this.arc=false;}else{this.start=this.angles.slice();this.elapsed=0;}}
    }
    return this.angles.slice();
  }
}
