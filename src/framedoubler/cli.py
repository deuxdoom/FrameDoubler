"""Command line entry point. Subcommands are added as each module lands."""

import argparse
import sys

from framedoubler import __version__


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="framedoubler",
        description="Restore true motion from 30fps game captures and interpolate to real 60fps.",
    )
    parser.add_argument("--version", action="version", version=f"framedoubler {__version__}")
    sub = parser.add_subparsers(dest="command")

    analyze = sub.add_parser("analyze", help="report the input format, motion rate and processing plan")
    analyze.add_argument("input", help="video file to analyze")
    return parser


def _analyze(args: argparse.Namespace) -> int:
    from framedoubler.analyze import analyze, format_report

    print(format_report(analyze(args.input)))
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    if args.command is None:
        parser.print_help()
        return 0
    try:
        return {"analyze": _analyze}[args.command](args)
    except RuntimeError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
