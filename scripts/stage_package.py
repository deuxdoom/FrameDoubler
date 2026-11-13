"""Stage notices and manifest, then zip a locally built folder candidate."""

import argparse
import hashlib
import importlib.metadata as metadata
import json
import shutil
import subprocess
import sys
import time
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LEGAL_URLS = {
    "LGPL-3.0.txt": "https://www.gnu.org/licenses/lgpl-3.0.txt",
    "GPL-3.0.txt": "https://www.gnu.org/licenses/gpl-3.0.txt",
    "CUDA-13.0-EULA.html": "https://docs.nvidia.com/cuda/archive/13.0.0/eula/index.html",
    "cuDNN-EULA.html": "https://docs.nvidia.com/deeplearning/cudnn/latest/reference/eula.html",
}


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def fetch_notice(url, cache):
    """Keep a verified local snapshot and tolerate transient documentation outages."""
    key = hashlib.sha256(url.encode()).hexdigest()
    target, record = cache / key, cache / f"{key}.json"
    try:
        saved = json.loads(record.read_text(encoding="utf-8"))
        if saved["url"] == url and digest(target) == saved["sha256"]:
            return target.read_bytes()
    except (OSError, ValueError, KeyError):
        pass
    cache.mkdir(parents=True, exist_ok=True)
    print(f"Downloading license: {url}", flush=True)
    for attempt in range(3):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 (compatible; FrameDoubler-build/0.1)"})
            with urllib.request.urlopen(request, timeout=30) as response:
                data = response.read()
            if len(data) < 1000:
                raise OSError("License response is unexpectedly short.")
            partial = target.with_suffix(".part")
            partial.write_bytes(data)
            partial.replace(target)
            record.write_text(json.dumps({"url": url, "sha256": digest(target)}), encoding="utf-8")
            return data
        except OSError:
            if attempt == 2:
                raise
            time.sleep(attempt + 1)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--legal-cache", type=Path, default=Path(r"F:\Temp\FrameDoubler\license-cache"))
    args = parser.parse_args()
    bundle = args.bundle.resolve()
    if ((bundle.is_relative_to(ROOT) and not bundle.is_relative_to(ROOT / "dist"))
            or not (bundle / "FrameDoubler.exe").is_file()):
        raise RuntimeError("Use a built package outside the source repository or under dist.")
    tools = bundle / "tools/ffmpeg"
    shutil.copytree(ROOT / "tools/ffmpeg/bin", tools / "bin", dirs_exist_ok=True)
    shutil.copyfile(ROOT / "tools/ffmpeg/LICENSE.txt", tools / "LICENSE.txt")
    for name in ("LICENSE", "THIRD_PARTY_NOTICES.md", "README.md", "README.ko.md", "plan.md", "todo.md"):
        shutil.copyfile(ROOT / name, bundle / name)
    (bundle / "docs").mkdir(exist_ok=True)
    shutil.copyfile(ROOT / "docs/distribution.md", bundle / "docs/distribution.md")
    shutil.copyfile(ROOT / "docs/distribution.md", bundle / "DISTRIBUTION.md")
    licenses = bundle / "licenses"
    licenses.mkdir(exist_ok=True)
    shutil.copyfile(Path(sys.base_prefix) / "LICENSE.txt", licenses / "Python-LICENSE.txt")
    inventory = {}
    for dist in metadata.distributions():
        name = dist.metadata["Name"]
        if name.lower() in {"pip", "framedoubler", "pytest", "iniconfig", "pluggy", "pygments", "colorama"}:
            continue
        notices = []
        for entry in dist.files or ():
            if Path(entry).name.lower().startswith(("license", "copying", "notice")):
                source = Path(dist.locate_file(entry))
                if not source.is_file():
                    continue
                relative = Path(*[part for part in Path(entry).parts if part not in ("..", ".")])
                target = licenses / name / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, target)
                notices.append(str(target.relative_to(bundle)))
        inventory[name] = {"version": dist.version, "notices": notices}
    # The GUI wheels expose their LGPL license in metadata but only ship a
    # commercial-license notice. Include the actual LGPL/GPL texts separately.
    for name, url in LEGAL_URLS.items():
        (licenses / name).write_bytes(fetch_notice(url, args.legal_cache))
    manifest = {
        "status": "LOCAL CANDIDATE: clean Windows 11 and redistribution review pending",
        "python": sys.version,
        "source_commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
        "source_dirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip()),
        "ffmpeg": subprocess.check_output([str(tools / "bin/ffmpeg.exe"), "-version"], text=True).splitlines()[0],
        "packages_in_build_environment": inventory,
        "additional_license_sources": LEGAL_URLS,
        "files": {str(p.relative_to(bundle)).replace("\\", "/"): digest(p)
                  for p in sorted(bundle.rglob("*")) if p.is_file() and p.name != "manifest.json"},
    }
    (bundle / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    archive = bundle.parent / "FrameDoubler-windows-x64.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=1, allowZip64=True) as output:
        for path in sorted(bundle.rglob("*")):
            if path.is_file():
                output.write(path, path.relative_to(bundle.parent))
    checksum = archive.with_suffix(".zip.sha256")
    checksum.write_text(f"{digest(archive)}  {archive.name}\n", encoding="ascii")
    print(f"Archive: {archive}\nSHA-256: {checksum}")


if __name__ == "__main__":
    main()
