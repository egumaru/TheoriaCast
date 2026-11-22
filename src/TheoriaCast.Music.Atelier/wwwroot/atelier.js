"use strict";
(() => {
  const rows=document.querySelector("#serverRows"), note=document.querySelector("#connectionNote");
  const picker=document.querySelector("#importServer");
  const labels={checking:window.musicText("確認中"),online:window.musicText("接続中"),unavailable:window.musicText("応答待ち"),unauthorized:window.musicText("接続ファイルの更新が必要"),mismatch:window.musicText("別のサーバーが応答")};
  let started=false, refreshing=false, lastView="";
  async function start(id) {
    if (started) return;
    started=true;
    window.atelierServerId=id;
    for (const src of ["/studio.js","/waveform.js","/consultation.js","/composition.js"]) {
      await new Promise((resolve,reject)=>{
        const script=document.createElement("script");
        script.onload=resolve; script.onerror=reject; script.src=src; document.head.append(script);
      });
    }
    document.body.classList.remove("connecting");
  }
  async function refresh() {
    if (refreshing) return;
    refreshing=true;
    try {
      const res=await fetch("/atelier/servers",{cache:"no-store"});
      if (!res.ok) throw Error(window.musicText("接続先を確認できませんでした。"));
      const state=await res.json();
      if (started && state.selected!==window.atelierServerId) { location.reload(); return; }
      const view=JSON.stringify(state);
      if (lastView!==view) {
        lastView=view; rows.replaceChildren();
        for (const s of state.servers) {
          const p=document.createElement("p"), heartbeat=s.heartbeat;
          const seen=heartbeat.last_seen?new Date(heartbeat.last_seen).toLocaleTimeString():"";
          p.textContent=s.name+" · "+labels[heartbeat.state]+(seen?window.musicText("（最終応答 ")+seen+window.musicText("）"):"");
          if (s.server_id!==state.selected) {
            const button=document.createElement("button"); button.textContent=window.musicText("ここに切り替える");
            button.onclick=async()=>{ const r=await fetch("/atelier/servers/"+s.server_id+"/select",{method:"POST"});
              if (r.ok) location.reload(); else note.textContent=window.musicText("切り替えに失敗しました。"); };
            p.append(" ",button);
          }
          rows.append(p);
        }
        if(state.selected) note.textContent=window.musicText("別の接続ファイルを読み込むとサーバーを追加できます。");
      }
      if (state.selected) await start(state.selected);
    } catch (e) { note.textContent=e.message || window.musicText("接続先を確認できませんでした。"); }
    finally { refreshing=false; }
  }
  picker.onchange=async()=>{
    const file=picker.files[0]; if (!file) return;
    try {
      if (file.size>16384) throw Error(window.musicText("サーバーから受け取った接続ファイルを選んでください。"));
      const res=await fetch("/atelier/servers",{method:"POST",headers:{"Content-Type":"application/json"},body:file});
      if (!res.ok) throw Error(window.musicText("登録できませんでした。接続ファイルの内容を確認してください。"));
      location.reload();
    } catch (e) { note.textContent=e.message; }
    finally { picker.value=""; }
  };
  refresh(); setInterval(refresh,5000);
})();
