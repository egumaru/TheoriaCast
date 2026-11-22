"""Build self-contained Music archives. Requires .NET 10 and Python 3.11+."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET
import zipfile


MUSIC = Path(__file__).resolve().parent
ROOT = MUSIC.parents[1]
PROJECTS = ("TheoriaCast.Music.Api", "TheoriaCast.Music.Atelier")
RUNTIMES = ("win-x64", "linux-x64")


def digest(path, algorithm="sha256"):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, algorithm)


def notices(app_dir, project, packages):
    deps = json.loads((app_dir / (project + ".deps.json")).read_text("utf-8"))
    notice_dir = app_dir / "licenses"
    notice_dir.mkdir()
    lines = ["# Third-party licenses and notices", "",
             "Component versions and copyright notices come from the packages used for this build.", ""]
    for key, item in sorted(deps["libraries"].items()):
        if item["type"] == "project":
            continue
        name, version = key.removeprefix("runtimepack.").split("/")
        folder = packages / name.lower() / version
        archive = folder / (name.lower() + "." + version + ".nupkg")
        checksum = digest(archive, "sha512").digest()
        disk_hash = Path(str(archive) + ".sha512").read_text("ascii").strip()
        metadata = json.loads((folder / ".nupkg.metadata").read_text("utf-8-sig"))
        # NuGet's signed-package content hash differs from the hash of the signed ZIP.
        if disk_hash != base64.b64encode(checksum).decode() or (
                item.get("sha512") and item["sha512"] != "sha512-" + metadata["contentHash"]):
            raise ValueError("Package bytes differ from the resolved dependency: " + key)
        with zipfile.ZipFile(archive) as z:
            nuspec, = [f for f in z.namelist() if f.endswith(".nuspec")]
            meta = ET.fromstring(z.read(nuspec)).find("{*}metadata")
            lic = meta.find("{*}license")
            if lic is None or not lic.text:
                raise ValueError("Missing license metadata: " + key)
            copyright_text = meta.findtext("{*}copyright") or meta.findtext("{*}authors")
            repo = meta.find("{*}repository")
            lines.extend(["## " + name + " " + version, "", copyright_text,
                          "Declared license: " + lic.text, "Package SHA-512: " + checksum.hex()])
            if repo is not None and repo.get("url"):
                lines.append("Source: " + repo.get("url") + " " + repo.get("commit", ""))
            files = []
            for member in z.namelist():
                base = Path(member).name
                if member.endswith("/") or not (base.lower().startswith(("license", "notice", "copyright",
                        "copying", "third-party-notice", "thirdpartynotice", "third_party_notice"))
                        or lic.get("type") == "file" and member == lic.text):
                    continue
                target = notice_dir / name / member.replace("/", "_")
                target.parent.mkdir(exist_ok=True)
                target.write_bytes(z.read(member))
                files.append(target.relative_to(app_dir).as_posix())
            if lic.get("type") == "file" and not files:
                raise ValueError("Missing bundled license: " + key)
            if lic.get("type") == "expression":
                if lic.text == "Apache-2.0":
                    files.append("LICENSE")
                elif lic.text == "MIT":
                    files.append("licenses/MIT.txt")
                else:
                    raise ValueError("Add the text of license expression " + lic.text)
            lines.extend(["Included terms: " + ", ".join(files), ""])
    runtime_licenses = list(notice_dir.glob("Microsoft.NETCore.App.Runtime.*/LICENSE.TXT"))
    if len(runtime_licenses) != 1:
        raise ValueError("Missing .NET runtime license")
    text = runtime_licenses[0].read_text("utf-8-sig")
    terms = text[text.index("Permission is hereby granted"):]
    (notice_dir / "MIT.txt").write_text("MIT License\n\n" + terms, encoding="utf-8")
    (app_dir / "THIRD-PARTY-NOTICES.md").write_text("\n".join(lines), encoding="utf-8")


def include_materials(app_dir, project):
    for name in ("README.md", "README.en.md", "LICENSE"):
        shutil.copyfile(MUSIC / name, app_dir / name)
    skill = ROOT / ".agents/skills/theoriacast-music"
    if not (skill / "SKILL.md").is_file():
        raise FileNotFoundError(skill / "SKILL.md")
    shutil.copytree(skill, app_dir / "skills/theoriacast-music", ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    if project.endswith(".Api"):
        # These are TheoriaCast integration scripts; users provide the actual engines.
        (app_dir / "worker").mkdir(exist_ok=True)
        for name in ("configure.py", "legacy-idle.ps1", "receipt.py", "reference_generators.py",
                     "render.py", "screening.py", "singing.py"):
            shutil.copyfile(MUSIC / "worker" / name, app_dir / "worker" / name)


def archive_app(app_dir, dest, project, rid):
    paths = sorted(p for p in app_dir.rglob("*") if p.is_file())
    names = [p.relative_to(app_dir).as_posix() for p in paths]
    blocked = {"connection.json", "server-access.json", "worker.env", "worker.token", "node.json"}
    if any(Path(n).name in blocked or n.endswith((".sqlite", ".db", ".token", ".ckpt", ".safetensors",
            ".pt", ".pth", ".onnx", ".gguf", ".pdb")) for n in names):
        raise ValueError("Unexpected private data, model, or debug files in publish output")
    app = project + (".exe" if rid == "win-x64" else "")
    if app not in names:
        raise FileNotFoundError(app)
    if rid == "win-x64":
        with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED, compresslevel=4) as z:
            for path, name in zip(paths, names):
                z.write(path, name)
        with zipfile.ZipFile(dest) as z:
            bad = z.testzip()
            if bad:
                raise ValueError("ZIP integrity error: " + bad)
    else:
        with tarfile.open(dest, "w:gz", compresslevel=4) as tar:
            for path, name in zip(paths, names):
                info = tar.gettarinfo(str(path), arcname=name)
                info.mode = 0o755 if name in (app, "createdump") else 0o644
                info.uid = info.gid = 0
                info.uname = info.gname = ""
                with path.open("rb") as f:
                    tar.addfile(info, f)
        with tarfile.open(dest, "r:gz") as tar:
            if tar.getnames() != names or not tar.getmember(app).mode & 0o111:
                raise ValueError("TAR integrity error")


def main():
    p = argparse.ArgumentParser(__doc__)
    p.add_argument("--dotnet", default="dotnet")
    p.add_argument("--nuget-config", type=Path)
    p.add_argument("--packages", type=Path, default=Path(os.environ.get("NUGET_PACKAGES", Path.home()/".nuget/packages")))
    p.add_argument("--output", type=Path, default=ROOT/"artifacts/music-release")
    args = p.parse_args()
    packages = args.packages.resolve()
    dotnet = str(Path(shutil.which(args.dotnet) or args.dotnet).resolve())
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".music-pack-", dir=output) as temp:
        work = Path(temp)
        sums = []
        for rid in RUNTIMES:
            for project in PROJECTS:
                print("Publishing", project, rid, flush=True)
                file = ROOT / "src" / project / (project + ".csproj")
                build = work / "build"
                common = [str(file), "-r", rid, "-p:ArtifactsPath=" + str(build),
                          "-p:RestorePackagesPath=" + str(packages), "-p:SelfContained=true"]
                restore = [dotnet, "restore", *common]
                if args.nuget_config:
                    restore += ["--configfile", str(args.nuget_config.resolve())]
                subprocess.run(restore, cwd=ROOT, check=True)
                with tempfile.TemporaryDirectory(prefix="publish-", dir=work) as stage:
                    app_dir = Path(stage)
                    subprocess.run([dotnet, "publish", *common, "-c", "Release", "--no-restore",
                                    "--self-contained", "true", "-p:DebugSymbols=false", "-p:DebugType=None",
                                    "-o", str(app_dir)], cwd=ROOT, check=True)
                    include_materials(app_dir, project)
                    notices(app_dir, project, packages)
                    suffix = ".zip" if rid == "win-x64" else ".tar.gz"
                    name = project + "-" + rid + suffix
                    archive_app(app_dir, work / name, project, rid)
                    sums.append((digest(work / name).hexdigest(), name))
                shutil.rmtree(build)
        (work / "SHA256SUMS").write_text("".join(h + "  " + n + "\n" for h, n in sums), encoding="ascii")
        for name in [n for _, n in sums] + ["SHA256SUMS"]:
            (work / name).replace(output / name)
        print(output, flush=True)


if __name__ == "__main__":
    main()
