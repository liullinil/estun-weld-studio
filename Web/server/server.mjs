import express from 'express';
import multer from 'multer';
import { createHash, randomUUID } from 'node:crypto';
import { CadWorker } from './cad-worker.mjs';
import { mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here=path.dirname(fileURLToPath(import.meta.url)),root=path.resolve(here,'../..');
const app=express(),router=express.Router(),port=Number(process.env.PORT||18740);
const planner=process.env.PLANNER_URL||'http://127.0.0.1:18741';
const python=process.env.CAD_PYTHON||(process.platform==='win32'?path.join(root,'.tools/cad-python/python.exe'):'python3');
const worker=process.env.CAD_WORKER||path.join(root,'tools/cad_import.py');
const upload=multer({storage:multer.memoryStorage(),limits:{fileSize:64*1024*1024,files:1,fields:0}});
let importing=false,activePlan=null;
const cad=new CadWorker(python,worker),importJobs=new Map(),importCache=new Map();let cachedBytes=0;
cad.warm().catch(error=>console.error('CAD warmup:',error.message));
function cacheDocument(key,document,bytes){if(bytes>32*1024*1024)return;while(importCache.size>=4||cachedBytes+bytes>64*1024*1024){const first=importCache.keys().next().value;if(!first)break;cachedBytes-=importCache.get(first).bytes;importCache.delete(first);}importCache.set(key,{document,bytes});cachedBytes+=bytes;}
function importProgress(message){return message.startsWith('Reading')?.08:message.startsWith('Sewing')?.18:message.startsWith('Tessellating')?.25:message.startsWith('Recognizing')?.55:message.startsWith('Checking')?.72:message.startsWith('Imported')?.95:.05;}
async function runImport(job,file){let temp;const started=performance.now();
 try{const key=createHash('sha256').update(file.buffer).digest('hex'),cached=importCache.get(key);let document;
  if(cached){document=cached.document;job.message='Reusing unchanged CAD geometry';}else{temp=await mkdtemp(path.join(tmpdir(),'ency-cad-'));const input=path.join(temp,'part'+path.extname(file.originalname).toLowerCase()),output=path.join(temp,'part.json');await writeFile(input,file.buffer);file.buffer=null;
   await cad.import(input,output,message=>{job.message=message;job.progress=importProgress(message);},job.cancel.signal);
   const info=await stat(output);if(info.size>60*1024*1024)throw Error('Tessellated part exceeds the 60 MB browser limit');document=JSON.parse(await readFile(output,'utf8'));cacheDocument(key,document,info.size);
  }
  if(job.cancel.signal.aborted)throw Error('Import cancelled');job.result={name:path.basename(file.originalname).replace(/\.(stp|step|igs|iges)$/i,''),document,timing:{seconds:(performance.now()-started)/1000,cached:!!cached}};job.status='complete';job.progress=1;job.message='CAD imported';
 }catch(error){job.status=job.cancel.signal.aborted?'cancelled':'failed';job.error=error.message;job.message=error.message;}finally{file.buffer=null;try{if(temp)await rm(temp,{recursive:true,force:true,maxRetries:3,retryDelay:150});}catch(error){console.error('CAD temporary cleanup:',error.message);}finally{importing=false;}}
}

app.disable('x-powered-by');
app.use((req,res,next)=>{res.setHeader('X-Content-Type-Options','nosniff');res.setHeader('Referrer-Policy','same-origin');if(!['GET','HEAD'].includes(req.method)&&req.headers.origin){try{if(new URL(req.headers.origin).host!==req.headers.host)return res.status(403).json({error:'Cross-origin requests are disabled'});}catch{return res.status(403).json({error:'Invalid origin'});}}next();});
router.get('/health',async(req,res)=>{let p;try{p=await fetch(`${planner}/health`,{signal:AbortSignal.timeout(3000)}).then(r=>r.json());}catch{p={ok:false};}res.json({ok:true,service:'ENCY HYPER - ESTUN',planner:p});});
router.post('/import',(req,res,next)=>{
 if(importing||activePlan)return res.status(429).json({error:'The CAD worker is busy. Wait for the current operation.'});importing=true;
 upload.single('file')(req,res,error=>{if(error){importing=false;return next(error);}if(!req.file||!/\.(step|stp|iges|igs)$/i.test(req.file.originalname)){importing=false;return res.status(400).json({error:'Choose a STEP or IGES file (.step, .stp, .iges, .igs)'});}next();});
},(req,res)=>{
 for(const [id,job] of importJobs)if(job.status!=='running'&&(Date.now()-job.created>600000||importJobs.size>=2))importJobs.delete(id);
 const job={id:randomUUID(),created:Date.now(),status:'running',progress:0,message:'Reading CAD…',result:null,error:'',cancel:new AbortController()};importJobs.set(job.id,job);void runImport(job,req.file);res.status(202).json({jobId:job.id,status:job.status});
});
router.route('/imports/:id').get((req,res)=>{const job=importJobs.get(req.params.id);if(!job)return res.status(404).json({error:'Unknown import job'});res.setHeader('Cache-Control','no-store');res.json({jobId:job.id,status:job.status,progress:job.progress,message:job.message,result:job.result,error:job.error});}).delete((req,res)=>{const job=importJobs.get(req.params.id);if(!job)return res.status(404).json({error:'Unknown import job'});if(job.status==='running')job.cancel.abort();res.json({status:'cancellation requested'});});
router.use('/planner',express.json({limit:'64mb'}));
router.use('/planner',async(req,res,next)=>{
  const allowed=req.method==='GET'&&/^\/(health|jobs\/[a-zA-Z0-9-]+)$/.test(req.path)||req.method==='POST'&&/^\/(plan|check|jobs\/[a-zA-Z0-9-]+\/export)$/.test(req.path)||req.method==='DELETE'&&/^\/jobs\/[a-zA-Z0-9-]+$/.test(req.path);
  if(!allowed)return res.status(404).json({error:'Unknown planner endpoint'});
  if(req.method==='POST'&&importing)return res.status(429).json({error:'CAD import is in progress'});
  try{const upstream=await fetch(planner+req.path,{method:req.method,headers:{'Content-Type':'application/json'},body:req.method==='POST'?JSON.stringify(req.body):undefined,signal:AbortSignal.timeout(45000)});const body=await upstream.text();let parsed;try{parsed=JSON.parse(body);}catch{return res.status(502).json({error:'Invalid planner response'});}if((req.path==='/plan'||req.path.endsWith('/export'))&&upstream.ok)activePlan=parsed.jobId;if(req.method==='GET'&&req.path.startsWith('/jobs/')&&['complete','failed','cancelled'].includes(parsed.status)&&req.path.endsWith(activePlan))activePlan=null;res.status(upstream.status).type('json').send(body);}catch(e){next(Error('Planning service is unavailable. Please try again shortly.'));}
});
// Clear a finished job even if the browser disconnects while planning.
const reap=setInterval(async()=>{const tracked=activePlan;if(!tracked)return;try{const response=await fetch(`${planner}/jobs/${tracked}`,{signal:AbortSignal.timeout(3000)}),j=await response.json();if(activePlan===tracked&&(response.status===404||['complete','failed','cancelled'].includes(j.status)))activePlan=null;}catch{}},5000);reap.unref();
app.use('/hyper/api',router);app.use('/api',router);
const assets=express.static(path.resolve(here,'../dist'),{etag:true,maxAge:'1h',setHeaders(res,file){if(file.endsWith('index.html'))res.setHeader('Cache-Control','no-cache');}});
app.get(/^\/hyper$/,(req,res)=>res.redirect(308,'/hyper/'));app.use('/hyper',assets);app.use('/',assets);
app.use((err,req,res,next)=>{console.error('Request error:',err.message);if(res.headersSent)return next(err);res.status(err.code==='LIMIT_FILE_SIZE'||err.type==='entity.too.large'?413:400).json({error:err.code==='LIMIT_FILE_SIZE'?'Browser import limit: 64 MB':err.message||'Request failed'});});
const server=app.listen(port,process.env.HOST||'127.0.0.1',()=>console.log(`ESTUN browser gateway http://${process.env.HOST||'127.0.0.1'}:${port}/hyper/`));server.requestTimeout=360000;
