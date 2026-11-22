"""Re-evaluate stored transcripts after the multiline kana comparator fix."""
import argparse
from datetime import datetime,timezone
import json
from pathlib import Path
import sqlite3
import sys

import pykakasi


def main():
    p=argparse.ArgumentParser()
    p.add_argument("data",type=Path)
    p.add_argument("worker",type=Path)
    p.add_argument("ids",nargs="+")
    args=p.parse_args()
    sys.path.insert(0,str(args.worker.resolve()))
    from singing import audit
    converter=pykakasi.kakasi()
    with sqlite3.connect(args.data/"music.sqlite",timeout=20) as db:
        for job_id in args.ids:
            db.execute("BEGIN IMMEDIATE")
            row=db.execute("SELECT folder,origin FROM tracks WHERE id=?",(job_id,)).fetchone()
            if not row:
                raise KeyError(job_id)
            folder,origin=row[0],json.loads(row[1])
            check=origin["singing_check"]
            if check.get("comparison_revision")!="per-line-kana-2":
                previous=list(check["issues"])
                for attempt in check["attempts"]:
                    attempt["issues"]=audit(attempt["singing_lyrics"],attempt["transcript"],converter)
                issues=list(check["attempts"][-1]["issues"])
                if issues:
                    issues.extend(msg for msg in previous if msg.startswith("聞き取りだけでは"))
                check.update(issues=issues,passed=not issues,comparison_revision="per-line-kana-2",
                             reassessed_at=datetime.now(timezone.utc).isoformat(),previous_issues=previous)
                db.execute("UPDATE tracks SET origin=? WHERE id=?",(json.dumps(origin,ensure_ascii=False),job_id))
            # Re-running the command also repairs an interrupted sidecar projection.
            take=args.data/folder
            for path in [*take.glob("*.wav.json"),*take.glob("*.mp3.json")]:
                side=json.loads(path.read_text(encoding="utf-8"))
                side["singing_check"]=check
                tmp=path.with_suffix(".recheck-tmp")
                tmp.write_text(json.dumps(side,indent=2,ensure_ascii=False),encoding="utf-8")
                tmp.replace(path)
            db.commit()
            print(job_id,check["passed"],check["issues"])


if __name__=="__main__":
    main()
