"""Restore identical source pixels inside explicitly selected static HUD regions."""

import numpy as np

Region = tuple[int, int, int, int]  # x, y, width, height


def validate_regions(regions: tuple[Region, ...], width: int, height: int) -> None:
    for x, y, w, h in regions:
        if min(x, y) < 0 or min(w, h) <= 0 or x + w > width or y + h > height:
            raise RuntimeError(f"HUD region {x},{y},{w},{h} lies outside {width}x{height}.")


def restore_static(a: np.ndarray, b: np.ndarray, middle: np.ndarray,
                   regions: tuple[Region, ...]) -> np.ndarray:
    """Copies only exact RGB matches; changed HUD pixels keep their interpolation.

    Regions must contain a static overlay, rather than moving scene content.
    Sources and the interpolated input are never modified.
    """
    output = middle
    for x, y, w, h in regions:
        first, second = a[y:y+h, x:x+w], b[y:y+h, x:x+w]
        mask = np.all(first == second, axis=2)
        if mask.any():
            if output is middle:
                output = middle.copy()
            output[y:y+h, x:x+w][mask] = first[mask]
    return output
