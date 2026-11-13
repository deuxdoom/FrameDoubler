"""Command line entry point."""

import argparse
import sys
from fractions import Fraction
from pathlib import Path

from framedoubler import __version__
from framedoubler.models import DEFAULT_MODEL, MODELS


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
    run.add_argument("--fps", type=Fraction, help="output frame rate, e.g. 60 or 60000/1001 (default: automatic)")
    run.add_argument("--model", choices=sorted(MODELS), default=DEFAULT_MODEL, help="RIFE model")
    run.add_argument("--no-dedup", action="store_true", help="treat every input frame as a new frame")
    run.add_argument("--max-frames", type=int, help="process only the first N input frames")
    run.add_argument("--verify", action="store_true", help="check the output after converting")

    verify = sub.add_parser("verify", help="check an output file against its input")
    verify.add_argument("input", help="original video file")
    verify.add_argument("output", help="converted video file")
    return parser


def _analyze(args: argparse.Namespace) -> int:
    from framedoubler.analyze import analyze, format_report

    print(format_report(analyze(args.input)))
    return 0


def _run(args: argparse.Namespace) -> int:
    from framedoubler.pipeline import Options, Progress, run

    source = Path(args.input)
    output = Path(args.output) if args.output else source.with_name(f"{source.stem}_60fps.mp4")
    if output.resolve() == source.resolve():
        raise RuntimeError("The output file must differ from the input file.")

    def show(p: Progress) -> None:
        remaining = (p.frames_total - p.frames_out) / p.fps if p.fps > 0 else 0
        print(f"\r{p.frames_out}/{p.frames_total} frames  {p.fps:5.1f} fps  "
              f"{remaining:5.0f} s left ", end="", flush=True)

    result = run(source, output, Options(fps=args.fps, model=args.model, dedup=not args.no_dedup,
                                         max_frames=args.max_frames), on_progress=show)
    print()
    print(f"Input   : {result.frames_in} frames at {float(result.source.fps):.3f} fps "
          f"({result.unique} new, {result.duplicates} duplicate, {result.static} static)")
    print(f"Output  : {result.frames_out} frames at {float(result.fps):.3f} fps "
          f"({result.interpolated} interpolated, {result.scene_cuts} scene cuts)")
    print(f"Time    : {result.elapsed:.1f} s ({result.frames_out / result.elapsed:.1f} fps)")
    print(f"Written : {result.output}")
    if args.verify and args.max_frames is None:
        return _verify(argparse.Namespace(input=str(source), output=str(output)))
    return 0


def _verify(args: argparse.Namespace) -> int:
    from framedoubler.verify import format_report, verify

    report = verify(args.input, args.output)
    print(format_report(report))
    return 0 if report.ok else 2


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    if args.command is None:
        parser.print_help()
        return 0
    try:
        return {"analyze": _analyze, "run": _run, "verify": _verify}[args.command](args)
    except RuntimeError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
