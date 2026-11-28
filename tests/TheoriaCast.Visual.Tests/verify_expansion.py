"""Check Visual resize variants, contact sheets, color matching, inspection and MV export."""

import argparse
from base64 import b64encode
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import sys
import tempfile
from zipfile import ZipFile

from PIL import Image, ImageChops, ImageDraw, ImageStat

from verify_visual import ROOT, PREFIX, Server, atelier_check, cpu_checks, image, recipe_packet, request


def imported(server, name, picture):
    encoded = BytesIO()
    picture.save(encoded, format="PNG")
    data = encoded.getvalue()
    return server.post("/assets/import", {
        "name": name, "sha256": sha256(data).hexdigest(), "bytes": b64encode(data).decode(),
    }, 201)["reference"]


def finished(server, settings, inputs=None, set_id=None):
    body = {"settings": settings, "hardware": "cpu", **({"setId": set_id} if set_id else {"inputs": inputs})}
    return server.finish(server.post("/jobs", body, 202))


def unpack(server, item):
    data = image(server, item["output"])
    return Image.open(BytesIO(data)).convert("RGBA"), recipe_packet(data)


def fixture(server):
    first = Image.new("RGBA", (120, 80), (195, 88, 48, 255))
    draw = ImageDraw.Draw(first)
    draw.rectangle((0, 0, 20, 20), fill="red")
    draw.rectangle((99, 0, 119, 20), fill="green")
    draw.rectangle((0, 59, 20, 79), fill="blue")
    draw.rectangle((99, 59, 119, 79), fill="yellow")
    second = first.point([min(255, i + 15) for i in range(256)] + [i for i in range(256)] +
                         [max(0, i - 32) for i in range(256)] + [i for i in range(256)])
    a = imported(server, "余白試験.png", first)
    b = imported(server, "色合わせ試験.png", second)
    set_id = server.post("/sets", {"name": "横・縦の元セット", "assets": [a, b]}, 201)["id"]
    return [a, b], set_id


def resize_checks(server, refs, set_id):
    pad = {"operation": "resize", "fitMode": "pad", "width": 1920, "height": 1080, "padStyle": "solid",
           "padColor": "#F5F2EB", "safeAreaMargin": {"left": .15, "top": .05, "right": .10, "bottom": .20}}
    landscape = finished(server, pad, set_id=set_id)
    horizontal, recipe = unpack(server, landscape["items"][0])
    settings = recipe["settings"]
    assert recipe["targetAspect"] == "16:9" and recipe["targetResolution"] == {"width": 1920, "height": 1080}
    assert settings["fitMode"] == "pad" and settings["padColor"] == "#f5f2eb"
    vertical = server.finish(server.mcp("replay_recipe", request={"recipeId": recipe["id"], "setId": set_id,
        "change": {"parameter": "targetResolution", "value": {"width": 1080, "height": 1920}}}), via_mcp=True)
    portrait, changed = unpack(server, vertical["items"][0])
    assert len(vertical["items"]) == len(refs) and changed["parentId"] == recipe["id"]
    assert changed["targetAspect"] == "9:16" and changed["settings"]["marginOffsets"] == settings["marginOffsets"]
    # 120x80 source contained in the asymmetric safe areas of the two canvases.
    expected_rectangles = [(400, 54, 1615, 864), (162, 546, 972, 1086)]
    corners = [(255, 0, 0), (0, 128, 0), (0, 0, 255), (255, 255, 0)]
    for edited, bounds in zip((horizontal, portrait), expected_rectangles):
        rgb = edited.convert("RGB")
        background = Image.new("RGB", rgb.size, (245, 242, 235))
        assert ImageChops.difference(rgb, background).getbbox() == bounds
        left, top, right, bottom = bounds
        positions = [(left + 5, top + 5), (right - 6, top + 5),
                     (left + 5, bottom - 6), (right - 6, bottom - 6)]
        assert [rgb.getpixel(p) for p in positions] == corners
    for style in ("edge", "blur"):
        result = finished(server, {"operation": "resize", "fitMode": "pad", "width": 128, "height": 256,
                                    "padStyle": style}, inputs=refs[:1])
        rendered, recipe = unpack(server, result["items"][0])
        assert recipe["settings"]["padStyle"] == style and rendered.getpixel((0, 0))[3] == 255
    print("PASS 1-A: FHD/vertical pad bounds, all four margins and source corner positions. All three fills.", flush=True)


def sweep_checks(server, refs, set_id):
    body = {"setId": set_id, "representatives": refs, "hardware": "cpu",
            "touches": ["illustration", "pencil", "detail"], "strengths": [.3, .9]}
    sweep = server.finish(server.mcp("preview_sweep", request=body), via_mcp=True)
    assert sweep["kind"] == "previewSweep" and len(sweep["items"]) == 12
    candidate = server.mcp("get_recipe", id=sweep["items"][2]["recipeId"])
    result = server.finish(server.mcp("replay_recipe", request={"recipeId": candidate["id"], "setId": set_id}), via_mcp=True)
    assert result["parentJobId"] == sweep["id"] and result["sourceSetId"] == set_id
    assert [unpack(server, item)[1]["input"] for item in result["items"]] == refs
    for item in result["items"]:
        _, recipe = unpack(server, item)
        assert recipe["parentId"] == candidate["id"] and recipe["settings"] == candidate["settings"]
    print("PASS 2-A: MCP sweep completed a 2 × 3 × 2 grid and applied one candidate to the entire set.", flush=True)
    return [item["output"] for item in result["items"]]


def color_checks(server, photo):
    reference = Image.open(BytesIO(photo)).convert("RGB").resize((256, 256))
    weaker = reference.point([min(255, i // 2 + 45) for i in range(256)] + [min(255, i // 2 + 20) for i in range(256)] +
                             [min(255, i // 2 + 75) for i in range(256)])
    a = imported(server, "自然光.png", reference)
    b = imported(server, "色かぶり.png", weaker)
    set_id = server.post("/sets", {"name": "色合わせの検証", "assets": [a, b]}, 201)["id"]
    job = finished(server, {"operation": "colorMatch", "referenceAsset": a}, set_id=set_id)
    matched, recipe = unpack(server, job["items"][1])
    assert recipe["settings"]["referenceAsset"] == a and recipe["settings"]["colorMatchStrength"] == 1
    expected = ImageStat.Stat(reference).mean

    def distance(picture):
        return sum(abs(x - y) for x, y in zip(expected, ImageStat.Stat(picture.convert("RGB")).mean))

    variation = server.finish(server.post("/recipes/replay", {"recipeId": recipe["id"],
        "change": {"parameter": "colorMatchStrength", "value": .5}}, 202))
    mixed, changed = unpack(server, variation["items"][0])
    identity = server.finish(server.post("/recipes/replay", {"recipeId": recipe["id"],
        "change": {"parameter": "colorMatchStrength", "value": 0}}, 202))
    original, _ = unpack(server, identity["items"][0])
    assert distance(matched) < distance(mixed) < distance(weaker)
    assert original.convert("RGB").tobytes() == weaker.tobytes()
    assert changed["parentId"] == recipe["id"] and changed["settings"]["referenceAsset"] == a
    weaker.save(server.directory.parent / "color-before.png")
    matched.save(server.directory.parent / "color-after.png")
    print(f"PASS 3-A: RGB mean distance {distance(weaker):.1f} → {distance(mixed):.1f} → {distance(matched):.1f}; strength 0 is identity.", flush=True)


def inspection_checks(server, model=None):
    transparent = imported(server, "透明検品.png", Image.new("RGBA", (80, 100)))
    white = imported(server, "白の検品.png", Image.new("RGB", (100, 80), "white"))
    set_id = server.post("/sets", {"name": "検品中も承認は保持", "assets": [transparent, white]}, 201)["id"]
    before = server.get("/sets/" + set_id)
    count = len(server.get("/assets"))
    result = finished(server, {"operation": "inspect"}, set_id=set_id)
    assert server.get("/sets/" + set_id) == before and len(server.get("/assets")) == count
    assert result["items"][0]["inspection"]["flags"][0]["code"] == "matte-missing"
    assert result["items"][1]["inspection"]["flags"][0]["code"] == "highlight-clipping"
    assert all(item["inspection"]["flags"][0]["reason"] for item in result["items"])
    found = server.mcp("search_assets", request={"query": "横長 明るい"})
    assert white in [asset["reference"] for asset in found] and transparent not in [asset["reference"] for asset in found]
    assert white in [asset["reference"] for asset in server.post("/assets/search", {"query": "横長 明るい"})]
    print("PASS 3-B: asynchronous inspection reports missing subjects and clipped highlights, with searchable Japanese tags and unchanged decisions.", flush=True)

    cutout = Image.new("RGBA", (128, 128))
    ImageDraw.Draw(cutout).rectangle((28, 24, 104, 104), fill=(90, 120, 145, 255))
    clean = cutout.copy()
    clean.putpixel((1, 1), (90, 120, 145, 255))
    clean.putpixel((60, 60), (0, 0, 0, 0))
    drawn = ImageDraw.Draw(cutout)
    drawn.rectangle((52, 48, 76, 72), fill=(0, 0, 0, 0))
    drawn.rectangle((0, 65, 12, 92), fill=(90, 120, 145, 255))
    suspect = imported(server, "透過マスクの検品.png", cutout)
    control = imported(server, "透過マスクの正常例.png", clean)
    reviewed = server.post("/sets", {"name": "透過検品後も承認を保持", "assets": [suspect, control]}, 201)
    review_path = "/sets/" + reviewed["id"]
    for ref in (suspect, control):
        reviewed = server.post(review_path + "/review", {
            "asset": ref, "decision": "accepted", "version": reviewed["version"],
        })
    approved = server.post(review_path + "/approve", {"version": reviewed["version"]})
    result = finished(server, {"operation": "inspect"}, set_id=approved["id"])
    flags = result["items"][0]["inspection"]["flags"]
    assert {flag["code"] for flag in flags} == {"matte-hole", "matte-border-residue"} and all(flag["reason"] for flag in flags)
    assert result["items"][1]["inspection"]["flags"] == []
    assert server.get(review_path) == approved
    recipe_id = result["items"][0]["recipeId"]
    recipe = server.mcp("get_recipe", id=recipe_id)
    assert recipe["engine"]["version"] == "2" and "matteHoles" in recipe["engine"]["fixedParameters"]
    replay = server.finish(server.mcp("replay_recipe", request={"recipeId": recipe_id}), via_mcp=True)
    assert replay["items"][0]["inspection"]["flags"] == flags

    source = imported(server, "検品比較用.png", Image.new("RGB", (100, 80), (90, 120, 145)))
    edit = finished(server, {"operation": "resize", "width": 96, "height": 72}, inputs=[source])
    processed = edit["items"][0]["output"]
    comparison = finished(server, {"operation": "inspect"}, inputs=[processed])["items"][0]["inspection"]
    assert comparison["sourceAsset"] == source and comparison["asset"] == processed
    print("PASS 3-B: enclosed holes and detached border regions get advisory reasons, noise is ignored, and the original asset is traceable.", flush=True)
    if model:
        real = next(asset["reference"] for asset in server.get("/assets") if asset["name"] == "確認用.png")
        job = server.post("/jobs", {"settings": {"operation": "inspect", "inspectionMode": "cosmos"},
            "inputs": [real]}, 202)
        reservation = server.get("/recipes/" + job["items"][0]["recipeId"])
        item = server.finish(job, seconds=600)["items"][0]
        assert item["pinnedEngine"] == reservation["engine"]
        assert item["inspection"]["providerTags"] and item["inspection"]["modelLicense"]["license"] == "OpenMDW-1.1"
        print("PASS 3-B Cosmos: reviewed checkpoint ran through the public Provider and returned tags; first claim recorded engine.", flush=True)


def manifest_checks(server, refs, with_matting):
    if with_matting:
        removed = finished(server, {"operation": "background"}, inputs=refs[:1])
        refs = [refs[0], removed["items"][0]["output"]]
    set_id = server.post("/sets", {"name": "MV 検証", "assets": refs}, 201)["id"]
    server.get("/sets/" + set_id + "/manifest", 409)
    state = server.get("/sets/" + set_id)
    for ref in refs:
        state = server.post("/sets/" + set_id + "/review", {"asset": ref, "decision": "accepted", "version": state["version"]})
    cuts = [{"asset": refs[1], "seconds": 2.75, "note": "歌い出し", "lyricLine": 3},
            {"asset": refs[0], "seconds": 4, "note": "間奏"}]
    approved = server.mcp("approve_set", id=set_id, request={"version": state["version"], "cuts": cuts})
    manifest = server.mcp("get_set_manifest", id=set_id)
    assert manifest == server.get("/sets/" + set_id + "/manifest") and manifest["version"] == approved["version"]
    data = request(server.base, PREFIX + "/sets/" + set_id + "/export", token=server.token)[0]
    with ZipFile(BytesIO(data)) as archive:
        assert json.loads(archive.read("visual-set-manifest.json")) == manifest
        assert [cut["asset"] for cut in manifest["cuts"]] == refs[::-1]
        assert manifest["cuts"][0]["lyricLine"] == 3 and manifest["cuts"][0]["note"] == "歌い出し"
        for position, cut in enumerate(manifest["cuts"], 1):
            assert cut["order"] == position and sha256(archive.read(cut["path"])).hexdigest() == cut["asset"]["sha256"]
            assert len(cut["recipes"]) >= 2 and cut["recipes"][-1] == recipe_packet(archive.read(cut["path"]))
        if with_matting:
            assert manifest["cuts"][0]["modelLicenses"][0]["license"] == "MIT"
    (server.directory.parent / "visual-mv-verification.zip").write_bytes(data)
    print("PASS 3-C: MCP and ZIP restore frozen cut order, notes, duration, lyric line, SHA-256, full recipes and model credits.", flush=True)
    return set_id, manifest


def browser_expansion(base, folder, browser_libs):
    sys.path.insert(0, str(browser_libs))
    from playwright.sync_api import expect, sync_playwright

    with sync_playwright() as pw:
        browser = pw.chromium.launch(channel="msedge", headless=True)
        page = browser.new_page()
        errors = []
        page.on("pageerror", lambda error: errors.append(error.message))
        page.goto(base)
        page.get_by_label("素材を検索").fill("横長 明るい")
        page.get_by_role("button", name="検索", exact=True).click()
        expect(page.get_by_text("白の検品.png")).to_be_visible()
        page.locator("#asset-grid .asset").filter(has_text="白の検品.png").get_by_role("button", name="要確認 1 件").click()
        expect(page.get_by_role("dialog")).to_contain_text("白飛び")
        page.get_by_role("button", name="閉じる").click()
        page.get_by_label("素材を検索").fill("検品比較用-resize.png")
        page.get_by_role("button", name="検索", exact=True).click()
        expect(page.get_by_label("検品比較用-resize.png")).to_be_visible()
        expect(page.locator("#files")).to_be_enabled()
        result = page.locator("#asset-grid .asset").filter(has_text="検品比較用-resize.png")
        result.get_by_role("button", name="検品結果", exact=True).click()
        dialog = page.get_by_role("dialog")
        for label in ("元画像", "検品対象"):
            preview = dialog.get_by_role("img", name=label, exact=True)
            expect(preview).to_be_visible()
            expect(preview).to_have_js_property("complete", True)
            assert preview.evaluate("(image) => image.naturalWidth > 0")
        page.screenshot(path=str(folder / "atelier-inspection-comparison.png"))
        dialog.get_by_role("button", name="閉じる").click()
        page.get_by_role("button", name="素材セット", exact=True).click()
        expect(page.locator("#files")).to_be_enabled()
        page.locator(".set-row").filter(has_text="横・縦の元セット").get_by_role("button", name="確認").click()
        page.get_by_role("button", name="タッチと強さを比較").click()
        dialog = page.get_by_role("dialog")
        dialog.get_by_role("checkbox").first.check()
        dialog.get_by_label("計算資源").select_option("cpu")
        dialog.get_by_role("button", name="比較ジョブを登録").click()
        first_job = page.locator("#job-list details").first
        expect(first_job.locator("summary")).to_contain_text("完了", timeout=90000)
        first_job.locator("summary").click()
        first_job.get_by_role("button", name="コンタクトシート").click()
        expect(dialog.locator(".asset")).to_have_count(10)
        page.screenshot(path=str(folder / "atelier-contact-sheet.png"))
        dialog.get_by_role("button", name="このレシピでセット全体を一括登録").first.click()
        expect(page.get_by_text("選んだレシピでセット全体を登録しました。")).to_be_visible()
        expect(page.locator("#job-list details").first.locator("summary")).to_contain_text("2/2", timeout=60000)
        page.get_by_role("button", name="素材セット", exact=True).click()
        expect(page.locator("#files")).to_be_enabled()
        page.locator(".set-row").filter(has_text="MV 検証").get_by_role("button", name="確認").click()
        expect(page.locator("#set-detail h2")).to_have_text("MV 検証")
        with page.expect_download() as event:
            page.get_by_role("link", name="画像とマニフェストを保存").click()
        assert event.value.suggested_filename.endswith(".zip")
        assert not errors, errors
        browser.close()
    print("PASS Atelier: searched inspection tags, read flags, ran contact sheet → set batch and downloaded an approved ZIP.", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("photo", type=Path)
    parser.add_argument("--matting", type=Path)
    parser.add_argument("--browser-libs", type=Path)
    parser.add_argument("--cosmos-model", type=Path)
    parser.add_argument("--cosmos-revision")
    parser.add_argument("--cosmos-sha256")
    parser.add_argument("--configuration", default="Debug")
    args = parser.parse_args()
    artifacts = ROOT / "artifacts"
    artifacts.mkdir(exist_ok=True)
    folder = Path(tempfile.mkdtemp(prefix="visual-expansion-qa-", dir=artifacts))
    photo = args.photo.read_bytes()
    config = ["--Visual:EnableGpu=false", "--Visual:AutoInspect=off"]
    if args.matting:
        config += ["--Visual:MattingModel", str(args.matting)]
    if args.cosmos_model:
        config += ["--Visual:CosmosModel", str(args.cosmos_model), "--Visual:CosmosRevision", args.cosmos_revision,
                   "--Visual:CosmosManifestSha256", args.cosmos_sha256]
    print("Verification data:", folder, flush=True)
    server = Server("cpu", folder, config, args.configuration)
    try:
        cpu_checks(server, photo, args.matting)
        refs, set_id = fixture(server)
        resize_checks(server, refs, set_id)
        swept = sweep_checks(server, refs, set_id)
        color_checks(server, photo)
        inspection_checks(server, args.cosmos_model)
        approved, saved = manifest_checks(server, swept, args.matting)
        atelier_check(server, folder, args.configuration, photo, args.browser_libs, browser_expansion)
        server.stop()
        server.file.unlink()
        server.start()
        assert server.get("/sets/" + approved + "/manifest") == saved
        assert server.token.encode() not in server.log.read_bytes()
        print("PASS: frozen manifest persists across restart.", flush=True)
    finally:
        if server.process.poll() is None:
            server.stop()


if __name__ == "__main__":
    main()
