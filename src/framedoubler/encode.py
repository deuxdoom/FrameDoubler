"""NVENC encoding through an FFmpeg pipe, with upload settings and audio muxing."""

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
    # None selects HEVC normally, H.264 for the YouTube preset.
    codec: str | None = None
    preset: str = "p5"
    quality: int | None = None  # NVENC CQ, lower is better; default 19 without an upload preset
    bitrate: int | None = None  # target bits/s, VBR (not a file-size guarantee)
    upload_preset: str | None = None

    def __post_init__(self) -> None:
        if self.codec not in (None, "h264_nvenc", "hevc_nvenc", "av1_nvenc"):
            raise EncodeError("Codec must be h264_nvenc, hevc_nvenc or av1_nvenc.")
        if self.preset not in {f"p{i}" for i in range(1, 8)}:
            raise EncodeError("NVENC preset must be p1 through p7.")
        if self.upload_preset not in (None, "youtube"):
            raise EncodeError("Unknown upload preset; choose youtube.")
        if self.quality is not None and (type(self.quality) is not int or not 1 <= self.quality <= 51):
            raise EncodeError("Quality must be an integer from 1 to 51 (lower is better).")
        if self.bitrate is not None and (type(self.bitrate) is not int or self.bitrate <= 0):
            raise EncodeError("Bitrate must be a positive integer in bits/s.")
        if self.quality is not None and self.bitrate is not None:
            raise EncodeError("Choose quality or bitrate, not both.")

    @property
    def encoder(self) -> str:
        return self.codec or ("h264_nvenc" if self.upload_preset == "youtube" else "hevc_nvenc")


def youtube_bitrate(width: int, height: int, fps: Fraction) -> int:
    """YouTube SDR H.264 targets; use the upper end of the published 4K ranges."""
    if not 0 < fps <= 60:
        raise EncodeError("The YouTube preset supports output rates up to 60 fps.")
    short, long = sorted((width, height))
    for max_short, max_long, standard, high in (
        (1080, 1920, 8_000_000, 12_000_000),
        (1440, 2560, 16_000_000, 24_000_000),
        (2160, 3840, 45_000_000, 68_000_000),
    ):
        if 0 < short <= max_short and long <= max_long:
            return high if fps > 30 else standard
    raise EncodeError("The YouTube preset supports resolutions up to 3840x2160 (including portrait).")


class Encoder:
    """Writes frames at a constant `fps` and muxes the audio of the source file.

    Frames must be RGB arrays shaped like the source frames. Because the output is
    constant frame rate, frame k is presented at exactly k / fps seconds.
    """

    def __init__(self, output: str | Path, source: VideoInfo, fps: Fraction,
                 settings: EncodeSettings = EncodeSettings(), duration: Fraction | None = None):
        """`duration` cuts the output (audio included) to that many seconds; None keeps everything."""
        youtube = settings.upload_preset == "youtube"
        codec = settings.encoder
        bitrate = settings.bitrate
        if youtube:
            recommended = youtube_bitrate(source.width, source.height, fps)
            if settings.quality is None and bitrate is None:
                bitrate = recommended
            if Path(output).suffix.lower() != ".mp4":
                raise EncodeError("The YouTube preset requires an .mp4 output file.")
        if source.bit_depth > 8 and codec == "h264_nvenc":
            raise EncodeError("H.264 10-bit encoding is not supported on RTX 4080 SUPER; choose HEVC or AV1.")
        self.output = Path(output)
        self._frames = 0
        deep = source.bit_depth > 8
        self._dtype = np.dtype("<u2") if deep else np.dtype(np.uint8)
        self._shape = (source.height, source.width, 3)
        # SDR output is always tagged BT.709, converting from RGB with the matching matrix.
        matrix = "bt709"
        pix_fmt = "p010le" if deep else "yuv420p"
        rate_args = (["-b:v", str(bitrate)] if bitrate is not None else
                     ["-cq", str(settings.quality if settings.quality is not None else 19), "-b:v", "0"])
        video_args = []
        audio_args = ["-c:a", "copy"]
        if youtube:
            video_args = ["-g", str(max(1, round(fps / 2))), "-flags", "+cgop"]
            if codec == "h264_nvenc":
                video_args += ["-profile:v", "high", "-bf", "2", "-b_adapt", "0", "-coder", "cabac"]
            audio_args = ["-c:a", "aac", "-profile:a", "aac_low", "-ar:a", "48000"]
            for i, stream in enumerate(source.audio):
                channels = stream.channels if stream.channels in (1, 2, 6) else 2
                audio_args += [f"-ac:a:{i}", str(channels), f"-b:a:{i}",
                               str({1: 128_000, 2: 384_000, 6: 512_000}[channels])]
        cmd = [
            ffmpeg_path(), "-y", "-nostdin", "-loglevel", "error",
            "-f", "rawvideo", "-pix_fmt", "rgb48le" if deep else "rgb24",
            "-s", f"{source.width}x{source.height}",
            "-r", f"{fps.numerator}/{fps.denominator}", "-i", "-",
            "-i", str(source.path),
            "-map", "0:v:0", "-map", "1:a?",
            "-vf", f"scale=out_color_matrix={matrix}:out_range=tv,format={pix_fmt}",
            "-c:v", codec, "-preset", settings.preset, "-tune", "hq",
            "-rc", "vbr", *rate_args, *video_args,
            "-colorspace", matrix, "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
            "-fps_mode", "cfr",
            *audio_args,
            "-movflags", "+faststart",
            str(self.output),
        ]
        if duration is not None:
            cmd[-1:-1] = ["-t", f"{float(duration):.6f}"]
        if codec == "hevc_nvenc":
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
