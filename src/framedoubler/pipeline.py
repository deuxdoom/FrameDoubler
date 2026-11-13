"""The whole conversion: analyze, drop duplicates, interpolate, encode.

Used by the CLI and, later, the GUI. Everything runs sequentially in one thread.
"""

import time
from collections.abc import Callable
from dataclasses import dataclass
from fractions import Fraction
from pathlib import Path

from framedoubler.cadence import CadenceConfig, Label, classify
from framedoubler.decode import iter_frames
from framedoubler.encode import Encoder, EncodeSettings
from framedoubler.frame_diff import signature
from framedoubler.hud import Region, restore_static, validate_regions
from framedoubler.interp.base import Interpolator
from framedoubler.models import DEFAULT_MODEL
from framedoubler.probe import VideoInfo, probe
from framedoubler.scene import DEFAULT_THRESHOLD, cut_score, descriptor
from framedoubler.timeline import default_output_fps, output_count, source_rate, steps


@dataclass(frozen=True)
class Options:
    fps: Fraction | None = None  # None: 60fps-class input keeps its rate, 30fps-class is doubled
    model: str = DEFAULT_MODEL
    scale: float | None = None
    dedup: bool = True
    scene_threshold: float = DEFAULT_THRESHOLD
    encode: EncodeSettings = EncodeSettings()
    max_frames: int | None = None  # process only the first N input frames (for trials)
    hud_regions: tuple[Region, ...] = ()


@dataclass(frozen=True)
class Progress:
    frames_out: int
    frames_total: int
    elapsed: float

    @property
    def fps(self) -> float:
        return self.frames_out / self.elapsed if self.elapsed > 0 else 0.0


@dataclass(frozen=True)
class Result:
    output: Path
    source: VideoInfo
    fps: Fraction
    frames_in: int
    frames_out: int
    unique: int
    duplicates: int
    static: int
    interpolated: int
    scene_cuts: int
    elapsed: float


def run(input_path: str | Path, output_path: str | Path, options: Options = Options(),
        interpolator: Interpolator | None = None,
        on_progress: Callable[[Progress], None] | None = None) -> Result:
    info = probe(input_path)
    if info.is_hdr:
        raise RuntimeError("HDR input is not supported yet.")
    validate_regions(options.hud_regions, info.width, info.height)
    fps_out = options.fps or default_output_fps(source_rate(info.fps, info.nominal_fps, info.is_vfr))
    if interpolator is None:
        from framedoubler.interp.rife import RifeInterpolator
        interpolator = RifeInterpolator(options.model, options.scale)

    limit = info.frame_count if options.max_frames is None else min(options.max_frames, info.frame_count)
    partial = limit < info.frame_count
    duration = (info.pts[limit] - info.pts[0]) * info.time_base if partial else info.duration
    total = output_count(duration, fps_out)

    frames = iter_frames(info, max_frames=options.max_frames)
    if options.dedup:
        labelled = classify(frames, CadenceConfig())
    else:
        labelled = ((pts, frame, Label.UNIQUE) for pts, frame in frames)

    counts = {label: 0 for label in Label}
    interpolated = cuts = 0
    started = time.perf_counter()
    last_report = 0.0

    def report(force: bool = False) -> None:
        nonlocal last_report
        now = time.perf_counter()
        if on_progress and (force or now - last_report >= 0.5):
            last_report = now
            on_progress(Progress(encoder.frames_written, total, now - started))

    with Encoder(output_path, info, fps_out, options.encode, duration if partial else None) as encoder:
        # `a` is the source frame currently on screen: (time, frame, prepared, descriptor)
        a = None
        for pts, frame, label in labelled:
            counts[label] += 1
            if label is Label.DUP:
                continue
            t = (pts - info.pts[0]) * info.time_base
            b = (t, frame, None, descriptor(signature(frame)))
            if a is not None:
                # A static frame repeats `a`, and nothing may be blended across a cut.
                hold = label is Label.STATIC
                if not hold and cut_score(a[3], b[3]) > options.scene_threshold:
                    hold = True
                    cuts += 1
                for _, position in steps(a[0], t, fps_out):
                    if position == 0.0 or (hold and position < 1.0):
                        encoder.write(a[1])
                    elif position == 1.0:
                        encoder.write(frame)
                    else:
                        if a[2] is None:
                            a = (a[0], a[1], interpolator.prepare(a[1]), a[3])
                        if b[2] is None:
                            b = (t, frame, interpolator.prepare(frame), b[3])
                        middle = interpolator.interpolate(a[2], b[2], position)
                        if options.hud_regions:
                            middle = restore_static(a[1], frame, middle, options.hud_regions)
                        encoder.write(middle)
                        interpolated += 1
                    report()
            a = b
        if a is not None:
            # The last source frame stays on screen until the end of the clip.
            while encoder.frames_written < total:
                encoder.write(a[1])
        report(force=True)
        frames_out = encoder.frames_written

    return Result(Path(output_path), info, fps_out, sum(counts.values()), frames_out,
                  counts[Label.UNIQUE], counts[Label.DUP], counts[Label.STATIC],
                  interpolated, cuts, time.perf_counter() - started)
