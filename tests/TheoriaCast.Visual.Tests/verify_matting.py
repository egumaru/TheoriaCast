"""Exercise the edge-refinement options with official 0.2.0 and 0.3.0 Visual API builds."""

import argparse
from base64 import b64encode
from hashlib import sha256
from io import BytesIO
import os
from pathlib import Path
import sys
import tempfile
from unittest.mock import patch

from PIL import Image, ImageOps

from verify_visual import CHUNK, ROOT, Server, atelier_check, executable, image, recipe_packet


BASELINE = "0.2.0"
CANDIDATE = "0.3.0"
PROVIDER = "AIKernel.Providers.Image"
DEFAULTS = {"maskRefineRadius": 0, "maskErodePx": 0, "decontaminateEdge": False}
REFINED = {"maskRefineRadius": 6, "maskErodePx": 1, "decontaminateEdge": True}


def import_fhd(server, photo):
    with Image.open(photo) as source:
        picture = ImageOps.contain(source.convert("RGB"), (1920, 1080), Image.Resampling.LANCZOS)
    frame = Image.new("RGB", (1920, 1080), "#25292a")
    frame.paste(picture, ((1920 - picture.width) // 2, (1080 - picture.height) // 2))
    encoded = BytesIO()
    frame.save(encoded, format="PNG")
    data = encoded.getvalue()
    chunks = [
        server.post("/assets/chunks", {"bytes": b64encode(data[start:start + CHUNK]).decode()})["sha256"]
        for start in range(0, len(data), CHUNK)
    ]
    return server.post("/assets/import", {
        "name": "FHD エッジ補正検証.png", "sha256": sha256(data).hexdigest(), "chunks": chunks,
    }, 201)["reference"]


def submit(server, reference, settings, status=202):
    return server.post("/jobs", {"settings": settings, "inputs": [reference], "hardware": "cpu"}, status)


def result(server, job, use_mcp=False):
    item = server.finish(job, via_mcp=use_mcp, seconds=180)["items"][0]
    data = image(server, item["output"])
    return data, recipe_packet(data)


def pixels(data):
    with Image.open(BytesIO(data)) as picture:
        assert picture.size == (1920, 1080)
        return picture.convert("RGBA").tobytes()


def verify(server, reference, old_data, old_recipe, old_stylization):
    server.initialize_mcp()
    job = server.mcp("replay_recipe", request={"recipeId": old_recipe["id"]})
    replay, frozen = result(server, job, use_mcp=True)
    assert replay == old_data and frozen == old_recipe
    pinned = server.get("/jobs/" + job["id"])["items"][0]["pinnedEngine"]
    assert pinned["libraries"][PROVIDER] == CANDIDATE
    style_data, style_recipe = old_stylization
    styled, frozen_style = result(server, server.mcp("replay_recipe", request={"recipeId": style_recipe["id"]}), use_mcp=True)
    assert styled == style_data and frozen_style == style_recipe
    print("PASS: an unchanged 0.2.0 background recipe replays byte-for-byte after the upgrade.", flush=True)
    print("PASS: a 0.2.0 illustration recipe also replays byte-for-byte.", flush=True)

    background = {"operation": "background", "background": "transparent"}
    zero, recipe = result(server, submit(server, reference, background))
    assert recipe["engine"]["libraries"][PROVIDER] == CANDIDATE
    assert all(recipe["settings"][key] == value for key, value in DEFAULTS.items())
    assert pixels(zero) == pixels(old_data)
    refined, applied = result(server, submit(server, reference, {**background, **REFINED}))
    assert all(applied["settings"][key] == value for key, value in REFINED.items())
    assert pixels(refined) != pixels(old_data)
    repeated, frozen = result(server, server.post("/recipes/replay", {"recipeId": applied["id"]}, 202))
    assert repeated == refined and frozen == applied

    for parameter, value in REFINED.items():
        change = {"parameter": parameter, "value": value}
        job = server.mcp("replay_recipe", request={"recipeId": recipe["id"], "change": change})
        _, derived = result(server, job, use_mcp=True)
        assert derived["parentId"] == recipe["id"] and derived["change"] == change
        assert derived["settings"] == {**recipe["settings"], parameter: value}

    change = {"parameter": "maskRefineRadius", "value": REFINED["maskRefineRadius"]}
    _, migrated = result(server, server.post("/recipes/replay", {"recipeId": old_recipe["id"], "change": change}, 202))
    assert migrated["parentId"] == old_recipe["id"] and migrated["change"] == change
    assert migrated["engine"]["libraries"][PROVIDER] == CANDIDATE
    assert migrated["settings"] == {**old_recipe["settings"], **DEFAULTS, "maskRefineRadius": change["value"]}
    assert server.get("/recipes/" + old_recipe["id"]) == old_recipe

    for invalid in ({"maskRefineRadius": -1}, {"maskRefineRadius": 33}, {"maskErodePx": 3}):
        submit(server, reference, {**background, **invalid}, 400)
    submit(server, reference, {"operation": "resize", "width": 320, "height": 240, **REFINED}, 400)
    (server.directory.parent / "visual-matte-default.png").write_bytes(zero)
    (server.directory.parent / "visual-matte-refined.png").write_bytes(refined)
    print("PASS: all edge options are recorded, independently replayable, and limited to background editing.", flush=True)


def browser_matting(server, base, folder, browser_libs):
    sys.path.insert(0, str(browser_libs))
    from playwright.sync_api import expect, sync_playwright

    with sync_playwright() as pw:
        browser = pw.chromium.launch(channel="msedge", headless=True)
        page = browser.new_page(viewport={"width": 1280, "height": 900})
        errors = []
        page.on("pageerror", lambda error: errors.append(error.message))
        page.goto(base)
        asset = page.locator("#asset-grid .asset").filter(has_text="FHD エッジ補正検証.png")
        asset.get_by_role("checkbox").check()
        page.locator("#edit-form [name=operation]").select_option("background")
        edges = page.get_by_role("group", name="背景のエッジ補正")
        expect(edges).to_be_visible()
        edges.get_by_label("輪郭に合わせる半径（px）").fill("6")
        edges.get_by_label("マスクを内側へ縮める幅（px）").fill("1")
        edges.get_by_label("縁に残る背景色の除去").select_option("true")
        page.screenshot(path=str(folder / "atelier-matte-controls.png"))
        page.get_by_role("button", name="ジョブ登録").click()
        expect(page.get_by_text("一括編集を登録しました。")).to_be_visible()
        initial_job = server.get("/jobs")[0]
        _, original = result(server, initial_job)
        assert all(original["settings"][key] == value for key, value in REFINED.items())

        latest = page.locator("#job-list details").first
        latest.locator("summary").click()
        latest.get_by_role("button", name="結果 1 のレシピ").click()
        dialog = page.get_by_role("dialog")
        dialog.get_by_label("変更する項目").select_option("decontaminateEdge")
        dialog.locator("select[name=value]").select_option("false")
        dialog.get_by_role("button", name="1項目だけ変えて再実行").click()
        expect(page.get_by_text("再実行を登録しました。")).to_be_visible()
        _, updated = result(server, server.get("/jobs")[0])
        assert updated["parentId"] == original["id"]
        assert updated["settings"] == {**original["settings"], "decontaminateEdge": False}
        assert not errors, errors
        browser.close()
    print("PASS Atelier: submitted all three options and replayed a single toggle from the recipe screen.", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("photo", type=Path)
    parser.add_argument("--matting", required=True, type=Path)
    parser.add_argument("--baseline-artifacts", required=True, type=Path)
    parser.add_argument("--updated-artifacts", required=True, type=Path)
    parser.add_argument("--browser-libs", type=Path)
    parser.add_argument("--configuration", default="Debug")
    args = parser.parse_args()
    artifacts = ROOT / "artifacts"
    artifacts.mkdir(exist_ok=True)
    folder = Path(tempfile.mkdtemp(prefix="visual-matte-verification-", dir=artifacts))
    print("Verification data:", folder, flush=True)
    settings = ["--Visual:EnableGpu=false", "--Visual:MattingModel", str(args.matting.resolve())]
    project = "TheoriaCast.Visual.Api"
    with patch.dict(os.environ, {"THEORIA_VISUAL_ARTIFACTS": str(args.baseline_artifacts.resolve())}):
        server = Server("cpu", folder, settings, args.configuration)
    try:
        reference = import_fhd(server, args.photo)
        old_data, old_recipe = result(server, submit(server, reference, {"operation": "background"}))
        assert old_recipe["engine"]["libraries"][PROVIDER] == BASELINE
        assert all(key not in old_recipe["settings"] for key in DEFAULTS)
        old_stylization = result(server, submit(server, reference, {
            "operation": "stylize", "touch": "pencil", "strength": .5,
        }))
        assert old_stylization[1]["engine"]["libraries"][PROVIDER] == BASELINE
        server.stop()
        server.file.unlink()
        with patch.dict(os.environ, {"THEORIA_VISUAL_ARTIFACTS": str(args.updated_artifacts.resolve())}):
            server.command[1] = str(executable(project, args.configuration))
        server.start()
        verify(server, reference, old_data, old_recipe, old_stylization)
        if args.browser_libs:
            with patch.dict(os.environ, {"THEORIA_VISUAL_ARTIFACTS": str(args.updated_artifacts.resolve())}):
                atelier_check(server, folder, args.configuration, None, args.browser_libs,
                              lambda base, directory, libs: browser_matting(server, base, directory, libs),
                              basic_browser=False)
    finally:
        if server.process.poll() is None:
            server.stop()


if __name__ == "__main__":
    main()
