"""Exercise the packaged image providers through the Visual HTTP, MCP and Atelier entry points."""

import argparse
import contextlib
import hashlib
import json
import os
from pathlib import Path
import socket
import struct
import subprocess
import sys
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
import uuid
import zlib
from base64 import b64encode


ROOT = Path(__file__).resolve().parents[2]
PREFIX = "/api/v1/visual"
CHUNK = 384 * 1024


def request(base, path, *, token=None, body=None, method=None, headers=None, status=200):
    headers = dict(headers or {})
    if token:
        headers["Authorization"] = "Bearer " + token
    if body is not None:
        headers["Content-Type"] = "application/json"
        body = json.dumps(body).encode()
    req = Request(base + path, data=body, headers=headers, method=method)
    try:
        with urlopen(req, timeout=20) as response:
            assert response.status == status, (path, response.status)
            return response.read(), response.headers
    except HTTPError as error:
        payload = error.read()
        assert error.code == status, (path, error.code, payload[:2000].decode(errors="replace"))
        return payload, error.headers


def await_result(callback, seconds=120):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = callback()
        if value:
            return value
        time.sleep(0.5)
    raise AssertionError("Timed out waiting for the service or work to finish")

def executable(project, configuration):
    if build := os.environ.get("THEORIA_VISUAL_ARTIFACTS"):
        return Path(build) / "bin" / project / configuration.lower() / (project + ".dll")
    return ROOT / "src" / project / "bin" / configuration / "net10.0" / (project + ".dll")


class Server:
    def __init__(self, label, directory, settings, configuration, cuda_dir=None):
        self.directory = directory / label
        self.file = self.directory / "connection.json"
        self.log = directory / (label + ".log")
        self.env = os.environ.copy()
        if cuda_dir:
            self.env["PATH"] = str(cuda_dir) + os.pathsep + self.env["PATH"]
        dll = executable("TheoriaCast.Visual.Api", configuration)
        self.command = ["dotnet", str(dll), "--urls", "http://127.0.0.1:0", "--Visual:Data", str(self.directory), *settings]
        self.session = {}
        self.rpc_id = 0
        self.start()

    def start(self):
        with self.log.open("ab") as out:
            self.process = subprocess.Popen(self.command, env=self.env, stdout=out, stderr=out,
                                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))

        def connected():
            if self.process.poll() is not None:
                raise AssertionError("Visual API exited. See " + str(self.log))
            try:
                return json.loads(self.file.read_bytes())
            except FileNotFoundError:
                return None

        try:
            self.connection = await_result(connected, 35)
            self.base = self.connection["api_url"]
            self.token = self.connection["token"]
            self.id = self.connection["server_id"]
            self.session.clear()
            assert self.get("/assets") is not None
        except BaseException:
            if self.process.poll() is None:
                self.stop()
            raise

    def stop(self):
        self.process.terminate()
        self.process.wait(timeout=15)

    def get(self, path, status=200):
        return json.loads(request(self.base, PREFIX + path, token=self.token, status=status)[0])

    def post(self, path, body, status=200):
        return json.loads(request(self.base, PREFIX + path, token=self.token, body=body, status=status)[0])

    def rpc(self, message, status=200):
        headers = {"Accept": "application/json, text/event-stream", **self.session}
        payload, response_headers = request(self.base, "/mcp", token=self.token, body=message, headers=headers, status=status)
        if response_headers.get("Mcp-Session-Id"):
            self.session["Mcp-Session-Id"] = response_headers["Mcp-Session-Id"]
        if not payload:
            return None
        if "text/event-stream" in response_headers.get("Content-Type", ""):
            payload = next(line[6:] for line in payload.splitlines() if line.startswith(b"data: "))
        result = json.loads(payload)
        assert "error" not in result, result
        return result["result"]

    def initialize_mcp(self):
        self.rpc({"jsonrpc": "2.0", "method": "initialize", "id": 1, "params": {
            "protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "visual-check", "version": "1"}
        }})
        self.rpc({"jsonrpc": "2.0", "method": "notifications/initialized"}, 202)
        names = {item["name"] for item in self.rpc({"jsonrpc": "2.0", "method": "tools/list", "id": 2})["tools"]}
        assert {"visual_import_asset", "visual_submit_batch", "visual_get_job", "visual_replay_recipe"} <= names

    def mcp(self, name, **arguments):
        self.rpc_id += 1
        result = self.rpc({"jsonrpc": "2.0", "method": "tools/call", "id": self.rpc_id + 2,
                           "params": {"name": "visual_" + name, "arguments": arguments}})
        assert not result.get("isError"), result
        return result.get("structuredContent") or json.loads(result["content"][0]["text"])

    def finish(self, job, via_mcp=False, seconds=120):
        def read():
            current = self.mcp("get_job", id=job["id"]) if via_mcp else self.get("/jobs/" + job["id"])
            return current if current["status"] not in {"queued", "running"} else None

        result = await_result(read, seconds)
        assert result["status"] == "done", result
        return result


def png_chunks(png):
    assert png.startswith(b"\x89PNG\r\n\x1a\n")
    head = 8
    while head < len(png):
        size, tag = struct.unpack_from(">I4s", png, head)
        data = png[head + 8:head + 8 + size]
        (crc,) = struct.unpack_from(">I", png, head + 8 + size)
        assert zlib.crc32(tag + data) == crc, tag
        yield tag, data
        head += 12 + size


def recipe_packet(png):
    marker = b"TheoriaCast.Recipe\0\0\0\0\0"
    return next(json.loads(data[len(marker):]) for tag, data in png_chunks(png) if tag == b"iTXt" and data.startswith(marker))


def image(server, reference):
    path = PREFIX + "/assets/" + reference["assetId"] + "/image"
    png, headers = request(server.base, path, token=server.token)
    assert headers.get_content_type() == "image/png"
    assert hashlib.sha256(png).hexdigest() == reference["sha256"]
    return png


def cpu_checks(server, photo, matting):
    request(server.base, PREFIX + "/assets", status=401)
    server.initialize_mcp()
    parts = [server.mcp("stage_image_chunk", chunk={"bytes": b64encode(photo[offset:offset + CHUNK]).decode()})["sha256"]
             for offset in range(0, len(photo), CHUNK)]
    digest = hashlib.sha256(photo).hexdigest()
    asset_a = server.mcp("import_asset", request={"name": "確認用.png", "chunks": parts, "sha256": digest})
    asset_b = server.post("/assets/import", {"name": "別の素材.png", "chunks": parts, "sha256": digest}, 201)
    assert asset_a["importedAt"] and asset_b["originalSha256"] == digest
    assert asset_a["reference"]["serverId"] == server.id
    assert asset_a["reference"]["assetId"] != asset_b["reference"]["assetId"]
    assert server.post("/assets/import", {"name": "確認用.png", "chunks": parts, "sha256": digest}, 201) == asset_a
    refs = [asset_a["reference"], asset_b["reference"]]
    server.post("/assets/import", {"name": "wrong.png", "chunks": parts, "sha256": "0" * 64}, 400)
    settings = {"operation": "stylize", "touch": "illustration", "strength": 0.8}
    batch = {"hardware": "cpu", "settings": settings, "inputs": refs}
    estimate = server.post("/jobs/estimate", batch)
    assert estimate["hardware"] == "cpu" and estimate["imagesPerNight"] > 0
    job = server.finish(server.mcp("submit_batch", request=batch), via_mcp=True)
    source_recipe_id = job["items"][0]["recipeId"]
    recipe = server.get("/recipes/" + source_recipe_id)
    output = job["items"][0]["output"]
    assert recipe["input"] == refs[0] and recipe["engine"]["algorithm"] == "OpenCV NPR"
    assert recipe["engine"]["libraries"]["AIKernel.Providers.Image"] == "0.3.0"
    assert recipe_packet(image(server, output)) == recipe

    set_view = server.post("/sets", {"name": "実機確認セット", "jobId": job["id"]}, 201)
    set_id = set_view["id"]
    server.post("/sets/" + set_id + "/approve", {"version": set_view["version"]}, 409)
    for item in set_view["items"]:
        version = set_view["version"]
        set_view = server.post("/sets/" + set_id + "/review", {"asset": item["asset"], "decision": "accepted", "version": version})
        assert set_view["version"] == version + 1
    approved = server.post("/sets/" + set_id + "/approve", {"version": set_view["version"]})
    assert approved["state"] == "approved" and approved["items"][0]["recipeId"] == source_recipe_id
    server.post("/sets/" + set_id + "/review", {"asset": output, "decision": "pending", "version": approved["version"]}, 409)
    offline_ref = dict(output, serverId=uuid.uuid4().hex)
    offline = server.post("/sets", {"name": "到達不能", "assets": [offline_ref]}, 201)
    assert offline["items"][0]["asset"] == offline_ref and offline["items"][0]["availability"] == "unavailable"

    replay = server.finish(server.post("/recipes/replay", {"recipeId": source_recipe_id}, 202))
    assert replay["items"][0]["recipeId"] == source_recipe_id
    change = {"parameter": "strength", "value": 0.4}
    diff = server.finish(server.mcp("replay_recipe", request={"recipeId": source_recipe_id, "change": change}), via_mcp=True)
    varied = server.get("/recipes/" + diff["items"][0]["recipeId"])
    assert varied["parentId"] == source_recipe_id and varied["change"] == change
    assert varied["settings"] == dict(recipe["settings"], strength=0.4)
    assert recipe_packet(image(server, diff["items"][0]["output"])) == varied
    alternate = server.finish(server.post("/recipes/replay", {
        "recipeId": source_recipe_id, "change": {"parameter": "touch", "value": "pencil"}}, 202))
    pencil = image(server, alternate["items"][0]["output"])
    assert recipe_packet(pencil)["engine"]["fixedParameters"]["OpenCV photo"].startswith("pencilSketch")
    manga = server.finish(server.post("/recipes/replay", {
        "recipeId": source_recipe_id, "change": {"parameter": "touch", "value": "manga"}}, 202))
    manga_png = image(server, manga["items"][0]["output"])
    pixels = lambda png: b"".join(data for tag, data in png_chunks(png) if tag == b"IDAT")
    assert recipe_packet(manga_png)["engine"]["fixedParameters"]["style"] == "pencil"
    assert pixels(manga_png) == pixels(pencil)
    assert pixels(image(server, output)) != pixels(image(server, diff["items"][0]["output"]))

    crop = server.finish(server.post("/jobs", {"settings": {"operation": "resize", "width": 360, "height": 640},
                                               "inputs": [refs[0]]}, 202))
    resized = server.get("/assets/" + crop["items"][0]["output"]["assetId"])
    assert (resized["width"], resized["height"]) == (360, 640)
    if matting:
        for parameters in [{"background": "transparent"}, {"background": "solid", "color": "#103040"},
                           {"background": "image", "backgroundAsset": refs[1]}]:
            background = server.finish(server.post("/jobs", {"settings": {"operation": "background", **parameters},
                                                             "inputs": [refs[0]]}, 202))
            png = image(server, background["items"][0]["output"])
            assert png[25] == 6
            assert "birefnet-lite-512.onnx" in recipe_packet(png)["engine"]["models"]
    print("CPU: MCP import/batch/results, crop, set decisions/approval, unavailable references and exact/one-field replay.")
    if matting:
        print("CPU: pinned BiRefNet cutout, solid and user-image backgrounds.")
    return refs[0], set_id


def browser_check(base, folder, photo, library_path):
    sys.path.insert(0, str(library_path))
    from playwright.sync_api import expect, sync_playwright

    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(channel="msedge", headless=True)
        page = browser.new_page(viewport={"width": 1280, "height": 800})
        errors = []
        page.on("pageerror", lambda error: errors.append(str(error)))
        page.goto(base)
        expect(page.get_by_role("heading", name="素材のアトリエ")).to_be_visible()
        expect(page.get_by_label("確認用.png")).to_be_visible()
        expect(page.locator("#files")).to_be_enabled()
        page.locator("#files").set_input_files({"name": "Atelier 実機.png", "mimeType": "image/png", "buffer": photo})
        expect(page.get_by_text("1 件を取り込みました。")).to_be_visible(timeout=30000)
        expect(page.get_by_label("Atelier 実機.png")).to_be_checked()
        page.get_by_label("計算資源").select_option("cpu")
        page.get_by_role("button", name="所要時間を確認").click()
        expect(page.get_by_text("一晩（8時間）", exact=False)).to_be_visible()
        page.get_by_role("button", name="ジョブ登録").click()
        expect(page.get_by_text("一括編集を登録しました。")).to_be_visible()
        first_job = page.locator("#job-list details").first
        expect(first_job.locator("summary")).to_contain_text("完了", timeout=30000)
        first_job.locator("summary").click()
        first_job.get_by_role("button", name="結果を素材セットで確認").click()
        expect(page.locator("#set-detail .preview")).to_be_visible()
        expect(page.locator("#set-detail").get_by_role("heading", name="Atelier 実機-stylize.png")).to_be_visible()
        page.screenshot(path=str(folder / "atelier-desktop.png"))
        page.locator("#set-detail").get_by_role("button", name="採用", exact=True).click()
        page.once("dialog", lambda dialog: dialog.accept())
        page.get_by_role("button", name="この版を承認").click()
        expect(page.locator("#set-detail")).to_contain_text("承認済み")
        page.locator("#set-detail").get_by_role("button", name="レシピ").click()
        expect(page.get_by_role("dialog")).to_be_visible()
        expect(page.get_by_role("button", name="このレシピで再実行")).to_be_visible()
        page.get_by_role("button", name="閉じる").click()
        page.set_viewport_size({"width": 390, "height": 844})
        assert page.locator("body").evaluate("body => body.scrollWidth <= window.innerWidth")
        page.screenshot(path=str(folder / "atelier-mobile.png"), full_page=True)
        assert not errors, errors
        browser.close()
    print("Atelier browser: upload, estimate, submit, review, approval and recipe controls on Edge.")


def atelier_check(server, folder, configuration, photo, browser_libs, expanded_browser=None, *, basic_browser=True):
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    dll = executable("TheoriaCast.Visual.Atelier", configuration)
    base = "http://localhost:" + str(port)
    command = ["dotnet", str(dll), "--port", str(port), "--profile", str(folder / "atelier"), "--import", str(server.file)]
    with (folder / "atelier.log").open("wb") as out:
        process = subprocess.Popen(command, stdout=out, stderr=out, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    try:
        def homepage():
            if process.poll() is not None:
                raise AssertionError("Atelier exited. See " + str(folder / "atelier.log"))
            try:
                return request(base, "/")[0].decode()
            except URLError:
                return None

        assert "素材のアトリエ" in await_result(homepage, 30)
        assert "let server" in request(base, "/visual.js")[0].decode()
        listed = request(base, "/atelier/servers")[0]
        assert server.token.encode() not in listed
        assert json.loads(listed)["selected"] == server.id
        request(base, PREFIX + "/assets", status=409)
        thumbnail = json.loads(request(base, PREFIX + "/assets", headers={"X-Theoria-Server": server.id})[0])[0]["reference"]
        path = PREFIX + "/asset-refs/" + "/".join(thumbnail[key] for key in ("serverId", "assetId", "sha256")) + "/image?server=" + server.id
        assert hashlib.sha256(request(base, path)[0]).hexdigest() == thumbnail["sha256"]
        print("Atelier: static UI, connection selection, token-free list and authenticated image relay.")
        if browser_libs:
            if basic_browser:
                browser_check(base, folder, photo, browser_libs)
            if expanded_browser:
                expanded_browser(base, folder, browser_libs)
    finally:
        process.terminate()
        process.wait(timeout=15)


def gpu_checks(gpu, coordinator, photo, generated):
    assert any(node["serverId"] == gpu.id and node["hardware"] == "gpu" for node in coordinator.get("/nodes"))
    body = {"name": "GPU 素材.png", "sha256": hashlib.sha256(photo).hexdigest(), "chunks": []}
    for index in range(0, len(photo), CHUNK):
        body["chunks"].append(gpu.post("/assets/chunks", {"bytes": b64encode(photo[index:index + CHUNK]).decode()})["sha256"])
    ref = gpu.post("/assets/import", body, 201)["reference"]
    request_body = {"settings": {"operation": "stylize", "touch": "manga", "resolution": 256}, "inputs": [ref]}
    coordinator.post("/jobs/estimate", dict(request_body, settings=dict(request_body["settings"], seed=-1)), 400)
    job = coordinator.finish(coordinator.post("/jobs", request_body, 202), seconds=300)
    item = job["items"][0]
    assert job["estimate"]["hardware"] == "gpu" and item["executedOn"] == gpu.id
    recipe = recipe_packet(image(coordinator, item["output"]))
    assert recipe["effectivePrompt"] and recipe["engine"]["algorithm"] == "FLUX.2 [klein] 4B"
    assert len(recipe["engine"]["models"]) == 3
    if generated:
        request_body["settings"] = {"operation": "background", "background": "generated",
                                    "prompt": "A soft green studio gradient.", "resolution": 256}
        result = coordinator.finish(coordinator.post("/jobs", request_body, 202), seconds=300)
        assert recipe_packet(image(coordinator, result["items"][0]["output"]))["engine"]["hardware"] == "gpu"
    remote_result = gpu.finish(gpu.post("/jobs", {
        "settings": {"operation": "resize", "width": 256, "height": 256}, "inputs": [ref]}, 202))["items"][0]
    remote_ref = remote_result["output"]
    reference_path = "/asset-refs/" + "/".join(remote_ref[key] for key in ("serverId", "assetId", "sha256"))
    assert coordinator.get(reference_path + "/recipe")["id"] == remote_result["recipeId"]
    set_view = coordinator.post("/sets", {"name": "GPU サーバー素材", "assets": [remote_ref]}, 201)
    assert set_view["items"][0]["availability"] == "available"
    print("GPU: automatic dispatch to a separate CUDA node, FLUX output and remote asset references.")
    return set_view["id"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("photo", type=Path, help="PNG photo to process")
    parser.add_argument("--matting", type=Path, help="Pinned BiRefNet_lite ONNX file")
    parser.add_argument("--flux-models", type=Path, help="Directory with the three pinned FLUX files")
    parser.add_argument("--cuda-dir", type=Path, help="Directory with the CUDA runtime DLLs")
    parser.add_argument("--browser-libs", type=Path, help="Optional Python Playwright package directory for headless Edge checks")
    parser.add_argument("--configuration", default="Debug")
    args = parser.parse_args()
    photo = args.photo.read_bytes()
    artifacts = ROOT / "artifacts"
    artifacts.mkdir(exist_ok=True)
    folder = Path(tempfile.mkdtemp(prefix="visual-verification-", dir=artifacts))
    print("Verification data:", folder, flush=True)
    matting = ["--Visual:MattingModel", str(args.matting.resolve())] if args.matting else []
    with contextlib.ExitStack() as cleanup:
        gpu = None
        if args.flux_models:
            gpu = Server("cuda", folder, [
                *matting, "--Visual:FluxModels", str(args.flux_models.resolve())
            ], args.configuration, args.cuda_dir)
            cleanup.callback(lambda: gpu.stop() if gpu.process.poll() is None else None)
        settings = [*matting, "--Visual:EnableGpu=false"]
        if gpu:
            settings += ["--Visual:Peers:0:Connection", str(gpu.file), "--Visual:Peers:0:Hardware", "gpu"]
        cpu = Server("cpu", folder, settings, args.configuration)
        cleanup.callback(lambda: cpu.stop() if cpu.process.poll() is None else None)
        _, set_id = cpu_checks(cpu, photo, args.matting)
        atelier_check(cpu, folder, args.configuration, photo, args.browser_libs)
        if gpu:
            remote_set = gpu_checks(gpu, cpu, photo, args.matting)
            gpu.stop()
            assert cpu.get("/sets/" + remote_set)["items"][0]["availability"] == "unavailable"
        cpu.stop()
        cpu.file.unlink()
        cpu.start()
        assert cpu.get("/sets/" + set_id)["state"] == "approved"
        for server in [cpu, gpu]:
            if server:
                assert server.token.encode() not in server.log.read_bytes(), server.log
    print("Persistent approval and log redaction verified.")


if __name__ == "__main__":
    main()
