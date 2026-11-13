"""Small, validated desktop preferences; processing jobs are never persisted."""

import json
import os
import uuid
from dataclasses import asdict, dataclass
from pathlib import Path


@dataclass(frozen=True)
class Settings:
    preset: str = "youtube"
    output_dir: str | None = None
    language: str = "ko"


def settings_path() -> Path:
    override = os.environ.get("FRAMEDOUBLER_CONFIG_DIR")
    if override:
        return Path(override) / "settings.json"
    base = Path(os.environ.get("APPDATA", Path.home() / ".config"))
    return base / "FrameDoubler" / "settings.json"


def load_settings() -> Settings:
    try:
        data = json.loads(settings_path().read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return Settings()
    if not isinstance(data, dict) or type(data.get("version")) is not int or data["version"] != 1:
        return Settings()
    preset = data.get("preset")
    language = data.get("language")
    output = data.get("output_dir")
    return Settings(preset=preset if isinstance(preset, str) and preset in ("youtube", "default") else "youtube",
                    output_dir=output if isinstance(output, str) else None,
                    language=language if isinstance(language, str) and language in ("ko", "en") else "ko")


def save_settings(settings: Settings) -> None:
    path = settings_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".settings.{uuid.uuid4().hex}.tmp")
    try:
        with temporary.open("w", encoding="utf-8") as stream:
            json.dump({"version": 1, **asdict(settings)}, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)
