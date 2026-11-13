"""Snapshot a folder and convert its videos sequentially through the pipeline."""

from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

from framedoubler.control import Cancelled, CancellationToken
from framedoubler.interp.base import Interpolator
from framedoubler.pipeline import Options, Progress, Result, run

VIDEO_SUFFIXES = {".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".ts", ".mts", ".m2ts"}


@dataclass(frozen=True)
class BatchItem:
    source: Path
    output: Path
    result: Result | None = None
    error: str | None = None


@dataclass(frozen=True)
class BatchResult:
    items: tuple[BatchItem, ...]
    total: int
    cancelled: bool = False

    @property
    def ok(self) -> bool:
        return not self.cancelled and len(self.items) == self.total and all(item.error is None for item in self.items)


def discover(folder: str | Path, output_dir: str | Path | None = None,
             recursive: bool = False) -> tuple[BatchItem, ...]:
    root = Path(folder).resolve()
    destination = Path(output_dir).resolve() if output_dir is not None else root / "FrameDoubler"
    if not root.is_dir():
        raise RuntimeError(f"Input folder does not exist: {root}")
    if root.is_relative_to(destination):
        raise RuntimeError("The output folder must not be the input folder or its parent.")
    candidates = root.rglob("*") if recursive else root.iterdir()
    sources = sorted((p for p in candidates if p.is_file() and p.suffix.lower() in VIDEO_SUFFIXES
                      and ".partial" not in p.name.lower() and not p.resolve().is_relative_to(destination)),
                     key=lambda p: (str(p.relative_to(root)).casefold(), str(p)))
    used, items = set(), []
    for source in sources:
        parent = destination / source.relative_to(root).parent
        output = parent / f"{source.stem}_smooth.mp4"
        number = 2
        while str(output).casefold() in used:
            output = parent / f"{source.stem}_smooth_{number}.mp4"
            number += 1
        used.add(str(output).casefold())
        items.append(BatchItem(source, output))
    if not items:
        raise RuntimeError(f"No supported videos found in {root}")
    return tuple(items)


def run_folder(folder: str | Path, output_dir: str | Path | None = None, options: Options = Options(),
               recursive: bool = False, stop_on_error: bool = False,
               cancel: CancellationToken | None = None, interpolator: Interpolator | None = None,
               on_progress: Callable[[int, int, BatchItem, Progress | None], None] | None = None) -> BatchResult:
    """Continue after file errors by default; cancellation leaves completed outputs intact.

    The callback receives (one-based index, total, item, pipeline progress).
    A None progress marks either the beginning or the final result of an item.
    """
    token = cancel or CancellationToken()
    jobs = discover(folder, output_dir, recursive)
    completed = []
    for index, job in enumerate(jobs, 1):
        try:
            token.check()
            if on_progress:
                on_progress(index, len(jobs), job, None)

            def progress(event):
                if on_progress:
                    on_progress(index, len(jobs), job, event)

            result = run(job.source, job.output, options, interpolator=interpolator,
                         on_progress=progress, cancel=token)
            error = ("Output verification failed: " + "; ".join(result.verification.problems)
                     if result.verification is not None and not result.verification.ok else None)
            item = BatchItem(job.source, job.output, result, error)
        except Cancelled:
            return BatchResult(tuple(completed), len(jobs), cancelled=True)
        except RuntimeError as exc:
            item = BatchItem(job.source, job.output, error=str(exc))
        completed.append(item)
        if on_progress:
            on_progress(index, len(jobs), item, None)
        if item.error and stop_on_error:
            break
    return BatchResult(tuple(completed), len(jobs))
