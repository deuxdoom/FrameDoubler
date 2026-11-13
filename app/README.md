# FrameDoubler Windows application

C#/.NET 10 + independently authored C++ ncnn/Vulkan. Windows 11 x64.
Intel/AMD CPUs and NVIDIA/AMD/Intel Vulkan GPUs are the support targets.
Actual physical validation currently covers i7-14700KF + RTX 4080 SUPER only;
see `HARDWARE-TESTS.md` in the repository for the matrix and procedure.

Run `FrameDoubler.exe` in `dist/FrameDoubler`. Click the drop area or use Ctrl+O
to select videos; dropping files works too. Review file and estimated motion fps,
select an output folder/preset and convert. Existing files are protected.
Device/encoder/model options are under Advanced; activity logs are collapsible.
Each queued video shows a representative thumbnail (FFmpeg thumbnail filter).
The Settings button (gear) opens General, Window and tray, Data and About pages:
language, update check at startup, close/minimize button actions, data folder,
time-estimate reset, working-file cleanup, version and device information.
The frameless title bar supports dragging, resizing, minimizing and maximizing.
The device menu supports automatic choice, individual Vulkan devices and CPU.
The encoder menu contains only combinations that passed an actual startup test.
NVENC/AMF/QSV and software H.264/HEVC/AV1 are considered independently of the
inference GPU. CPU/software fallbacks are shown and can be much slower.

No Python, PyTorch, CUDA Toolkit, separate SDK or separately installed .NET is
required by the self-contained package. GPU drivers are provided by the operating
system/hardware vendor and are not bundled. Vulkan unavailable: use CPU inference.
HDR (HLG/PQ) input is tone-mapped to 8-bit BT.709 SDR (zscale + Hable) when
decoded; 8-bit and 10-bit SDR paths are kept as they are.

```text
FrameDoubler.Cli hardware
FrameDoubler.Cli analyze video.mp4
FrameDoubler.Cli run -i video.mp4 -o smooth.mp4 --preset youtube
FrameDoubler.Cli run -i video.mp4 --device cpu --software --codec h264
FrameDoubler.Cli run -i video.mp4 --device gpu:1 --encoder qsv --codec hevc
FrameDoubler.Cli run -i video.mp4 --fps 120 --model v4.26
FrameDoubler.Cli verify video.mp4 smooth.mp4 --fps 60
FrameDoubler.Cli batch recordings -o converted --recursive
```

`--no-verify` skips output validation. `--overwrite` explicitly allows replacement.
`--hud-region X,Y,W,H` may be repeated. `--quality 1..51` or `--bitrate 24M`
controls encoding; quality scales differ by encoder. The automatic codec respects
input bit depth and available encoders. YouTube defaults to H.264 when available.
For >60fps output, low-motion/low-resolution footage can raise a heuristic quality
warning; a completed file is kept for review and the CLI returns 2. Structural
timing/audio/color errors stop finalization. Actual duplicates at input-proven
scene/static hold positions and required final-source holds are reported separately
from residual moving duplicates. The 1% moving-duplicate limit is unchanged.

The package is portable: `settings.json`, `performance.json` (measured speed profiles
for time estimates) and the `work` folder live next to `FrameDoubler.exe`.
AppData is not used. Output verification is off by default in the GUI and
included in the time estimate when enabled; `run` in the CLI verifies unless
`--no-verify` is given. `estimate FILE [--preset youtube] [--verify]` prints the
sampled analysis and the expected time range for the selected device/settings. The minimize and close
buttons (and tray Exit) ask whether to minimize to the tray, where work continues,
or to exit, which cancels active work and cleans up. FFmpeg and engine processes
are in a kill-on-close job, so none survive an exit or crash. The Updates button
checks stable releases: when up to date it only says so; a newer release opens
the notes and download/verify/keep-settings/restart steps. It installs only `FrameDoubler.zip`
with a GitHub SHA-256 digest and a matching native manifest. Installation verifies
every file, preserves the previous directory and restores it if swapping fails.
Legacy Torch assets cannot be installed. Local staging/swap tests passed; an actual
published native-release update has not been tested.

Development:

```powershell
dotnet run --project app/FrameDoubler.Tests/FrameDoubler.Tests.csproj
dotnet run --project app/FrameDoubler.Tests/FrameDoubler.Tests.csproj -- --integration --pipeline
dotnet run --project app/FrameDoubler.Tests/FrameDoubler.Tests.csproj -- --long
dotnet run --project app/FrameDoubler.Tests/FrameDoubler.Tests.csproj -- --update-package dist/FrameDoubler.zip
pwsh scripts/setup-dev.ps1
pwsh scripts/build.ps1
```

Build tools live outside the repository and do not install Torch or Qt.
The build uses converted MIT models, the existing FFmpeg executables and
pinned external ncnn/compiler sources described in `poc/ncnn-rife/README.md`.
The installer-size recommendation is about 500MB including runtime and models.
FFmpeg remains separate and replaceable. See `THIRD_PARTY_NOTICES.md` and `licenses/`.
Real-hardware verification covers only the development PC; other GPU vendors and
multi-GPU systems follow `HARDWARE-TESTS.md`. The minimal optional Torch model
definition used only for model export is isolated under `poc/ncnn-rife/baseline`.
