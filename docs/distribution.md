# Windows package candidates

Run `pwsh scripts\build.ps1` after development setup. Each build gets a new folder
under `F:\Temp\FrameDoubler\package`; `-OutputRoot` selects an external directory
on another build machine. No existing bundle is deleted or overwritten. The
build uses PyInstaller 6.22.3 and hooks 2026.10, which support Python 3.14.

For a user test build in the checkout, run `pwsh scripts\build.ps1 -DistPath dist`.
This writes `dist/FrameDoubler/FrameDoubler.exe`, the companion CLI/runtime and
`dist/FrameDoubler-windows-x64.zip` with its checksum. Intermediate work and caches
stay outside the checkout. An existing `FrameDoubler` bundle is protected; use an
empty destination for the next build. `dist` is excluded from Git.

The folder contains `FrameDoubler.exe` (windowed GUI), `FrameDoubler-cli.exe`, a
shared `_internal` runtime, replaceable `tools\ffmpeg\bin` executables, notices
and a manifest with versions and SHA-256 hashes. The ZIP and its checksum sit
beside the folder. Keep the whole folder together. Model weights are downloaded
on first use with SHA-256 validation; the packaged app stores them in
`%LOCALAPPDATA%\FrameDoubler\models`. `FRAMEDOUBLER_MODELS` overrides this location.
The archive contains no recordings or pretrained weights.

License downloads retry transient network failures and reuse snapshots from
`<OutputRoot>/license-cache` only when their stored URL and SHA-256 match.
To refresh a legal snapshot for a new distribution review, use a new output/cache
folder. The manifest records the license URLs and hashes of the staged files.

`--self-test <absolute report.json>` on either executable checks packaged imports,
Qt startup/shutdown, icons, FFmpeg and a small CUDA operation when CUDA is present.
It does not download weights. Set `FRAMEDOUBLER_CONFIG_DIR` to a disposable folder
when testing. Automated CI uses Qt's offscreen platform and cannot certify GPU
conversion or a physical Windows desktop.

## Before public distribution

This is a **local candidate**, not a published or signed release. The remaining
acceptance test is a separate Windows 11 x64 PC without Python, PyTorch, FFmpeg
or the source checkout: extract the ZIP, start the GUI, download the first model,
convert and verify a sample, cancel a job, restart preferences, and run the CLI.
Test a current NVIDIA driver compatible with the bundled CUDA runtime. Record
the GPU/driver, self-test JSON and conversion report in `todo.md`.

License inventory is copied from the build environment and supplemented with
LGPL/GPL texts and NVIDIA license snapshots. The manifest lists these sources.
Before publishing, confirm the redistribution terms for every bundled NVIDIA
DLL and the exact FFmpeg/Qt corresponding-source obligations. FFmpeg and Qt are
unmodified dynamic/executable components and can be replaced by the user. Do not
apply terms that restrict this replacement or debugging permitted by LGPL.

Source locations: [FrameDoubler](https://github.com/deuxdoom/FrameDoubler),
[FFmpeg build scripts/configuration](https://github.com/BtbN/FFmpeg-Builds),
[FFmpeg source](https://git.ffmpeg.org/ffmpeg.git),
[Qt source archives](https://download.qt.io/archive/qt/),
[PySide/Shiboken source](https://download.qt.io/official_releases/QtForPython/),
[PyTorch source](https://github.com/pytorch/pytorch).
The included FFmpeg version string records its upstream commit, but a stable
copy of the matching build configuration and corresponding sources must be
prepared before external distribution; the setup URL currently tracks a moving
BtbN release. The Qt version is recorded in the self-test/build manifest.

The manual GitHub Actions workflow creates an artifact only. It never pushes,
creates tags or publishes releases. A local candidate can be tested before any
distribution decision. Code signing and an installer are not part of this step.
