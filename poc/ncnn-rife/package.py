"""Stage only the C++ engine, converted ncnn models and notices (development tool)."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--work", type=Path, default=Path(r"F:\Temp\FrameDoubler\ncnn-poc"))
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    dest = args.output or args.work / "package" / "FrameDoubler-ncnn-PoC"
    if dest.exists():
        raise RuntimeError(f"Use an empty output directory: {dest}")
    dest.mkdir(parents=True)
    shutil.copy2(args.work / "deploy-vulkan/rife-poc.exe", dest)
    for model in ("v4.25", "v4.26"):
        target = dest / "models" / model
        target.mkdir(parents=True)
        original = args.work / "models" / f"{model}-vulkan"
        half_scale = args.work / "models" / f"{model}-vulkan-s05"
        if hashlib.sha256((original / "rife.ncnn.bin").read_bytes()).digest() != hashlib.sha256((half_scale / "rife.ncnn.bin").read_bytes()).digest():
            raise RuntimeError("Scale variants unexpectedly have different weights")
        for name in ("rife.ncnn.param", "rife.ncnn.bin", "provenance.json"):
            shutil.copy2(original / name, target / name)
        shutil.copy2(half_scale / "rife.ncnn.param", target / "rife-s05.ncnn.param")
        shutil.copy2(half_scale / "provenance.json", target / "provenance-s05.json")
    notices = dest / "licenses"
    notices.mkdir()
    shutil.copy2(root / "LICENSE", notices / "FrameDoubler-MIT.txt")
    practical = (root / "THIRD_PARTY_NOTICES.md").read_text(encoding="utf-8").split("## Practical-RIFE notice")[1].split("```")[1].strip().removeprefix("text\n")
    (notices / "Practical-RIFE-MIT.txt").write_text(practical + "\n", encoding="utf-8")
    native = args.work / "native/ncnn-20261020"
    shutil.copy2(native / "LICENSE.txt", notices / "ncnn.txt")
    shutil.copy2(native / "glslang/LICENSE.txt", notices / "glslang.txt")
    shutil.copytree(native / "glslang/LICENSES", notices / "glslang")
    compiler = args.work / "compiler/llvm-mingw-20261104-ucrt-x86_64"
    shutil.copy2(compiler / "LICENSE.TXT", notices / "LLVM-runtime.txt")
    for path in (compiler / "x86_64-w64-mingw32/share/mingw32").glob("COPYING*"):
        shutil.copy2(path, notices / path.name)
    (dest / "README.txt").write_text(
        "FrameDoubler independent C++ ncnn/Vulkan compatibility PoC. Not a GUI app.\n"
        "Requires Windows x64 and a Vulkan-capable GPU driver (tested RTX 4080 SUPER).\n"
        "No Python, PyTorch, CUDA runtime, FFmpeg or .NET is bundled or required by this engine.\n"
        "Raw RGB8 30->60fps stream: rife-poc models/v4.25 WIDTH HEIGHT fp16 gpu SCALE sequence\n"
        "Use scale 1 at 1080p/1440p, 0.5 at 4K. Model variants share identical weights.\n"
        "stdin/stdout are binary raw RGB24; use an external decoder and encoder.\n"
        "fp16 means storage only; arithmetic is FP32. Last source frame is held twice.\n"
        "Converted from official MIT Practical-RIFE weights. No Video2X/librife code or assets.\n"
        "See licenses and repository poc/ncnn-rife for sources and validation limitations.\n", encoding="utf-8")
    files = [{"path": p.relative_to(dest).as_posix(), "bytes": p.stat().st_size,
              "sha256": hashlib.sha256(p.read_bytes()).hexdigest()}
             for p in sorted(dest.rglob("*")) if p.is_file()]
    manifest = {"ncnn_tag": "20261020", "glslang_commit": "780b6a209132dc74e99458f62ebe5fa7e57c41cf",
                "compiler": "llvm-mingw-20261104-ucrt-x86_64", "files": files}
    (dest / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps({"directory": str(dest), "total_bytes": sum(p.stat().st_size for p in dest.rglob("*") if p.is_file())}))


if __name__ == "__main__":
    main()
