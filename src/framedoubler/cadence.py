"""Duplicate frame detection and motion frame rate estimation.

A 30fps game recorded at 60fps shows every game frame twice (more often when the
game drops frames). Duplicates are not bit-identical after lossy encoding, so a
frame is judged by how its difference from the previous frame compares with the
differences around it: a duplicate differs only by compression noise while its
neighbours carry real motion.
"""

from collections import deque
from collections.abc import Iterable, Iterator
from dataclasses import dataclass
from enum import Enum
from fractions import Fraction

import numpy as np

from framedoubler.frame_diff import Diff, diff, signature


class Label(str, Enum):
    UNIQUE = "unique"  # a new game frame
    DUP = "dup"  # repeats the previous frame; dropped before interpolation
    STATIC = "static"  # repeats the previous frame inside a long hold; kept as is


@dataclass(frozen=True)
class CadenceConfig:
    # A duplicate's block difference must be at most this fraction of the largest
    # neighbouring difference.
    ratio: float = 0.5
    # Above this block difference a frame is never a duplicate, whatever its neighbours.
    max_diff: float = 4.0
    # Longest run of duplicates that is still treated as frame repetition. Longer
    # runs are holds (pause menus, loading screens) and must not be interpolated across.
    max_run: int = 4
    # A frame whose block difference is below the noise floor is a duplicate even
    # when its neighbours are equally quiet (the middle of a triple repeat, a hold).
    # The floor is this multiple of the median difference of recent duplicates...
    noise_factor: float = 2.5
    # ...and this value until enough duplicates have been seen.
    initial_floor: float = 0.5


class CadenceDetector:
    """Labels frames one at a time. Each decision needs the following frame, so
    `push` returns the label of the previous frame and `flush` the last one."""

    def __init__(self, config: CadenceConfig = CadenceConfig()):
        self.config = config
        self._prev_sig: np.ndarray | None = None
        self._pending: Diff | None = None  # difference of the undecided frame to its predecessor
        self._before: Diff | None = None  # difference of the frame before that
        self._count = 0
        self._run = 0
        self._noise: deque[float] = deque(maxlen=31)  # differences of confirmed duplicates

    def push(self, frame: np.ndarray) -> Label | None:
        sig = signature(frame)
        self._count += 1
        if self._prev_sig is None:
            self._prev_sig = sig
            return None
        current = diff(self._prev_sig, sig)
        self._prev_sig = sig
        if self._count == 2:
            # The first frame has no predecessor and is always new.
            self._pending = current
            return Label.UNIQUE
        label = self._decide(self._pending, self._before, current)
        self._before, self._pending = self._pending, current
        return label

    def flush(self) -> Label | None:
        if self._count == 0:
            return None
        if self._count == 1:
            return Label.UNIQUE
        return self._decide(self._pending, self._before, None)

    def _decide(self, own: Diff, before: Diff | None, after: Diff | None) -> Label:
        neighbours = [d.block for d in (before, after) if d is not None]
        by_contrast = (
            bool(neighbours)
            and own.block < self.config.max_diff
            and own.block <= self.config.ratio * max(neighbours)
        )
        if by_contrast:
            # Only duplicates confirmed against real motion calibrate the noise floor.
            self._noise.append(own.block)
        if len(self._noise) >= 5:
            floor = min(self.config.noise_factor * float(np.median(self._noise)), self.config.max_diff)
        else:
            floor = self.config.initial_floor
        is_dup = by_contrast or own.block < floor
        if not is_dup:
            self._run = 0
            return Label.UNIQUE
        self._run += 1
        return Label.DUP if self._run <= self.config.max_run else Label.STATIC


def classify(frames: Iterable[tuple[int, np.ndarray]],
             config: CadenceConfig = CadenceConfig()) -> Iterator[tuple[int, np.ndarray, Label]]:
    """Adds a label to every (pts, frame) pair, preserving order."""
    detector = CadenceDetector(config)
    held: tuple[int, np.ndarray] | None = None
    for pts, frame in frames:
        label = detector.push(frame)
        if label is not None:
            yield (*held, label)
        held = (pts, frame)
    label = detector.flush()
    if label is not None:
        yield (*held, label)


@dataclass(frozen=True)
class CadenceSummary:
    frames: int
    unique: int
    dup: int
    static: int
    container_fps: Fraction

    @property
    def dup_ratio(self) -> float:
        moving = self.unique + self.dup
        return self.dup / moving if moving else 0.0

    @property
    def motion_fps(self) -> float:
        """Frame rate of the actual motion, ignoring static holds."""
        return float(self.container_fps) * (1.0 - self.dup_ratio)

    @property
    def has_duplicates(self) -> bool:
        return self.dup_ratio > 0.25


def summarize(labels: Iterable[Label], container_fps: Fraction) -> CadenceSummary:
    counts = {label: 0 for label in Label}
    for label in labels:
        counts[label] += 1
    return CadenceSummary(sum(counts.values()), counts[Label.UNIQUE], counts[Label.DUP],
                          counts[Label.STATIC], container_fps)
