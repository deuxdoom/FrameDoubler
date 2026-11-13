"""Video encoding through an FFmpeg pipe, with the source audio copied alongside."""

import subprocess
import threading
from dataclasses import dataclass
from fractions import Fraction
from pathlib import Path

import numpy as np

from framedoubler.ffmpeg import ffmpeg_path
from framedoubler.probe import VideoInfo


class EncodeError(RuntimeError):
    pass


@dataclass(frozen=True)
class EncodeSettings:
    codec: str = "hevc_nvenc"
    preset: str = "p5"
    # Constant quality level (lower is better). 19 is visually transparent for game footage.
    quality: int = 19


class Encoder:
    """Writes frames at a constant `fps` and muxes the audio of the source file.

    Frames must be RGB arrays shaped like the source frames. Because the output is
    constant frame rate, frame k is presented at exactly k / fps seconds.
    """

    def __init__(self, output: str | Path, source: VideoInfo, fps: Fraction,
                 settings: EncodeSettings = EncodeSettings(), duration: Fraction | None = None):
        """`duration` cuts the output (audio included) to that many seconds; None keeps everything."""
        self.output = Path(output)
        self._frames = 0
        deep = source.bit_depth > 8
        self._dtype = np.dtype("<u2") if deep else np.dtype(np.uint8)
        self._shape = (source.height, source.width, 3)
        # SDR output is always tagged BT.709, converting from RGB with the matching matrix.
        matrix = "bt709"
        pix_fmt = "p010le" if deep else "yuv420p"
        cmd = [
            ffmpeg_path(), "-y", "-nostdin", "-loglevel", "error",
            "-f", "rawvideo", "-pix_fmt", "rgb48le" if deep else "rgb24",
            "-s", f"{source.width}x{source.height}",
            "-r", f"{fps.numerator}/{fps.denominator}", "-i", "-",
            "-i", str(source.path),
            "-map", "0:v:0", "-map", "1:a?",
            "-vf", f"scale=out_color_matrix={matrix}:out_range=tv,format={pix_fmt}",
            "-c:v", settings.codec, "-preset", settings.preset, "-tune", "hq",
            "-rc", "vbr", "-cq", str(settings.quality), "-b:v", "0",
            "-colorspace", matrix, "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
            "-fps_mode", "cfr",
            "-c:a", "copy",
            "-movflags", "+faststart",
            str(self.output),
        ]
        if duration is not None:
            cmd[-1:-1] = ["-t", f"{float(duration):.6f}"]
        if settings.codec == "hevc_nvenc":
            cmd[-1:-1] = ["-tag:v", "hvc1"]  # required for playback in Apple software
        self._proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stderr=subprocess.PIPE)
        self._errors: list[bytes] = []
        self._drain = threading.Thread(target=lambda: self._errors.append(self._proc.stderr.read()), daemon=True)
        self._drain.start()

    @property
    def frames_written(self) -> int:
        return self._frames

    def write(self, frame: np.ndarray) -> None:
        if frame.shape != self._shape or frame.dtype != self._dtype:
            raise EncodeError(f"Frame {frame.shape} {frame.dtype} does not match the output format")
        try:
            self._proc.stdin.write(frame.data if frame.flags.c_contiguous else frame.tobytes())
        except (BrokenPipeError, OSError) as exc:
            raise EncodeError(self._failure()) from exc
        self._frames += 1

    def close(self) -> None:
        """Finishes the file. Raises if FFmpeg reported an error."""
        if self._proc.stdin and not self._proc.stdin.closed:
            try:
                self._proc.stdin.close()
            except OSError:
                pass
        if self._proc.wait() != 0:
            raise EncodeError(self._failure())

    def abort(self) -> None:
        """Stops encoding and removes the incomplete file."""
        if self._proc.poll() is None:
            self._proc.kill()
        self._proc.wait()
        self.output.unlink(missing_ok=True)

    def _failure(self) -> str:
        self._proc.wait()
        self._drain.join(timeout=5)
        detail = b"".join(self._errors).decode(errors="replace").strip()
        return f"FFmpeg encoding failed: {detail or 'no error message'}"

    def __enter__(self) -> "Encoder":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        if exc_type is None:
            self.close()
        else:
            self.abort()
