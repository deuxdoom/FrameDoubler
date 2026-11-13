"""Locates the FFmpeg executables used for probing, decoding and encoding."""

import os
import shutil
from functools import cache
from pathlib import Path


class FFmpegNotFoundError(RuntimeError):
    pass


@cache
def _find(name: str) -> str:
    exe = f"{name}.exe" if os.name == "nt" else name
    candidates = []
    env_dir = os.environ.get("FRAMEDOUBLER_FFMPEG_DIR")
    if env_dir:
        candidates.append(Path(env_dir) / exe)
    # Repository layout: <root>/src/framedoubler/ffmpeg.py -> <root>/tools/ffmpeg/bin
    candidates.append(Path(__file__).resolve().parents[2] / "tools" / "ffmpeg" / "bin" / exe)
    for candidate in candidates:
        if candidate.is_file():
            return str(candidate)
    on_path = shutil.which(name)
    if on_path:
        return on_path
    raise FFmpegNotFoundError(
        f"{exe} was not found. Run scripts\\setup.ps1 or set FRAMEDOUBLER_FFMPEG_DIR."
    )


def ffmpeg_path() -> str:
    return _find("ffmpeg")


def ffprobe_path() -> str:
    return _find("ffprobe")
