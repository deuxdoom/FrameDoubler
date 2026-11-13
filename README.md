# FrameDoubler

RIFE frame interpolation for 30fps game captures. FrameDoubler detects the duplicated
frames in a 59.94/60fps recording of a 30fps game, restores the original motion, and
interpolates it to real 60fps video for upload.

Interpolation uses the pretrained [Practical-RIFE](https://github.com/hzwer/Practical-RIFE)
models through an inference-only PyTorch/CUDA implementation. FrameDoubler's
capture analysis, timeline, processing pipeline and user interface are developed
independently. Model and dependency notices are in `THIRD_PARTY_NOTICES.md`.

The Windows desktop interface and command line converter are available. See `plan.md` for the design and `todo.md` for progress and remaining validation.

## Requirements

- Windows 11, NVIDIA GPU
- Python 3.14 (tested development environment)

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
framedoubler gui
# Or launch .venv\Scripts\framedoubler-gui.exe
```

Drop videos into the desktop window, review the file and estimated motion rates,
then start conversion. The default YouTube preset includes output verification.
The queue processes videos sequentially and saves `<input>_smooth.mp4` in the
selected folder. The interface supports Korean and English, cancellation and
explicit overwrite. Verification passed and conversion without verification are
shown separately.

The last preset, output folder and language are restored on restart. Preferences
are stored at `%APPDATA%\FrameDoubler\settings.json`; set
`FRAMEDOUBLER_CONFIG_DIR` to use another directory. Overwrite permission and the
queue are reset on each launch.

```powershell
framedoubler analyze input.mp4            # format, motion rate and processing plan
framedoubler run -i input.mp4 --verify    # writes input_60fps.mp4 and checks it
framedoubler run -i input.mp4 --preset youtube --verify
framedoubler run -i input.mp4 --codec av1 --quality 19
framedoubler run -i input.mp4 --codec hevc --bitrate 24M
framedoubler run -i input.mp4 --fps 120 --verify  # local high-frame-rate output
framedoubler verify input.mp4 output.mp4  # checks an existing conversion
framedoubler verify input.mp4 input_120fps.mp4 --fps 120
framedoubler batch "D:\Captures" --preset youtube --verify
framedoubler batch "D:\Captures" -o "D:\Converted" --recursive
```

The RIFE model is downloaded on first use.

`--fps` accepts a positive integer or rational rate such as `120000/1001`
(119.88fps), `120` or `144`. An explicit rate is also used by `run --verify`;
pass it to standalone `verify --fps` when checking a high-rate file later.
Without `-o`, an explicit rate is included in the filename. The desktop interface
offers 119.88/120fps and switches to the default HEVC preset for these rates.
The YouTube preset supports up to 60fps. Higher rates increase interpolation work
and output size; playback depends on the player's codec and display support.
Duplicate detection is a heuristic. Its luma thresholds scale with the shorter
frame interval above 60fps; timing and audio checks still use one output frame
as their tolerance. Non-integral motion multipliers can produce duplicate
warnings and should be reviewed rather than treated as guaranteed quality.

`batch` snapshots supported videos in a folder and converts them sequentially.
By default, outputs go into its `FrameDoubler` subfolder with `_smooth.mp4` names.
`--recursive` preserves relative subfolders and excludes the output tree. Name
collisions receive `_2`, `_3`, etc.; names remain stable on reruns. Existing files
still require `--overwrite`. Individual errors are reported and remaining files
continue; `--stop-on-error` stops at the first failure. Exit status is 0 on full
success, 2 on file/verification failures and 130 on cancellation. Completed files
remain available when a later job fails or is cancelled. Supported extensions:
MP4, MKV, MOV, AVI, WebM, M4V, TS, MTS and M2TS. Actual codec support is checked
for each file. `--max-frames` trial conversions skip full-file verification.

Existing outputs are protected by default. Use `--overwrite` to replace one after
a successful conversion. Ctrl+C cancels conversion and removes the incomplete
output. A failed or cancelled overwrite preserves the previous file.

Add `--profile` to report time spent probing, decoding, identifying duplicates,
interpolating and encoding. These are wall-clock timings and include pipe waits.
For reproducible resolution comparisons and device-wide GPU counters, use
`scripts/benchmark.py`; keep its outputs and JSON reports outside the repository.
Input decoding and duplicate classification run ahead in a bounded two-frame
queue. `--serial` disables this overlap for diagnostics. Profile reports show
the input worker separately because its time overlaps the main processing stages.

The default encoder is HEVC NVENC at quality 19. `--codec` accepts `h264`, `hevc`
or `av1`. Choose `--quality 1`–`51` (lower is better) or a target VBR `--bitrate`
in bits/s, with optional `k` or `M` units. Quality values are encoder-specific;
the same number does not guarantee the same visual quality across codecs.

`--preset youtube` selects H.264 High, 4:2:0, Fast Start MP4 and an automatic
SDR bitrate: 12/24/68 Mbps for 1080p/1440p/4K above 30fps, or 8/16/45 Mbps up
to 30fps. It keeps the resolution and output frame rate. The preset supports up
to 4K and 60fps, including 59.94fps. Video bitrate is a target; simple scenes
can use fewer bits. Audio is converted to AAC-LC at 48kHz, preserving mono,
stereo or 5.1; other channel counts are mixed to stereo. Normal conversions copy
the source audio. Explicit codec, quality or bitrate options override the preset.
HEVC/AV1 overrides use the same conservative bitrate targets. H.264 encoding of
10-bit input is unavailable on RTX 4080 SUPER; choose HEVC or AV1.

Targets follow [YouTube's upload recommendations](https://support.google.com/youtube/answer/1722171?hl=en).
MP4 edit lists remain enabled to preserve AAC timing; details are in `docs/research.md` (R8).

For a fixed HUD, add `--hud-region X,Y,W,H` (coordinates in source pixels),
repeating the option for multiple rectangles. Only pixels identical in both
source frames are restored. Leave moving scene content outside these regions;
HUD protection is disabled by default. Restoration is exact before encoding,
while the output codec still introduces its normal compression loss.

## License

MIT. Third-party components are listed in `THIRD_PARTY_NOTICES.md`.
