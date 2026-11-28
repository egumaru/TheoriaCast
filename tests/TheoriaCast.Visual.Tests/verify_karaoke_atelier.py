"""Exercise SRT import, recipe download and replay in the Visual Atelier."""

import argparse
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import sys
from tempfile import mkdtemp
import wave

from PIL import Image

from verify_expansion import imported
from verify_visual import ROOT, Server, atelier_check


def verify_browser(base, folder, library_path):
    sys.path.insert(0, str(library_path))
    from playwright.sync_api import expect, sync_playwright

    audio = BytesIO()
    with wave.open(audio, "wb") as wav:
        wav.setparams((1, 2, 48000, 0, "NONE", ""))
        wav.writeframes(bytes(48000 * 2 * 3))
    with sync_playwright() as runner:
        browser = runner.chromium.launch(channel="msedge", headless=True)
        page = browser.new_page(viewport={"width": 1280, "height": 860}, accept_downloads=True)
        errors = []
        page.on("pageerror", lambda error: errors.append(str(error)))
        page.goto(base)
        expect(page.get_by_role("checkbox", name="fixture.png")).to_be_visible()
        expect(page.locator("#files")).to_be_enabled()
        page.get_by_role("button", name="カラオケ動画").click()
        form = page.locator("#karaoke-form")
        expect(form).to_be_visible()
        expect(page.locator("#karaoke-jobs")).to_contain_text("生成したカラオケ動画がここに表示されます。")
        form.locator('[name="mode"]').select_option("score")
        form.locator('[name="title"]').fill("Atelier score check")
        form.locator('[name="audio"]').set_input_files({"name": "voice.wav", "mimeType": "audio/wav",
                                                      "buffer": audio.getvalue()})
        notes = {"recipe": {"notes": [{"start": .7, "duration": .42, "midi": 66}]}}
        form.locator('[name="guide"]').set_input_files({"name": "guide.json", "mimeType": "application/json",
                                                      "buffer": json.dumps(notes).encode()})
        srt = "1\n00:00:00,300 --> 00:00:01,650\n夜空を見上げる\n\n2\n00:00:02,000 --> 00:00:02,900\n星が光る\n"
        form.locator('[name="cues"]').set_input_files({"name": "lyrics.srt", "mimeType": "application/x-subrip",
                                                     "buffer": srt.encode()})
        form.get_by_text("描画設定").click()
        form.locator('[name="width"]').fill("640")
        form.locator('[name="height"]').fill("360")
        form.locator('[name="style"]').fill('{"leadMs":120,"lines":1}')
        form.get_by_role("button", name="動画を生成").click()
        jobs = page.locator("#karaoke-jobs")
        expect(jobs.get_by_text("流れる楽譜 ・ 完了")).to_be_visible(timeout=60000)
        with page.expect_download() as event:
            jobs.get_by_role("link", name="レシピJSON").click()
        recipe = json.loads(Path(event.value.path()).read_bytes())
        assert recipe["style"]["leadMs"] == 120 and recipe["lyrics"][0]["text"] == "夜空を見上げる"
        with page.expect_download() as event:
            jobs.get_by_role("link", name="動画を保存").click()
        first = sha256(Path(event.value.path()).read_bytes()).hexdigest()
        jobs.get_by_role("button", name="再生成").click()
        expect(jobs.get_by_text("流れる楽譜 ・ 完了")).to_have_count(2, timeout=60000)
        with page.expect_download() as event:
            jobs.get_by_role("link", name="動画を保存").first.click()
        assert first == sha256(Path(event.value.path()).read_bytes()).hexdigest()
        assert not errors, errors
        page.screenshot(path=str(folder / "karaoke-ui.png"))
        browser.close()
    print("Atelier UI PASS: guide JSON, SRT, video download, recipe download and matching replay.", flush=True)


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--ffmpeg", required=True, type=Path)
    cli.add_argument("--font", required=True, type=Path)
    cli.add_argument("--browser-libs", required=True, type=Path)
    args = cli.parse_args()
    artifacts = ROOT / "artifacts"
    artifacts.mkdir(exist_ok=True)
    folder = Path(mkdtemp(prefix="karaoke-ui-", dir=artifacts))
    print("Verification data:", folder, flush=True)
    config = ["--Visual:EnableGpu=false", "--Visual:KaraokeFFmpeg", str(args.ffmpeg.resolve()),
              "--Visual:KaraokeFont", str(args.font.resolve())]
    api = Server("api", folder, config, "Debug")
    try:
        imported(api, "fixture.png", Image.new("RGB", (32, 32), "white"))
        atelier_check(api, folder, "Debug", None, args.browser_libs, verify_browser, basic_browser=False)
    finally:
        api.stop()


if __name__ == "__main__":
    main()
