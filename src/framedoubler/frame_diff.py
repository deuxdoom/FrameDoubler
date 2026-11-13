"""Frame difference metrics on a reduced luma image."""

from dataclasses import dataclass

import numpy as np

_TARGET_WIDTH = 480
_BLOCK = 16


@dataclass(frozen=True)
class Diff:
    mean: float  # mean absolute luma difference over the whole frame, 8-bit scale
    block: float  # largest mean absolute difference among 16x16 blocks, 8-bit scale


def signature(frame: np.ndarray) -> np.ndarray:
    """Reduces an RGB frame (uint8 or uint16) to a small float32 luma image on an 8-bit scale.

    Pixels are point-sampled: both frames of a comparison are sampled at the same
    positions, so no filtering is needed and a 4K frame costs about a millisecond.
    """
    step = max(1, frame.shape[1] // _TARGET_WIDTH)
    small = frame[::step, ::step].astype(np.float32)
    luma = small[:, :, 0] * 0.2126 + small[:, :, 1] * 0.7152 + small[:, :, 2] * 0.0722
    if frame.dtype != np.uint8:
        luma /= 257.0
    return luma


def diff(a: np.ndarray, b: np.ndarray) -> Diff:
    """Compares two signatures of the same shape."""
    delta = np.abs(a - b)
    h, w = delta.shape
    bh, bw = h // _BLOCK, w // _BLOCK
    blocks = delta[: bh * _BLOCK, : bw * _BLOCK].reshape(bh, _BLOCK, bw, _BLOCK).mean(axis=(1, 3))
    return Diff(float(delta.mean()), float(blocks.max()))
