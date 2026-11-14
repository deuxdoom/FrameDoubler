"""Model weight files: where they live and how they are downloaded."""

import hashlib
import io
import os
import urllib.request
import zipfile
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path


class ModelError(RuntimeError):
    pass


@dataclass(frozen=True)
class ModelSpec:
    name: str
    drive_id: str  # Google Drive file id published in the Practical-RIFE README
    sha256: str  # of flownet.pkl


MODELS = {
    "v4.25": ModelSpec("v4.25", "1ZKjcbmt1hypiFprJPIKW0Tt0lr_2i7bg",
                       "6615790efd627772917205db291f51cd392528a157ecbb2ecaeec3bff8eb6de2"),
    "v4.26": ModelSpec("v4.26", "1gViYvvQrtETBgU1w8axZSsr7YUuw31uy",
                       "45c7f74156704769dc9f85cfcaf8552e1e926f9399dcfa3a553dee88fac6f53f"),
}
DEFAULT_MODEL = "v4.25"


def models_dir() -> Path:
    """Development-only weight cache; no app or frozen-runtime paths."""
    return Path(os.environ.get("FRAMEDOUBLER_MODELS", Path(__file__).resolve().parents[3] / "tools/dev/ncnn/weights"))


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def ensure_model(name: str = DEFAULT_MODEL, check_cancelled: Callable[[], None] | None = None) -> Path:
    """Returns the path of the weight file, downloading it on first use."""
    spec = MODELS.get(name)
    if spec is None:
        raise ModelError(f"Unknown model '{name}'. Available: {', '.join(MODELS)}")
    target = models_dir() / f"rife-{name}" / "flownet.pkl"
    if target.is_file():
        return target

    url = f"https://drive.usercontent.google.com/download?id={spec.drive_id}&export=download&confirm=t"
    try:
        with urllib.request.urlopen(url, timeout=15) as response:
            chunks = []
            while True:
                if check_cancelled:
                    check_cancelled()
                chunk = response.read(1024 * 1024)
                if not chunk:
                    break
                chunks.append(chunk)
            data = b"".join(chunks)
        archive = zipfile.ZipFile(io.BytesIO(data))
    except (OSError, zipfile.BadZipFile) as exc:
        if check_cancelled:
            check_cancelled()
        raise ModelError(
            f"Could not download RIFE {name}: {exc}. Download it from the Practical-RIFE "
            f"README and place flownet.pkl at {target}"
        ) from exc
    # Only the weights are taken; the archive's Python files are never extracted or run.
    member = next((m for m in archive.namelist()
                   if m.endswith("/flownet.pkl") and "__MACOSX" not in m), None)
    if member is None:
        raise ModelError(f"The RIFE {name} archive does not contain flownet.pkl")
    target.parent.mkdir(parents=True, exist_ok=True)
    partial = target.with_suffix(".part")
    try:
        if check_cancelled:
            check_cancelled()
        partial.write_bytes(archive.read(member))
        actual = _sha256(partial)
        if actual != spec.sha256:
            raise ModelError(f"RIFE {name} checksum mismatch: expected {spec.sha256}, got {actual}")
        partial.replace(target)
    finally:
        partial.unlink(missing_ok=True)
    return target
