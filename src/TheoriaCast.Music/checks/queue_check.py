"""Run an isolated acceptance test against a published host executable."""
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import json
import os
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

exe=Path(sys.argv[1]).resolve()
with tempfile.TemporaryDirectory() as tmp:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1",0))
        base="http://127.0.0.1:"+str(sock.getsockname()[1])
    host=subprocess.Popen([str(exe),"--urls",base,"--Music:Capabilities:A:0","generate_bgm"],cwd=exe.parent,env={**os.environ,"Music__Data":tmp},
                          stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    def api(path,body=None):
        data=None if body is None else json.dumps(body).encode()
        credentials=Path(tmp)/"server-access.json"
        if not credentials.exists(): raise OSError("Waiting for server identity")
        keys=json.loads(credentials.read_text("utf-8-sig"))
        role="worker_token" if path.startswith("/worker/") else "client_token"
        with urllib.request.urlopen(urllib.request.Request(base+path,data,{"Content-Type":"application/json",
                "Authorization":"Bearer "+keys[role]}),timeout=10) as r:
            return json.loads(r.read() or b"null")
    try:
        for _ in range(50):
            try:
                api("/nodes");break
            except OSError:
                time.sleep(.2)
        def submit(i):
            return api("/jobs",{"type":"bgm","theme":str(i),"priority":"batch"})["id"]
        with ThreadPoolExecutor(max_workers=6) as pool:
            ids=list(pool.map(submit,range(8)))
        assert len(set(ids))==8
        first=api("/jobs",{"type":"bgm","theme":"interactive","bpm":112})["id"]
        report={"node":"A","free_mib":11000,"types":["generate_bgm"],"ready":True,"detail":""}
        assert api("/worker/poll?claim=true",{**report,"free_mib":8191}) is None
        with ThreadPoolExecutor(max_workers=6) as pool:
            claims=[j for j in pool.map(lambda _:api("/worker/poll?claim=true",report),range(6)) if j]
        assert len(claims)==1 and claims[0]["id"]==first
        path="/worker/"+first+"/progress"
        api(path,{"state":"queued","lease":claims[0]["lease"]})
        second=api("/worker/poll?claim=true",report)
        assert second["id"]==first and second["lease"]!=claims[0]["lease"]
        try:
            api(path,{"state":"failed","lease":claims[0]["lease"]})
            raise AssertionError("stale worker overwrote current claim")
        except urllib.error.HTTPError as e:
            assert e.code==409
        assert api("/jobs/"+first+"/cancel",{})["state"]=="cancelled"
        assert len(api("/jobs"))==9
        print("PASS concurrent submission, priority, VRAM gate, single claim, stale lease and cancellation")
    finally:
        host.terminate()
        host.wait(timeout=10)
