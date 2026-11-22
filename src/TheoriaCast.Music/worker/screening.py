"""Objective triage for a review master. Human review owns the adoption decision."""
import json
import math
import re
import subprocess

import numpy as np
import soundfile as sf


def measure(ff,path,loop,target):
    args=[ff,"-hide_banner","-nostdin","-threads","2","-i",str(path),"-af",
          "silencedetect=n=-50dB:d=0.25,loudnorm=I=-18:TP=-1.5:LRA=11:print_format=json","-f","null","-"]
    result=subprocess.run(args,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE,text=True,errors="replace",check=True)
    summary=json.loads(re.findall(r'\{\s*"input_i".*?\}',result.stderr,re.S)[-1])
    lufs=float(summary["input_i"])
    lengths=[float(s) for s in re.findall(r"silence_duration:\s*([\deE.+-]+)",result.stderr)]
    peak=0.;first=None;last=None
    with sf.SoundFile(path) as wav:
        duration=wav.frames/wav.samplerate
        for chunk in wav.blocks(blocksize=65536,dtype="float32",always_2d=True):
            peak=max(peak,float(np.abs(chunk).max()))
            if first is None:
                first=chunk[0].copy()
            last=chunk[-1].copy()
    if first is None or not duration:
        raise ValueError("音声が空です。")
    silence=min(1,sum(lengths)/duration)
    boundary=float(np.abs(last-first).max()) if loop else None
    flags=[]
    if not math.isfinite(lufs) or not -21<=lufs<=-15:flags.append("音量を確認")
    if peak>=.99999:flags.append("音割れを確認")
    if silence>.3:flags.append("無音が多い")
    if target and abs(duration-target)>.2*target:flags.append("長さを確認")
    if boundary is not None and boundary>.02:flags.append("繰り返しのつなぎ目を確認")
    return {"integrated_lufs":lufs if math.isfinite(lufs) else None,"peak":peak,"clip_detected":peak>=.99999,
            "silence_ratio":silence,"silence_threshold_db":-50,"silence_min_seconds":.25,
            "loop_boundary_delta":boundary,"audio_seconds":duration,"target_seconds":target,"flags":flags}
