"use strict";
(() => {
  const form=$("#composeForm"), input=form.elements, picker=$("#importMaterial"), status=$("#materialsStatus");
  const key="theoria.materials."+window.atelierServerId;
  let final=null, originals={};
  const fieldFor={"prompt.md":"prompt_md","lyrics.txt":"lyrics","score.abc":"score_abc"};
  function value(name) {
    const text=input[name].value, original=originals[name];
    return original!=null && original.replace(/\r\n?/g,"\n")===text ? original : text;
  }
  function spec() {
    const vocal=input.kind.value==="generate_vocal", readings={};
    for (const line of input.reading_rules.value.split(/\r?\n/).filter(l=>l.trim())) {
      const parts=line.split(/→|->/).map(x=>x.trim());
      if (parts.length!==2 || parts.some(x=>!x)) throw Error(window.musicText("読み置換は「ことば → よみ」で入力してください。"));
      Object.defineProperty(readings,parts[0],{value:parts[1],enumerable:true,configurable:true});
    }
    return {type:input.kind.value,theme:input.theme.value,style:input.style.value,seconds:+input.seconds.value,
      instruments:input.instruments.value,bpm:input.bpm.value?+input.bpm.value:null,
      seed:input.seed.value?+input.seed.value:null,preferred_node:vocal&&input.alternate.checked?"A":null,
      shorts:vocal&&input.shorts.checked,lyrics:vocal?value("lyrics"):"",prompt_md:value("prompt_md"),
      score_abc:vocal?value("score_abc"):null,readings:vocal?readings:{}};
  }
  window.compositionSpec=spec;
  const documentFor=()=>({format:"TheoriaCast.Music",version:1,job:spec()});
  async function send(path,blob,contentType) {
    const response=await fetch(path,{method:"POST",body:blob,headers:{"Content-Type":contentType,"X-Theoria-Server":window.atelierServerId}});
    if (!response.ok) throw Error(window.musicText((await response.json().catch(()=>({}))).error)||window.musicText("作曲ファイルを読み込めません。"));
    return response;
  }
  function save(blob,name) {
    const url=URL.createObjectURL(blob),a=document.createElement("a");
    a.href=url; a.download=name; a.click(); setTimeout(()=>URL.revokeObjectURL(url),30000);
  }
  function remember() {
    try {sessionStorage.setItem(key,JSON.stringify({score:input.score_abc.value,seed:input.seed.value,
      rules:input.reading_rules.value,originals,final}));} catch {status.textContent=window.musicText("この下書きはファイルに保存してください。");}
  }
  function setText(name,text) {originals[name]=text??""; input[name].value=text??"";}
  function syncKind() {$("#scoreField").hidden=input.kind.value!=="generate_vocal";}
  function load(doc,recipe=null) {
    const s=doc.job;
    input.kind.value=s.type;
    for(const name of ["prompt_md","score_abc","lyrics"]) setText(name,s[name]);
    for(const name of ["theme","seconds","seed","instruments","bpm"]) input[name].value=s[name]??"";
    if (![...input.style.options].some(o=>o.value===s.style)) input.style.add(new Option(s.style||"—",s.style||""));
    input.style.value=s.style||""; input.shorts.checked=!!s.shorts; input.alternate.checked=s.preferred_node==="A";
    input.reading_rules.value=Object.entries(s.readings||{}).map(([word,r])=>word+" → "+r).join("\n");
    final=recipe; $("#replayBox").hidden=!final;
    kindChanged(); syncKind(); form.dispatchEvent(new Event("input",{bubbles:true}));
  }
  picker.onchange=async()=>{
    const file=picker.files[0]; if(!file)return;
    try {
      if(file.size>4*1024*1024) throw Error(window.musicText("作曲ファイルは4 MiBまでです。"));
      const ext=file.name.toLowerCase().split(".").pop();
      if (ext==="json"||ext==="zip") {
        const pack=await (await send("/composition/import",file,"application/octet-stream")).json();
        load(pack.draft,pack.final);
      } else {
        const name={md:"prompt_md",txt:"lyrics",abc:"score_abc"}[ext];
        if (!name) throw Error(window.musicText("指示書・歌詞・楽譜・作曲ファイルを選んでください。"));
        const text=new TextDecoder("utf-8",{fatal:true}).decode(await file.arrayBuffer());
        if(text.length>(name==="score_abc"?262144:32768)) throw Error(window.musicText("素材が長すぎます。"));
        if(name!=="prompt_md") input.kind.value="generate_vocal";
        setText(name,text);
        if (!input.theme.value) input.theme.value=file.name.replace(/\.[^.]+$/,"");
        final=null; $("#replayBox").hidden=true;
        kindChanged(); syncKind(); form.dispatchEvent(new Event("input",{bubbles:true}));
      }
      status.textContent=window.musicText("下書きに読み込みました。");
    } catch(e){status.textContent=e.message;} finally{picker.value="";}
  };
  for(const button of all("[data-save]")) button.onclick=async()=>{
    try {
      const name=button.dataset.save;
      if(fieldFor[name]) save(new Blob([value(fieldFor[name])],{type:"text/plain;charset=utf-8"}),name);
      else if(name.endsWith(".zip")) save(await (await send("/composition/pack",JSON.stringify(documentFor()),"application/json")).blob(),name);
      else save(new Blob([JSON.stringify(documentFor(),null,2)],{type:"application/json"}),name);
    } catch(e){status.textContent=e.message;}
  };
  $("#askPrompt").onclick=()=>{
    $("#studioHelp").open=true;
    $("#questionForm").elements.question.value=window.musicText("この下書きに合う曲の指示書を作り、prompt_md の変更案として返してください。");
    $("#questionForm").requestSubmit();
  };
  $("#replayTake").onclick=async e=>{
    const button=e.currentTarget; button.disabled=true;
    try {if(final)await submit({...final.job,replay:final.take});}
    catch(err){status.textContent=err.message;} finally{button.disabled=false;}
  };
  window.trackMaterials=(container,track)=>{
    const dir="/tracks/"+encodeURIComponent(track.id), files=track.provenance.materials||[];
    const links=el("div"); links.className="actions";
    function link(text,url,name){const a=el("a",text); a.href=url; a.download=name; links.append(a);}
    for (const name of files) link(name,dir+"/materials/"+name,name);
    if(jobs.some(j=>j.id===track.id && ["generate_bgm","generate_vocal","regen_reading_fix"].includes(j.spec.type)))
      link(window.musicText("作曲セットを保存"),dir+"/composition.zip","composition.zip");
    if(files.includes("score.abc")) {
      const b=el("button",window.musicText("この楽譜で歌い直す")); b.type="button"; links.append(b);
      b.onclick=async()=>{try{
        const response=await fetch(dir+"/composition.zip",{headers:{"X-Theoria-Server":window.atelierServerId}});
        if(!response.ok)throw Error(window.musicText("作曲ファイルを読み込めません。"));
        const pack=await (await send("/composition/import",await response.blob(),"application/zip")).json();
        load(pack.draft); page("compose"); $("#materials").open=true;
      }catch(e){notice(e.message);}};
    }
    container.append(links);
  };
  try {const s=JSON.parse(sessionStorage.getItem(key)); if(s){originals=s.originals||{}; final=s.final;
    input.score_abc.value=s.score??"";input.seed.value=s.seed??"";input.reading_rules.value=s.rules??"";$("#replayBox").hidden=!final;}}
  catch {sessionStorage.removeItem(key);}
  form.addEventListener("input",remember); form.addEventListener("change",syncKind); syncKind(); renderTracks();
})();
