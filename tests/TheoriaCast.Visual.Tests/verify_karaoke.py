"""Generate complete Visual karaoke videos from existing Music exports through MCP."""

import argparse
from base64 import b64decode, b64encode
from hashlib import sha256
import json
import math
from pathlib import Path
import subprocess
from tempfile import mkdtemp

from PIL import Image

from verify_visual import CHUNK, ROOT, Server, await_result


MODES = {"bars": "karaoke-bars.mp4", "score": "scrolling-score.mp4", "lyrics": "lyrics-overlay.mov"}


def stage_audio(server, path):
    digest = sha256()
    chunks = []
    with path.open("rb") as source:
        while chunk := source.read(CHUNK):
            digest.update(chunk)
            result = server.mcp("stage_karaoke_audio", chunk={"bytes": b64encode(chunk).decode("ascii")})
            assert result["sha256"] == sha256(chunk).hexdigest()
            chunks.append(result["sha256"])
    return {"sha256": digest.hexdigest(), "chunks": chunks}


def finish(server, id):
    def completed():
        job = server.mcp("get_karaoke_job", id=id)
        return job if job["state"] in {"done", "failed"} else None

    result = await_result(completed, 1800)
    assert result["state"] == "done", result
    return result


def download(server, receipt, path):
    digest = sha256()
    with path.open("wb") as target:
        while target.tell() < receipt["videoBytes"]:
            offset = target.tell()
            part = server.mcp("read_karaoke_video", id=receipt["id"], offset=offset, count=CHUNK)
            assert part["offset"] == offset and part["totalBytes"] == receipt["videoBytes"]
            assert part["sha256"] == receipt["videoSha256"]
            chunk = b64decode(part["bytes"], validate=True)
            assert chunk
            target.write(chunk)
            digest.update(chunk)
    assert digest.hexdigest() == receipt["videoSha256"]


def decode_frames(ffmpeg, path, frames, width, height):
    expression = "+".join(f"eq(n\\,{index})" for index in frames)
    raw = subprocess.check_output([ffmpeg, "-v", "error", "-i", str(path), "-vf", f"select={expression}",
        "-fps_mode", "passthrough", "-pix_fmt", "rgba", "-f", "rawvideo", "-"], timeout=180)
    size = width * height * 4
    assert len(raw) == size * len(frames)
    return {index: Image.frombytes("RGBA", (width, height), raw[i * size:(i + 1) * size])
            for i, index in enumerate(frames)}


def audio_hash(ffmpeg, path):
    result = subprocess.check_output([ffmpeg, "-v", "error", "-i", str(path), "-map", "0:a:0",
        "-c:a", "pcm_s16le", "-f", "hash", "-hash", "sha256", "-"], timeout=90)
    return result.decode().strip()


def rgb(value):
    return tuple(bytes.fromhex(value[1:]))


def color_hits(picture, box, expected, tolerance):
    def close(pixel):
        return pixel[3] >= 180 and all(abs(got - want) <= tolerance for got, want in zip(pixel, expected))
    return sum(close(pixel) for pixel in picture.crop(box).get_flattened_data())


def inspect_frames(server, ffmpeg, job, path, recipe, folder):
    width, height, fps = (recipe["style"][key] for key in ("width", "height", "fps"))
    cues, notes = recipe["lyrics"], recipe["notes"]
    note_index = next(index for index, note in enumerate(notes) if note["start"] > 2
                      and note["duration"] >= 2 / fps
                      and any(cue["start"] <= note["start"] < cue["end"] for cue in cues))
    onset = math.ceil(notes[note_index]["start"] * fps)
    cue = next(cue for cue in cues if cue["start"] > 2 and cue["end"] - cue["start"] > .2)
    frames = sorted({onset, onset + 1, math.ceil(cue["start"] * fps), math.ceil(cue["end"] * fps) - 1,
                     min(recipe["frames"] - 1, math.ceil(cue["end"] * fps))})
    pictures = decode_frames(ffmpeg, path, frames, width, height)
    report = []
    mode = recipe["mode"]
    style = recipe["style"]
    head = math.floor(width * style["head"] + .5)
    for index, picture in pictures.items():
        scene = server.mcp("get_karaoke_frame", id=job["id"], frame=index)
        time = index / fps
        assert abs(scene["seconds"] - time) < 1e-8
        expected_line = next((i for i, cue in enumerate(cues) if cue["start"] <= time < cue["end"]), None)
        active = [line for line in scene["lyrics"] if line["active"]]
        assert [line["index"] for line in active] == ([] if expected_line is None else [expected_line])
        if active:
            assert active[0]["text"] == cues[expected_line]["text"]
            assert active[0]["underline"] == (style["lyrics"] == "underline")
            top = math.floor(height * style["lyricsTop"])
            color = rgb(active[0]["color"])
            line_height = math.ceil(height * style["fontSize"] * 1.25)
            hits = color_hits(picture, (0, top - 3, width, top + line_height), color, 36)
            assert hits >= 16, (mode, index, color, hits)
        if mode == "lyrics":
            assert scene["playheadX"] is None and picture.getpixel((0, 0))[3] == 0
        else:
            assert scene["playheadX"] == head
            y = math.floor(height * .18)
            assert color_hits(picture, (head - 2, y, head + 2, y + 10), rgb(style["playhead"]), 52) >= 12
            if index in (onset, onset + 1):
                note = next(n for n in scene["notes"] if n["index"] == note_index)
                assert note["midi"] == notes[note_index]["midi"] and note["active"]
                assert abs(note["x"] - (head + (notes[note_index]["start"] - time) * width * style["scroll"])) < .01
                x = round(note["x"])
                assert color_hits(picture, (x - 10, note["y"] - 14, x + 16, note["y"] + 14),
                                  rgb(style["notes"]), 45) >= 8, (mode, index, x, note["y"])
        screenshot = folder / f"{mode}-frame-{index:05d}.png"
        picture.save(screenshot)
        report.append({"frame": index, "seconds": time, "activeCueIndex": expected_line, "playheadX": scene["playheadX"],
                       "onsetNote": next((n for n in scene["notes"] if n["index"] == note_index), None),
                       "lyrics": scene["lyrics"], "image": screenshot.name})
    return frames, pictures, report


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--track", required=True, type=Path)
    cli.add_argument("--guide", required=True, type=Path)
    cli.add_argument("--wav", required=True, type=Path)
    cli.add_argument("--ffmpeg", required=True, type=Path)
    cli.add_argument("--font", required=True, type=Path)
    cli.add_argument("--audio-track-id")
    args = cli.parse_args()
    track = json.loads(args.track.read_bytes())
    guide = json.loads(args.guide.read_bytes())["recipe"]
    lyrics = track["provenance"]["cues"]
    artifacts = ROOT / "artifacts"
    artifacts.mkdir(exist_ok=True)
    folder = Path(mkdtemp(prefix="karaoke-acceptance-", dir=artifacts))
    print("Verification data:", folder, flush=True)
    settings = ["--Visual:EnableGpu=false", "--Visual:KaraokeFFmpeg", str(args.ffmpeg.resolve()),
                "--Visual:KaraokeFont", str(args.font.resolve())]
    server = Server("api", folder, settings, "Debug")
    evidence = []
    try:
        server.initialize_mcp()
        wav = stage_audio(server, args.wav)
        authored = {"title": track["title"], "trackId": args.audio_track_id or track["id"],
                    "audio": wav, "lyrics": lyrics, "notes": guide["notes"]}
        jobs = {}
        for mode in MODES:
            jobs[mode] = server.mcp("submit_karaoke", request={"mode": mode, "input": authored})
            authored["audio"] = {"sha256": wav["sha256"]}
        print("Queued all three modes via Visual MCP.", flush=True)
        for mode, name in MODES.items():
            job = finish(server, jobs[mode]["id"])
            recipe = server.mcp("get_karaoke_recipe", id=job["id"])
            assert recipe["audioSha256"] == wav["sha256"] and recipe["notes"] == guide["notes"]
            assert recipe["lyrics"] == lyrics
            assert [recipe["style"][key] for key in ("width", "height", "fps")] == [1920, 1080, 30]
            print(mode, "rendered", job["videoBytes"], "bytes", flush=True)
            path = folder / name
            download(server, job, path)
            frames, pictures, report = inspect_frames(server, str(args.ffmpeg), job, path, recipe, folder)
            replay = finish(server, server.mcp("replay_karaoke", id=job["id"])["id"])
            assert replay["parentId"] == job["id"]
            method = "SHA-256 of the complete video file"
            if replay["videoSha256"] != job["videoSha256"]:
                copy = folder / ("replay-" + name)
                download(server, replay, copy)
                replay_frames = decode_frames(str(args.ffmpeg), copy, frames, 1920, 1080)
                assert all(pictures[i].tobytes() == replay_frames[i].tobytes() for i in frames)
                assert audio_hash(str(args.ffmpeg), path) == audio_hash(str(args.ffmpeg), copy)
                method = "exact RGBA samples and decoded audio SHA-256"
            evidence.append({"mode": mode, "video": name, "sha256": job["videoSha256"],
                             "replaySha256": replay["videoSha256"], "determinism": method, "frames": report})
            (folder / (mode + "-recipe.json")).write_text(json.dumps(recipe, ensure_ascii=False, indent=2), encoding="utf-8")
            (folder / "verification.json").write_text(json.dumps(evidence, ensure_ascii=False, indent=2), encoding="utf-8")
            print("PASS", mode, method, flush=True)
    finally:
        server.stop()


if __name__ == "__main__":
    main()
