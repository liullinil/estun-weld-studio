import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { randomUUID } from 'node:crypto';

export class CadWorker {
  constructor(python,script){this.python=python;this.script=script;this.process=null;this.ready=null;this.pending=null;}
  warm(){
    if(this.ready)return this.ready;
    this.ready=new Promise((resolve,reject)=>{
      const proc=spawn(this.python,[this.script,'--daemon'],{stdio:['pipe','pipe','pipe'],windowsHide:true});this.process=proc;
      const timer=setTimeout(()=>{proc.kill();reject(Error('CAD runtime startup timed out'));},45000);
      const stdout=createInterface({input:proc.stdout}),stderr=createInterface({input:proc.stderr});
      stdout.on('line',line=>{
        if(line==='CAD_READY'){clearTimeout(timer);resolve();return;}
        if(!line.startsWith('CAD_RESULT '))return;
        try{const answer=JSON.parse(line.slice(11));if(this.pending?.id===answer.id){const pending=this.pending;this.pending=null;answer.ok?pending.resolve():pending.reject(Error(answer.error||'CAD import failed'));}}catch{}
      });
      stderr.on('line',line=>{if(this.process===proc&&line.startsWith('PROGRESS '))this.pending?.progress?.(line.slice(9));});
      const failure=e=>{clearTimeout(timer);if(this.process===proc){this.process=null;this.ready=null;const pending=this.pending;this.pending=null;pending?.reject(e);}reject(e);};
      proc.stdin.on('error',failure);proc.on('error',failure);proc.on('exit',()=>{stdout.close();stderr.close();failure(Error('CAD worker stopped'));});
    });
    return this.ready;
  }
  async import(input,output,progress,signal){
    if(signal?.aborted)throw Error('Import cancelled');
    const cancelWarm=()=>this.process?.kill('SIGKILL');signal?.addEventListener('abort',cancelWarm,{once:true});
    try{await this.warm();}finally{signal?.removeEventListener('abort',cancelWarm);}
    if(signal?.aborted)throw Error('Import cancelled');if(this.pending)throw Error('CAD worker is busy');
    await new Promise((resolve,reject)=>{
      const id=randomUUID(),timer=setTimeout(()=>abort('CAD import exceeded five minutes'),300000);
      const cleanup=()=>{clearTimeout(timer);signal?.removeEventListener('abort',onAbort);};
      const abort=message=>{const proc=this.process;this.process=null;this.ready=null;this.pending=null;proc?.kill('SIGKILL');cleanup();reject(Error(message));};
      const onAbort=()=>abort('Import cancelled');signal?.addEventListener('abort',onAbort,{once:true});
      this.pending={id,progress,resolve:()=>{cleanup();resolve();},reject:e=>{cleanup();reject(e);}};
      const proc=this.process;if(!proc||proc.stdin.destroyed){cleanup();this.pending=null;reject(Error('CAD worker is unavailable'));return;}
      proc.stdin.write(JSON.stringify({id,input,output})+'\n',error=>{if(error&&this.pending?.id===id){const pending=this.pending;this.pending=null;pending.reject(error);}});
    });
  }
}
