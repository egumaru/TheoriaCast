"""Exercise the open composition form and the registered Codex MCP client together."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys


class CodexClient:
    def __init__(self, atelier_url=None):
        self.server = "theoria-music" if atelier_url is None else "theoria-music-check"
        cmd = [shutil.which("codex")]
        if atelier_url is not None:
            cmd += ["-c", f"mcp_servers.{self.server}.url=" + json.dumps(atelier_url.rstrip("/") + "/mcp")]
        self.process = subprocess.Popen([*cmd, "app-server"], stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, encoding="utf-8")
        self.sequence = 0
        self.call("initialize", {"clientInfo":{"name":"studio_check","version":"1"},
                                  "capabilities":{"experimentalApi":True}})
        self.process.stdin.write('{"method":"initialized"}\n')
        self.process.stdin.flush()
        self.thread = self.call("thread/start", {"cwd":str(Path.cwd()), "ephemeral":True,
                                "approvalPolicy":"never", "sandbox":"read-only"})["thread"]["id"]

    def call(self, method, params):
        self.sequence += 1
        self.process.stdin.write(json.dumps({"id":self.sequence,"method":method,"params":params}) + "\n")
        self.process.stdin.flush()
        for line in self.process.stdout:
            reply = json.loads(line)
            if reply.get("id") == self.sequence:
                if "error" in reply:
                    raise ValueError(reply["error"])
                return reply["result"]
        raise RuntimeError("Codex disconnected")

    def tool(self, name, **args):
        reply = self.call("mcpServer/tool/call", {"threadId":self.thread, "server":self.server,
                          "tool":name, "arguments":args})
        if reply.get("isError"):
            raise RuntimeError(reply["content"])
        return json.loads(reply["content"][0]["text"])

    def close(self):
        self.process.terminate()
        self.process.wait(timeout=15)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("url")
    parser.add_argument("--playwright-path")
    args = parser.parse_args()
    if args.playwright_path:
        sys.path.insert(0, args.playwright_path)
    from playwright.sync_api import expect, sync_playwright
    codex = CodexClient(args.url)
    try:
        with sync_playwright() as p:
            browser = p.chromium.launch(channel="msedge", headless=True)
            page = browser.new_page(locale="ja-JP",viewport={"width":412,"height":860})
            errors = []
            page.on("pageerror", lambda e: errors.append(str(e)))
            page.goto(args.url)
            page.get_by_label("テーマ",exact=True).fill("雨上がりの散歩")
            page.get_by_label("長さ（秒）",exact=True).fill("12")
            page.get_by_text("Codex と一緒に考える",exact=True).click()
            page.get_by_label("相談したいこと").fill("曲の最後に雨粒がひとつ落ちるような、楽器とテンポを考えてください。")

            def ask():
                with page.expect_response(lambda r:r.url.endswith("/studio/requests") and r.request.method=="POST") as response:
                    page.get_by_text("今の下書きで相談する",exact=True).click()
                return response.value.json()["id"]

            first = ask()
            assert first in [r["id"] for r in codex.tool("list_studio_requests")]
            assert codex.tool("get_studio_request",id=first)["draft"]["theme"] == "雨上がりの散歩"
            codex.tool("suggest_in_studio",id=first,reply="最後の一音を響かせましょう。",changes={"theme":"雨粒の余韻"})
            page.get_by_label("テーマ",exact=True).fill("窓辺の雨粒")
            page.get_by_text("この提案を下書きに使う",exact=True).click()
            assert "相談の後で編集" in page.locator("#studioStatus").inner_text()
            assert page.get_by_label("テーマ",exact=True).input_value() == "窓辺の雨粒"
            second = ask()
            assert codex.tool("get_studio_request",id=first)["status"] == "closed"
            other = browser.new_page(locale="ja-JP")
            other.goto(args.url)
            assert other.get_by_label("テーマ",exact=True).input_value() == ""
            page.reload()
            suggestion = {"style":"delicate piano waltz", "instruments":"ピアノの三拍子、最後に鉄琴を一音", "bpm":84}
            codex.tool("suggest_in_studio",id=second,reply="ピアノの三拍子が止まったところに、鉄琴を一音だけ残す案です。",changes=suggestion)
            page.get_by_text("この提案を下書きに使う",exact=True).click()
            expect(page.locator("[name=instruments]")).to_have_value(suggestion["instruments"])
            assert codex.tool("get_studio_request",id=second)["status"] == "applied"
            assert other.locator("[name=instruments]").input_value() == ""
            with page.expect_response(lambda r:r.url.endswith("/jobs") and r.request.method=="POST") as response:
                page.get_by_text("作曲を予約",exact=True).click()
            job = response.value.json()
            assert response.value.status == 202 and job["spec"]["bpm"] == 84 and job["spec"]["style"] == suggestion["style"]
            assert not errors, errors
            print(json.dumps({"consultation":second,"job":job["id"],"overlap_protected":True,"tab_isolation":True,
                              "page_reload":True,"state":job["state"]}),flush=True)
            browser.close()
    finally:
        codex.close()


if __name__ == "__main__":
    main()
