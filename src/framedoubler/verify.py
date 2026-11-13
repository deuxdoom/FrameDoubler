"""Output verification: frame rate, length, audio and leftover duplicate frames."""

from dataclasses import dataclass
from collections.abc import Callable
from contextlib import closing
from fractions import Fraction
from pathlib import Path

from framedoubler.cadence import CadenceConfig, classify, summarize
from framedoubler.decode import iter_frames
from framedoubler.probe import VideoInfo, _run, probe
from framedoubler.timeline import default_output_fps, source_rate

MAX_DUP_RATIO = 0.01


@dataclass(frozen=True)
class SyncPoint:
    track: int
    fraction: Fraction
    error: Fraction


@dataclass(frozen=True)
class AudioTimeline:
    segments: tuple[tuple[Fraction, Fraction], ...]

    @property
    def duration(self) -> Fraction:
        return sum((length for _, length in self.segments), Fraction(0))

    def time_at(self, offset: Fraction) -> Fraction:
        for start, length in self.segments:
            if offset <= length:
                return start + offset
            offset -= length
        start, length = self.segments[-1]
        return start + length + offset


def _audio_timelines(info: VideoInfo) -> tuple[AudioTimeline, ...]:
    if not info.audio:
        return ()
    frames = _run(["-select_streams", "a", "-show_frames", "-show_entries",
                   "frame=stream_index,pts,nb_samples,sample_rate", str(info.path)]).get("frames", [])
    origin = min([info.pts[0] * info.time_base, *(stream.start for stream in info.audio)])
    timelines = []
    for stream in info.audio:
        segments = []
        if stream.start > origin:
            # A late-starting track represents silence before its first sample.
            segments.append((origin, stream.start - origin))
        for frame in frames:
            if frame["stream_index"] != stream.index or "pts" not in frame:
                continue
            start = int(frame["pts"]) * stream.time_base
            end = start + Fraction(int(frame.get("nb_samples", 0)), stream.sample_rate)
            # Exclude AAC priming and padding outside the declared playback window.
            start = max(start, stream.start)
            if stream.duration is not None:
                end = min(end, stream.start + stream.duration)
            if end > start:
                segments.append((start, end - start))
        if not segments:
            raise RuntimeError(f"No timed audio packets in track {stream.index} of {info.path}")
        timelines.append(AudioTimeline(tuple(sorted(segments))))
    return tuple(timelines)


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
    sync: tuple[SyncPoint, ...] = ()
    audio_tracks_match: bool = True

    @property
    def problems(self) -> list[str]:
        found = []
        if self.fps != self.expected_fps:
            found.append(f"frame rate is {float(self.fps):.3f}, expected {float(self.expected_fps):.3f}")
        if abs(self.duration_diff) > self.frame_time * 1.01:
            found.append(f"length differs from the input by {self.duration_diff * 1000:.1f} ms")
        if self.audio_diff is not None and abs(self.audio_diff) > self.frame_time * 1.01:
            found.append(f"audio length differs from the input by {self.audio_diff * 1000:.1f} ms")
        if not self.audio_tracks_match:
            found.append("audio track count differs from the input")
        worst = max((abs(point.error) for point in self.sync), default=Fraction(0))
        if float(worst) > self.frame_time * 1.01:
            found.append(f"audio/video timing changed by up to {float(worst) * 1000:.1f} ms")
        if self.dup_ratio > MAX_DUP_RATIO:
            found.append(f"{self.dup_ratio:.1%} of moving frames are still duplicates")
        return found

    @property
    def ok(self) -> bool:
        return not self.problems


def verify(input_path: str | Path, output_path: str | Path,
           expected_fps: Fraction | None = None,
           check_cancelled: Callable[[], None] | None = None) -> Report:
    source, out = probe(input_path), probe(output_path)
    expected = expected_fps or default_output_fps(source_rate(source.fps, source.nominal_fps, source.is_vfr))

    def summary(info, config=CadenceConfig()):
        with closing(iter_frames(info)) as frames:
            def labels():
                for _, _, label in classify(frames, config):
                    if check_cancelled:
                        check_cancelled()
                    yield label
            return summarize(labels(), info.fps)

    # A 120fps neighbour spans half the time of a 60fps neighbour. Scale the
    # luma thresholds so small genuine motion is not calibrated as codec noise.
    # This remains a heuristic; the input detector and <=60fps output stay unchanged.
    scale = min(1.0, float(Fraction(60) / out.fps))
    config = CadenceConfig(max_diff=4.0 * scale, initial_floor=0.5 * scale)
    out_summary, in_summary = summary(out, config), summary(source)
    if check_cancelled:
        check_cancelled()
    audio_in, audio_out = _audio_timelines(source), _audio_timelines(out)
    if check_cancelled:
        check_cancelled()
    shift = out.pts[0] * out.time_base - source.pts[0] * source.time_base
    sync = tuple(SyncPoint(i, portion, after.time_at(after.duration * portion)
                           - before.time_at(before.duration * portion) - shift)
                 for i, (before, after) in enumerate(zip(audio_in, audio_out))
                 for portion in (Fraction(0), Fraction(1, 4), Fraction(1, 2), Fraction(3, 4), Fraction(1)))
    audio_diffs = [after.duration - before.duration for before, after in zip(audio_in, audio_out)]
    return Report(
        fps=out.fps,
        expected_fps=expected,
        frames=out.frame_count,
        duration_diff=float(out.duration - source.duration),
        frame_time=float(1 / expected),
        audio_diff=float(max(audio_diffs, key=abs)) if audio_diffs else None,
        dup_ratio=0.0 if out_summary.unique == 1 and out_summary.static else out_summary.dup_ratio,
        input_dup_ratio=0.0 if in_summary.unique == 1 and in_summary.static else in_summary.dup_ratio,
        static=out_summary.static,
        sync=sync,
        audio_tracks_match=len(audio_in) == len(audio_out),
    )


def format_report(r: Report) -> str:
    audio = "no audio" if r.audio_diff is None else f"{r.audio_diff * 1000:+.1f} ms vs input"
    lines = [
        f"Frame rate : {float(r.fps):.3f} fps (expected {float(r.expected_fps):.3f})",
        f"Frames     : {r.frames}",
        f"Length     : {r.duration_diff * 1000:+.1f} ms vs input (one frame is {r.frame_time * 1000:.1f} ms)",
        f"Audio      : {audio}",
        "Sync       : " + (", ".join(f"a{p.track + 1} {float(p.fraction):.0%}: {float(p.error) * 1000:+.1f} ms"
                                       for p in r.sync) if r.sync else "no audio"),
        f"Duplicates : {r.dup_ratio:.2%} of moving frames (input: {r.input_dup_ratio:.2%}), "
        f"{r.static} static frames",
        "Result     : " + ("OK" if r.ok else "FAILED - " + "; ".join(r.problems)),
    ]
    return "\n".join(lines)
