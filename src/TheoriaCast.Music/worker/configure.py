"""Create a node profile from the operator's installed engine manifest."""
import argparse
import json
from pathlib import Path
import subprocess


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument("node", choices=["A","B"])
    p.add_argument("--api",required=True)
    p.add_argument("--root",type=Path,required=True)
    p.add_argument("--engine-manifest",type=Path,required=True)
    p.add_argument("--gpu",help="GPU UUID, required when multiple GPUs are present")
    p.add_argument("--lock",type=Path,required=True,help="Shared GPU lock used by other workloads")
    p.add_argument("--token-file",type=Path,help="Private worker token for a standalone worker")
    p.add_argument("--pause-file",type=Path,action="append")
    args=p.parse_args()
    profile=json.loads(args.engine_manifest.read_text(encoding="utf-8-sig"))
    if not all(isinstance(profile.get(k),str) and profile[k].strip() for k in ("model","version","license")):
        p.error("Specify the installed model, version and license in the manifest.")
    types=profile.get("types")
    if not isinstance(types,list) or not types or any(t not in {"generate_bgm","generate_vocal","regen_reading_fix","mix","edit"} for t in types):
        p.error("Specify the supported job types.")
    if not profile.get("render_command"):
        if not profile.get("python") or not profile.get("generator_module") or not profile.get("ffmpeg"):
            p.error("Supply render_command, or python, generator_module and ffmpeg for the reference renderer.")
        profile["renderer"]=str(Path(__file__).with_name("render.py").resolve())
        from receipt import model_info
        try:
            model_info(profile)
        except ValueError as error:
            p.error(str(error))
    if args.token_file and not args.token_file.is_file():
        p.error("The worker token file does not exist.")
    gpu=args.gpu
    if not gpu:
        rows=subprocess.check_output(["nvidia-smi","--query-gpu=uuid","--format=csv,noheader"],text=True).splitlines()
        if len(rows)!=1: p.error("Select a GPU with --gpu.")
        gpu=rows[0].strip()
    root=args.root.resolve()
    root.mkdir(parents=True,exist_ok=True)
    path=root/("node-"+args.node+".json")
    profile.update(node=args.node,api=args.api,gpu=gpu,lock=str(args.lock.resolve()),scratch=str(root/"scratch"),
                   pause_files=[str(f.resolve()) for f in args.pause_file or [root/("pause-"+args.node)]],
                   token_file=str(args.token_file.resolve()) if args.token_file else None)
    with path.open("x",encoding="utf-8") as output:
        json.dump(profile,output,indent=2,ensure_ascii=False)
    print(path)


if __name__=="__main__":
    main()
