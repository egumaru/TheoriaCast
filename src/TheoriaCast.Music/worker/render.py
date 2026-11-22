"""Isolated, interruptible render process. Audio and its receipt are committed together."""
from datetime import datetime, timezone
import hashlib
import json
import math
import os
from pathlib import Path
import re
import secrets
import shutil
import socket
import subprocess
import sys
import time

import soundfile as sf

from singing import audit, correction, prepare, lyric_cues
from receipt import finalize
from screening import measure


def write(path, data):
    temp=path.with_suffix(".tmp")
    temp.write_text(json.dumps(data,ensure_ascii=False,indent=2,allow_nan=False),encoding="utf-8")
    for retry in range(5):
        try:
            temp.replace(path)
            return
        except PermissionError:
            if retry==4:
                raise
            time.sleep(.05*(retry+1))


def progress(value, message):
    write(Path("progress.json"),[value,message])


def sha(path):
    with path.open("rb") as f:
        return hashlib.file_digest(f,"sha256").hexdigest()


def media(ff, inputs, options, dest):
    args=[ff,"-nostdin","-hide_banner","-loglevel","error","-y","-threads","2"]
    for path in inputs:
        args.extend(["-i",str(path)])
    result=subprocess.run([*args,*map(str,options),str(dest)],capture_output=True)
    if result.returncode:
        raise ValueError(result.stderr.decode(errors="replace")[-700:])


def seconds(path):
    return sf.info(str(path)).duration


def stamp(value):
    ms=round(1000*value)
    h,ms=divmod(ms,3600000)
    m,ms=divmod(ms,60000)
    s,ms=divmod(ms,1000)
    return f"{h:02}:{m:02}:{s:02}.{ms:03}"


def track_info(c, track):
    return json.loads((Path("inputs") / (track + ".json")).read_text(encoding="utf-8"))


def get_input(c, track, index):
    t=track_info(c,track)
    f=Path("inputs") / (track + ".wav")
    digest=sha(f)
    if digest!=t["provenance"]["sha256"]:
        raise ValueError("元の音声と出自の照合に失敗しました。")
    return f,t["provenance"],{"track":track,"sha256":digest,"processing":t["provenance"].get("processing",[])}


def edit(c,s,ff,master):
    f,origin,parent=get_input(c,s["source"],0)
    if s.get("replacement"):
        return replace_interval(c,s,ff,master,f,origin,parent)
    cues_from_source=origin.get("cues",[])
    points=s.get("timing") or []
    if points:
        length=seconds(f)
        anchors=[(0,0),*((p["from"],p["to"]) for p in points)]
        last_from,last_to=anchors[-1]
        if last_from>length:
            raise ValueError("移動する歌の位置が曲の長さを超えています。")
        if last_from<length:
            anchors.append((length,last_to+length-last_from))
        pieces=[]
        def mapped(t):
            for (a,x),(b,y) in zip(anchors,anchors[1:]):
                if t<=b:
                    return x+(t-a)*(y-x)/(b-a)
            return anchors[-1][1]
        for i,((a,x),(b,y)) in enumerate(zip(anchors,anchors[1:])):
            pieces.append(f"[0:a]atrim=start={a}:end={b},asetpts=PTS-STARTPTS,atempo={(b-a)/(y-x)}[p{i}]")
        tags="".join(f"[p{i}]" for i in range(len(pieces)))
        pieces.append(f"{tags}concat=n={len(anchors)-1}:v=0:a=1[out]")
        warped=Path("timing.wav")
        media(ff,[f],["-filter_complex",";".join(pieces),"-map","[out]","-c:a","pcm_s24le"],warped)
        f=warped
        cues_from_source=[{**row,"start":mapped(row["start"]),"end":mapped(row["end"])} for row in cues_from_source]
    finish=s["end"] if s["end"] is not None else seconds(f)
    duration=finish-s["start"]
    if duration<=0 or finish>seconds(f)+.001 or max(s["fade_in"],s["fade_out"])>duration:
        raise ValueError("切り取りまたはフェードが曲の長さを超えています。")
    filt=[f"atrim=start={s['start']}:end={finish}","asetpts=PTS-STARTPTS"]
    if s["fade_in"]:
        filt.append(f"afade=t=in:d={s['fade_in']}")
    if s["fade_out"]:
        filt.append(f"afade=t=out:st={duration-s['fade_out']}:d={s['fade_out']}")
    media(ff,[f],["-af",",".join(filt),"-c:a","pcm_s24le"],master)
    cues=s["cues"]
    if cues is None:
        cues=[{**row,"start":max(0,row["start"]-s["start"]),"end":min(finish,row["end"])-s["start"]}
              for row in cues_from_source if row["end"]>s["start"] and row["start"]<finish]
    return origin,[{k:v for k,v in parent.items() if k!="processing"}],cues


def replace_interval(c,s,ff,master,source,origin,parent):
    donor,other,donor_parent = get_input(c,s["replacement"],1)
    info = sf.info(str(source))
    rate = info.samplerate
    start, end = (round(s[key]*rate) for key in ("start","end"))
    offset = round(s["replacement_start"]*rate)
    count = end-start
    if not (0 <= start < end <= info.frames and 0 <= offset and
            offset+count <= round(seconds(donor)*rate)):
        raise ValueError("差し替える範囲が曲の長さを超えています。")

    filters, tags, cues = [], [], []
    layout = f"aformat=sample_rates={rate}:channel_layouts={info.channels}c"
    def piece(stream, first, last, target, metadata):
        if first == last:
            return
        tag = f"[p{len(tags)}]"
        filters.append(f"[{stream}:a]{layout},atrim=start_sample={first}:end_sample={last},asetpts=PTS-STARTPTS{tag}")
        tags.append(tag)
        for cue in metadata.get("cues", []):
            left, right = max(cue["start"], first/rate), min(cue["end"], last/rate)
            if right > left:
                cues.append({**cue, "start":left+(target-first)/rate, "end":right+(target-first)/rate})

    piece(0,0,start,0,origin)
    piece(1,offset,offset+count,start,other)
    piece(0,end,info.frames,end,origin)
    filters.append("".join(tags)+f"concat=n={len(tags)}:v=0:a=1[out]")
    media(ff,[source,donor],["-filter_complex",";".join(filters),"-map","[out]","-c:a","pcm_s24le"],master)
    components = {x["revision"]:x for meta in (origin,other) for x in meta.get("components",[])}
    result = {"model":" + ".join(dict.fromkeys([origin["model"], other["model"]])),
              "model_version":origin["model_version"], "source_versions":[m["model_version"] for m in (origin,other)],
              "license":" / ".join(dict.fromkeys([origin["license"], other["license"]])),
              "components":list(components.values()), "lyrics":"\n".join(cue["text"] for cue in cues)}
    return result, [parent,donor_parent], cues


def mix(c,s,ff,master):
    paths=[]; parents=[]; filters=[]; origins=[]; times=[]
    for i,clip in enumerate(s["clips"]):
        f,origin,parent=get_input(c,clip["track"],i)
        paths.append(f); parents.append(parent); origins.append(origin); times.append(seconds(f))
        filters.append(f"[{i}:a]aformat=sample_rates=48000:channel_layouts=stereo,volume={clip['gain']}[a{i}]")
    tags="".join(f"[a{i}]" for i in range(len(paths)))
    overlap=s["crossfade"] if s["mode"]=="join" else 0
    if s["mode"]=="layer":
        filters.append(f"{tags}amix=inputs={len(paths)}:duration=longest:normalize=0,alimiter=limit=0.95:latency=1[out]")
    elif not overlap:
        filters.append(f"{tags}concat=n={len(paths)}:v=0:a=1[out]")
    else:
        if min(times)<=overlap:
            raise ValueError("重ねる長さを、それぞれの曲より短く指定してください。")
        last="a0"
        for i in range(1,len(paths)):
            tag="out" if i==len(paths)-1 else f"join{i}"
            filters.append(f"[{last}][a{i}]acrossfade=d={overlap}:c1=tri:c2=tri[{tag}]")
            last=tag
    media(ff,paths,["-filter_complex",";".join(filters),"-map","[out]","-c:a","pcm_s24le"],master)
    cues=[]; offset=0
    for o,t in zip(origins,times):
        cues.extend({**row,"start":row["start"]+offset,"end":row["end"]+offset} for row in o.get("cues",[]))
        if s["mode"]=="join":
            offset+=t-overlap
    components={item["revision"]:item for o in origins for item in o.get("components",[])}
    return {"model":" + ".join(dict.fromkeys(o["model"] for o in origins)),"model_version":origins[0]["model_version"],
            "components":list(components.values()),"source_versions":[o["model_version"] for o in origins],
            "renderer":subprocess.check_output([ff,"-version"],text=True).splitlines()[0],
            "license":" / ".join(dict.fromkeys(o["license"] for o in origins))},parents,sorted(cues,key=lambda x:x["start"])


def transcribe(c,ff,audio):
    mono=Path("asr.wav")
    media(ff,[audio],["-ac","1","-ar","16000","-c:a","pcm_s16le"],mono)
    subprocess.run([c["whisper"],"-ng","-l","ja","-t","3","-mc","0","-oj","-m",c["whisper_model"],
                    "-f",str(mono),"-of","asr"],check=True,stdout=subprocess.DEVNULL)
    rows=json.loads(Path("asr.json").read_text(encoding="utf-8"))["transcription"]
    return [{"start":r["offsets"]["from"]/1000,"end":r["offsets"]["to"]/1000,"text":r["text"].strip()} for r in rows]


def generate(c,s,ff,raw,folder,seed):
    import importlib
    # Legacy profiles predate the generator_module field and used the reference adapter.
    module=importlib.import_module(c.get("generator_module", "reference_generators"))
    return module.generate(c,s,ff,raw,folder,seed)


def master_filters(ff, raw, filters):
    target="loudnorm=I=-18:TP=-1.5:LRA=11"
    chain=",".join([*filters,target+":print_format=json"])
    log=subprocess.run([ff,"-nostdin","-hide_banner","-i",str(raw),"-af",chain,"-f","null","-"],
                       check=True,capture_output=True,text=True,errors="replace").stderr
    measured=json.loads(log[log.rindex("{"):log.rindex("}")+1])
    fields={"measured_I":"input_i","measured_TP":"input_tp","measured_LRA":"input_lra",
            "measured_thresh":"input_thresh","offset":"target_offset"}
    if all(math.isfinite(float(measured[key])) for key in fields.values()):
        settings=":".join(key+"="+measured[value] for key,value in fields.items())
        return [*filters,target+":"+settings+":linear=true"],measured
    # Silence and very short signals have no finite integrated loudness.
    return [*filters,"anull"],measured


def main():
    started=time.monotonic()
    cfg=Path(sys.argv[1])
    c=json.loads(cfg.read_text(encoding="utf-8-sig"))
    sys.path.insert(0,str(cfg.parent/"python-libs"))
    job=json.loads((Path(sys.argv[2])/"job.json").read_text(encoding="utf-8"))
    s=job["spec"]; folder=Path("deliver"); folder.mkdir()
    ff=c["ffmpeg"]
    if ff=="imageio":
        import imageio_ffmpeg
        ff=imageio_ffmpeg.get_ffmpeg_exe()
    raw=folder/"raw.wav"; master=folder/"master.wav"
    seed=s.get("seed") if s.get("seed") is not None else secrets.randbelow(2147483646)
    origin={"schema_version":1,"model":c["model"],"model_version":c["version"],"license":c["license"],
            "operator":"TheoriaCast","created_at":datetime.now(timezone.utc).isoformat(),"machine":socket.gethostname(),
            "gpu":c["gpu"],"processing":[],"generation":s,"lyrics_ref":None,"seed":None,"lyrics":s["lyrics"],
            "prompt":s["theme"],"params":s,"readings":s.get("readings") or {}}
    progress(5,"曲を準備しています")
    if s["type"] in ("mix","edit"):
        parent,parents,cues=edit(c,s,ff,master) if s["type"]=="edit" else mix(c,s,ff,master)
        for k in ("model","model_version","components","source_versions","renderer","license","generation","lyrics","readings","seed","singing_check"):
            if k in parent:
                origin[k]=parent[k]
        origin["processing"]=[*parent.get("processing",[]),{"operation":s["type"],"parents":parents,"params":s}]
        if origin.get("lyrics"):
            (folder/"lyrics.txt").write_text(origin["lyrics"],encoding="utf-8")
            origin["lyrics_ref"]="lyrics.txt"
    else:
        meta,cues,take=generate(c,s,ff,raw,folder,seed)
        origin.update(meta)
        if s.get("prompt_md"):
            (folder/"prompt.md").write_bytes(s["prompt_md"].encode("utf-8"))
        raw_seconds=seconds(raw)
        filters=[]
        if s["shorts"] and raw_seconds>180:
            filters.extend(["atrim=end=180","afade=t=out:st=177:d=3"])
        filters,measured=master_filters(ff,raw,filters)
        media(ff,[raw],["-af",",".join(filters),"-ar","48000","-c:a","pcm_s24le"],master)
        take["master_sha256"]=sha(master)
        if s.get("replay") and s["replay"]["master_sha256"]!=take["master_sha256"]:
            raise ValueError("元テイクと生成 WAV の SHA-256 が一致しません。")
        frozen={k:s[k] for k in ("type","theme","lyrics","style","seconds","prompt_md","score_abc",
                                  "bpm","instruments","shorts","preferred_node") if k in s}
        if frozen["type"]=="regen_reading_fix":
            frozen["type"]="generate_vocal"
        frozen.update(seed=meta["seed"],readings={**(s.get("readings") or {}),**meta["readings"]})
        if frozen["type"]=="generate_vocal":
            frozen["preferred_node"]=c["node"]
        write(folder/"generation.json",dict(format="TheoriaCast.Music",version=1,job=frozen,take=take))
        origin["generation"]={k:v for k,v in s.items() if k!="replay"}
        if s.get("replay"):
            origin["replay_verified"]=True
        origin["processing"]=[{"operation":"master","measured":measured,"filters":filters,"parent_sha256":sha(raw)}]
        if s["type"]=="regen_reading_fix":
            parent=track_info(c,s["source"])["provenance"]
            origin["processing"].insert(0,{"operation":"regen_reading_fix","source":s["source"],"parent_sha256":parent["sha256"]})
        origin["raw_seconds"]=raw_seconds
    duration=seconds(master)
    if s["type"]=="edit" and s.get("cues") and any(r["end"]>duration+.001 for r in cues):
        raise ValueError("歌詞の表示時刻が曲の長さを超えています。")
    cues=[{**r,"end":min(r["end"],duration)} for r in cues if 0<=r["start"]<min(r["end"],duration)]
    origin["renderer"]=subprocess.check_output([ff,"-version"],text=True).splitlines()[0]
    origin.update(actual_seconds=duration,cues=cues,file="master.wav",sha256=sha(master))
    if s["type"] not in ("mix","edit"):
        origin.update(target_seconds=s["seconds"],duration_delta=round(duration-s["seconds"],3),
                      duration_needs_review=abs(duration-s["seconds"])>s["seconds"]*.2)
    if cues:
        (folder/"lyrics.vtt").write_text("WEBVTT\n\n"+"\n\n".join(stamp(r["start"])+" --> "+stamp(r["end"])+"\n"+r["text"] for r in cues)+"\n",encoding="utf-8")
    progress(94,"試聴用の音声を整えています")
    media(ff,[master],["-c:a","libmp3lame","-q:a","2"],folder/"preview.mp3")
    origin["screening"]=measure(ff,master,s["type"]=="generate_bgm",origin.get("target_seconds"))
    finalize(origin,c,s,folder,started)
    write(folder/"master.wav.json",origin)


if __name__=="__main__":
    main()
