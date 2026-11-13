"""Command line entry point."""

import argparse
import re
import sys
from fractions import Fraction
from pathlib import Path

from framedoubler import __version__
from framedoubler.models import DEFAULT_MODEL, MODELS


def _quality(value: str) -> int:
    try:
        quality = int(value)
    except ValueError:
        raise argparse.ArgumentTypeError("quality must be an integer from 1 to 51") from None
    if not 1 <= quality <= 51:
        raise argparse.ArgumentTypeError("quality must be an integer from 1 to 51")
    return quality


def _bitrate(value: str) -> int:
    if not re.fullmatch(r"[0-9]+(?:\.[0-9]+)?[kKmM]?", value):
        raise argparse.ArgumentTypeError("bitrate must be positive bits/s, e.g. 12000000, 12000k or 12M")
    suffix = value[-1].lower()
    rate = Fraction(value[:-1]) * {"k": 1000, "m": 1_000_000}[suffix] if suffix in "km" else Fraction(value)
    if rate <= 0 or rate.denominator != 1:
        raise argparse.ArgumentTypeError("bitrate must be a positive whole number of bits/s")
    return int(rate)


def _hud_region(value: str) -> tuple[int, int, int, int]:
    try:
        x, y, width, height = map(int, value.split(","))
        if min(x, y) < 0 or min(width, height) <= 0:
            raise ValueError
    except ValueError:
        raise argparse.ArgumentTypeError("HUD region must be x,y,width,height with positive dimensions") from None
    return x, y, width, height


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="framedoubler",
        description="Restore true motion from 30fps game captures and interpolate to real 60fps.",
    )
    parser.add_argument("--version", action="version", version=f"framedoubler {__version__}")
    sub = parser.add_subparsers(dest="command")

    analyze = sub.add_parser("analyze", help="report the input format, motion rate and processing plan")
    analyze.add_argument("input", help="video file to analyze")

    run = sub.add_parser("run", help="convert a video to real 60fps")
    run.add_argument("-i", "--input", required=True, help="input video file")
    run.add_argument("-o", "--output", help="output file (default: <input>_60fps.mp4 next to the input)")
    _conversion_arguments(run)

    batch = sub.add_parser("batch", help="convert all videos in a folder sequentially")
    batch.add_argument("input", help="input folder")
    batch.add_argument("-o", "--output", help="output folder (default: <input>/FrameDoubler)")
    batch.add_argument("--recursive", action="store_true", help="include subfolders and preserve their relative paths")
    batch.add_argument("--stop-on-error", action="store_true", help="stop after the first failed file")
    _conversion_arguments(batch)

    verify = sub.add_parser("verify", help="check an output file against its input")
    verify.add_argument("input", help="original video file")
    verify.add_argument("output", help="converted video file")
    gui = sub.add_parser("gui", help="open the Windows desktop interface")
    gui.add_argument("inputs", nargs="*", help="videos to add to the queue")
    gui.add_argument("--lang", choices=("ko", "en"))
    gui.add_argument("--output-dir")
    return parser


def _conversion_arguments(parser):
    parser.add_argument("--fps", type=Fraction, help="output frame rate, e.g. 60 or 60000/1001 (default: automatic)")
    parser.add_argument("--model", choices=sorted(MODELS), default=DEFAULT_MODEL, help="RIFE model")
    parser.add_argument("--no-dedup", action="store_true", help="treat every input frame as a new frame")
    parser.add_argument("--max-frames", type=int, help="process only the first N input frames")
    parser.add_argument("--verify", action="store_true", help="check the output after converting")
    parser.add_argument("--overwrite", action="store_true", help="replace an existing output only after a successful conversion")
    parser.add_argument("--profile", action="store_true", help="record time spent in each processing stage")
    parser.add_argument("--serial", action="store_true", help="disable input prefetch for diagnostics")
    parser.add_argument("--hud-region", type=_hud_region, action="append", default=[], metavar="X,Y,W,H",
                     help="protect identical pixels inside a static HUD rectangle; repeat for multiple regions")
    parser.add_argument("--preset", choices=["youtube"], help="YouTube SDR upload settings, automatic bitrate by resolution/fps")
    parser.add_argument("--codec", choices=["h264", "hevc", "av1"], help="NVENC codec (default: hevc; youtube preset: h264)")
    rate = parser.add_mutually_exclusive_group()
    rate.add_argument("--quality", type=_quality, help="NVENC quality 1-51, lower is better (default: 19 without --preset)")
    rate.add_argument("--bitrate", type=_bitrate, help="target video bitrate, e.g. 24M or 24000k; overrides preset bitrate")



def _options(args, *, verify_output=False):
    from framedoubler.encode import EncodeSettings
    from framedoubler.pipeline import Options

    return Options(fps=args.fps, model=args.model, dedup=not args.no_dedup,
                   max_frames=args.max_frames, overwrite=args.overwrite, profile=args.profile,
                   verify_output=verify_output, prefetch=0 if args.serial else 2,
                   hud_regions=tuple(args.hud_region),
                   encode=EncodeSettings(codec=f"{args.codec}_nvenc" if args.codec else None,
                                         quality=args.quality, bitrate=args.bitrate, upload_preset=args.preset))


def _analyze(args: argparse.Namespace) -> int:
    from framedoubler.analyze import analyze, format_report

    print(format_report(analyze(args.input)))
    return 0


def _run(args: argparse.Namespace) -> int:
    from framedoubler.pipeline import Progress, run

    source = Path(args.input)
    output = Path(args.output) if args.output else source.with_name(f"{source.stem}_60fps.mp4")
    if output.resolve() == source.resolve():
        raise RuntimeError("The output file must differ from the input file.")

    def show(p: Progress) -> None:
        if p.stage != "convert":
            print(f"\r{p.stage:<70}", end="", flush=True)
            return
        remaining = (p.frames_total - p.frames_out) / p.fps if p.fps > 0 else 0
        print(f"\r{p.frames_out}/{p.frames_total} frames  {p.fps:5.1f} fps  "
              f"{remaining:5.0f} s left ", end="", flush=True)

    result = run(source, output, _options(args), on_progress=show)
    print()
    print(f"Input   : {result.frames_in} frames at {float(result.source.fps):.3f} fps "
          f"({result.unique} new, {result.duplicates} duplicate, {result.static} static)")
    print(f"Output  : {result.frames_out} frames at {float(result.fps):.3f} fps "
          f"({result.interpolated} interpolated, {result.scene_cuts} scene cuts)")
    print(f"Time    : {result.elapsed:.1f} s ({result.frames_out / result.elapsed:.1f} fps)")
    print(f"Written : {result.output}")
    if args.profile:
        _show_timings(result.timings)
    if args.verify and args.max_frames is None:
        return _verify(argparse.Namespace(input=str(source), output=str(output)))
    return 0


def _verify(args: argparse.Namespace) -> int:
    from framedoubler.verify import format_report, verify

    report = verify(args.input, args.output)
    print(format_report(report))
    return 0 if report.ok else 2


def _show_timings(timings):
    print("Stage timings (exclusive seconds, including pipe waits):")
    for name, seconds in timings.seconds.items():
        print(f"  {name:10s} {seconds:7.3f} s  {seconds / timings.total:6.1%}")
    if timings.worker_seconds:
        print("Input worker (overlaps the main stages):")
        for name, seconds in timings.worker_seconds.items():
            print(f"  {name:10s} {seconds:7.3f} s")


def _batch(args: argparse.Namespace) -> int:
    from framedoubler.batch import run_folder

    def show(index, total, item, progress):
        if progress is not None:
            if progress.stage == "convert":
                print(f"\r[{index}/{total}] {progress.frames_out}/{progress.frames_total} frames  "
                      f"{progress.fps:.1f} fps", end="", flush=True)
        elif item.result is None and item.error is None:
            print(f"[{index}/{total}] {item.source}\n  -> {item.output}", flush=True)
        else:
            print(f"\n  {'FAILED: ' + item.error if item.error else 'OK'}", flush=True)
            if item.result is not None and item.result.verification is not None:
                print(item.result.verification_text)
            if item.result is not None and args.profile:
                _show_timings(item.result.timings)

    result = run_folder(args.input, args.output,
                        _options(args, verify_output=args.verify and args.max_frames is None),
                        recursive=args.recursive, stop_on_error=args.stop_on_error, on_progress=show)
    failed = sum(item.error is not None for item in result.items)
    print(f"Batch: {len(result.items) - failed} succeeded, {failed} failed, "
          f"{result.total - len(result.items)} not processed.")
    return 130 if result.cancelled else (0 if result.ok else 2)


def _gui(args: argparse.Namespace) -> int:
    try:
        from framedoubler.gui import main as gui_main
    except ImportError as exc:
        raise RuntimeError("The desktop interface needs PySide6. Run scripts\\setup.ps1 or install .[gui].") from exc
    options = (["--lang", args.lang] if args.lang else []) + (["--output-dir", args.output_dir] if args.output_dir is not None else [])
    return gui_main([*options, *args.inputs])


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    if args.command is None:
        parser.print_help()
        return 0
    try:
        return {"analyze": _analyze, "run": _run, "batch": _batch, "verify": _verify, "gui": _gui}[args.command](args)
    except RuntimeError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("\nConversion cancelled.", file=sys.stderr)
        return 130
