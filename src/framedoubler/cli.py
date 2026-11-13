"""Command line entry point. Subcommands are added as each module lands."""

import argparse

from framedoubler import __version__


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="framedoubler",
        description="Restore true motion from 30fps game captures and interpolate to real 60fps.",
    )
    parser.add_argument("--version", action="version", version=f"framedoubler {__version__}")
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    parser.parse_args(argv)
    parser.print_help()
    return 0
