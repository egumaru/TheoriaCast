"""Exercise a private 0.1.4 API and Atelier using Edge and the real Codex MCP client."""
import argparse
from contextlib import ExitStack, contextmanager
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

from studio_check import CodexClient


@contextmanager
def service(dotnet, dll, arguments, folder):
    env = {k: v for k, v in os.environ.items() if not k.lower().startswith(("music__", "distributed__"))}
    env.pop("THEORIA_STUDIO_OPEN", None)
    log = folder / (dll.stem + ".log")
    with log.open("w", encoding="utf-8") as output:
        process = subprocess.Popen([dotnet, dll, *arguments], cwd=folder, stdout=output,
                                   stderr=subprocess.STDOUT, env=env)
        try:
            yield process
        finally:
            process.terminate()
            process.wait(timeout=15)
            if process.returncode not in (0, 1, -15):
                print(log.read_text("utf-8", errors="replace")[-3000:], file=sys.stderr)


def wait_for(check, process):
    for _ in range(200):
        if process.poll() is not None:
            raise RuntimeError(f"Server exited with code {process.returncode}")
        try:
            if check():
                return
        except OSError:
            pass
        time.sleep(0.15)
    raise TimeoutError("Server startup timed out")


def read_json(url):
    with urllib.request.urlopen(url, timeout=2) as response:
        return json.load(response)


def exercise(url, descriptor, libraries, node):
    if libraries:
        sys.path.insert(0, str(libraries))
    from playwright.sync_api import expect, sync_playwright

    with sync_playwright() as playwright, ExitStack() as stack:
        browser = playwright.chromium.launch(channel="msedge", headless=True, args=["--disable-gpu"])
        stack.callback(browser.close)
        page = browser.new_page(locale="ja-JP")
        page.goto(url)
        expect(page.locator("#importServer")).to_be_visible()
        page.locator("#importServer").set_input_files(descriptor)
        expect(page.locator("body")).not_to_have_class("connecting", timeout=20000)
        with page.expect_response(lambda r: r.url.endswith("/jobs") and r.request.method == "POST") as response:
            page.get_by_label("テーマ", exact=True).fill("Atelier の予約試験")
            page.get_by_label("長さ（秒）", exact=True).fill("10")
            page.get_by_role("button", name="作曲を予約", exact=True).click()
        assert response.value.status == 202
        gui_job = response.value.json()
        assert gui_job["state"] == "queued" and gui_job["spec"]["creator"] == "human"
        row = lambda job: page.locator(f'#jobs article[data-id="{job["id"]}"]')
        expect(row(gui_job)).to_contain_text("順番待ち")

        codex = CodexClient(url)
        stack.callback(codex.close)
        inventory = codex.call("mcpServerStatus/list", {"threadId":codex.thread, "serverName":codex.server,
                                                        "detail":"toolsAndAuthOnly"})["data"]
        names = set(next(s["tools"] for s in inventory if s["name"] == codex.server))
        assert {"submit_job","get_job","suggest_in_studio","edit_track","fix_readings","mix_tracks"} <= names
        assert codex.tool("get_job", id=gui_job["id"]) == gui_job
        mcp_job = codex.tool("submit_job", spec={"type":"generate_bgm", "theme":"Codex の予約試験", "seconds":12})
        saved = codex.tool("get_job", id=mcp_job["id"])
        assert saved["state"] == "queued" and saved["spec"]["creator"] == "codex"
        assert {gui_job["id"], saved["id"]} == {j["id"] for j in codex.tool("list_jobs")}
        expect(row(saved)).to_contain_text("Codex の予約試験", timeout=15000)
        expect(row(saved)).to_contain_text("順番待ち")
        row(saved).get_by_role("button", name="取り消す").click()
        expect(row(saved)).to_contain_text("取り消し済み")
        final_state = codex.tool("get_job", id=saved["id"])["state"]
        assert final_state == "cancelled"
        page.get_by_role("button",name="曲を作る",exact=True).click()
        page.get_by_text("Codex と一緒に考える",exact=True).click()
        page.get_by_label("相談したいこと").fill("この曲に使う楽器を提案してください。")
        with page.expect_response(lambda r:r.url.endswith("/studio/requests") and r.request.method=="POST") as response:
            page.get_by_text("今の下書きで相談する",exact=True).click()
        request_id = response.value.json()["id"]
        assert codex.tool("get_studio_request",id=request_id)["question"]
        codex.tool("suggest_in_studio",id=request_id,reply="ピアノの余韻を使いましょう。",changes={"instruments":"ピアノ"})
        page.get_by_text("この提案を下書きに使う",exact=True).click()
        expect(page.locator("[name=instruments]")).to_have_value("ピアノ")
        assert codex.tool("get_studio_request",id=request_id)["status"] == "applied"
        expect(page.locator("#serverRows")).to_contain_text("接続中", timeout=30000)
        print(json.dumps({"atelier_job":gui_job["id"], "atelier_via_mcp":gui_job["state"],
                          "codex_job":saved["id"], "codex_via_atelier":saved["state"],
                          "after_gui_cancel":final_state, "heartbeat":"online","mcp_tools":len(names),
                          "consultation":request_id,"suggestion":"applied"}), flush=True)
        english=browser.new_page(locale="en-US")
        english.goto(url)
        expect(english.get_by_role("heading",name="Sound Atelier")).to_be_visible()
        english.locator("#materials summary").click()
        content=b"# A quiet room\r\nPiano and rain.\r\n"
        picker=english.locator("#importMaterial")
        picker.set_input_files({"name":"prompt.md","mimeType":"text/markdown","buffer":content})
        expect(english.locator("#materialsStatus")).to_have_text("Imported into your draft.")
        with english.expect_download() as download:
            english.get_by_role("button",name="Save prompt",exact=True).click()
        assert download.value.path().read_bytes()==content
        with english.expect_download() as download:
            english.get_by_role("button",name="Save composition bundle").click()
        bundle=download.value.path().read_bytes()
        with english.expect_response(lambda r:r.url.endswith("/composition/import")) as result:
            picker.set_input_files({"name":"composition.zip","mimeType":"application/zip","buffer":bundle})
        assert result.value.status==200
        expect(english.locator("[name=prompt_md]")).to_have_value(content.decode().replace("\r\n","\n"))
        with english.expect_response(lambda r:r.url.endswith("/studio/requests") and r.request.method=="POST") as response:
            english.locator("#askPrompt").click()
        question=response.value.json()["id"]
        codex.tool("suggest_in_studio",id=question,reply="Keep the opening sparse.",changes={"prompt_md":"Only solo piano."})
        english.get_by_role("button",name="Apply suggestion to draft").click()
        expect(english.locator("[name=prompt_md]")).to_have_value("Only solo piano.")
        english.locator("#language").select_option("ja")
        expect(english.get_by_role("heading",name="音のアトリエ")).to_be_visible()
        expect(english.locator("[name=prompt_md]")).to_have_value("Only solo piano.")
        english.close()
        print("PASS English UI, bilingual draft, material import/export and Codex prompt suggestion",flush=True)
        if node:
            from waveform_check import check_editor
            check_editor(page, codex, gui_job, descriptor, node)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--playwright-path", type=Path)
    parser.add_argument("--node-config", type=Path, help="Also exercise waveform editing with this installed renderer and its existing idle gate.")
    args = parser.parse_args()
    src = Path(__file__).resolve().parents[2]
    def dll(name):
        return src / name / "bin/Release/net10.0" / (name + ".dll")

    with tempfile.TemporaryDirectory(prefix="theoria-provider-") as root, ExitStack() as stack:
        folder = Path(root)
        descriptor = folder / "connection.json"
        node = None
        worker_args = []
        if args.node_config:
            node = json.loads(args.node_config.read_text("utf-8-sig"))
            node.update(node="A", types=["edit"], scratch=str(folder/"takes"), poll_seconds=1,
                        vocal_profile=None, render_command=[node["python"], str(src/"TheoriaCast.Music/worker/render.py")])
            path = folder/"node-check.json"
            path.write_text(json.dumps(node), encoding="utf-8")
            worker_args = ["--node-config", str(path)]
        api = stack.enter_context(service(args.dotnet, dll("TheoriaCast.Music.Api"),
                      ["--urls", "http://127.0.0.1:0", "--Music:Data", str(folder/"data"),
                       "--connection-file", str(descriptor), "--server-name", "結合試験",
                       "--Music:Capabilities:A:0","generate_bgm","--Music:Capabilities:A:1","edit", *worker_args], folder))
        wait_for(descriptor.exists, api)
        endpoint = json.loads(descriptor.read_text("utf-8-sig"))["api_url"]
        try:
            read_json(endpoint.rstrip("/") + "/jobs")
        except urllib.error.HTTPError as e:
            assert e.code == 401
        else:
            raise AssertionError("Unauthenticated request succeeded")
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            port = sock.getsockname()[1]
        url = f"http://localhost:{port}"
        atelier = stack.enter_context(service(args.dotnet, dll("TheoriaCast.Music.Atelier"),
                      ["--port", str(port), "--profile", str(folder/"profiles")], folder))
        wait_for(lambda: read_json(url+"/atelier/servers").get("servers") == [], atelier)
        exercise(url, descriptor, args.playwright_path, node)


if __name__ == "__main__":
    main()
