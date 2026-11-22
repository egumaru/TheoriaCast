"""Run against the isolated acceptance host supplied by the test launcher."""
import hashlib
import io
import json
import time
import urllib.request
import zipfile


def render_roundtrip(descriptor,kinds):
    server=json.loads(descriptor.read_text("utf-8-sig"))
    def request(path,body=None,content="application/json"):
        req=urllib.request.Request(server["api_url"].rstrip("/")+path,body,
                {"Authorization":"Bearer "+server["token"],"Content-Type":content})
        with urllib.request.urlopen(req,timeout=90) as reply:
            return reply.read()
    def run(spec):
        created=json.loads(request("/jobs",json.dumps(spec).encode()))
        id=created["id"]
        print("Job",id,spec["type"],"replay" if spec.get("replay") else "new",flush=True)
        for _ in range(720):
            j=json.loads(request("/jobs/"+id))
            if j["state"]=="done":
                return id
            if j["state"] in ("failed","cancelled"):
                raise RuntimeError(j["detail"])
            time.sleep(2)
        raise TimeoutError(id)
    for kind in kinds:
        spec=dict(type=kind,theme="雨上がり",seconds=6,prompt_md="# 雨上がり\nSoft piano, gentle walking rhythm.",seed=39241)
        if kind=="generate_vocal":
            spec.update(preferred_node="A",lyrics="[Intro]\nあめのあと\nそらがひかる",score_abc="X:1\nM:4/4\nL:1/4\nK:C\nC D E G | G E D C |]\n")
        first=run(spec)
        path="/tracks/"+first
        recipe=json.loads(request(path+"/materials/generation.json"))
        bundle=request(path+"/composition.zip")
        with zipfile.ZipFile(io.BytesIO(bundle)) as z:
            assert z.read("prompt.md").decode()==spec["prompt_md"]
        imported=json.loads(request("/composition/import",bundle,"application/zip"))
        assert imported["final"]["take"]==recipe["take"]
        if kind=="generate_vocal":
            assert recipe["take"]["arguments"]["abc"]==spec["score_abc"]
            assert "Intro" not in recipe["take"]["arguments"]["lyrics"]
        repeat=run(dict(recipe["job"],replay=recipe["take"]))
        digest=hashlib.sha256(request("/tracks/"+repeat+"/audio")).hexdigest()
        assert digest==recipe["take"]["master_sha256"]==hashlib.sha256(request(path+"/audio")).hexdigest()
        print(json.dumps({"kind":kind,"original":first,"replay":repeat,"sha256":digest}),flush=True)
