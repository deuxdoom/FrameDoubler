"""The one operation every interpolation backend provides."""

from abc import ABC, abstractmethod
from typing import Any

import numpy as np


class Interpolator(ABC):
    @abstractmethod
    def prepare(self, frame: np.ndarray) -> Any:
        """Converts an RGB frame (H, W, 3; uint8 or uint16) to the backend's own form.

        A source frame takes part in two intervals, so it is prepared once and reused.
        """

    @abstractmethod
    def interpolate(self, a: Any, b: Any, step: float) -> np.ndarray:
        """Returns the frame at `step` (0 < step < 1) between prepared frames A and B,
        in the shape and dtype of the original frames."""
