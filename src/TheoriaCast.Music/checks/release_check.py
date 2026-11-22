"""Extract and run both Music release archives for the current OS, using only loopback."""
import argparse
from contextlib import ExitStack
import hashlib
import json
import os
from pathlib import Path
import socket
import subprocess
import tarfile
import tempfile
import time
import urllib.request
import zipfile


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def wait(test, process):
    for _ in range(160):
        if process.poll() is not None:
            raise RuntimeError("Packaged process exited with " + str(process.returncode))
        try:
            if test():
                return
        except OSError:
            pass
        time.sleep(.25)
    raise TimeoutError("Packaged process did not become ready")


def start(stack, path, args, cwd):
    env = dict(os.environ, DOTNET_ROOT=str(cwd/"no-global-dotnet"), DOTNET_MULTILEVEL_LOOKUP="0")
    proc = subprocess.Popen([str(path), *args], cwd=cwd, env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    def stop():
        if proc.poll() is None:
            proc.terminate()
        try:
            proc.wait(10)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()
    stack.callback(stop)
    return proc


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("release_dir", type=Path)
    args = p.parse_args()
    windows = os.name == "nt"
    target = "win-x64.zip" if windows else "linux-x64.tar.gz"
    sums = {name: checksum for checksum, name in (line.split() for line in
            (args.release_dir/"SHA256SUMS").read_text("ascii").splitlines())}
    with tempfile.TemporaryDirectory(prefix="theoria-release-check-") as tmp, ExitStack() as stack:
        root = Path(tmp)
        apps = {}
        for app in ("Api", "Atelier"):
            project = "TheoriaCast.Music." + app
            archive = args.release_dir / (project + "-" + target)
            with archive.open("rb") as f:
                if hashlib.file_digest(f, "sha256").hexdigest() != sums[archive.name]:
                    raise ValueError("Archive checksum mismatch: " + archive.name)
            folder = root / app
            if windows:
                with zipfile.ZipFile(archive) as z:
                    z.extractall(folder)
            else:
                with tarfile.open(archive, "r:gz") as tar:
                    tar.extractall(folder, filter="data")
            for name in ("README.md", "README.en.md", "LICENSE", "THIRD-PARTY-NOTICES.md",
                         "skills/theoriacast-music/SKILL.md", "skills/theoriacast-music/references/ja.md"):
                assert (folder/name).stat().st_size > 0, name
            assert len(list((folder/"licenses").glob("*/THIRD-PARTY-NOTICES.TXT"))) >= 2
            apps[app] = folder / (project + (".exe" if windows else ""))
        descriptor = root/"connection.json"
        api_port = free_port()
        api = start(stack, apps["Api"], ["--urls", "http://127.0.0.1:" + str(api_port),
                    "--connection-file", str(descriptor), "--Music:Data", str(root/"data")], root)
        wait(lambda: descriptor.is_file() and descriptor.stat().st_size > 0, api)
        gui_port = free_port()
        gui = start(stack, apps["Atelier"], ["--port", str(gui_port), "--profile", str(root/"profile"),
                    "--import", str(descriptor)], root)
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        def get(path):
            with opener.open("http://localhost:" + str(gui_port) + path, timeout=3) as response:
                return response.read()
        wait(lambda: json.loads(get("/atelier/servers"))["selected"], gui)
        assert b"TheoriaCast.Music.Atelier" in get("/")
        assert json.loads(get("/jobs")) == []
        assert len(json.loads(get("/nodes"))) == 2
        print(target + ": archive hash, notices, manuals, skill, isolated startup, connection import, GUI and API relay passed")


if __name__ == "__main__":
    main()
