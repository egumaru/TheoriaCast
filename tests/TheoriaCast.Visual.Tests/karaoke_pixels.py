"""Pixel assertions for frame boundaries using compact, independently timed fixtures."""

import json
from hashlib import sha256

from verify_karaoke import decode_frames, download, finish


def render(server, output, ffmpeg, audio, mode, style, cues, notes=()):
    settings = {"width": 640, "height": 360, "fontSize": 0.08, "lyricsTop": 0.70, **style}
    inp = {"audio": {"sha256": audio}, "title": "boundary fixture", "lyrics": cues, "notes": notes}
    job = finish(
        server, server.mcp("submit_karaoke", request={"mode": mode, "input": inp, "style": settings})["id"]
    )
    ext = "mov" if mode == "lyrics" else "mp4"
    target = output / f"{mode}-{settings['lyrics']}-{settings['fps']}.{ext}"
    download(server, job, target)
    replay = finish(server, server.mcp("replay_karaoke", id=job["id"])["id"])
    assert replay["videoSha256"] == job["videoSha256"], (mode, replay, job)
    return job, target


def line_mask(image, row):
    y = 252 if row == 0 else 297
    return image.getchannel("A").crop((0, y, 640, y + 40)).point(lambda a: 255 if a >= 180 else 0)


def horizontal_run(mask):
    longest = 0
    for y in range(mask.height):
        row = mask.crop((0, y, mask.width, y + 1)).tobytes()
        longest = max(longest, max(map(len, row.split(b"\0"))))
    return longest


def verify_edges(server, output, ffmpeg, audio):
    cues = [
        {"start": a, "end": b, "text": t}
        for a, b, t in [(0, 0.2, "I"), (0.3, 0.5, "WWWWWW"), (3.7, 4.1, "I I I"), (4.8, 5, "OOO")]
    ]
    evidence = {}
    for fps in (30, 60):
        # Times below are boundaries in tenths, chosen independently of the scene API.
        state = [(0, 0), (2, 1), (6, None), (36, 2), (42, None), (47, 3)]
        transitions = [0, 2, 3, 5, 6, 36, 37, 41, 42, 47, 48, 50]
        frames = sorted(
            {f for t in transitions for f in (t * fps // 10 - 1, t * fps // 10) if 0 <= f < 5 * fps}
        )
        for treatment in ("follow", "underline"):
            job, path = render(
                server,
                output,
                ffmpeg,
                audio,
                "lyrics",
                {"fps": fps, "lines": 2, "lyrics": treatment, "leadMs": 100, "holdMs": 100},
                cues,
            )
            images = decode_frames(ffmpeg, path, frames, 640, 360)
            signatures, widths = {}, {}
            observations = []
            for f, image in images.items():
                main = next(i for t, i in reversed(state) if f >= t * fps // 10)
                active = main is not None and round(cues[main]["start"] * fps) <= f < round(
                    cues[main]["end"] * fps
                )
                plan = server.mcp("get_karaoke_frame", id=job["id"], frame=f)
                indexes = [] if main is None else list(range(main, min(main + 2, len(cues))))
                assert [item["index"] for item in plan["lyrics"]] == indexes
                row_states = []
                for row in range(2):
                    idx = indexes[row] if row < len(indexes) else None
                    under = treatment == "underline" and active and row == 0
                    mask = line_mask(image, row)
                    bounds = mask.getbbox()
                    assert bool(bounds) == (idx is not None), (treatment, fps, f, row, bounds)
                    signature = sha256(mask.tobytes()).hexdigest()
                    key = (row, idx, under)
                    if key in signatures:
                        assert signatures[key] == signature, (treatment, fps, f, key)
                    else:
                        # Distinct text on the same row must actually change the glyph mask.
                        assert signature not in [
                            v for k, v in signatures.items() if k[0] == row and k != key
                        ]
                        signatures[key] = signature
                    if bounds:
                        widths[row, idx, under] = bounds[2] - bounds[0]
                    if row == 0 and idx == 2 and treatment == "underline":
                        # The gaps between the three I's remain empty on held/lead frames.
                        run = horizontal_run(mask)
                        assert (run > (bounds[2] - bounds[0]) * 0.70) == under, (fps, f, run, bounds, under)
                    row_states.append((idx, bounds, under))
                observations.append((f, row_states))
            for row in range(2):
                # The six W glyphs must be visibly wider than the spaced I's on either row.
                assert max(w for (r, i, u), w in widths.items() if r == row and i == 1) > 2 * max(
                    w for (r, i, u), w in widths.items() if r == row and i == 2
                )
            evidence[path.name] = observations

        for mode in ("bars", "score"):
            job, path = render(
                server,
                output,
                ffmpeg,
                audio,
                mode,
                {"fps": fps, "scroll": 1.5, "lyrics": "follow", "notes": "#00FF00"},
                cues,
                [{"start": 1, "duration": 0.2, "midi": 64}],
            )
            frames = [fps + i for i in (-2, -1, 1, 2)]
            images = decode_frames(ffmpeg, path, frames, 640, 360)
            measurements = []
            for f, pic in images.items():
                note = server.mcp("get_karaoke_frame", id=job["id"], frame=f)["notes"][0]
                expected = 218 + (1 - f / fps) * 960
                assert abs(note["x"] - expected) < 0.01
                # Measure the sole note's green onset edge or the staff note head centre.
                y = note["y"]
                xs = [
                    x
                    for yy in range(y - 2, y + 3)
                    for x in range(32, 608)
                    if green_note(pic.getpixel((x, yy)))
                ]
                assert xs, (mode, f, y)
                actual = min(xs) if mode == "bars" else (min(xs) + max(xs)) / 2
                tolerance = 960 / fps / 2
                assert abs(actual - expected) < tolerance, (mode, fps, f, expected, actual, tolerance)
                measurements.append((f, expected, actual))
            evidence[path.name] = measurements
    (output / "pixel-boundaries.json").write_text(json.dumps(evidence, indent=2), encoding="utf8")


def green_note(pixel):
    red, green, blue, _ = pixel
    return green > 125 and green > red + 65 and green > blue + 65
