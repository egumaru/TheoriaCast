"""Waveform regression check with generated test tones and the installed edit worker."""
from datetime import datetime, timezone
from io import BytesIO
import json
import os
from pathlib import Path
import subprocess
import urllib.request
import zipfile


def check_panel(page):
    from playwright.sync_api import expect
    wave = page.locator("#wave")
    start, end = page.locator('[name=start]'), page.locator('[name=end]')
    with page.expect_response(lambda r: "/waveform?start=2&end=4" in r.url) as response:
        page.locator("#waveZoom").click()
    data = response.value.json()
    assert (data["start"],data["end"],data["duration"]) == (2,4,6) and len(data["peaks"]) == 1800
    assert page.request.get(response.value.url.split("?")[0]+"?start=4&end=2").status == 400
    expect(page.locator("#wavePan")).to_be_visible()
    wave.press("Home")
    wave.press("Shift+ArrowRight")
    expect(wave).to_have_attribute("aria-valuenow","3")
    for button, field in [("#waveMarkStart",start),("#waveMarkEnd",end)]:
        page.locator(button).click()
        expect(field).to_have_value("3")
        page.locator("#waveUndo").click()
        expect(start).to_have_value("2")
        expect(end).to_have_value("4")
    with page.expect_response(lambda r: "/waveform?start=4&end=6" in r.url):
        page.locator("#wavePan").press("End")
    expect(start).to_have_value("2")
    expect(page.locator("#waveView")).to_have_text("0:04.00 ～ 0:06.00")
    page.locator("#waveFit").click()
    expect(page.locator("#wavePan")).to_be_hidden()
    wave.press("Home")
    wave.press("ArrowRight")
    expect(wave).to_have_attribute("aria-valuenow","0.1")
    wave.press("Space")
    page.wait_for_function("document.querySelector('#waveAudio').currentTime > .2")
    wave.press("Space")
    page.wait_for_function("document.querySelector('#waveAudio').paused")
    page.locator("#waveVolume").press("End")
    page.locator("#waveVolume").press("ArrowLeft")
    assert abs(page.locator("#waveAudio").evaluate("audio => audio.volume")-.99) < .001
    page.locator("#waveLoop").check()
    page.locator("#waveListen").click()
    page.wait_for_function("document.querySelector('#waveAudio').currentTime > 3.6")
    page.wait_for_function("!document.querySelector('#waveAudio').paused && document.querySelector('#waveAudio').currentTime < 2.6")
    page.locator("#waveListen").click()
    page.locator("#waveLoop").uncheck()
    assert page.evaluate("document.documentElement.scrollWidth <= innerWidth")
    wave.scroll_into_view_if_needed()
    box = wave.bounding_box()
    touch = page.context.new_cdp_session(page)
    try:
        touch.send("Emulation.setTouchEmulationEnabled", {"enabled":True})
        for phase, fraction in [("touchStart",.10),("touchMove",.50),("touchMove",.88),("touchEnd",None)]:
            points = [] if fraction is None else [{"x":box["x"]+box["width"]*fraction, "y":box["y"]+box["height"]/2}]
            touch.send("Input.dispatchTouchEvent", {"type":phase,"touchPoints":points})
        expect(start).to_have_value("0")
        expect(end).to_have_value("6")
        page.locator("#waveUndo").click()
        expect(start).to_have_value("2")
        expect(end).to_have_value("4")
    finally:
        touch.send("Emulation.setTouchEmulationEnabled", {"enabled":False})
        touch.detach()


def check_editor(page, codex, first_job, descriptor, node):
    from playwright.sync_api import expect
    folder = descriptor.parent
    endpoint = json.loads(descriptor.read_text("utf-8-sig"))["api_url"].rstrip("/")
    worker_token = json.loads((folder/"data/server-access.json").read_text("utf-8-sig"))["worker_token"]
    ff = node["ffmpeg"]
    rate = 16000
    def worker(path, body, media="application/json"):
        data = json.dumps(body).encode() if media == "application/json" else body
        req = urllib.request.Request(endpoint + path, data, {"Authorization":"Bearer "+worker_token, "Content-Type":media})
        with urllib.request.urlopen(req, timeout=15) as res:
            return json.load(res)

    def tone(job, hz, seconds=6, sample_rate=rate, channels=1):
        claim = worker("/worker/poll?claim=true", {"node":"A", "ready":True, "free_mib":8192,
                       "detail":"音声素材の取り込み試験", "types":["generate_bgm"]})
        assert claim["id"] == job["id"]
        args = [ff, "-v","error","-f","lavfi","-i",f"sine=frequency={hz}:duration={seconds}:sample_rate={sample_rate}"]
        wav = folder/(job["id"]+".wav")
        subprocess.run([*args,"-af","tremolo=f=1.7:d=.88,afade=in:d=1","-ac",str(channels),str(wav)], check=True)
        mp3 = subprocess.check_output([ff,"-v","error","-i",str(wav),"-f","mp3","pipe:1"])
        meta = dict(model="test tone", model_version={"frequency":hz}, license="CC0", params={},
                    actual_seconds=seconds, generation={"bpm":120}, operator="test", machine="test", gpu="CPU",
                    processing=[], created_at=datetime.now(timezone.utc).isoformat(),
                    cues=[{"start":0,"end":6,"text":str(hz)}])
        archive = BytesIO()
        with zipfile.ZipFile(archive,"w") as z:
            z.write(wav,"master.wav")
            z.writestr("preview.mp3",mp3)
            z.writestr("master.wav.json",json.dumps(meta))
        worker(f'/worker/{job["id"]}/finish?lease={claim["lease"]}',archive.getvalue(),"application/zip")
        return wav

    donor = codex.tool("submit_job", spec={"type":"generate_bgm","theme":"差し替え用の試験音","seconds":6})
    base_file, donor_file = tone(first_job,220), tone(donor,880)
    page.get_by_role("button",name="曲を直す",exact=True).click()
    page.set_viewport_size({"width":412,"height":860})
    source = page.locator("#editForm [name=source]")
    source.select_option(first_job["id"], timeout=15000)
    page.locator("#editMode").select_option("replace")
    expect(page.locator("#waveListen")).to_be_enabled()
    page.get_by_text("小節に合わせる",exact=True).click()
    expect(page.locator("#waveBpm")).to_have_value("120")
    page.locator("#waveSnap").check()
    wave = page.locator("#wave")
    wave.scroll_into_view_if_needed()
    box = wave.bounding_box()
    y = box["y"] + box["height"]/2
    page.mouse.move(box["x"] + box["width"]*.36,y)
    page.mouse.down()
    page.mouse.move(box["x"] + box["width"]*.69,y,steps=6)
    page.mouse.up()
    expect(page.locator('[name=start]')).to_have_value("2")
    expect(page.locator('[name=end]')).to_have_value("4")
    check_panel(page)
    page.locator("#waveListen").click()
    page.wait_for_function("document.querySelector('#waveAudio').currentTime > 2.1")
    page.wait_for_function("document.querySelector('#waveAudio').paused && Math.abs(document.querySelector('#waveAudio').currentTime-4)<.05")
    page.locator('[name=replacement]').select_option(donor["id"])
    page.locator('[name=replacement_start]').fill("1")
    if screenshot := os.environ.get("THEORIA_WAVE_CHECK_SCREENSHOT"):
        page.get_by_text("小節に合わせる",exact=True).click()
        panel = page.locator(".wave-editor")
        panel.screenshot(path=screenshot, animations="disabled")
        page.set_viewport_size({"width":1200,"height":900})
        desktop = Path(screenshot).with_name(Path(screenshot).stem.removesuffix("-mobile")+"-desktop.png")
        panel.screenshot(path=str(desktop), animations="disabled")
    def reserve():
        with page.expect_response(lambda r:r.url.endswith("/jobs") and r.request.method=="POST") as res:
            page.get_by_role("button",name="修正を予約").click()
        assert res.value.status == 202, res.value.text()
        job = res.value.json()
        row = page.locator(f'#jobs article[data-id="{job["id"]}"]')
        expect(row).to_contain_text("できました", timeout=180000)
        assert codex.tool("get_job",id=job["id"])["state"] == "done"
        return codex.tool("get_track",id=job["id"])
    replaced = reserve()
    def pcm(path):
        return subprocess.check_output([ff,"-v","error","-i",str(path),"-f","s16le","pipe:1"])
    def delivered_pcm(track):
        download = folder/(track["id"]+".wav")
        with urllib.request.urlopen(urllib.parse.urljoin(page.url,track["audio"]),timeout=15) as response:
            download.write_bytes(response.read())
        return pcm(download)
    source_samples, donor_samples = pcm(base_file), pcm(donor_file)
    unit = rate*2
    assert delivered_pcm(replaced) == source_samples[:2*unit] + donor_samples[unit:3*unit] + source_samples[4*unit:]
    assert [p["track"] for p in replaced["provenance"]["processing"][-1]["parents"]] == [first_job["id"],donor["id"]]
    assert [(c["start"],c["end"],c["text"]) for c in replaced["provenance"]["cues"]] == [(0,2,"220"),(2,4,"880"),(4,6,"220")]
    page.get_by_role("button",name="曲を直す",exact=True).click()
    page.locator("#editMode").select_option("trim")
    page.locator('[name=start]').fill("2")
    page.locator('[name=end]').fill("4")
    trimmed = reserve()
    assert delivered_pcm(trimmed) == source_samples[2*unit:4*unit]
    print(json.dumps({"replace":replaced["id"],"trim":trimmed["id"], "base_seconds":6, "selection":[2,4],
                      "donor_seconds":[1,3], "pcm_exact":True, "captions":True, "preview_stop":4}),flush=True)
    if os.environ.get("THEORIA_CHECK_LARGE_AUDIO") == "1":
        long_job = codex.tool("submit_job",spec={"type":"generate_bgm","theme":"長い音声の転送試験","seconds":125})
        original = tone(long_job,440,125,48000,2)
        edit = codex.tool("edit_track",track=long_job["id"],end=125)
        row = page.locator(f'#jobs article[data-id="{edit["id"]}"]')
        expect(row).to_contain_text("できました",timeout=180000)
        track = codex.tool("get_track",id=edit["id"])
        audio_url = urllib.parse.urljoin(page.url,track["audio"])
        with urllib.request.urlopen(urllib.request.Request(audio_url,headers={"Range":"bytes=0-43"})) as r:
            length = int(r.headers["Content-Range"].split("/")[-1])
            assert r.status == 206 and length > 32000000 and len(r.read()) == 44
        assert delivered_pcm(track) == pcm(original)
        print(json.dumps({"long_edit":edit["id"],"wav_bytes":length,"seconds":125,"range":206,"pcm_exact":True}),flush=True)
