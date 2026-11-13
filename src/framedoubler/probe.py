"""Input analysis: stream metadata and exact per-frame timestamps."""

import json
import subprocess
from dataclasses import dataclass, field
from fractions import Fraction
from pathlib import Path

from framedoubler.ffmpeg import ffprobe_path

_HDR_TRANSFERS = {"smpte2084", "arib-std-b67"}


class ProbeError(RuntimeError):
    pass


@dataclass(frozen=True)
class AudioStream:
    index: int
    codec: str
    channels: int
    sample_rate: int


@dataclass(frozen=True)
class VideoInfo:
    path: Path
    width: int
    height: int
    codec: str
    pix_fmt: str
    bit_depth: int
    time_base: Fraction  # seconds per PTS tick
    fps: Fraction  # average frame rate measured from the timestamps
    nominal_fps: Fraction  # frame rate declared by the container
    is_vfr: bool
    frame_count: int
    duration: Fraction  # seconds, last frame end minus first frame start
    color_range: str | None
    color_space: str | None
    color_transfer: str | None
    color_primaries: str | None
    is_hdr: bool
    audio: tuple[AudioStream, ...]
    # Presentation timestamps in time_base ticks, ascending, one per frame.
    pts: tuple[int, ...] = field(repr=False)

    def seconds(self, index: int) -> Fraction:
        """Presentation time of frame `index` relative to the first frame."""
        return (self.pts[index] - self.pts[0]) * self.time_base


def _run(args: list[str]) -> dict:
    proc = subprocess.run(
        [ffprobe_path(), "-v", "error", "-of", "json", *args],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    if proc.returncode != 0:
        raise ProbeError(proc.stderr.strip() or "ffprobe failed")
    return json.loads(proc.stdout)


def _fraction(value: str | None) -> Fraction:
    if not value or value in ("0/0", "N/A"):
        return Fraction(0)
    return Fraction(value)


def _bit_depth(stream: dict) -> int:
    raw = stream.get("bits_per_raw_sample")
    if raw and raw.isdigit() and int(raw) > 0:
        return int(raw)
    pix_fmt = stream.get("pix_fmt", "")
    for depth in (16, 14, 12, 10):
        if f"p{depth}" in pix_fmt:
            return depth
    return 8


def probe(path: str | Path) -> VideoInfo:
    path = Path(path)
    if not path.is_file():
        raise ProbeError(f"File not found: {path}")

    streams = _run(["-show_streams", str(path)]).get("streams", [])
    video = next((s for s in streams if s.get("codec_type") == "video"
                  and not s.get("disposition", {}).get("attached_pic")), None)
    if video is None:
        raise ProbeError(f"No video stream in {path}")

    # Packet timestamps are read without decoding, so this stays fast on long files.
    packets = _run([
        "-select_streams", f"{video['index']}", "-show_entries", "packet=pts,duration", str(path),
    ]).get("packets", [])
    timed = sorted((int(p["pts"]), int(p.get("duration") or 0)) for p in packets if "pts" in p)
    if not timed:
        raise ProbeError(f"No timestamped video frames in {path}")
    pts = tuple(p for p, _ in timed)

    time_base = _fraction(video.get("time_base"))
    nominal_fps = _fraction(video.get("r_frame_rate")) or _fraction(video.get("avg_frame_rate"))
    deltas = [b - a for a, b in zip(pts, pts[1:])]
    last_duration = timed[-1][1] or (min(deltas) if deltas else 0)
    if last_duration <= 0 and nominal_fps:
        last_duration = round(1 / (nominal_fps * time_base))
    span = (pts[-1] - pts[0] + last_duration) * time_base
    fps = Fraction(len(pts)) / span if span > 0 else nominal_fps
    # One tick of jitter is tolerated: containers round timestamps to their time base.
    is_vfr = bool(deltas) and max(deltas) - min(deltas) > 1

    transfer = video.get("color_transfer")
    return VideoInfo(
        path=path,
        width=int(video["width"]),
        height=int(video["height"]),
        codec=video.get("codec_name", ""),
        pix_fmt=video.get("pix_fmt", ""),
        bit_depth=_bit_depth(video),
        time_base=time_base,
        fps=fps if is_vfr or not nominal_fps else nominal_fps,
        nominal_fps=nominal_fps,
        is_vfr=is_vfr,
        frame_count=len(pts),
        duration=span,
        color_range=video.get("color_range"),
        color_space=video.get("color_space"),
        color_transfer=transfer,
        color_primaries=video.get("color_primaries"),
        is_hdr=transfer in _HDR_TRANSFERS,
        audio=tuple(
            AudioStream(int(s["index"]), s.get("codec_name", ""), int(s.get("channels", 0)),
                        int(s.get("sample_rate", 0)))
            for s in streams if s.get("codec_type") == "audio"
        ),
        pts=pts,
    )
