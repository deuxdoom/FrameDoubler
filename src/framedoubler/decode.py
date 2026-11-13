"""Sequential frame decoding through an FFmpeg pipe."""

import subprocess
import threading
from collections.abc import Iterator

import numpy as np

from framedoubler.ffmpeg import ffmpeg_path
from framedoubler.probe import VideoInfo


class DecodeError(RuntimeError):
    pass


def iter_frames(info: VideoInfo, max_frames: int | None = None,
                stop_event: threading.Event | None = None) -> Iterator[tuple[int, np.ndarray]]:
    """Yields (pts, frame) for every frame in presentation order.

    `pts` is in `info.time_base` ticks. Frames are RGB arrays of shape (H, W, 3):
    uint8 for 8-bit sources and uint16 for deeper sources, so precision is kept.
    """
    deep = info.bit_depth > 8
    dtype = np.dtype("<u2") if deep else np.dtype(np.uint8)
    frame_bytes = info.width * info.height * 3 * dtype.itemsize
    cmd = [
        ffmpeg_path(), "-nostdin", "-loglevel", "error",
        "-i", str(info.path),
        "-map", "0:v:0", "-fps_mode", "passthrough",
        "-f", "rawvideo", "-pix_fmt", "rgb48le" if deep else "rgb24", "-",
    ]
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)
    errors: list[bytes] = []
    drain = threading.Thread(target=lambda: errors.append(proc.stderr.read()), daemon=True)
    drain.start()
    finished = threading.Event()
    watcher = None
    if stop_event is not None:
        def watch():
            while not finished.wait(0.05):
                if stop_event.is_set():
                    try:
                        if proc.poll() is None:
                            proc.kill()
                    except OSError:
                        pass
                    return
        watcher = threading.Thread(target=watch, name="FrameDoubler-decode-stop", daemon=True)
        watcher.start()

    limit = info.frame_count if max_frames is None else min(max_frames, info.frame_count)
    index = 0
    try:
        while index < limit:
            buf = bytearray(frame_bytes)
            view = memoryview(buf)
            filled = 0
            # A short read means the frame is still arriving; only EOF ends the stream.
            while filled < frame_bytes:
                n = proc.stdout.readinto(view[filled:])
                if not n:
                    break
                filled += n
            if filled == 0:
                break
            if filled != frame_bytes:
                raise DecodeError(f"Truncated frame {index}: {filled} of {frame_bytes} bytes")
            frame = np.frombuffer(buf, dtype=dtype).reshape(info.height, info.width, 3)
            yield info.pts[index], frame
            index += 1
        if max_frames is None and index != info.frame_count:
            drain.join(timeout=5)
            detail = b"".join(errors).decode(errors="replace").strip()
            raise DecodeError(
                f"Decoded {index} frames but the container has {info.frame_count}. {detail}"
            )
    finally:
        finished.set()
        proc.stdout.close()
        try:
            if proc.poll() is None:
                proc.kill()
        except OSError:
            pass
        proc.wait()
        drain.join(timeout=5)
        proc.stderr.close()
        if watcher:
            watcher.join(timeout=1)
