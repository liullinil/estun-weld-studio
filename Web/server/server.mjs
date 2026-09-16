import express from 'express';
import multer from 'multer';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm, stat } from 'node:fs/promises';
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
app.disable('x-powered-by');
app.use((req,res,next)=>{res.setHeader('X-Content-Type-Options','nosniff');res.setHeader('Referrer-Policy','same-origin');if(!['GET','HEAD'].includes(req.method)&&req.headers.origin){try{if(new URL(req.headers.origin).host!==req.headers.host)return res.status(403).json({error:'Cross-origin requests are disabled'});}catch{return res.status(403).json({error:'Invalid origin'});}}next();});
router.get('/health',async(req,res)=>{let p;try{p=await fetch(`${planner}/health`,{signal:AbortSignal.timeout(3000)}).then(r=>r.json());}catch{p={ok:false};}res.json({ok:true,service:'ESTUN Weld Studio',planner:p});});
router.post('/import',(req,res,next)=>{if(importing||activePlan)return res.status(429).json({error:'The CAD worker is busy. Try again after planning/import finishes.'});importing=true;upload.single('file')(req,res,error=>{if(error){importing=false;return next(error);}if(!req.file||!/\.(step|stp)$/i.test(req.file.originalname)){importing=false;return res.status(400).json({error:'Choose a STEP file (.step or .stp)'});}next();});},async(req,res,next)=>{
  if(!req.file)return res.status(400).json({error:'Choose a STEP file'});
  if(!/\.(step|stp)$/i.test(req.file.originalname))return res.status(400).json({error:'Choose a STEP file (.step or .stp)'});
  importing=true;let temp;
  try{
    temp=await mkdtemp(path.join(tmpdir(),'estun-web-'));const input=path.join(temp,'part.step'),output=path.join(temp,'part.json');
    const {writeFile}=await import('node:fs/promises');await writeFile(input,req.file.buffer);
    await new Promise((resolve,reject)=>{const proc=spawn(python,[worker,'--input',input,'--output',output],{stdio:['ignore','pipe','pipe'],windowsHide:true});let diagnostics='';const collect=b=>{diagnostics=(diagnostics+b.toString()).slice(-2400);};proc.stdout.on('data',collect);proc.stderr.on('data',collect);const timer=setTimeout(()=>{proc.kill('SIGKILL');reject(Error('CAD import exceeded five minutes. Import a smaller subassembly.'));},300000);const closed=()=>{if(!res.writableEnded)proc.kill('SIGKILL');};res.on('close',closed);proc.on('error',e=>{clearTimeout(timer);res.off('close',closed);reject(e);});proc.on('exit',code=>{clearTimeout(timer);res.off('close',closed);code===0?resolve():reject(Error(diagnostics.match(/CAD import failed:([^\n]+)/)?.[0]||'OpenCascade could not read this STEP file.'));});});
    if((await stat(output)).size>60*1024*1024)throw Error('Tessellated part exceeds the 60 MB browser limit. Import a smaller subassembly.');
    const document=JSON.parse(await readFile(output,'utf8'));res.json({name:path.basename(req.file.originalname).replace(/\.(stp|step)$/i,''),document});
  }catch(e){next(e);}finally{importing=false;if(temp)await rm(temp,{recursive:true,force:true});}
});
router.use('/planner',express.json({limit:'64mb'}));
router.use('/planner',async(req,res,next)=>{
  const allowed=req.method==='GET'&&/^\/(health|jobs\/[a-zA-Z0-9-]+)$/.test(req.path)||req.method==='POST'&&/^\/(plan|check)$/.test(req.path)||req.method==='DELETE'&&/^\/jobs\/[a-zA-Z0-9-]+$/.test(req.path);
  if(!allowed)return res.status(404).json({error:'Unknown planner endpoint'});
  if(req.method==='POST'&&importing)return res.status(429).json({error:'CAD import is in progress'});
  try{const upstream=await fetch(planner+req.path,{method:req.method,headers:{'Content-Type':'application/json'},body:req.method==='POST'?JSON.stringify(req.body):undefined,signal:AbortSignal.timeout(45000)});const body=await upstream.text();let parsed;try{parsed=JSON.parse(body);}catch{return res.status(502).json({error:'Invalid planner response'});}if(req.path==='/plan'&&upstream.ok)activePlan=parsed.jobId;if(req.method==='GET'&&req.path.startsWith('/jobs/')&&['complete','failed','cancelled'].includes(parsed.status)&&req.path.endsWith(activePlan))activePlan=null;res.status(upstream.status).type('json').send(body);}catch(e){next(Error('Planning service is unavailable. Please try again shortly.'));}
});
// Clear a finished job even if the browser disconnects while planning.
const reap=setInterval(async()=>{const tracked=activePlan;if(!tracked)return;try{const response=await fetch(`${planner}/jobs/${tracked}`,{signal:AbortSignal.timeout(3000)}),j=await response.json();if(activePlan===tracked&&(response.status===404||['complete','failed','cancelled'].includes(j.status)))activePlan=null;}catch{}},5000);reap.unref();
app.use('/hyper/api',router);app.use('/api',router);
const assets=express.static(path.resolve(here,'../dist'),{etag:true,maxAge:'1h',setHeaders(res,file){if(file.endsWith('index.html'))res.setHeader('Cache-Control','no-cache');}});
app.get(/^\/hyper$/,(req,res)=>res.redirect(308,'/hyper/'));app.use('/hyper',assets);app.use('/',assets);
app.use((err,req,res,next)=>{console.error('Request error:',err.message);if(res.headersSent)return next(err);res.status(err.code==='LIMIT_FILE_SIZE'||err.type==='entity.too.large'?413:400).json({error:err.code==='LIMIT_FILE_SIZE'?'Browser import limit: 64 MB':err.message||'Request failed'});});
const server=app.listen(port,process.env.HOST||'127.0.0.1',()=>console.log(`ESTUN browser gateway http://${process.env.HOST||'127.0.0.1'}:${port}/hyper/`));server.requestTimeout=360000;
