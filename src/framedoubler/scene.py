"""Scene change detection between consecutive source frames.

Pixel differences cannot tell a cut from a fast camera pan (both change most of the
picture), but a pan keeps the brightness distribution of each screen region while
a cut replaces it. Frames are compared by luma histograms on a 4x4 grid.
"""

import numpy as np

_GRID = 4
_BINS = 64
# Measured grid-histogram distances: fast 4K camera pans stay below 0.12, cuts in
# the synthetic clips are above 0.7 (docs/research.md R7).
DEFAULT_THRESHOLD = 0.35


def descriptor(sig: np.ndarray) -> np.ndarray:
    """Normalised luma histograms of the grid cells of a signature (see frame_diff)."""
    h, w = sig.shape
    cells = []
    for i in range(_GRID):
        for j in range(_GRID):
            cell = sig[i * h // _GRID:(i + 1) * h // _GRID, j * w // _GRID:(j + 1) * w // _GRID]
            hist = np.histogram(cell, bins=_BINS, range=(0, 256))[0]
            cells.append(hist / max(1, cell.size))
    return np.asarray(cells, dtype=np.float32)


def cut_score(a: np.ndarray, b: np.ndarray) -> float:
    """Distance between two descriptors: 0 for identical frames, 1 for unrelated ones."""
    return float((0.5 * np.abs(a - b).sum(axis=1)).mean())


def is_cut(a: np.ndarray, b: np.ndarray, threshold: float = DEFAULT_THRESHOLD) -> bool:
    return cut_score(a, b) > threshold
