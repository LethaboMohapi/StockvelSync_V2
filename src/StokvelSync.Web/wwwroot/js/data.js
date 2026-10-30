export const isDemo=document.documentElement.dataset.demo==='true';
export let forcedOffline=false;
export const connected=()=>navigator.onLine && !forcedOffline;
export function setForcedOffline(value) { forcedOffline=value; }
const database=new Promise((resolve,reject)=>{
 const request=indexedDB.open(isDemo?'stokvelsync-demo-v1':'stokvelsync-v1',1);
 request.onupgradeneeded=()=>{request.result.createObjectStore('kv');request.result.createObjectStore('outbox',{keyPath:'id'});};
 request.onsuccess=()=>resolve(request.result);request.onerror=()=>reject(new Error('Browser storage is unavailable. Enable site storage to use StokvelSync.'));
});
async function store(name,mode,fn) { const db=await database;return new Promise((resolve,reject)=>{const tx=db.transaction(name,mode);const req=fn(tx.objectStore(name));let value;req.onsuccess=()=>value=req.result;tx.oncomplete=()=>resolve(value);tx.onerror=()=>reject(tx.error);tx.onabort=()=>reject(tx.error);}); }
export const get=key=>store('kv','readonly',s=>s.get(key));
export const put=(key,value)=>store('kv','readwrite',s=>s.put(value,key));
export const queueAll=()=>store('outbox','readonly',s=>s.getAll());
export const removeQueued=id=>store('outbox','readwrite',s=>s.delete(id));
const putQueued=row=>store('outbox','readwrite',s=>s.put(row));
export async function clearDevice() { await store('kv','readwrite',s=>s.clear());await store('outbox','readwrite',s=>s.clear()); }
export class ApiError extends Error { constructor(message,status=0){super(message);this.status=status;} }
export async function request(path,{method='GET',body,id}={}) {
 if(!connected())throw new ApiError('Offline',0);
 if(isDemo)return demoRequest(path,{method,body,id});
 let response;
 try {response=await fetch(path,{method,credentials:'same-origin',cache:'no-store',headers:{'Content-Type':'application/json','X-StokvelSync':'1',...(id?{'Idempotency-Key':id}:{})},...(body!==undefined?{body:JSON.stringify(body)}:{}),signal:AbortSignal.timeout(20000)});}
 catch {throw new ApiError('Could not reach the server. Check your connection.',0);}
 const json=await response.json().catch(()=>({}));
 if(!response.ok)throw new ApiError(json.error || json.title || 'Request failed.',response.status);
 return json;
}
export async function session() {
 if(isDemo)await ensureDemo();
 try {const data=await request('/api/me');await put('session',data);return data;}
 catch(e){if(e.status===401){await put('session',null);return null;}if(e.status===0){const cached=await get('session');if(cached)return {...cached,offline:true};return null;}throw e;}
}
export async function loadGroup(id) {
 try{const data=await request('/api/groups/'+id);await put('group:'+id,data);await put('lastGroup',id);return data;}
 catch(e){if(e.status===0){const data=await get('group:'+id);if(data)return data;}throw e;}
}
export async function mutation(groupId,path,body,method='POST',id=crypto.randomUUID()) {return request('/api/groups/'+groupId+path,{method,body,id});}
export async function contributeOffline(user,groupId,body) {
 const id=crypto.randomUUID();
 const row={id,userId:user.id,groupId,body,createdAt:new Date().toISOString(),state:'queued',error:null};
 // Persist before the first network request. A lost response can be replayed with the same ID.
 await putQueued(row);
 if(!connected())return {queued:true};
 try {await mutation(groupId,'/contributions',body,'POST',id);await removeQueued(id);return {queued:false};}
 catch(e){if(e.status===0 || e.status===401)return {queued:true};row.state='blocked';row.error=e.message;await putQueued(row);throw e;}
}
let syncing=false;
export async function syncQueue(userId) {
 if(syncing || !connected())return {sent:0};syncing=true;let sent=0;
 try {for(const row of await queueAll()){
  if(row.userId!==userId || row.state==='blocked')continue;
  try{await mutation(row.groupId,'/contributions',row.body,'POST',row.id);await removeQueued(row.id);sent++;}
  catch(e){if(e.status===0 || e.status===401)break;row.state='blocked';row.error=e.message;await putQueued(row);}
 }return {sent};}finally{syncing=false;}
}
export function cents(value) { if(!/^\d{1,7}(\.\d{1,2})?$/.test(String(value)))throw new Error('Use a rand amount with at most two decimals.');const n=Math.round(Number(value)*100);if(n<100 || n>100000000)throw new Error('Amount must be between R1 and R1,000,000.');return n; }
export function monthDate(start,offset){const [y,m,d]=start.split('-').map(Number);const first=new Date(Date.UTC(y,m-1+offset,1));const last=new Date(Date.UTC(first.getUTCFullYear(),first.getUTCMonth()+1,0)).getUTCDate();return new Date(Date.UTC(first.getUTCFullYear(),first.getUTCMonth(),Math.min(d,last))).toISOString().slice(0,10);}
export function today(){const p=new Intl.DateTimeFormat('en-CA',{timeZone:'Africa/Johannesburg',year:'numeric',month:'2-digit',day:'2-digit'}).formatToParts(new Date());return ['year','month','day'].map(k=>p.find(v=>v.type===k).value).join('-');}
export const currentPeriod=()=>today().slice(0,7);

async function ensureDemo() {
 if(await get('demo'))return;
 const names=['Nomsa Dlamini','Thabo Molefe','Lerato Nkosi','Sipho Mthembu','Ayanda Ndlovu','Pieter van Wyk','Zanele Khumalo','Bongani Mokoena'];
 const ids=names.map(()=>crypto.randomUUID()),groupId=crypto.randomUUID(),now=today(),period=currentPeriod();
 const members=names.map((name,i)=>({id:ids[i],name,phone:'+2782000000'+(i+1),language:['en','zu','en','xh','zu','af','zu','xh'][i],smsConsent:true,role:i===0?'treasurer':'member',joinedAt:monthDate(now,-7)+'T08:00:00Z'}));
 const group={id:groupId,name:'Ubuntu Savings Circle',description:'Building brighter futures, one contribution at a time.',location:'Soweto, Gauteng',contributionCents:50000,dueDay:5,role:'treasurer',joinCode:'ubuntu-demo-circle26'};
 const contributions=[],ledger=[],rotations=[];
 for(let month=-6;month<=0;month++)for(let i=0;i<(month===0?6:8);i++){
  const dt=monthDate(now.slice(0,8)+'05',month),id=crypto.randomUUID(),reference='UBU-'+dt.slice(0,7).replace('-','')+'-'+(i+1),createdAt=dt+'T'+String(8+i).padStart(2,'0')+':15:00Z';
  contributions.push({id,userId:ids[i],name:names[i],amountCents:50000,period:dt.slice(0,7),method:i%2?'eft':'mobile',reference,status:'confirmed',createdAt});
  ledger.push({id:crypto.randomUUID(),userId:ids[i],name:names[i],amountCents:50000,kind:'contribution',reference,period:dt.slice(0,7),createdAt});
 }
 contributions.unshift({id:crypto.randomUUID(),userId:ids[6],name:names[6],amountCents:50000,period,method:'cash',reference:'Meeting contribution',status:'pending',createdAt:now+'T09:00:00Z'});
 for(let i=0;i<3;i++){const dt=monthDate(now.slice(0,8)+'05',i-3),id=crypto.randomUUID();rotations.push({id,userId:ids[i],name:names[i],amountCents:400000,date:dt,status:'paid',reference:'PAYOUT-'+dt.slice(0,7)});ledger.push({id:crypto.randomUUID(),userId:ids[i],name:names[i],amountCents:-400000,kind:'payout',reference:'PAYOUT-'+dt.slice(0,7),period:dt.slice(0,7),createdAt:dt+'T12:00:00Z'});}
 for(let i=0;i<8;i++)rotations.push({id:crypto.randomUUID(),userId:ids[(i+3)%8],name:names[(i+3)%8],amountCents:400000,date:monthDate(now.slice(0,8)+'05',i+1),status:'planned'});
 const data={group,members,contributions,ledger,rotations,messages:[],events:[{id:crypto.randomUUID(),name:names[0],action:'group.created',detail:'Ubuntu Savings Circle is ready to grow together',createdAt:monthDate(now,-7)+'T08:00:00Z'}],serverTime:new Date().toISOString()};
 await put('demo',{user:{id:ids[0],name:names[0],phone:members[0].phone,language:'en',smsConsent:true,smsGroupId:groupId},groups:[data],intents:{},operations:{},pin:'123456'});
}
async function demoRequest(path,{method='GET',body={},id}={}) {
 await ensureDemo();const d=await get('demo');
 if(path.endsWith('/sms-test'))id=body.operationId;
 if(path==='/api/me'){
  if(method==='PATCH'){Object.assign(d.user,{language:body.language,smsConsent:body.smsConsent,smsGroupId:body.smsGroupId});if(body.pin)d.pin=body.pin;await put('demo',d);return {ok:true};}
  return {user:d.user,groups:d.groups.map(s=>s.group),providers:{payment:'sandbox',sms:'console'}};
 }
 if(path==='/api/auth/logout')return {ok:true};
 if(path==='/api/me/password')return {ok:true};
 if(path==='/api/groups' && method==='POST'){
  const id=crypto.randomUUID(),group={...body,id,role:'treasurer',joinCode:crypto.randomUUID().replaceAll('-','').slice(0,20)};
  d.groups.push({group,members:[{...d.user,role:'treasurer',joinedAt:new Date().toISOString()}],contributions:[],ledger:[],rotations:[],messages:[],events:[],serverTime:new Date().toISOString()});await put('demo',d);return {id};
 }
 if(path==='/api/groups/join'){
  const s=d.groups.find(s=>s.group.joinCode===body.code);if(!s)throw new ApiError('Invitation code not found in this demo.',404);return {id:s.group.id};
 }
 if(id && d.operations[id])return d.operations[id];
 const confirm=path.match(/^\/api\/payments\/([^/]+)\/sandbox-confirm$/);
 if(confirm){const p=d.intents[confirm[1]];if(!p)throw new ApiError('Payment not found.',404);if(p.status!=='complete'){const s=d.groups.find(s=>s.group.id===p.groupId);const c={id:confirm[1],userId:d.user.id,name:d.user.name,amountCents:p.amountCents,period:p.period,method:'mobile',reference:'DEMO-'+confirm[1].slice(0,8),status:'confirmed',createdAt:new Date().toISOString()};s.contributions.unshift(c);s.ledger.unshift({...c,id:crypto.randomUUID(),kind:'contribution'});p.status='complete';}await put('demo',d);return {status:'complete'};}
 const match=path.match(/^\/api\/groups\/([^/]+)(.*)$/);if(!match)throw new ApiError('This action is available in the full C# app.',400);
 const s=d.groups.find(x=>x.group.id===match[1]);if(!s)throw new ApiError('Stokvel not found.',404);const action=match[2];if(!action)return structuredClone(s);
 const now=new Date().toISOString();let result={ok:true};
 if(action==='/contributions'){
  const member=s.members.find(m=>m.id===(body.userId || d.user.id));if(!member)throw new ApiError('Member not found.',400);
  const item={...body,id:crypto.randomUUID(),userId:member.id,name:member.name,status:'pending',createdAt:now};s.contributions.unshift(item);result={id:item.id,status:'pending'};
 }else if(/^\/contributions\/.+\/review$/.test(action)){
  const item=s.contributions.find(c=>c.id===action.split('/')[2]);if(!item || item.status!=='pending')throw new ApiError('Already reviewed or missing.',409);item.status=body.status;
  if(body.status==='confirmed')s.ledger.unshift({...item,id:crypto.randomUUID(),kind:'contribution'});
 }else if(action==='/payments'){const paymentId=crypto.randomUUID();d.intents[paymentId]={...body,groupId:s.group.id,status:'pending'};result={id:paymentId,provider:'sandbox'};
 }else if(action==='/settings'){Object.assign(s.group,body);
 }else if(action==='/invite-code'){s.group.joinCode=crypto.randomUUID().replaceAll('-','').slice(0,20);result={code:s.group.joinCode};
 }else if(action==='/rotations'){
  if(new Set(body.userIds).size!==body.userIds.length)throw new ApiError('Each member can appear only once.',400);
  s.rotations=s.rotations.filter(r=>r.status==='paid');body.userIds.forEach((uid,i)=>{const m=s.members.find(m=>m.id===uid);s.rotations.push({id:crypto.randomUUID(),userId:uid,name:m.name,date:monthDate(body.startDate,i),amountCents:body.amountCents,status:'planned'});});
 }else if(/^\/rotations\/.+\/payout$/.test(action)){
  const r=s.rotations.find(r=>r.id===action.split('/')[2]);if(r.status==='paid')throw new ApiError('Already paid.',409);if(r.date>today())throw new ApiError('Record a payout on or after its scheduled date.',409);if(s.ledger.reduce((a,l)=>a+l.amountCents,0)<r.amountCents)throw new ApiError('Confirmed balance is too low.',409);r.status='paid';r.reference=body.reference;s.ledger.unshift({id:crypto.randomUUID(),userId:r.userId,name:r.name,amountCents:-r.amountCents,kind:'payout',reference:body.reference,period:r.date.slice(0,7),createdAt:now});
 }else if(action==='/reminders'){
  if(s.messages.some(m=>Date.now()-new Date(m.createdAt)<86400000))throw new ApiError('Reminders were already prepared in the last 24 hours.',429);
  const due=s.members.filter(m=>m.smsConsent && s.contributions.filter(c=>c.userId===m.id && c.period===currentPeriod() && c.status==='confirmed').reduce((a,c)=>a+c.amountCents,0)<s.group.contributionCents);for(const m of due)s.messages.unshift({id:crypto.randomUUID(),name:m.name,userId:m.id,body:s.group.name+': Your contribution is due. R'+s.group.contributionCents/100+'.',status:'simulated',direction:'outbound',createdAt:now});result={count:due.length};
 }else if(action==='/sms-test'){
  const p=body.body.trim().split(/\s+/),cmd=p[0].toUpperCase();let reply;
  if(cmd==='HELP')reply='BAL <PIN>, NEXT <PIN>, PAY <PIN> <rand amount>. PAY records a claim only.';
  else{if(p[1]!==d.pin)throw new ApiError('Incorrect SMS PIN. Demo default is 123456.',403);if(cmd==='BAL')reply=s.group.name+': Confirmed balance R'+s.ledger.reduce((a,l)=>a+l.amountCents,0)/100;else if(cmd==='NEXT'){const r=s.rotations.find(r=>r.status==='planned');reply=r?r.name+', '+r.date:'No upcoming payout.';}else if(cmd==='PAY'){const value=cents(p[2]);s.contributions.unshift({id:crypto.randomUUID(),userId:d.user.id,name:d.user.name,amountCents:value,period:currentPeriod(),method:'sms',reference:'SMS contribution claim',status:'pending',createdAt:now});reply='Recorded for review. No money transferred.';}else throw new ApiError('Send BAL <PIN>, NEXT <PIN> or PAY <PIN> <amount>.',400);}result={reply};
 }else throw new ApiError('This action is available in the full C# app.',400);
 s.serverTime=now;s.events.unshift({id:crypto.randomUUID(),name:d.user.name,action:action.split('/')[1]+'.updated',detail:'Stokvel record updated',createdAt:now});
 if(id)d.operations[id]=result;await put('demo',d);return result;
}
