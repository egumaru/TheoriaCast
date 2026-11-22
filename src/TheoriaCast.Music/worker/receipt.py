"""Provenance fields shared with EgumaruMusic sidecar.schema.json."""
import csv
from datetime import datetime,timezone
import hashlib
import json
import re
import subprocess
import time
from pathlib import Path


def model_info(c):
    version=c.get("model_version")
    components=c.get("components")
    if version is None and components is None:
        from legacy_profiles import metadata
        previous=metadata(c)
        if previous:
            return previous
    if not isinstance(version,dict) or not version.get("code_repository") or not re.fullmatch(r"[0-9a-f]{40}",version.get("code_revision","")):
        raise ValueError("Supply the installed engine's model_version.code_repository and code_revision in the manifest.")
    version={"local_patch_sha256":None,**version}
    if not isinstance(components,list) or not components or any(not isinstance(part,dict) or
            not all(isinstance(part.get(k),str) and part[k].strip() for k in ("name","role","source","license","license_evidence")) or
            not re.fullmatch(r"[0-9a-f]{40}",part.get("revision","")) for part in components):
        raise ValueError("Supply the installed components with their source, revision and license evidence.")
    return version,components


def finalize(origin,c,s,folder,started):
    version,components=model_info(c)
    if not isinstance(origin.get("model_version"),dict):
        origin["model_version"]=version
    origin.setdefault("components",components)
    fields=subprocess.check_output(["nvidia-smi","-i",c["gpu"],"--query-gpu=uuid,name,driver_version","--format=csv,noheader"],text=True)
    uuid,name,driver=next(csv.reader(fields.splitlines(),skipinitialspace=True))
    origin["gpu"]={"uuid":uuid,"name":name,"driver_version":driver}
    origin["operator"]=s.get("creator","human")
    origin["review"]={"status":"pending","reviewer":None,"reviewed_at":None}
    origin["prompt"]=origin.get("prompt") or s["type"]
    origin["measurements"]={"audio_seconds":origin["actual_seconds"],"generation_wall_seconds":round(time.monotonic()-started,3)}
    if (folder/"lyrics.txt").exists():
        origin["lyrics_sha256"]=hashlib.sha256((folder/"lyrics.txt").read_bytes()).hexdigest()
    for step in origin["processing"]:
        step.setdefault("at",datetime.now(timezone.utc).isoformat())
        step.setdefault("params",{"filters":step.get("filters",[])})
