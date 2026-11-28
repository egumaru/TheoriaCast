"use strict";
(() => {
 const host=document.getElementById("mixer"), t=(ja,en)=>document.documentElement.lang==="en"?en:ja, sid=window.atelierServerId;
 const sheet=document.createElement("link");
 sheet.rel="stylesheet";sheet.href="/mix-projects.css?v=023";document.head.append(sheet);
 const tools=document.createElement("section");tools.id="projectMix";tools.hidden=true;
 host.append(tools);
 tools.innerHTML=`
 <fieldset id="pmControls"><legend>${t("曲棚のミックスプロジェクト","Shelf mix project")}</legend>
 <div class="pm-line"><label>${t("保存したプロジェクト","Saved projects")}<select id="pmProjects"></select></label><button id="pmOpen">${t("開く","Open")}</button><button id="pmNew">${t("新規","New")}</button></div>
 <div class="pm-line"><label>${t("プロジェクト名","Project name")}<input id="pmName" maxlength="200"></label><label>${t("マスター音量 (dB)","Master gain (dB)")}<input id="pmMaster" type="number" min="-120" max="36" step="any"></label></div>
 <div class="pm-line"><label>${t("曲棚の素材","Shelf recording")}<select id="pmSource" data-tracks></select></label><button id="pmAdd">${t("プロジェクトに追加","Add to project")}</button></div>
 <div id="pmLanes"></div><div class="actions"><button id="pmSave">${t("配置を保存","Save arrangement")}</button><button id="pmRender" class="primary">${t("ミックスを曲棚に書き出す","Render mix to shelf")}</button></div>
 <details id="pmRevisions"><summary>${t("保存した版に戻す","Revisions and snapshots")}</summary><div class="pm-line"><label>${t("スナップショット名","Snapshot name")}<input id="pmLabel" maxlength="200"></label><button id="pmSnapshot">${t("スナップショットを保存","Save snapshot")}</button></div>
 <div class="pm-line"><label>${t("版の履歴","Revision history")}<select id="pmHistory"></select></label><button id="pmRollback">${t("この版の配置に戻す","Restore this arrangement")}</button></div></details></fieldset>
 <p id="pmMessage" role="status" aria-live="polite"></p><div id="pmResult"></div>`;
 const q=id=>tools.querySelector("#"+id);
 function refreshTakeNames(){
   const titles=new Map(tracks.map(track=>[track.id,track.title]));
   for(const label of q("pmLanes").querySelectorAll("[data-take]")){
     const title=titles.get(label.dataset.take);
     if(title)label.textContent=title;
   }
 }
 new MutationObserver(refreshTakeNames).observe(q("pmSource"),{childList:true});
 const key="theoria.static-mix.v1."+sid, storage=localStorage;
 const blank=()=>({project:null,head:null,manifest:{title:"",master_gain_db:0,tracks:[]},dirty:false,job:null});
 let state=blank(),busy=false,available=false,loading,projects=[],history=[],polling=false;
 try{const saved=JSON.parse(storage.getItem(key));if(saved?.manifest?.tracks)state=saved;}catch{}
 const persist=()=>{try{storage.setItem(key,JSON.stringify(state));}catch{}};
 const msg=s=>q("pmMessage").textContent=s;
 const call=(action,args={})=>api("/creator/mix-projects","POST",{action,...args},sid);
 function options(select,rows,empty){
   const old=select.value;select.replaceChildren();
   if(!rows.length)select.add(new Option(empty,""));
   for(const [title,value] of rows)select.add(new Option(title,value));
   if(rows.some(r=>r[1]===old))select.value=old;
 }
 function marked(){state.dirty=true;persist();msg(t("未保存の変更があります。","Unsaved changes."));}
 function audioResult(id) {
   const box=q("pmResult");box.querySelector("audio")?.pause();box.replaceChildren();
   if(!id)return;
   const url="/tracks/"+encodeURIComponent(id)+"/audio";
   const title=el("strong",t("書き出したミックス · 試聴","Rendered mix · Listen"));
   const player=document.createElement("audio");player.controls=true;player.preload="none";player.src=url;
   player.onplay=()=>window.atelierMix?.stop();
   const link=el("a",t("WAVを保存","Download WAV"));link.href=url;link.download="mix-"+id+".wav";
   box.append(title,player,link);
 }
 function historyOptions(){
   const names={create:t("新規","Created"),save:t("保存","Saved"),snapshot:t("スナップショット","Snapshot"),render:t("書き出し","Rendered"),rollback:t("巻き戻し","Restored")};
   options(q("pmHistory"),history.map(({revision:v})=>[new Date(v.created).toLocaleString()+" · "+(v.label||names[v.action]||v.action)+" · "+v.id.slice(0,6),v.id]),t("まだ版がありません","No revisions yet"));
   q("pmRollback").disabled=!history.length;q("pmSnapshot").disabled=!state.manifest.tracks.length;
 }
 function numeric(lane,key,caption,min,max,step,scale=1) {
   const label=el("label",caption),field=document.createElement("input");
   field.type="number";field.min=min;field.max=max;field.step=step;field.value=(lane[key]||0)/scale;
   field.oninput=()=>{if(field.value!=="" && field.validity.valid){lane[key]=scale===1?field.valueAsNumber:Math.round(field.valueAsNumber*scale);marked();}};
   field.required=true;label.append(field);return label;
 }
 function draw(){
   q("pmName").value=state.manifest.title;q("pmMaster").value=state.manifest.master_gain_db??0;
   const list=q("pmLanes");list.replaceChildren();
   if(!state.manifest.tracks.length)list.append(el("p",t("曲棚から伴奏・歌声・ガイドなどを追加してください。","Add backing, vocals or a guide from the shelf.")));
   state.manifest.tracks.forEach((l,i)=>{
     const row=el("article");row.className="pm-lane";
     const title=el("h3",(i+1)+". ");
     const name=el("span",tracks.find(item=>item.id===l.take)?.title||l.take);
     name.dataset.take=l.take;title.append(name);
     const knobs=el("div");knobs.className="pm-knobs";
     knobs.append(numeric(l,"start_sample",t("開始 (秒)","Start (s)"),0,14913,"any",48000),numeric(l,"gain_db",t("音量 (dB)","Gain (dB)"),-120,36,"any"),numeric(l,"pan",t("パン (左 −1 / 右 +1)","Pan (L −1 / R +1)"),-1,1,"any"));
     const switches=el("div");switches.className="pm-switches";
     for(const [k,caption] of [["mute",t("ミュート","Mute")],["solo",t("ソロ","Solo")],["invert_polarity",t("極性反転","Invert polarity")]]){
       const label=el("label"),input=document.createElement("input");input.type="checkbox";input.checked=!!l[k];input.onchange=()=>{l[k]=input.checked;marked();};label.append(input,caption);switches.append(label);
     }
     const remove=el("button",t("外す","Remove"));remove.onclick=()=>{state.manifest.tracks.splice(i,1);draw();marked();};
     switches.append(remove);row.append(title,knobs,switches);list.append(row);
   });
   q("pmRender").disabled=!state.manifest.tracks.length;q("pmSnapshot").disabled=!state.manifest.tracks.length;
 }
 async function listProjects(){
   projects=await call("list");options(q("pmProjects"),projects.map(p=>[p.title,p.project]),t("保存済みのプロジェクトはありません","No saved projects"));
   if(projects.some(p=>p.project===state.project))q("pmProjects").value=state.project;
   q("pmOpen").disabled=!projects.length;
 }
 async function revisions(){
   history=state.project?(await call("history",{project:state.project})).history:[];historyOptions();
 }
 async function init(){
   if(available)return true;
   if(loading)return loading;
   loading=(async()=>{
     try{await listProjects();await revisions();available=true;tools.hidden=false;
       fillSelect(q("pmSource"));draw();
       msg(state.dirty?t("未保存の配置を復元しました。","Restored unsaved arrangement."):"");
       if(state.job)void watch();
       return true;
     }catch(e){tools.hidden=false;q("pmControls").hidden=true;msg(t("この接続先のプロジェクトミックスには Music API 0.2.3 以降が必要です。","Project mixing needs Music API 0.2.3 or newer on this connection.")+" "+e.message);return false;}
     finally{loading=null;if(available)q("pmControls").hidden=false;}
   })();return loading;
 }
 const discard=()=>!state.dirty||window.confirm(t("未保存の配置を破棄して切り替えますか？","Discard the unsaved arrangement and switch?"));
 function add(track){state.manifest.tracks.push({id:crypto.randomUUID(),take:track.id,start_sample:0,gain_db:0,pan:0,mute:false,solo:false,invert_polarity:false});draw();marked();}
 function valid(){
   if(!q("pmName").value.trim()){q("pmName").focus();throw Error(t("プロジェクト名を入力してください。","Enter a project name."));}
   for(const field of tools.querySelectorAll("input[type=number]"))if(!field.reportValidity())throw Error(t("入力した数値を確認してください。","Check the numeric values."));
   state.manifest.title=q("pmName").value.trim();
 }
 function apply(version){
   state.project=version.project;state.head=version.head;state.manifest=version.revision.manifest;state.dirty=false;persist();draw();
 }
 async function save(){
   valid();
   if(!state.project)apply(await call("create",{manifest:state.manifest}));
   else if(state.dirty)apply(await call("save",{project:state.project,expected:state.head,manifest:state.manifest}));
 }
 async function watch(){
   if(polling)return;polling=true;
   try{
     while(state.job){
       const id=state.job,job=await api("/creator/jobs/"+id,"GET",undefined,sid);
       if(state.job!==id)continue;
       if(job.state==="done"){
         audioResult(job.recipe.result_track);
         if(state.addJob===id){window.atelierWorkspace?.add(sid,[job.recipe.result_track]);delete state.addJob;persist();}
         msg(t("書き出しました。曲棚で試聴できます。","Rendered. The mix is ready on the shelf."));await revisions();return;
       }
       if(!["queued","running"].includes(job.state))throw Error(job.error||job.state);
       msg(t("曲棚で書き出しています…","Rendering on the shelf…"));await new Promise(resolve=>setTimeout(resolve,1000));
     }
   }catch(e){msg(e.message);}finally{polling=false;}
 }
 async function run(action,validate=false){
   if(busy)return;
   if(validate){try{valid();}catch(e){msg(e.message);return;}}
   busy=true;q("pmControls").disabled=true;
   try{await action();}catch(e){msg(e.message);}finally{busy=false;q("pmControls").disabled=false;}
 }
 q("pmName").oninput=e=>{state.manifest.title=e.target.value;marked();};
 q("pmMaster").required=true;q("pmMaster").oninput=e=>{if(e.target.value!=="" && e.target.validity.valid){state.manifest.master_gain_db=e.target.valueAsNumber;marked();}};
 q("pmNew").onclick=()=>run(async()=>{if(!discard())return;state=blank();history=[];historyOptions();audioResult(null);persist();draw();msg("");q("pmName").focus();});
 q("pmOpen").onclick=()=>run(async()=>{if(!discard())return;const project=q("pmProjects").value;await openProject(project);});
 async function openProject(project){
   const v=await call("get",{project});state=blank();apply(v);await revisions();
   state.job=history.find(h=>h.output?.state==="done")?.output.job||null;persist();audioResult(null);msg("");if(state.job)void watch();
 }
 q("pmAdd").onclick=()=>{const track=tracks.find(s=>s.id===q("pmSource").value);if(track)add(track);};
 q("pmSave").onclick=()=>run(async()=>{await save();await listProjects();await revisions();msg(t("配置を保存しました。","Arrangement saved."));},true);
 q("pmRender").onclick=()=>run(async()=>{
   valid();if(!state.project)await save();
   const v=await call("render",{project:state.project,expected:state.head,manifest:state.manifest});apply(v);
   state.job=v.job;state.addJob=v.job;persist();audioResult(null);void watch();await listProjects();await revisions();
 },true);
 q("pmSnapshot").onclick=()=>run(async()=>{
   const label=q("pmLabel").value.trim();if(!label)throw Error(t("スナップショット名を入力してください。","Enter a snapshot name."));
   await save();apply(await call("snapshot",{project:state.project,expected:state.head,label}));await listProjects();await revisions();msg(t("スナップショットを保存しました。","Snapshot saved."));
 },true);
 q("pmRollback").onclick=()=>run(async()=>{
   if(!discard())return;apply(await call("rollback",{project:state.project,expected:state.head,revision:q("pmHistory").value}));
   state.job=null;persist();audioResult(null);await revisions();msg(t("配置を復元しました。書き出して試聴できます。","Arrangement restored. Render to hear this version."));
 });
 new MutationObserver(()=>{if(!host.hidden)void init();}).observe(host,{attributes:true,attributeFilter:["hidden"]});
 if(!host.hidden)void init();
})();
