"""Compare portrait EXIF processing before and after the review and exercise queue recovery."""

import argparse
from base64 import b64decode, b64encode
from copy import deepcopy
from hashlib import sha256
from io import BytesIO
import json
import os
from pathlib import Path
from tempfile import mkdtemp
from unittest.mock import patch
from uuid import uuid4
import wave

from PIL import Image, ImageOps

from verify_karaoke import finish, stage_audio
from verify_visual import ROOT, Server, await_result, image, recipe_packet


def read_json(path):
    return json.loads(path.read_bytes())


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")


def portrait_jpeg(photo, target):
    with Image.open(photo) as source:
        portrait = ImageOps.fit(source.convert("RGB"), (180, 270))
    portrait = portrait.transpose(Image.Transpose.ROTATE_90)
    orientation = Image.Exif()
    orientation[274] = 6
    portrait.save(target, format="JPEG", quality=96, exif=orientation)
    return target.read_bytes()


def import_photo(api, name, jpeg):
    return api.mcp("import_asset", request={"name": name, "sha256": sha256(jpeg).hexdigest(),
                                           "bytes": b64encode(jpeg).decode()})


def mcp_image(api, ref, media_type):
    data = bytearray()
    while True:
        block = api.mcp("read_image", reference=ref, offset=len(data), count=3072)
        assert block["offset"] == len(data) and block["mediaType"] == media_type and block["sha256"] == ref["sha256"]
        data.extend(b64decode(block["bytes"], validate=True))
        if len(data) == block["totalBytes"]:
            return bytes(data)
        assert len(data) < block["totalBytes"] and block["bytes"]


def run_image(api, ref, settings):
    job = api.finish(api.mcp("submit_batch", request={"settings": settings, "hardware": "cpu", "inputs": [ref]}),
                     via_mcp=True)
    return image(api, job["items"][0]["output"]), job


def with_exif_fixture(api, jpeg, folder, asset, old_style, old_matte):
    ref = asset["reference"]
    assert (asset["width"], asset["height"]) == (180, 270)
    saved = mcp_image(api, ref, "image/jpeg")
    assert saved == jpeg and sha256(saved).hexdigest() == ref["sha256"]
    style, job = run_image(api, ref, {"operation": "stylize", "touch": "detail", "strength": 0})
    assert mcp_image(api, job["items"][0]["output"], "image/png") == style
    assert sha256(style).hexdigest() == job["items"][0]["output"]["sha256"]

    after = Image.open(BytesIO(style)).convert("RGB")
    before = Image.open(BytesIO(old_style)).convert("RGB")
    assert before.size == (270, 180) and after.size == (180, 270)
    assert before.transpose(Image.Transpose.ROTATE_270).tobytes() == after.tobytes()
    assert recipe_packet(style)["engine"]["fixedParameters"]["jpegOrientation"].startswith("OpenCV EXIF")
    libraries = recipe_packet(style)["engine"]["libraries"]
    assert libraries["OpenCvSharp"].startswith("4.13") and libraries["OpenCV"]
    matte, _ = run_image(api, ref, {"operation": "background", "background": "transparent"})
    solid, _ = run_image(api, ref, {"operation": "background", "background": "solid", "color": "#103040"})
    after_mask = Image.open(BytesIO(matte))
    assert Image.open(BytesIO(old_matte)).size == before.size
    assert after_mask.mode == "RGBA" and after_mask.size == after.size
    assert after_mask.getchannel("A").getextrema()[0] < after_mask.getchannel("A").getextrema()[1]
    assert Image.open(BytesIO(solid)).size == after.size
    assert recipe_packet(matte)["engine"]["libraries"]["ONNX Runtime"]
    inspection = api.finish(api.mcp("submit_batch", request={"settings": {"operation": "inspect"},
                            "hardware": "cpu", "inputs": [ref]}), via_mcp=True)
    assert "縦長" in inspection["items"][0]["inspection"]["tags"]
    for prefix, data in [("before-style", old_style), ("after-style", style), ("before-matte", old_matte),
                         ("after-matte", matte), ("after-solid", solid)]:
        (folder / f"{prefix}.png").write_bytes(data)
    for baseline in (old_style, old_matte, style, matte):
        recipe = recipe_packet(baseline)
        replay = api.finish(api.mcp("replay_recipe", request={"recipeId": recipe["id"]}), via_mcp=True)
        assert replay["items"][0]["output"]["sha256"] == sha256(baseline).hexdigest()
    invalid = api.post("/jobs/estimate", {"settings": {"operation": "resize", "fitMode": "pad", "width": 320,
                       "height": 240, "marginOffsets": {"left": .7, "right": .4}}, "inputs": [ref]}, 400)
    assert "領域" in invalid["error"]
    print("PASS: EXIF 6 pixels, frozen old recipes, BiRefNet orientation, image chunks and API errors.",
          flush=True)
    return {"jpegSha256": ref["sha256"], "orientation": 6, "before": list(before.size), "after": list(after.size),
            "rotatedReferencePixelMatch": True, "legacyRecipeHashMatches": 2, "newRecipeHashMatches": 2,
            "matteOrientationMatches": True,
            "libraries": recipe_packet(matte)["engine"]["libraries"]}


def seed_recovery(api, folder, asset):
    image_job = api.finish(api.mcp("submit_batch", request={"settings": {"operation": "resize", "width": 80, "height": 120},
                           "hardware": "cpu", "inputs": [asset["reference"]]}), via_mcp=True)
    audio_path = folder / "queue-audio.wav"
    with wave.open(str(audio_path), "wb") as audio:
        audio.setparams((1, 2, 16000, 0, "NONE", ""))
        audio.writeframes(bytes(32000))
    wav = stage_audio(api, audio_path)
    video = finish(api, api.mcp("submit_karaoke", request={"mode": "bars",
                   "input": {"title": "Queue verification", "audio": wav,
                             "lyrics": [{"start": .1, "end": .8, "text": "確認"}],
                             "notes": [{"start": .2, "duration": .4, "midi": 64}]},
                   "style": {"width": 320, "height": 240}})["id"])
    api.stop()
    state_path = api.directory / "library" / "library.json"
    state = read_json(state_path)
    image_ids = [uuid4().hex for _ in range(5)]
    template = state["jobs"][-1]
    assert template["id"] == image_job["id"]
    for id, status, attempts in zip(image_ids, ("running", "queued", "running", "queued", "running"), (3, 0, 2, 0, 0)):
        job = deepcopy(template)
        job.update(id=id, status=status)
        items = [{"recipeId": template["items"][0]["recipeId"], "status": status, "attempts": attempts}]
        if id == image_ids[3]:
            items.insert(0, {"recipeId": uuid4().hex, "status": "queued"})
        elif id == image_ids[4]:
            items = [None]
        job["items"] = items
        state["jobs"].append(job)
    write_json(state_path, state)
    jobs = api.directory / "karaoke" / "jobs"
    recipe = read_json(jobs / video["id"] / "recipe.json")
    video_ids = [uuid4().hex for _ in range(5)]
    for id, status, attempts in zip(video_ids, ("running", "queued", "queued", "running", "queued"), (3, 0, 0, 2, 0)):
        job = jobs / id
        job.mkdir()
        write_json(job / "job.json", {k: v for k, v in video.items() if k in ("mode", "title", "createdAt")} |
                   {"id": id, "state": status, "attempts": attempts})
        if id == video_ids[2]:
            (job / "job.json").write_text("{", encoding="utf-8")
        elif id == video_ids[4]:
            (job / "recipe.json").write_text("{", encoding="utf-8")
        else:
            write_json(job / "recipe.json", recipe)
        os.utime(job / "job.json", (0, 0))
    api.file.unlink()
    api.start()
    api.initialize_mcp()
    return image_ids, video_ids, video["videoSha256"]


def assert_recovery(api, ids):
    image_ids, video_ids, hash_expected = ids

    def image_complete(id):
        result = api.get(f"/jobs/{id}")
        return result if result["status"] in ("done", "failed", "partial") else None

    image_results = [await_result(lambda: image_complete(id)) for id in image_ids]
    assert [(job["status"], job["items"][0]["attempts"]) for job in image_results] == [
        ("failed", 3), ("done", 1), ("done", 3), ("partial", 1), ("failed", 0)]
    assert image_results[3]["items"][1]["status"] == "done"
    assert "見つかりません" in image_results[3]["items"][0]["error"]
    assert "読み取れません" in image_results[4]["items"][0]["error"]
    assert "上限" in image_results[0]["items"][0]["error"]
    assert image_results[1]["items"][0]["finishedAt"] < image_results[0]["items"][0]["finishedAt"]

    def video_complete(id):
        result = api.mcp("get_karaoke_job", id=id)
        return result if result["state"] in ("done", "failed") else None

    video_results = [await_result(lambda: video_complete(id)) for id in video_ids]
    assert [(job["state"], job["attempts"]) for job in video_results] == [
        ("failed", 3), ("done", 1), ("failed", 0), ("done", 3), ("failed", 1)]
    assert video_results[1]["videoSha256"] == video_results[3]["videoSha256"] == hash_expected
    assert video_results[2] in api.mcp("list_karaoke_jobs")
    jobs = api.directory / "karaoke" / "jobs"
    assert (jobs / video_ids[1] / "job.json").stat().st_mtime_ns < (jobs / video_ids[0] / "job.json").stat().st_mtime_ns
    assert (jobs / video_ids[2] / "job.json").read_bytes() == b"{"
    print("PASS: both queues finish healthy work despite missing or unreadable state and exhausted attempts.", flush=True)
    return {"image": [job["status"] for job in image_results], "karaoke": [job["state"] for job in video_results],
            "videoReplayEqual": True, "queuedBeforeRecovered": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--photo", required=True, type=Path)
    parser.add_argument("--baseline-build", required=True)
    parser.add_argument("--matting", required=True, type=Path)
    parser.add_argument("--ffmpeg", required=True, type=Path)
    parser.add_argument("--font", required=True, type=Path)
    args = parser.parse_args()
    (ROOT / "artifacts").mkdir(exist_ok=True)
    folder = Path(mkdtemp(prefix="visual-base-review-", dir=ROOT / "artifacts"))
    print("Verification data:", folder, flush=True)
    jpeg = portrait_jpeg(args.photo, folder / "portrait-exif-6.jpg")
    settings = ["--Visual:EnableGpu=false", "--Visual:AutoInspect=off", "--Visual:MattingModel", str(args.matting.resolve()),
                "--Visual:KaraokeFFmpeg", str(args.ffmpeg.resolve()), "--Visual:KaraokeFont", str(args.font.resolve())]
    with patch.dict(os.environ, {"THEORIA_VISUAL_ARTIFACTS": args.baseline_build}):
        old = Server("api", folder, settings, "Debug")
    try:
        old.initialize_mcp()
        baseline = import_photo(old, "EXIF baseline.jpg", jpeg)
        assert (baseline["width"], baseline["height"]) == (270, 180)
        style, _ = run_image(old, baseline["reference"], {"operation": "stylize", "touch": "detail", "strength": 0})
        matte, _ = run_image(old, baseline["reference"], {"operation": "background", "background": "transparent"})
    finally:
        old.stop()
    old.file.unlink()
    now = Server("api", folder, settings, "Debug")
    try:
        now.initialize_mcp()
        normal = import_photo(now, "EXIF corrected.jpg", jpeg)
        result = {"exif": with_exif_fixture(now, jpeg, folder, normal, style, matte)}
        result["queue"] = assert_recovery(now, seed_recovery(now, folder, normal))
        write_json(folder / "verification.json", result)
    finally:
        if now.process.poll() is None:
            now.stop()


if __name__ == "__main__":
    main()
