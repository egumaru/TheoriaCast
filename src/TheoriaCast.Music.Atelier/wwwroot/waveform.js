"use strict";
(() => {
  const canvas = $("#wave"), audio = $("#waveAudio"), source = $("#editForm [name=source]");
  const start = $("#editForm [name=start]"), end = $("#editForm [name=end]");
  const bpm = $("#waveBpm"), meter = $("#waveMeter"), offset = $("#waveOffset"), snap = $("#waveSnap");
  const play = $("#wavePlay"), listen = $("#waveListen"), loop = $("#waveLoop"), status = $("#waveStatus");
  const pan = $("#wavePan"), undo = $("#waveUndo"), reset = $("#waveReset");
  const markStart = $("#waveMarkStart"), markEnd = $("#waveMarkEnd");
  let id, overview, detail, amplitude = 1, view = {start:0, end:0}, overviewRequest, detailRequest, panTimer;
  let history = [], committed = ["0",""], drag, playing, frame;
  const length = () => overview?.duration ?? 0;
  const bounds = () => [Number(start.value), end.value === "" ? length() : Number(end.value)];
  const values = () => [start.value,end.value];
  const clamp = (n,min,max) => Math.min(max,Math.max(min,n));
  const milli = n => Math.round(n*1000)/1000;
  const fmt = n => milli(n)+window.musicText("秒");
  const clock = value => {
    const ticks = Math.round(Math.max(0,value)*100);
    return Math.floor(ticks/6000)+":"+String(Math.floor(ticks/100)%60).padStart(2,"0")+"."+String(ticks%100).padStart(2,"0");
  };
  const span = () => view.end-view.start;
  const bar = () => bpm.validity.valid && bpm.valueAsNumber>0 ? 60*Number(meter.value)/bpm.valueAsNumber : 0;
  const valid = ([a,b]) => !!overview && start.validity.valid && end.validity.valid && a>=0 && b>a && b<=length();
  const zoomed = () => !!overview && (view.start>0 || view.end<length());

  function draw() {
    const w = canvas.clientWidth, h = canvas.clientHeight;
    if (!w || !h) return;
    const ratio = window.devicePixelRatio || 1, ctx = canvas.getContext("2d");
    if (canvas.width !== Math.round(w*ratio) || canvas.height !== Math.round(h*ratio)) {
      canvas.width = Math.round(w*ratio); canvas.height = Math.round(h*ratio);
    }
    ctx.setTransform(ratio,0,0,ratio,0,0);
    ctx.clearRect(0,0,w,h);
    if (!overview) return;
    const x = t => (t-view.start)*w/span();
    const [a,b] = bounds(), top = 23, mid = (top+h-26)/2;
    ctx.fillStyle = "#3b5b4b"; ctx.fillRect(x(a),top,x(b)-x(a),h-top-23);
    const interval = span()/Math.max(2,Math.floor(w/70));
    const power = 10**Math.floor(Math.log10(interval));
    const tick = [1,2,5,10].find(n=>n*power>=interval)*power;
    ctx.font = "11px system-ui"; ctx.textBaseline = "middle";
    for (let i=Math.ceil(view.start/tick); i*tick<=view.end; i++) {
      const left = x(i*tick);
      ctx.fillStyle = "#40594f"; ctx.fillRect(left,top,1,h-top-20);
      if (left<w-48) {ctx.fillStyle = "#bdcfc5"; ctx.fillText(clock(i*tick),left+4,11);}
    }
    const grid = bar(), origin = offset.valueAsNumber;
    if (grid && origin>=0 && origin<view.end) {
      const stride = Math.max(1,Math.ceil(48*span()/(w*grid)));
      const first = Math.max(0,Math.ceil((view.start-origin)/(grid*stride))*stride);
      for (let n=first; origin+n*grid<view.end; n+=stride) {
        const left = x(origin+n*grid);
        ctx.fillStyle = "#779383"; ctx.fillRect(left,h-25,1,8);
        if (left<w-36) {ctx.fillStyle = "#cfddcb"; ctx.fillText(document.documentElement.lang==="en"?"Bar "+(n+1):(n+1)+"小節",left+3,h-10);}
      }
    }
    const envelope = detail ?? overview, peaks = envelope.peaks;
    const bin = t => (t-envelope.start)*peaks.length/(envelope.end-envelope.start);
    ctx.fillStyle = "#d3e9c9";
    for (let i=0; i<w; i++) {
      const first = clamp(Math.floor(bin(view.start+i*span()/w)),0,peaks.length-1);
      const last = clamp(Math.ceil(bin(view.start+(i+1)*span()/w)),first+1,peaks.length);
      let peak = 0;
      for (let j=first; j<last; j++) peak = Math.max(peak,peaks[j]);
      const size = Math.max(.5,Math.min(1,peak/amplitude)*(mid-top-4)); ctx.fillRect(i,mid-size,1,size*2);
    }
    ctx.fillStyle = "#efbd71";
    for (const t of [a,b]) {
      if (t<view.start || t>view.end) continue;
      const left = clamp(x(t)-1,0,w-2);
      ctx.fillRect(left,top,2,h-top-22); ctx.fillRect(clamp(left-3,0,w-8),top,8,9);
    }
    const cursor = x(audio.currentTime);
    if (cursor>=0 && cursor<=w) {
      ctx.fillStyle = "#fff"; ctx.fillRect(clamp(cursor,0,w-1),top-4,1,h-top-18);
      ctx.beginPath(); ctx.moveTo(cursor-4,top-5); ctx.lineTo(cursor+4,top-5); ctx.lineTo(cursor,top+1); ctx.fill();
    }
  }

  function transport() {
    const t = audio.currentTime || 0, [a,b] = bounds();
    $("#waveClock").textContent = clock(t)+" / "+(overview?clock(length()):"—");
    canvas.setAttribute("aria-valuenow",String(milli(t)));
    canvas.setAttribute("aria-valuetext",fmt(t));
    play.textContent = playing==="track" ? window.musicText("一時停止") : window.musicText("ここから再生");
    listen.textContent = playing==="range" ? window.musicText("試聴を止める") : window.musicText("選んだ範囲を聴く");
    markStart.disabled = !overview || milli(t)>=b;
    markEnd.disabled = !overview || Math.min(length(),milli(t))<=a;
  }
  function update() {
    const [a,b] = bounds();
    end.setCustomValidity(overview && b<=a ? window.musicText("終了は開始より後に指定してください。") : "");
    const good = valid([a,b]);
    canvas.setAttribute("aria-disabled",String(!overview));
    play.disabled = !overview; listen.disabled = !good; loop.disabled = !good;
    $("#waveZoom").disabled = !good || (a===view.start && b===view.end);
    $("#waveFit").disabled = !zoomed();
    $("#wavePanBox").hidden = !zoomed();
    pan.max = Math.max(0,length()-span()); pan.value = view.start;
    pan.setAttribute("aria-valuetext",fmt(view.start));
    $("#waveView").textContent = zoomed() ? clock(view.start)+" ～ "+clock(view.end) : window.musicText("曲全体");
    $("#waveLength").textContent = good ? fmt(b-a) : "—";
    undo.disabled = history.length===0; reset.disabled = !overview || (a===0 && b===length());
    transport(); draw();
  }
  function stop() { playing=null; audio.pause(); cancelAnimationFrame(frame); transport(); }
  function commit() {
    const next = values();
    update();
    if (valid(bounds()) && (next[0]!==committed[0] || next[1]!==committed[1])) {
      history.push(committed); if (history.length>30) history.shift(); committed=next;
    }
    undo.disabled = history.length===0;
  }
  function select(a,b) {
    stop(); start.value=a; end.value=b; commit();
  }
  undo.onclick = () => {
    if (!history.length) return;
    stop(); committed=history.pop(); [start.value,end.value]=committed; update();
  };
  reset.onclick = () => select(0,"");
  markStart.onclick = () => select(milli(audio.currentTime),end.value);
  markEnd.onclick = () => select(start.value,Math.min(length(),milli(audio.currentTime)));
  for (const input of [start,end]) {
    input.addEventListener("input",()=>{stop(); update();});
    input.addEventListener("change",commit);
  }
  for (const input of [bpm,meter,offset,snap]) input.addEventListener("input",draw);

  async function envelope(signal,query="") {
    const response = await fetch("/tracks/"+encodeURIComponent(id)+"/waveform"+query,
      {signal,headers:{"X-Theoria-Server":window.atelierServerId}});
    if (!response.ok) throw Error(window.musicText("波形を読み込めませんでした。"));
    return response.json();
  }
  function cancelDetail() { clearTimeout(panTimer); detailRequest?.abort(); detail=null; }
  function setView(a,b) {
    const minimum = Math.min(length(),.05);
    if (b-a<minimum) {
      a=clamp((a+b-minimum)/2,0,length()-minimum); b=a+minimum;
    }
    cancelDetail(); view={start:a,end:b}; update();
    if (!zoomed()) return;
    detailRequest=new AbortController();
    const signal=detailRequest.signal;
    panTimer=setTimeout(async ()=>{
      try {
        const sub=await envelope(signal,"?"+new URLSearchParams({start:a,end:b}));
        if (!signal.aborted && sub.end>sub.start) {detail=sub; status.textContent=""; draw();}
      } catch {
        if (!signal.aborted) status.textContent=window.musicText("細かい波形を読み込めなかったため、全体の波形を拡大しています。");
      }
    },100);
  }
  $("#waveZoom").onclick = () => setView(...bounds());
  $("#waveFit").onclick = () => setView(0,length());
  pan.oninput = () => {
    const width=span(), a=clamp(Number(pan.value),0,length()-width);
    setView(a,Math.min(length(),a+width));
  };

  function position(event,align) {
    const rect=canvas.getBoundingClientRect();
    let t=view.start+(event.clientX-rect.left)*span()/rect.width;
    const grid=bar(), origin=offset.valueAsNumber;
    if (align && snap.checked && grid && origin>=0 && t>=origin) t=origin+Math.round((t-origin)/grid)*grid;
    return clamp(milli(t),view.start,view.end);
  }
  canvas.onpointerdown = e => {
    if (!overview || !e.isPrimary || e.button!==0) return;
    const raw=position(e,false), [a,b]=bounds(), tolerance=10*span()/canvas.clientWidth;
    let anchor=position(e,true);
    if (Math.min(Math.abs(raw-a),Math.abs(raw-b))<=tolerance) anchor=Math.abs(raw-a)<Math.abs(raw-b)?b:a;
    drag={anchor, x:e.clientX, before:values(), changed:false};
    canvas.focus({preventScroll:true}); canvas.setPointerCapture(e.pointerId);
  };
  canvas.onpointermove = e => {
    if (!drag || !e.isPrimary || (!drag.changed && Math.abs(e.clientX-drag.x)<3)) return;
    const t=position(e,true);
    if (t===drag.anchor) return;
    if (!drag.changed) stop();
    drag.changed=true; start.value=Math.min(drag.anchor,t); end.value=Math.max(drag.anchor,t); update();
  };
  canvas.onpointerup = e => {
    if (!drag || !e.isPrimary) return;
    if (drag.changed) commit(); else seek(position(e,false));
    drag=null;
  };
  canvas.onpointercancel = canvas.onlostpointercapture = () => {
    if (drag?.changed) {[start.value,end.value]=drag.before; update();}
    drag=null;
  };

  function seek(t) {
    if (playing==="range") stop();
    audio.currentTime=clamp(t,0,length()); transport(); draw();
  }
  async function audition(kind) {
    if (playing===kind) {stop(); return;}
    if (!overview || (kind==="range" && !valid(bounds()))) return;
    playing=kind;
    if (kind==="range") audio.currentTime=bounds()[0];
    else if (audio.ended || audio.currentTime>=length()) audio.currentTime=0;
    const trackId=id;
    try { await audio.play(); }
    catch (error) {
      if (trackId===id && playing===kind && error.name!=="AbortError") {
        stop(); status.textContent=window.musicText("音声を再生できませんでした。");
      }
    }
    transport();
  }
  play.onclick=()=>audition("track"); listen.onclick=()=>audition("range");
  $("#waveVolume").oninput = e => { audio.volume = e.target.valueAsNumber/100; };
  function tick() {
    cancelAnimationFrame(frame);
    const [a,b]=bounds();
    if (playing==="range" && (audio.ended || audio.currentTime>=b)) {
      if (loop.checked && valid([a,b])) {
        audio.currentTime=a;
        if (audio.paused) {playing=null; void audition("range");}
      } else {stop(); audio.currentTime=b;}
    } else if (audio.ended) playing=null;
    transport(); draw();
    if (!audio.paused && !audio.ended) frame=requestAnimationFrame(tick);
  }
  for (const name of ["play","pause","ended","timeupdate","seeked"]) audio.addEventListener(name,tick);
  canvas.onkeydown = e => {
    if (!overview || e.altKey || e.metaKey || e.ctrlKey) return;
    const step=e.shiftKey?1:.1, cursor=clamp(audio.currentTime,view.start,view.end);
    switch (e.key) {
      case "ArrowLeft": seek(Math.max(view.start,cursor-step)); break;
      case "ArrowRight": seek(Math.min(view.end,cursor+step)); break;
      case "Home": seek(view.start); break;
      case "End": seek(view.end); break;
      case " ": if (!e.repeat) void audition(playing ?? "track"); break;
      default: return;
    }
    e.preventDefault();
  };
  new ResizeObserver(draw).observe(canvas);

  async function sync() {
    if ($("#edit").hidden || !["trim","replace"].includes($("#editMode").value)) {stop(); return;}
    if (id===source.value) {update(); return;}
    id=source.value; overviewRequest?.abort(); cancelDetail();
    overviewRequest=new AbortController();
    const signal=overviewRequest.signal;
    stop(); overview=null; view={start:0,end:0}; drag=null; history=[];
    audio.removeAttribute("src"); audio.load(); audio.hidden=true;
    start.value="0"; end.value=""; committed=values();
    start.removeAttribute("max"); end.removeAttribute("max");
    bpm.value=""; offset.value=0; snap.checked=false; loop.checked=false; meter.value=4;
    canvas.setAttribute("aria-valuemax","0"); update();
    const track=tracks.find(t=>t.id===id);
    if (!track) {status.textContent=window.musicText("曲ができると、ここで範囲を選べます。"); return;}
    status.textContent=window.musicText("波形を読み込んでいます…");
    bpm.value=track.provenance.generation?.bpm ?? ""; audio.src=track.audio;
    try {
      const data=await envelope(signal);
      if (signal.aborted) return;
      overview={...data,start:0,end:data.duration};
      amplitude=Math.max(.02,...data.peaks);
      start.max=end.max=length(); canvas.setAttribute("aria-valuemax",String(length()));
      status.textContent=""; setView(0,length());
    } catch {
      if (!signal.aborted) {audio.hidden=false; status.textContent=window.musicText("波形を読み込めませんでした。秒数を入力して編集できます。");}
    }
  }
  source.addEventListener("change",sync); window.syncWaveform=sync; sync();
})();
