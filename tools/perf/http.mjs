// Usage: NODE_TLS_REJECT_UNAUTHORIZED=0 node tools/perf/http.mjs https://localhost:5443 tools/perf/results/local-30d.json
// Synthetic loopback stack only; 5 repetitions per route/concurrency. Never use against production.
import {writeFileSync} from 'node:fs';
import {performance} from 'node:perf_hooks';
import {randomUUID} from 'node:crypto';
import {execFileSync} from 'node:child_process';
const base=process.argv[2]||'https://localhost:5443', output=process.argv[3];
if(!/^https?:\/\/(localhost|127\.0\.0\.1)(:|\/|$)/.test(base))throw Error('Loopback only');
let cookies=new Map(), owner='';
async function req(path,form){
 const begin=performance.now();let response;
 try{response=await fetch(base+path,{method:form?'POST':'GET',redirect:'manual',headers:{Cookie:[...cookies].map(([k,v])=>`${k}=${v}`).join('; '),...(form?{'Content-Type':'application/x-www-form-urlencoded','RequestVerificationToken':token,'HX-Request':'true','X-Gym-User':owner}:{})},body:form?new URLSearchParams(form):undefined,signal:AbortSignal.timeout(45000)});
 for(const c of response.headers.getSetCookie()){const [kv]=c.split(';');const ix=kv.indexOf('=');cookies.set(kv.slice(0,ix),kv.slice(ix+1));}
 const html=await response.text(), t=response.headers.get('server-timing')||'';
 return {ms:performance.now()-begin,status:response.status,bytes:Buffer.byteLength(html),app:+(/app;dur=([\d.]+)/.exec(t)?.[1]||0),db:+(/db;dur=([\d.]+)/.exec(t)?.[1]||0),queries:+(/desc="(\d+) queries/.exec(t)?.[1]||0),html};
 }catch(e){return {ms:performance.now()-begin,status:0,error:e.message};}
}
let token='';const login=await req('/account/login');token=/name="__RequestVerificationToken"[^>]*value="([^"]+)"/.exec(login.html)?.[1];
const signed=await req('/account/login',{email:'demo@example.test',password:'demo-password-123',__RequestVerificationToken:token});
if(signed.status!==302)throw Error('Login failed '+signed.status);
const today=await req('/today');if(today.status!==200)throw Error('Today failed');
token=/name="__RequestVerificationToken"[^>]*value="([^"]+)"/.exec(today.html)?.[1]||token;
owner=/data-user-id="([^"]+)"/.exec(today.html)?.[1]||'';
const train=await req('/train'), sid=/href="\/train\/sessions\/(\d+)"/.exec(train.html)?.[1];
if(!sid)throw Error('Missing active session');
const session=await req(`/train/sessions/${sid}`), seid=/(?:name="SessionExerciseId"[^>]*value="|data-session-exercise-id=")(\d+)/i.exec(session.html)?.[1];
if(!seid)throw Error('Missing session exercise');
const date='2026-11-05';
const cases=[['today','/today'],['train','/train'],['session',`/train/sessions/${sid}`],['food','/food'],['food-recent','/food/add?tab=recent&meal=Lunch'],['search','/food/add?tab=search&q=chicken&meal=Lunch'],['progress','/progress'],['settings','/settings'],['history','/train/history'],['parse','/food/parse',()=>({date,meal:'Lunch',text:'200g chicken breast, 150g cooked rice, a banana'})],['set','/train/sets',()=>({ClientId:randomUUID(),SessionExerciseId:seid,WeightKg:'60',Reps:'10',IsWarmup:'false',CompletedAt:new Date().toISOString()})]];
if(process.argv.includes('--faults')){
 const trials=[];
 for(let i=0;i<5;i++){
  const parsed=await req('/food/parse',{date,meal:'Lunch',text:'100g banana'});
  const runId=/name="parseRunId" value="(\d+)"/.exec(parsed.html)?.[1];if(!runId)throw Error('No parse id');
  const form={date,meal:'Lunch',parseRunId:runId,'items[0].Include':'true','items[0].Description':'Synthetic duplicate probe','items[0].Grams':'100','items[0].Kcal':'100','items[0].Protein':'10','items[0].Carbs':'10','items[0].Fat':'2'};
  const count=()=>+execFileSync('docker',['exec','plate-perf-db','psql','-U','gym','-d','gym_perf','-Atc',`SELECT count(*) FROM "FoodLog" WHERE "ParseRunId"=${runId}`],{encoding:'utf8'}).trim();
  const before=count(),first=await req('/food/confirm',form),afterFirst=count(),second=await req('/food/confirm',form),afterSecond=count();
  trials.push({run:i+1,firstStatus:first.status,secondStatus:second.status,before,afterFirst,afterSecond});
 }
 writeFileSync(output,JSON.stringify({environment:'local synthetic only; same unchanged baseline app; five independent parse runs submitted twice',trials},null,2));process.exit(trials.every(x=>x.firstStatus===302&&x.secondStatus===302)?0:1);
}
const samples=[],batches=[];let sequence=0;
const ramp=process.argv.includes('--ramp'),coldMode=process.argv.includes('--cold');
if(!ramp) cases.push(['barcode','/food/barcode?date=2026-11-05&meal=Lunch&code=4006381333931'],['food-log','/food/log-item',()=>({date,meal:'Lunch',foodItemId:'1',grams:'150',source:'Barcode'})],['food-repeat','/food/log-recent',()=>({date,meal:'Lunch',description:'Synthetic repeat',foodItemId:'1',grams:'150',kcal:'200',protein:'20',carbs:'20',fat:'4'})]);
for(const [name,path,form]of cases.filter(x=>!ramp||x[0]==='search')){
 if(coldMode){if(form)continue;const r=name==='today'?today:name==='train'?train:name==='session'?session:await req(path);samples.push({name,phase:'first-route-after-process-start',c:1,run:0,...clean(r)});continue;}
 const cold=await req(path,form?.()); samples.push({name,phase:'first-route',c:1,run:0,...clean(cold)});
 for(let i=0;i<5;i++)await req(path,form?.());
 for(const c of (ramp?[1,5,10,20,40]:[1,5]))for(let run=1;run<=5;run++){
 const n=ramp?Math.max(c,10):(name==='set'?5:10);let index=0;const batchStart=performance.now();
 await Promise.all(Array.from({length:c},async()=>{while(index++<n){const r=await req(path,form?.());samples.push({name,phase:'warm',c,run,sequence:sequence++,...clean(r)});}}));
 batches.push({name,c,run,n,ms:performance.now()-batchStart});
 writeFileSync(output,JSON.stringify({base,commit:'462ef118827b00839941eab87cceb2a106c93065',environment:'native linux-arm64 Release; app256MiB1CPU; db256MiB,noCPUcap; pool8; synthetic',sid,seid,samples,batches}));
 }
 console.log(name, samples.filter(s=>s.name===name&&s.phase==='warm').length);
}
function clean({html,...r}){return r;}

if(samples.some(s=>s.status!==(s.name.startsWith('food-log')||s.name==='food-repeat'?302:200)))process.exitCode=1;

if(coldMode)writeFileSync(output,JSON.stringify({environment:'five separate process restarts orchestrated externally; login/bootstrap required; not disk-cold',samples}));
