"""Output timeline: which output frames fall between two source frames, and where.

Output frame k is shown at k / fps_out seconds. Source frames (after duplicates are
removed) are shown at arbitrary times, so one rule covers real 30fps input, 30fps
content inside a 60fps file, dropped game frames and variable frame rate input.
"""

from fractions import Fraction
from math import ceil

# An output time this close to a source frame (as a fraction of the gap between the
# two source frames) reuses that frame instead of interpolating. Containers with
# coarse time bases round timestamps, and a 2% blend is not worth a model pass.
SNAP = Fraction(1, 20)


def default_output_fps(container_fps: Fraction) -> Fraction:
    """60fps-class files keep their rate (their motion is restored in place);
    30fps-class files are doubled. 29.97 becomes 59.94 and 30 becomes 60."""
    return container_fps if container_fps > 45 else container_fps * 2


def output_count(duration: Fraction, fps_out: Fraction) -> int:
    """Number of output frames needed to cover `duration` seconds."""
    return ceil(duration * fps_out)


def steps(t_a: Fraction, t_b: Fraction, fps_out: Fraction) -> list[tuple[int, float]]:
    """Output frames with t_a <= k / fps_out < t_b, as (k, position).

    Position 0.0 means source frame A as is, 1.0 means source frame B as is, and
    anything between is the interpolation time step.
    """
    if t_b <= t_a:
        return []
    first = ceil(t_a * fps_out)
    last = ceil(t_b * fps_out) - 1
    gap = t_b - t_a
    result = []
    for k in range(first, last + 1):
        position = (Fraction(k) / fps_out - t_a) / gap
        if position <= SNAP:
            result.append((k, 0.0))
        elif position >= 1 - SNAP:
            result.append((k, 1.0))
        else:
            result.append((k, float(position)))
    return result
