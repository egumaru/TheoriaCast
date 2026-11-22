"use strict";
const $ = s => document.querySelector(s);
const all = s => [...document.querySelectorAll(s)];
let tracks = [], jobs = [], signatures = {}, alternateVocal = false;
const labels = {queued:window.musicText("順番待ち"),running:window.musicText("作っています"),done:window.musicText("できました"),failed:window.musicText("作れませんでした"),cancelled:window.musicText("取り消し済み"),pending:window.musicText("試聴待ち"),adopted:window.musicText("採用"),rejected:window.musicText("見送り")};
const machine = n => n === "B" ? window.musicText("歌を作る機器") : window.musicText("楽器の曲を作る機器");
function el(tag,text) { const e=document.createElement(tag); if(text!=null)e.textContent=text; return e; }
function notice(text) { $("#notice").textContent=text; }
async function api(path,method="GET",value) {
  const headers=value?{"Content-Type":"application/json"}:{};
  if(window.atelierServerId)headers["X-Theoria-Server"]=window.atelierServerId;
  const r=await fetch(path,{method,headers,body:value?JSON.stringify(value):undefined});
  const t=await r.text(); if(!r.ok){let why;try{why=JSON.parse(t).error;}catch{}throw Error(window.musicText(why)||window.musicText("通信できませんでした（")+r.status+window.musicText("）"));}
  return t?JSON.parse(t):null;
}
function page(id) { all("main>section").forEach(e=>e.hidden=e.id!==id); all("nav button").forEach(b=>{if(b.dataset.page===id)b.setAttribute("aria-current","page");else b.removeAttribute("aria-current");}); window.syncWaveform?.(); }
all("nav button").forEach(b=>b.onclick=()=>page(b.dataset.page));
function bindForm(selector,fn) { const form=$(selector);form.onsubmit=async e=>{
  e.preventDefault();const btn=form.querySelector('button:not([type="button"]):last-of-type'); if(btn)btn.disabled=true;
  try{await fn(new FormData(form));await refresh();}catch(err){notice(err.message);}finally{if(btn)btn.disabled=false;}
};}
function action(container,label,fn){const b=el("button",label);b.onclick=async()=>{b.disabled=true;try{await fn();await refresh();}catch(e){notice(e.message);}finally{b.disabled=false;}};container.append(b);}
async function submit(spec){const j=await api("/jobs","POST",spec);notice(window.musicText("予約しました。空き時間に作ります。"));page("history");return j;}
function kindChanged(){const vocal=$('input[name="kind"]:checked').value==="generate_vocal";$("#words").hidden=$("#shortsLabel").hidden=!vocal;$("#words textarea").required=vocal;$("#alternate").hidden=!vocal||!alternateVocal;$("#alternate input").disabled=$("#alternate").hidden;}
all('[name="kind"]').forEach(i=>i.onchange=kindChanged);kindChanged();
bindForm("#composeForm",()=>submit(window.compositionSpec()));
function fillSelect(s){const old=s.value;s.replaceChildren(...tracks.map(t=>new Option(t.title+" · "+t.id.slice(0,6),t.id)));if(tracks.some(t=>t.id===old))s.value=old;}
function extra(){const row=el("div");row.className="pair";const label=el("label",window.musicText("合わせる曲")),s=el("select");s.dataset.tracks="";s.name="other";s.required=true;fillSelect(s);label.append(s);const gain=el("label",window.musicText("音量（%）")),v=el("input");Object.assign(v,{name:"othergain",type:"number",min:"0",max:"400",value:"100"});gain.append(v);row.append(label,gain);$("#extra").append(row);}
extra();$("#more").onclick=extra;
function mode(){all("[data-mode]").forEach(e=>{e.hidden=!e.dataset.mode.split(" ").includes($("#editMode").value);e.querySelectorAll("input,select,textarea").forEach(c=>c.disabled=e.hidden);});window.syncWaveform?.();}
$("#editMode").onchange=mode;mode();
bindForm("#editForm",async f=>{
  const source=f.get("source"),m=$("#editMode").value,title=tracks.find(t=>t.id===source)?.title||window.musicText("曲");
  let s={type:"edit",source,theme:title+window.musicText("（修正）")};
  if(m==="trim")Object.assign(s,{start:+f.get("start"),end:f.get("end")?+f.get("end"):null,fade_in:+f.get("fade_in"),fade_out:+f.get("fade_out")});
  if(m==="replace"){const end=f.get("end") || tracks.find(t=>t.id===source)?.provenance.actual_seconds;
    if(!end)throw Error(window.musicText("差し替える範囲の終了位置を指定してください。"));
    Object.assign(s,{start:+f.get("start"),end:+end,replacement:f.get("replacement"),replacement_start:+f.get("replacement_start")});}
  if(m==="timing"){
    const lines=name=>f.get(name).trim().split(/\n/).filter(Boolean);
    if(lines("cues").length)s.cues=lines("cues").map(l=>{const p=l.split("|");if(p.length!==3||!p[0].trim()||!p[1].trim())throw Error(window.musicText("開始秒 | 終了秒 | 歌詞 の順で入力してください。"));return {start:Number(p[0]),end:Number(p[1]),text:p[2].trim()};});
    s.timing=lines("timing").map(l=>{const p=l.split("|");if(p.length!==2||p.some(x=>!x.trim()))throw Error(window.musicText("元の秒 | 動かす先の秒 の順で入力してください。"));return {from:Number(p[0]),to:Number(p[1])};});
    if(!s.cues&&!s.timing.length)throw Error(window.musicText("直したい歌詞か時刻を入力してください。"));
  }
  if(m==="reading"){s.type="regen_reading_fix";s.readings=Object.fromEntries(f.get("readings").trim().split(/\n/).map(l=>{const p=l.split(/→|->/).map(x=>x.trim());if(p.length!==2)throw Error(window.musicText("ことば → よみ の順で入力してください。"));return p;}));}
  if(m==="mix"){const gs=f.getAll("othergain");s={type:"mix",theme:title+window.musicText("（合成）"),mode:f.get("mixmode"),crossfade:+f.get("crossfade"),clips:[{track:source,gain:+f.get("gain")/100},...f.getAll("other").map((track,i)=>({track,gain:+gs[i]/100}))]};}
  await submit(s);
});
bindForm("#workForm",async f=>{await api("/works/"+encodeURIComponent(f.get("name")),"PUT",{track:f.get("track")});notice(window.musicText("作品の曲の差し替えを予約しました。"));page("history");});
function renderJobs(){const box=$("#jobs");box.replaceChildren();if(!jobs.length)box.append(el("p",window.musicText("まだ予約はありません。")));
 for(const j of jobs){const a=el("article");a.dataset.id=j.id;a.append(el("h3",j.spec.theme||window.musicText("曲の編集")),el("p",[labels[j.state],j.position?window.musicText("この機器で")+j.position+window.musicText("番目"):"",j.node?machine(j.node):"",j.state==="running"?j.progress+"%":"",window.musicText(j.detail)].filter(Boolean).join(" · ")));
 if(j.state==="done")action(a,window.musicText("試聴する"),()=>{page("library");document.getElementById("t"+j.track)?.scrollIntoView();});
 if(j.result?.singing_check){const c=j.result.singing_check;a.append(el("p",window.musicText("歌の確認 · ")+(c.passed?window.musicText("歌詞と一致"):window.musicText("要確認"))+window.musicText("（")+c.attempts.length+window.musicText("回）")));for(const issue of c.issues||[])a.append(el("p",window.musicText(issue)));}
 if(["queued","running"].includes(j.state))action(a,window.musicText("取り消す"),()=>api("/jobs/"+j.id+"/cancel","POST"));
 if(["failed","cancelled"].includes(j.state))action(a,window.musicText("もう一度予約"),()=>submit(j.spec));box.append(a);}
}
function renderTracks(){const box=$("#tracks");box.replaceChildren();if(!tracks.length)box.append(el("p",window.musicText("曲ができると、ここで聴けます。")));
 for(const t of tracks){const a=el("article");a.id="t"+t.id;const p=t.provenance;a.append(el("h3",t.title),el("small",labels[t.status]));
 const player=el("audio");player.controls=true;player.preload="none";player.src=t.audio;const line=el("div");line.className="cue";player.ontimeupdate=()=>{line.textContent=(p.cues||[]).find(c=>player.currentTime>=c.start&&player.currentTime<c.end)?.text||"";};
 a.append(player,line);const buttons=el("div");buttons.className="actions";
 for(const [name,status] of [[window.musicText("採用"),"adopted"],[window.musicText("見送り"),"rejected"],[window.musicText("試聴待ちに戻す"),"pending"]])action(buttons,name,()=>api("/tracks/"+t.id+"/adopt","POST",{status}));
 action(buttons,t.favorite?window.musicText("★ お気に入りから外す"):window.musicText("☆ お気に入り"),()=>api("/tracks/"+t.id+"/adopt","POST",{favorite:!t.favorite}));
 action(buttons,window.musicText("もう一度作る"),()=>submit(jobs.find(j=>j.id===t.id).spec));action(buttons,window.musicText("この曲を直す"),()=>{$("#editForm select[name=source]").value=t.id;page("edit");});
 a.append(buttons);
 const info=el("details"),summary=el("summary",window.musicText("歌詞・作った条件"));info.append(summary);
 const gen=p.generation||{};info.append(el("p",[gen.seconds?window.musicText("目標 ")+gen.seconds+window.musicText("秒"):"",p.actual_seconds?window.musicText("できた曲 ")+Math.round(p.actual_seconds)+window.musicText("秒"):"",gen.bpm?gen.bpm+window.musicText("拍/分"):"",gen.instruments].filter(Boolean).join(" · ")));
 if(p.lyrics){const lyrics=el("div",p.lyrics);lyrics.style.whiteSpace="pre-wrap";info.append(lyrics);}
 for(const [word,reading] of Object.entries(p.readings||{}))info.append(el("div",word+" → "+reading));
 if(p.singing_check){const check=p.singing_check;info.append(el("p",check.passed?window.musicText("歌詞と一致しました。"):window.musicText("聞き直したい箇所があります。")));for(const issue of check.issues)info.append(el("div",window.musicText(issue)));}
 if(p.duration_needs_review)info.append(el("p",window.musicText("希望の長さから2割以上ずれています。")));
 if(p.screening?.flags?.length)info.append(el("p",window.musicText("試聴で確認 · ")+p.screening.flags.map(window.musicText).join(window.musicText("、"))));
 const save=el("a",window.musicText("音声を保存"));save.href=t.audio;save.download=t.title+".wav";info.append(save);window.trackMaterials?.(info,t);a.append(info);box.append(a);}
 all("[data-tracks]").forEach(fillSelect); window.syncWaveform?.();
}
let fetching=false, works=[];
async function refresh(){if(fetching)return;fetching=true;try{const [revision,ns]=await Promise.all([api("/jobs/revision"),api("/nodes")]);
 const keys=["jobs","tracks","works"].filter(key=>signatures[key]!==revision[key]);
 const updates=await Promise.all(keys.map(key=>api("/"+key)));
 for(const [index,key] of keys.entries()){
   if(key==="jobs") jobs=updates[index];
   if(key==="tracks") tracks=updates[index];
   if(key==="works") works=updates[index];
 }
 if(keys.includes("jobs"))renderJobs();
 if(keys.includes("tracks"))renderTracks();
 signatures=revision;
 alternateVocal=ns.some(n=>n.node==="A"&&n.online&&n.report?.types?.includes("generate_vocal"));kindChanged();
 $("#nodes").replaceChildren(...ns.map(n=>{const e=el("span",machine(n.node)+" · "+(!n.online?window.musicText("接続待ち"):n.job?window.musicText("作曲中"):n.report?.ready?window.musicText("空いています"):window.musicText("ほかの作業中")));e.className="node";e.title=window.musicText(n.report?.detail)||"";return e;}));
 $("#works").replaceChildren(...works.map(w=>el("p",w.name+"　"+(tracks.find(t=>t.id===w.track)?.title||""))));}
 catch(e){notice(e.message);}finally{fetching=false;}}
refresh();setInterval(refresh,4000);
