"""Observe one submitted vocal, then verify one reading-fix reservation end to end."""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import time
import urllib.error
import urllib.request


def main():
    p=argparse.ArgumentParser()
    p.add_argument("url")
    p.add_argument("job")
    p.add_argument("word")
    p.add_argument("reading")
    p.add_argument("report",type=Path)
    a=p.parse_args()
    def api(path,data=None):
        req=urllib.request.Request(a.url+path,None if data is None else json.dumps(data).encode(),
                                   {"Content-Type":"application/json"})
        with urllib.request.urlopen(req,timeout=12) as r:
            return json.load(r)
    def state(job):
        out={k:job[k] for k in ("id","state","progress","detail","node")}
        origin=job.get("result") or {}
        if origin:
            out.update(actual_seconds=origin.get("actual_seconds"),readings=origin.get("readings"),
                       singing_check=origin.get("singing_check"),file=origin.get("file"))
        return out
    def save(rows):
        tmp=a.report.with_suffix(".tmp")
        tmp.write_text(json.dumps({"checked_at":datetime.now(timezone.utc).isoformat(),**rows},ensure_ascii=False,indent=2),encoding="utf-8")
        tmp.replace(a.report)
    replacement={a.word:a.reading}
    while True:
        try:
            first=api("/jobs/"+a.job)
            out={"vocal":state(first)}
            if first["state"]=="done":
                check=first["result"]["singing_check"]
                assert 1<=len(check["attempts"])<=3 and isinstance(check["passed"],bool)
                assert a.word in first["result"]["lyrics"]
                matches=[j for j in api("/jobs") if j["spec"]["type"]=="regen_reading_fix" and
                         j["spec"]["source"]==first["track"] and (j["spec"]["readings"] or {}).get(a.word)==a.reading]
                fix=matches[0] if matches else api("/jobs",{"type":"regen_reading_fix","source":first["track"],"readings":replacement})
                out["regeneration"]=state(fix)
                if fix["state"]=="done":
                    origin=api("/tracks/"+fix["track"])["provenance"]
                    assert origin["readings"][a.word]==a.reading
                    assert any(e.get("source")==first["track"] for e in origin["processing"])
                    out["verified"]=True
                    save(out)
                    return
                if fix["state"] in ("cancelled","failed"):
                    save(out)
                    raise RuntimeError("Reading-fix job "+fix["state"])
            save(out)
            if first["state"] in ("cancelled","failed"):
                raise RuntimeError("Vocal job "+first["state"])
        except (urllib.error.URLError,TimeoutError) as err:
            if isinstance(err,urllib.error.HTTPError) and 400<=err.code<500:
                raise
        time.sleep(15)


if __name__=="__main__":
    main()
