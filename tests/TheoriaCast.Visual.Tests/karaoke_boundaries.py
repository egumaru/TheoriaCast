"""Decode a synthetic cue at 3.700 s on both sides of frame 111."""

import argparse
import json
import wave
from pathlib import Path
from tempfile import mkdtemp
from uuid import uuid4

from verify_karaoke import Server, decode_frames, download, finish, stage_audio


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--output", type=Path, required=True)
    cli.add_argument("--ffmpeg", required=True)
    cli.add_argument("--font", required=True)
    cli.add_argument("--baseline", action="store_true")
    cli.add_argument(
        "--legacy-reference", type=Path, help="boundary.json from a baseline run on the same host"
    )
    args = cli.parse_args()
    out = args.output
    out.mkdir(parents=True, exist_ok=True)
    audio = out / "silence.wav"
    with wave.open(str(audio), "wb") as wav:
        wav.setparams((1, 2, 8000, 0, "NONE", ""))
        wav.writeframes(bytes(8000 * 2 * 5))
    server = Server(
        "api",
        Path(mkdtemp(dir=out)),
        [
            "--Visual:EnableGpu=false",
            "--Visual:KaraokeFFmpeg",
            args.ffmpeg,
            "--Visual:KaraokeFont",
            args.font,
        ],
        "Debug",
    )
    try:
        server.initialize_mcp()
        staged = stage_audio(server, audio)
        if not args.baseline:
            # A bad request can stage chunks, but must not promote the assembled WAV.
            invalid = {
                "mode": "lyrics",
                "input": {
                    "title": "invalid",
                    "audio": staged,
                    "lyrics": [{"start": 0, "end": 6, "text": "X"}],
                },
            }
            server.post("/karaoke/jobs", invalid, 400)
            assert not (server.directory / "karaoke" / "audio" / (staged["sha256"] + ".wav")).exists()
            jobs = server.directory / "karaoke" / "jobs"
            corrupt = jobs / uuid4().hex
            corrupt.mkdir(parents=True)
            (corrupt / "job.json").write_text("{", encoding="utf8")
            assert server.get("/karaoke/jobs/" + corrupt.name)["state"] == "failed"
        inputs = {
            "audio": staged,
            "title": "frame-111",
            "lyrics": [{"start": 3.7, "end": 4.1, "text": "FRAME 111"}],
        }
        style = {
            "lyrics": "follow",
            "leadMs": 0,
            "holdMs": 0,
            "lines": 1,
            "width": 640,
            "height": 360,
            "fps": 30,
        }
        request = {"mode": "lyrics", "input": inputs, "style": style}
        (out / "cue.json").write_text(json.dumps(request, indent=2), encoding="utf8")
        job = finish(server, server.mcp("submit_karaoke", request=request)["id"])
        video = out / "frame-111.mov"
        download(server, job, video)
        images = decode_frames(args.ffmpeg, video, [110, 111, 112, 122, 123, 124], 640, 360)
        opaque = {
            str(f): sum(n for alpha, n in enumerate(im.getchannel("A").histogram()) if alpha >= 180)
            for f, im in images.items()
        }
        for i, image in images.items():
            image.save(out / f"frame-{i}.png")
        record = {
            "opaque_pixels": opaque,
            "first_visible": next((f for f in (110, 111, 112) if opaque[str(f)] > 15), None),
            "video_sha256": job["videoSha256"],
        }
        print(record, flush=True)
        (out / "boundary.json").write_text(json.dumps(record, indent=2), encoding="utf8")
        if not args.baseline:
            assert record["first_visible"] == 111 and opaque["123"] == 0 and opaque["122"] > 15
            from karaoke_pixels import verify_edges

            verify_edges(server, out, args.ffmpeg, inputs["audio"]["sha256"])
            # Check libass glyph fallback and temporary-file cleanup after both outcomes.
            bad = {
                "audio": staged,
                "title": "font fallback",
                "srt": "00:00:00,000 --> 00:00:01,000\n" + chr(0x10FFFF),
            }
            failed_id = server.mcp("submit_karaoke", request=dict(request, input=bad))["id"]
            assert server.mcp("get_karaoke_recipe", id=failed_id)["lyrics"][0]["text"] == chr(0x10FFFF)
            from verify_visual import await_result

            def failed():
                return server.get("/karaoke/jobs/" + failed_id)["state"] == "failed"

            await_result(failed)
            assert "フォント" in server.get("/karaoke/jobs/" + failed_id)["error"]
            await_result(lambda: not list(jobs.glob("*/fonts")) and not list(jobs.glob("*/render.tmp.*")))
            if args.legacy_reference:
                # Seed a v1 recipe in this disposable store, simulating an upgrade.
                recipe_file = jobs / job["id"] / "recipe.json"
                recipe = json.loads(recipe_file.read_bytes())
                recipe["engine"] = "theoriacast-karaoke-ass/1"
                recipe_file.write_text(json.dumps(recipe), encoding="utf8")
                replay = finish(server, server.mcp("replay_karaoke", id=job["id"])["id"])
                assert (
                    replay["videoSha256"] == json.loads(args.legacy_reference.read_bytes())["video_sha256"]
                )
            print(
                "PASS: pixel boundaries, replay, invalid imports, isolated job, font fallback and cleanup.",
                flush=True,
            )
    finally:
        server.stop()


if __name__ == "__main__":
    main()
