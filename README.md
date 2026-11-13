# FrameDoubler

RIFE frame interpolation for 30fps game captures. FrameDoubler detects the duplicated
frames in a 59.94/60fps recording of a 30fps game, restores the original motion, and
interpolates it to real 60fps video for upload.

Status: the command line converter works; the GUI is not built yet. See `plan.md` for the design and `todo.md` for progress.

## Requirements

- Windows 11, NVIDIA GPU
- Python 3.12 or later

## Setup

```powershell
pwsh scripts\setup.ps1
```

The script creates `.venv`, installs PyTorch (CUDA build) and the project, and
downloads FFmpeg into `tools\ffmpeg`.

```powershell
.venv\Scripts\framedoubler --version
.venv\Scripts\python -m pytest
```

## Usage

```powershell
framedoubler analyze input.mp4            # format, motion rate and processing plan
framedoubler run -i input.mp4 --verify    # writes input_60fps.mp4 and checks it
framedoubler verify input.mp4 output.mp4  # checks an existing conversion
```

The RIFE model is downloaded on first use.

## License

MIT. Third-party components are listed in `THIRD_PARTY_NOTICES.md`.
