"""The whole conversion: analyze, drop duplicates, interpolate, encode.

Used by the CLI and, later, the GUI. Everything runs sequentially in one thread.
"""

import time
import os
import uuid
from collections.abc import Callable
from contextlib import closing
from dataclasses import dataclass, replace
from fractions import Fraction
from pathlib import Path

from framedoubler.cadence import CadenceConfig, Label, classify
from framedoubler.analyze import Analysis, analyze as analyze_input
from framedoubler.control import Cancelled, CancellationToken
from framedoubler.decode import iter_frames
from framedoubler.encode import Encoder, EncodeSettings
from framedoubler.frame_diff import signature
from framedoubler.hud import Region, restore_static, validate_regions
from framedoubler.interp.base import Interpolator
from framedoubler.models import DEFAULT_MODEL
from framedoubler.probe import VideoInfo, probe
from framedoubler.scene import DEFAULT_THRESHOLD, cut_score, descriptor
from framedoubler.timeline import default_output_fps, output_count, source_rate, steps
from framedoubler.verify import Report, format_report, verify


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
    overwrite: bool = False
    verify_output: bool = False

    def __post_init__(self) -> None:
        if self.fps is not None and self.fps <= 0:
            raise RuntimeError("Output frame rate must be positive.")
        if self.max_frames is not None and self.max_frames <= 0:
            raise RuntimeError("max_frames must be positive.")
        if not 0 <= self.scene_threshold <= 1:
            raise RuntimeError("Scene threshold must be between 0 and 1.")


@dataclass(frozen=True)
class Progress:
    frames_out: int
    frames_total: int
    elapsed: float
    stage: str = "convert"

    @property
    def fps(self) -> float:
        return self.frames_out / self.elapsed if self.elapsed > 0 else 0.0

    @property
    def remaining(self) -> float | None:
        return max(0, self.frames_total - self.frames_out) / self.fps if self.fps > 0 else None


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
    verification: Report | None = None

    @property
    def verification_text(self) -> str:
        return format_report(self.verification) if self.verification is not None else ""


def run(input_path: str | Path, output_path: str | Path | None = None, options: Options = Options(),
        interpolator: Interpolator | None = None,
        on_progress: Callable[[Progress], None] | None = None,
        cancel: CancellationToken | None = None, analyze_only: bool = False) -> Result | Analysis:
    """Runs a job, publishing only complete output; analysis also uses this API."""
    token = cancel or CancellationToken()
    started = time.perf_counter()
    staging = None

    def stage(name: str) -> None:
        if on_progress:
            on_progress(Progress(0, 0, time.perf_counter() - started, name))

    try:
        token.check()
        stage("analyze")
        if analyze_only:
            analysis = analyze_input(input_path, check_cancelled=token.check)
            token.check()
            stage("done")
            return analysis
        if output_path is None:
            raise RuntimeError("An output path is required for conversion.")
        output = Path(output_path)
        if Path(input_path).resolve() == output.resolve():
            raise RuntimeError("The output file must differ from the input file.")
        if output.exists() and not options.overwrite:
            raise RuntimeError(f"Output already exists: {output}. Choose another name or enable overwrite.")
        output.parent.mkdir(parents=True, exist_ok=True)
        staging = output.with_name(f".{output.stem}.{uuid.uuid4().hex}.partial{output.suffix}")
        result = _convert(input_path, staging, options, interpolator, on_progress, token)
        token.check()
        if options.verify_output:
            stage("verify")
            result = replace(result, verification=verify(input_path, staging, expected_fps=result.fps,
                                                         check_cancelled=token.check))
            token.check()
        if options.overwrite:
            staging.replace(output)
        elif os.name == "nt":
            staging.rename(output)  # Windows rename refuses an existing destination.
        else:
            os.link(staging, output)  # atomic no-replace publication on POSIX
            staging.unlink()
        stage("done")
        return replace(result, output=output)
    except Cancelled:
        stage("cancelled")
        raise
    except (OSError, ValueError) as exc:
        stage("error")
        raise RuntimeError(f"Cannot process {input_path}: {exc}") from exc
    except RuntimeError:
        stage("error")
        raise
    finally:
        if staging is not None:
            staging.unlink(missing_ok=True)


def _convert(input_path, output_path, options, interpolator, on_progress, cancel) -> Result:
    info = probe(input_path)
    cancel.check()
    if info.is_hdr:
        raise RuntimeError("HDR input is not supported yet.")
    validate_regions(options.hud_regions, info.width, info.height)
    fps_out = options.fps or default_output_fps(source_rate(info.fps, info.nominal_fps, info.is_vfr))
    if interpolator is None:
        if on_progress:
            on_progress(Progress(0, 0, 0.0, "model"))
        from framedoubler.interp.rife import RifeInterpolator
        interpolator = RifeInterpolator(options.model, options.scale, check_cancelled=cancel.check)
    cancel.check()

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

    with closing(frames), closing(labelled), Encoder(output_path, info, fps_out, options.encode, duration if partial else None) as encoder:
        # `a` is the source frame currently on screen: (time, frame, prepared, descriptor)
        a = None
        for pts, frame, label in labelled:
            cancel.check()
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
                    cancel.check()
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
                cancel.check()
                encoder.write(a[1])
        report(force=True)
        cancel.check()
        if on_progress:
            on_progress(Progress(encoder.frames_written, total, time.perf_counter() - started, "mux"))
        frames_out = encoder.frames_written

    return Result(Path(output_path), info, fps_out, sum(counts.values()), frames_out,
                  counts[Label.UNIQUE], counts[Label.DUP], counts[Label.STATIC],
                  interpolated, cuts, time.perf_counter() - started)
