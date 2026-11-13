"""Input analysis report: what the file is and how it will be processed."""

from dataclasses import dataclass
from collections.abc import Callable
from contextlib import closing
from fractions import Fraction
from pathlib import Path

from framedoubler.cadence import CadenceSummary, classify, summarize
from framedoubler.decode import iter_frames
from framedoubler.probe import VideoInfo, probe
from framedoubler.timeline import default_output_fps, source_rate

# Frames examined for the cadence estimate (15 s of a 60fps file).
SAMPLE_FRAMES = 900


@dataclass(frozen=True)
class Analysis:
    info: VideoInfo
    cadence: CadenceSummary
    output_fps: Fraction

    @property
    def motion_fps(self) -> float | None:
        if self.cadence.unique <= 1 and self.cadence.static:
            return None
        return self.cadence.motion_fps

    @property
    def kind(self) -> str:
        if self.cadence.has_duplicates:
            return "duplicated"  # low frame rate content stored at a higher container rate
        if self.info.fps > 45 and not self.info.is_vfr:
            return "native-high"  # already real 60fps-class motion
        return "native-low"  # real 30fps-class motion


def analyze(path: str | Path, sample_frames: int = SAMPLE_FRAMES,
            check_cancelled: Callable[[], None] | None = None) -> Analysis:
    info = probe(path)
    labels = []
    with closing(iter_frames(info, max_frames=sample_frames)) as frames:
        for _, _, label in classify(frames):
            if check_cancelled:
                check_cancelled()
            labels.append(label)
    rate = source_rate(info.fps, info.nominal_fps, info.is_vfr)
    return Analysis(info, summarize(labels, info.fps), default_output_fps(rate))


def _rate(value: Fraction) -> str:
    return f"{float(value):.3f}".rstrip("0").rstrip(".") + f" fps ({value.numerator}/{value.denominator})"


def format_report(a: Analysis) -> str:
    info, c = a.info, a.cadence
    audio = ", ".join(f"{s.codec} {s.channels}ch {s.sample_rate} Hz" for s in info.audio) or "none"
    plans = {
        "duplicated": "remove duplicate frames, then interpolate between the remaining frames",
        "native-low": "interpolate between every pair of frames",
        "native-high": "motion is already at the container rate; frames pass through unchanged",
    }
    lines = [
        f"File        : {info.path.name}",
        f"Video       : {info.width}x{info.height} {info.codec} {info.pix_fmt} ({info.bit_depth}-bit, "
        f"{'HDR' if info.is_hdr else 'SDR'}, {info.color_space or 'untagged'})",
        f"Frame rate  : {_rate(info.fps)}, {'variable (VFR)' if info.is_vfr else 'constant (CFR)'}",
        f"Length      : {float(info.duration):.3f} s, {info.frame_count} frames",
        f"Audio       : {audio}",
        f"Sampled     : first {c.frames} frames -> {c.unique} new, {c.dup} duplicate, {c.static} static",
        "Motion rate : " + (f"{a.motion_fps:.2f} fps ({c.dup_ratio:.0%} of moving frames are duplicates)"
                            if a.motion_fps is not None else "not enough motion in the sampled section"),
        f"Input type  : {a.kind}",
        f"Plan        : {plans[a.kind]}",
        f"Output rate : {_rate(a.output_fps)}",
    ]
    if info.is_hdr:
        lines.append("Warning     : HDR input is not supported yet; colours will be wrong.")
    return "\n".join(lines)
