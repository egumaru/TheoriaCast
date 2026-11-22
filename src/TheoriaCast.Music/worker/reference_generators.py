"""Optional adapters for separately installed ACE-Step and YuE2 engines."""
from contextlib import ExitStack, nullcontext
import hashlib
import os, platform, random, subprocess
from pathlib import Path
from render import progress, transcribe
from singing import prepare, audit, correction, lyric_cues


def seed_runtime(seed):
    import numpy as np
    import torch
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    torch.cuda.manual_seed_all(seed)
    torch.backends.cudnn.benchmark = False
    torch.backends.cudnn.deterministic = True
    torch.use_deterministic_algorithms(True, warn_only=True)


def source_hash(path):
    # Git and archive extraction can use different line endings for equivalent source.
    return hashlib.sha256(path.read_text(encoding="utf-8").encode("utf-8")).hexdigest()


def capture_take(c,s,ff,settings,arguments):
    import torch
    runtime = dict(settings, python=platform.python_version(), torch=str(torch.__version__),
                   cuda=torch.version.cuda, gpu=torch.cuda.get_device_name(0),
                   driver=subprocess.check_output(["nvidia-smi","-i",c["gpu"],"--query-gpu=driver_version",
                                                   "--format=csv,noheader"],text=True).strip(),
                   ffmpeg=subprocess.check_output([ff,"-version"],text=True).splitlines()[0],
                   adapter_sha256=source_hash(Path(__file__)), renderer_sha256=source_hash(Path(__file__).with_name("render.py")), singing_sha256=source_hash(Path(__file__).with_name("singing.py")))
    take = dict(model=c["model"],revision=c["version"],runtime=runtime,arguments=arguments)
    previous=s.get("replay")
    if previous:
        for key,value in take.items():
            if previous.get(key)!=value:
                raise ValueError("再現用 JSON と実行条件が一致しません（"+key+"）。")
    return take


def generate(c,s,ff,raw,folder,seed):
    with ExitStack() as resources:
        return _generate(c,s,ff,raw,folder,seed,resources)


def _generate(c,s,ff,raw,folder,seed,resources):
    os.environ["CUBLAS_WORKSPACE_CONFIG"]=":4096:8"
    seed_runtime(seed)
    replay=s.get("replay")
    prompt=", ".join(filter(None,[s["style"],s["theme"],s.get("instruments"),
                                  f"{s['bpm']} BPM" if s.get("bpm") else ""]))
    if s.get("prompt_md"):
        prompt += "\n\n"+s["prompt_md"]
    weights=Path(c["weights"])
    if not weights.is_dir():
        raise FileNotFoundError("モデルの保存場所を確認してください。")
    if s["type"]=="generate_bgm":
        from acestep.pipeline_ace_step import ACEStepPipeline
        opts=dict(audio_duration=s["seconds"],infer_step=32,guidance_scale=15,manual_seeds=[seed],
                  retake_seeds=[seed],lyrics="[inst]",prompt=prompt)
        free=int(subprocess.check_output(["nvidia-smi","-i",c["gpu"],"--query-gpu=memory.free","--format=csv,noheader,nounits"],text=True))
        offload=replay["runtime"].get("cpu_offload") if replay else free<11000
        if not isinstance(offload,bool):
            raise ValueError("メモリ設定が無効です。")
        take=capture_take(c,s,ff,dict(cpu_offload=offload,overlapped_decode=True),opts)
        pipe=ACEStepPipeline(checkpoint_dir=str(weights),cpu_offload=offload,overlapped_decode=True)
        progress(18,"楽器の音を作っています")
        # ACE-Step writes its own invocation JSON next to the raw audio.
        # Keep model scratch files outside the deliverable.
        generated=Path("ace-output.wav").resolve()
        pipe(**opts,save_path=str(generated))
        generated.replace(raw)
        return {"params":{**opts,"cpu_offload":offload},"seed":seed,"prompt":prompt,"readings":{}},[],take
    from yue2 import YuE2Pipeline
    import pykakasi
    class SingleTakePipeline(YuE2Pipeline):
        def decode(self, latents, **kwargs):
            # The final latent array is independent of the generator. Release its
            # weights before the decoder loads, avoiding a full CPU copy on TKY-PC.
            self.close()
            return super().decode(latents, **kwargs)

    converter=pykakasi.kakasi()
    free=int(subprocess.check_output(["nvidia-smi","-i",c["gpu"],"--query-gpu=memory.free","--format=csv,noheader,nounits"],text=True).strip())
    target=min(s["seconds"],180) if s["shorts"] else s["seconds"]
    hint=round(target*146/170)
    if s["shorts"]:
        hint=min(150,hint)
    prompt+=f", Japanese vocals, approximately {hint} seconds, clean ending"
    opts={"cot":"full","semantic_sampling":{"max_tokens":min(9000,max(240,round(hint*55)))}}
    attempts=[]; changes={}; rows=[]; issues=[]
    (folder/"lyrics.txt").write_text(s["lyrics"],encoding="utf-8")
    single=c.get("single_take",c["node"]=="A")
    backend=c.get("backend","torch")
    low_vram=c.get("low_vram",False)
    budget=replay["runtime"].get("memory_budget_gib") if replay else min(24,free/1024)
    if not isinstance(budget,(float,int)) or not 0<budget<=24:
        raise ValueError("Invalid memory budget")
    def load_pipeline():
        pipeline=SingleTakePipeline if single else YuE2Pipeline
        return pipeline.from_pretrained(str(weights/"YuE2-3B"),vae=str(weights/"YuE2-Vae"),device="cuda",
                                        local_files_only=True,low_vram=low_vram,memory_budget_gib=budget,backend=backend)
    shared=None
    for i in range(1 if replay else 3):
        progress(8+i*27,f"歌を作っています（{i+1}回目）")
        sheet,applied,original=prepare(s,changes)
        (folder/"singing.txt").write_text(sheet,encoding="utf-8")
        actual_seed=(seed+i)%2147483647
        seed_runtime(actual_seed)
        arguments=dict(style=prompt,lyrics=sheet,seed=actual_seed,**opts)
        if s.get("score_abc"):
            arguments["abc"]=s["score_abc"]
        take=capture_take(c,s,ff,dict(backend=backend,low_vram=low_vram,memory_budget_gib=budget,
                                     single_take=single),arguments)
        if not single and shared is None:
            shared=resources.enter_context(load_pipeline())
        with (load_pipeline() if single else nullcontext(shared)) as pipe:
            song=pipe(**arguments)
            song.save(raw)
            if song.abc:
                (folder/"score.abc").write_text(song.abc,encoding="utf-8")
            else:
                (folder/"score.abc").unlink(missing_ok=True)
        progress(27+i*27,"歌ったことばを確認しています")
        rows=transcribe(c,ff,raw)
        heard="".join(r["text"] for r in rows)
        issues=audit(sheet,heard,converter)
        attempts.append(dict(seed=actual_seed,singing_lyrics=sheet,readings=applied,transcript=heard,segments=rows,issues=list(issues)))
        if not issues or replay:
            break
        fix=correction(original,sheet,heard,converter,applied)
        if not fix:
            issues.append("聞き取りだけでは別の読みを決められません。試聴して読み方を指定してください。")
            break
        changes.update(fix)
    return {"generation":s,"requested_readings":s.get("readings") or {},
            "params":{**opts,"requested_model_seconds":hint,"lyrics":sheet,"low_vram":low_vram,"backend":backend},"seed":actual_seed,
            "lyrics_ref":"lyrics.txt","readings":applied,"prompt":prompt,
            "singing_check":{"passed":not issues,"issues":issues,"attempts":attempts}},lyric_cues(original,sheet,rows,converter),take
