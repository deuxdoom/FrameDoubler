"""Output verification: frame rate, length, audio and leftover duplicate frames."""

import json
import subprocess
from dataclasses import dataclass
from fractions import Fraction
from pathlib import Path

from framedoubler.cadence import classify, summarize
from framedoubler.decode import iter_frames
from framedoubler.ffmpeg import ffprobe_path
from framedoubler.probe import probe
from framedoubler.timeline import default_output_fps, source_rate

MAX_DUP_RATIO = 0.01


@dataclass(frozen=True)
class Report:
    fps: Fraction
    expected_fps: Fraction
    frames: int
    duration_diff: float  # output minus input, seconds
    frame_time: float  # one output frame, seconds
    audio_diff: float | None  # output minus input audio length, seconds; None without audio
    dup_ratio: float  # duplicates among moving frames of the output
    input_dup_ratio: float
    static: int

    @property
    def problems(self) -> list[str]:
        found = []
        if self.fps != self.expected_fps:
            found.append(f"frame rate is {float(self.fps):.3f}, expected {float(self.expected_fps):.3f}")
        if abs(self.duration_diff) > self.frame_time * 1.01:
            found.append(f"length differs from the input by {self.duration_diff * 1000:.1f} ms")
        if self.audio_diff is not None and abs(self.audio_diff) > 0.05:
            found.append(f"audio length differs from the input by {self.audio_diff * 1000:.1f} ms")
        if self.dup_ratio > MAX_DUP_RATIO:
            found.append(f"{self.dup_ratio:.1%} of moving frames are still duplicates")
        return found

    @property
    def ok(self) -> bool:
        return not self.problems


def _audio_duration(path: Path) -> float | None:
    proc = subprocess.run(
        [ffprobe_path(), "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=duration",
         "-of", "json", str(path)], capture_output=True, text=True)
    streams = json.loads(proc.stdout or "{}").get("streams", [])
    value = streams[0].get("duration") if streams else None
    return float(value) if value not in (None, "N/A") else None


def verify(input_path: str | Path, output_path: str | Path,
           expected_fps: Fraction | None = None) -> Report:
    source, out = probe(input_path), probe(output_path)
    expected = expected_fps or default_output_fps(source_rate(source.fps, source.nominal_fps, source.is_vfr))
    out_summary = summarize((label for _, _, label in classify(iter_frames(out))), out.fps)
    in_summary = summarize((label for _, _, label in classify(iter_frames(source))), source.fps)
    audio_in, audio_out = _audio_duration(source.path), _audio_duration(out.path)
    return Report(
        fps=out.fps,
        expected_fps=expected,
        frames=out.frame_count,
        duration_diff=float(out.duration - source.duration),
        frame_time=float(1 / expected),
        audio_diff=None if audio_in is None or audio_out is None else audio_out - audio_in,
        dup_ratio=out_summary.dup_ratio,
        input_dup_ratio=in_summary.dup_ratio,
        static=out_summary.static,
    )


def format_report(r: Report) -> str:
    audio = "no audio" if r.audio_diff is None else f"{r.audio_diff * 1000:+.1f} ms vs input"
    lines = [
        f"Frame rate : {float(r.fps):.3f} fps (expected {float(r.expected_fps):.3f})",
        f"Frames     : {r.frames}",
        f"Length     : {r.duration_diff * 1000:+.1f} ms vs input (one frame is {r.frame_time * 1000:.1f} ms)",
        f"Audio      : {audio}",
        f"Duplicates : {r.dup_ratio:.2%} of moving frames (input: {r.input_dup_ratio:.2%}), "
        f"{r.static} static frames",
        "Result     : " + ("OK" if r.ok else "FAILED - " + "; ".join(r.problems)),
    ]
    return "\n".join(lines)
