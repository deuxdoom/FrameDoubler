"""Development harness for actual 30->60fps files. C++ owns native interpolation.

FFmpeg executables are the project's existing tools, not part of the engine PoC.
The input is every other frame of the sole actual 60fps/30-motion capture.
"""
import argparse
from contextlib import closing
from dataclasses import asdict
from fractions import Fraction
import json
from pathlib import Path
import subprocess
import time

import numpy as np

from framedoubler.decode import iter_frames
from framedoubler.encode import Encoder, EncodeSettings
from framedoubler.ffmpeg import ffmpeg_path
from framedoubler.pipeline import Options, run
from framedoubler.probe import probe
from framedoubler.verify import verify


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--work", type=Path, default=Path(r"F:\Temp\FrameDoubler\ncnn-poc"))
    parser.add_argument("--model", choices=("v4.25", "v4.26"), required=True)
    parser.add_argument("--size", default="1920x1080")
    args = parser.parse_args()
    width, height = map(int, args.size.split("x"))
    work = args.work / "videos"
    work.mkdir(exist_ok=True)
    source = work / f"actual-30-{height}.mp4"
    if not source.exists():
        capture = Path(r"F:\FrameDoubler\samples\Fire Emblem Fortunes Weave 30fps.mp4")
        subprocess.run([ffmpeg_path(), "-v", "error", "-y", "-i", str(capture),
                        "-vf", f"select='not(mod(n,2))',setpts=N/(30*TB),scale={width}:{height}",
                        "-an", "-r", "30", "-c:v", "hevc_nvenc", "-preset", "p5", "-cq", "10", "-b:v", "0",
                        "-pix_fmt", "yuv420p", "-colorspace", "bt709", "-color_primaries", "bt709",
                        "-color_trc", "bt709", "-color_range", "tv", str(source)], check=True)
    info = probe(source)
    scale = .5 if height > 1440 else 1.
    native_output = work / f"ncnn-{args.model}-{height}-60.mp4"
    torch_output = work / f"torch-{args.model}-{height}-60.mp4"
    settings = EncodeSettings(quality=19)
    with (work / f"native-{args.model}-{height}.log").open("wb") as log:
        start = time.perf_counter()
        decoder = subprocess.Popen([ffmpeg_path(), "-v", "error", "-i", str(source),
                                    "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "rawvideo",
                                    "-pix_fmt", "rgb24", "-"], stdout=subprocess.PIPE, stderr=log)
        try:
            with Encoder(native_output, info, Fraction(60), settings=settings) as encoder:
                # No Python frame handling or inference on this path: OS pipes connect
                # FFmpeg -> independent C++ engine -> unchanged FFmpeg encoder.
                native = subprocess.Popen([
                    str(args.work / "deploy-vulkan/rife-poc.exe"),
                    str(args.work / "models" / f"{args.model}-vulkan{'-s05' if scale == .5 else ''}"),
                    str(width), str(height), "fp16", "gpu", str(scale), "sequence"],
                    stdin=decoder.stdout, stdout=encoder._proc.stdin, stderr=log)
                decoder.stdout.close()
                if native.wait() != 0:
                    raise RuntimeError("Native conversion failed; see worker log")
                if decoder.wait() != 0:
                    raise RuntimeError("Decode failed")
        finally:
            if decoder.poll() is None:
                decoder.terminate()
                decoder.wait()
        native_seconds = time.perf_counter() - start
    start = time.perf_counter()
    baseline = run(source, torch_output, Options(model=args.model, fps=Fraction(60), dedup=False,
                                                overwrite=True, encode=settings))
    torch_seconds = time.perf_counter() - start
    reports = {}
    for label, output in (("ncnn", native_output), ("torch", torch_output)):
        metadata = probe(output)
        check = verify(source, output, expected_fps=Fraction(60))
        # Require actual newly generated frames, in addition to fps/count checks.
        with closing(iter_frames(metadata, max_frames=3)) as frames:
            first = [frame.copy() for _, frame in frames]
        reports[label] = {"frames": len(metadata.pts), "fps": str(metadata.fps),
                          "verification": asdict(check), "ok": check.ok, "problems": check.problems,
                          "output": str(output),
                          "first_middle_mae": float(np.abs(first[0].astype(float) - first[1]).mean()),
                          "middle_next_mae": float(np.abs(first[1].astype(float) - first[2]).mean())}
        if not check.ok or len(metadata.pts) != len(info.pts) * 2 or metadata.fps != 60:
            raise RuntimeError("Incorrect native output timeline")
    result = {"model": args.model, "size": args.size, "scale": scale, "source_frames": len(info.pts),
              "source_fps": str(info.fps), "duration": str(info.duration),
              "native_seconds": native_seconds, "torch_seconds": torch_seconds,
              "torch_interpolated": baseline.interpolated, "reports": reports,
              "limitations": "Single 8-second SDR capture; audio intentionally excluded; no ground truth 60fps."}
    path = work / f"video-{args.model}-{height}.json"
    path.write_text(json.dumps(result, indent=2, default=str), encoding="utf-8")
    print(json.dumps(result, default=str), flush=True)


if __name__ == "__main__":
    main()
