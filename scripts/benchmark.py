"""Measure representative clips sequentially; write results outside the repository."""

import argparse
import ctypes
import json
import shutil
import statistics
import subprocess
import sys
import threading
from ctypes import wintypes
from dataclasses import asdict
from pathlib import Path

import torch

from framedoubler.decode import iter_frames
from framedoubler.interp.rife import RifeInterpolator
from framedoubler.pipeline import Options, run
from framedoubler.probe import probe

MIB = 1024 * 1024


class MemoryCounters(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("faults", wintypes.DWORD)] + [
        (name, ctypes.c_size_t) for name in ("peak_rss", "rss", "peak_paged", "paged", "peak_nonpaged",
                                          "nonpaged", "pagefile", "peak_pagefile", "private")]


def memory_mb():
    if sys.platform != "win32":
        return {}
    memory = MemoryCounters()
    memory.cb = ctypes.sizeof(memory)
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(MemoryCounters), wintypes.DWORD]
    if not psapi.GetProcessMemoryInfo(kernel.GetCurrentProcess(), ctypes.byref(memory), memory.cb):
        raise ctypes.WinError(ctypes.get_last_error())
    return {"rss_mb": memory.rss / MIB, "private_mb": memory.private / MIB}


class GpuSampler:
    """Device-wide counters include desktop/other applications; never stop them."""
    def __init__(self):
        self.samples = []
        self.process = None
        self.thread = None

    def __enter__(self):
        executable = shutil.which("nvidia-smi")
        if executable:
            self.process = subprocess.Popen([
                executable, "--id=0", "--query-gpu=utilization.gpu,utilization.encoder,utilization.decoder,memory.used",
                "--format=csv,noheader,nounits", "-lms", "200"],
                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)

            def collect():
                for line in self.process.stdout:
                    try:
                        values = list(map(float, line.strip().split(",")))
                    except ValueError:
                        continue
                    if len(values) == 4:
                        self.samples.append(values)

            self.thread = threading.Thread(target=collect, daemon=True)
            self.thread.start()
        return self

    def __exit__(self, *args):
        if self.process:
            self.process.terminate()
            self.process.wait(timeout=5)
            self.thread.join(timeout=5)
            self.process.stdout.close()


def warm(engine, info):
    frames = [frame for _, frame in iter_frames(info, max_frames=3)]
    engine.interpolate(engine.prepare(frames[0]), engine.prepare(frames[-1]), 0.5)
    torch.cuda.synchronize()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("inputs", nargs="+", type=Path)
    parser.add_argument("--out-dir", required=True, type=Path)
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--label", default="baseline")
    parser.add_argument("--frames", type=int)
    parser.add_argument("--repeats", type=int, default=1)
    args = parser.parse_args()
    args.out_dir.mkdir(parents=True, exist_ok=True)
    report = {"label": args.label, "python": sys.version, "torch": torch.__version__,
              "device": torch.cuda.get_device_name(), "gpu_counter_scope": "device-wide, 200ms; includes other apps",
              "runs": []}
    engine = RifeInterpolator()
    for path in args.inputs:
        info = probe(path)
        warm(engine, info)
        for repeat in range(args.repeats):
            records = []
            torch.cuda.reset_peak_memory_stats()

            def progress(event):
                if event.stage == "convert":
                    records.append({"frame": event.frames_out, **memory_mb(),
                                    "cuda_allocated_mb": torch.cuda.memory_allocated() / MIB})

            output = args.out_dir / f"{args.label}_{info.height}_{repeat + 1}.mp4"
            with GpuSampler() as gpu:
                result = run(path, output, Options(profile=True, max_frames=args.frames, overwrite=True),
                             interpolator=engine, on_progress=progress)
            row = {"input": str(path), "output": str(output), "resolution": [info.width, info.height],
                   "frames": result.frames_out, "interpolated": result.interpolated,
                   "elapsed": result.elapsed, "output_fps": result.frames_out / result.elapsed,
                   "timings": asdict(result.timings), "memory_samples": records,
                   "cuda_peak_allocated_mb": torch.cuda.max_memory_allocated() / MIB,
                   "cuda_peak_reserved_mb": torch.cuda.max_memory_reserved() / MIB,
                   "gpu_samples": gpu.samples}
            if gpu.samples:
                row["gpu_means"] = dict(zip(("gpu_percent", "encoder_percent", "decoder_percent", "device_memory_mb"),
                                            (statistics.mean(values) for values in zip(*gpu.samples))))
            report["runs"].append(row)
            args.json.parent.mkdir(parents=True, exist_ok=True)
            args.json.write_text(json.dumps(report, indent=2), encoding="utf-8")
            print(json.dumps({key: row[key] for key in ("resolution", "elapsed", "output_fps", "timings")}), flush=True)


if __name__ == "__main__":
    main()
