# FrameDoubler Windows application

C#/.NET 10 + independently authored C++ ncnn/Vulkan. Windows 11 x64.
Intel/AMD CPUs and NVIDIA/AMD/Intel Vulkan GPUs are the support targets.
Actual physical validation currently covers i7-14700KF + RTX 4080 SUPER only;
see `HARDWARE-TESTS.md` in the repository for the matrix and procedure.

Run `FrameDoubler.exe` in `dist/FrameDoubler`. Click the drop area or use Ctrl+O
to select videos; dropping files works too. Review file and estimated motion fps,
select an output folder/preset and convert. Queue items are numbered and can be
dragged to reorder; Convert all runs every remaining item in that order, Convert
selected only the selected ones (Ctrl/Shift multi-select). Existing files are protected.
Four fixed export presets: Source (keep bitrate, default), Source (bitrate scales with fps), YouTube 4K, YouTube 1080p. Both Source modes keep the source size/codec when available and pass audio through. The second multiplies video bitrate by output/input file fps (an already-60fps capture does not double). YouTube uses H.264 High VBR at 45 Mbps for 4K and 10 Mbps for 1080p, plus AAC-LC 48 kHz (256 kbps when the source is at least 256 kbps; otherwise 192 kbps, including unknown source bitrate). HDR is tone-mapped to SDR at the same bitrate. Actual VBR average and file size may vary. Device/encoder/model/HUD options are under Settings → Advanced; activity logs are collapsible.
Each queued video shows a representative thumbnail (FFmpeg thumbnail filter).
The Settings button (gear) opens General, Window and tray, Advanced, Data and About pages:
update check at startup, close/minimize button actions, advanced device/encoder/model/HUD options, data folder,
time-estimate reset, working-file cleanup, version and device information.
The frameless title bar supports dragging, resizing, minimizing and maximizing.
The device menu supports automatic choice, individual Vulkan devices and CPU.
The encoder menu contains only combinations that passed an actual startup test.
NVENC/AMF/QSV and software H.264/HEVC/AV1 are considered independently of the
inference GPU. CPU/software fallbacks are shown and can be much slower.
Interpolation and ordered encoding overlap. With sufficient GPU and available system
memory, up to three isolated inference workers run concurrently; CPU uses one.
Failed workers are retired individually before automatic CPU fallback is considered.
Time estimates measure the selected pool's throughput and keep separate profiles per worker count.
Large-frame RGB input/output conversion also uses a bounded number of CPU threads;
inference resolution and per-pixel calculations remain unchanged.
Smaller outputs are resized only by the encoder, after interpolation at the source resolution.

No Python, PyTorch, CUDA Toolkit, separate SDK or separately installed .NET is
required by the self-contained package. GPU drivers are provided by the operating
system/hardware vendor and are not bundled. Vulkan unavailable: use CPU inference.
HDR (HLG/PQ) input is tone-mapped to 8-bit BT.709 SDR (zscale + Hable) when
decoded; 8-bit and 10-bit SDR paths are kept as they are.

```text
FrameDoubler.Cli hardware
FrameDoubler.Cli analyze video.mp4
FrameDoubler.Cli run -i video.mp4 -o smooth.mp4 --preset youtube-2160
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

The package is portable: `settings.ini` (UTF-8, `[FrameDoubler]` section),
`performance.json` (measured speed profiles for time estimates) and the `work` folder
live next to `FrameDoubler.exe`. A `settings.json` from 1.0.1 or earlier is migrated
to `settings.ini` once. `FrameDoubler.exe` and `FrameDoubler.Cli.exe` are small native
launchers; the self-contained .NET runtime and the application assemblies are in `dll/`.
Keep the whole folder together. AppData is not used. Output verification is off by default in the GUI and
included in the time estimate when enabled; `run` in the CLI verifies unless
`--no-verify` is given. `estimate FILE [--preset youtube-2160] [--verify]` prints the
sampled analysis and a point estimate with its uncertainty range for the selected device/settings. Close (and tray Exit)
asks before exiting, which cancels active work and cleans up. Minimize goes to the
taskbar by default; hiding to the tray requires an explicit setting. The tray menu
uses the main graphite/orange palette and action icons. Launching the GUI again
activates the existing window, including minimized/tray windows, rather than starting
another GUI instance. Close behavior is configurable in Settings. FFmpeg and engine processes
are in a kill-on-close job, so none survive an exit or crash. The Updates button
checks stable releases: when up to date it only says so; a newer release opens
the notes and download/verify/keep-settings/restart steps. It installs only the asset
named for that release (`FrameDoubler_v102.zip` for 1.0.2) from that exact tag, with a
GitHub SHA-256 digest and a matching native manifest. A copy of the installer runs beside
the installation, re-checks every file and confirms it is ready before FrameDoubler exits.
After the exact process has exited, an installer window moves the old folder to a backup
(retrying locked files for up to 30 seconds), installs the new one and restarts it. The update
completes only when the new version confirms startup; otherwise the previous folder is
restored and reopened. The result is written to `work/updates/last-install.json`, and only
the newest `FrameDoubler.previous-*` backup is kept. Updaters in 1.0.0/1.0.1 look only for
`FrameDoubler.zip`; without that asset, download the new ZIP manually and copy
`settings.json`/`performance.json` into the new folder.

Development:

```powershell
dotnet run --project app/FrameDoubler.Tests/FrameDoubler.Tests.csproj
dotnet run --project app/FrameDoubler.Tests/FrameDoubler.Tests.csproj -- --integration --pipeline
dotnet run --project app/FrameDoubler.Tests/FrameDoubler.Tests.csproj -- --long
dotnet run --project app/FrameDoubler.Tests/FrameDoubler.Tests.csproj -- --installer
dotnet run --project app/FrameDoubler.Tests/FrameDoubler.Tests.csproj -- --update-package dist/FrameDoubler_v102.zip
dotnet run --project app/FrameDoubler.Tests/FrameDoubler.Tests.csproj -- --launcher-package dist/FrameDoubler dist/FrameDoubler_v102.zip
pwsh scripts/setup-dev.ps1
pwsh scripts/build.ps1
```

`--installer` exercises the update installer with real processes and folder moves (locks,
rollback, cancellation, tampering); `--launcher-package` runs the packaged launchers from a
Hangul/space path without PATH or a global .NET and drives the real GUI update flow.
Build tools are kept in the project but not in the repository, and do not install Torch or Qt.
The build uses converted MIT models, the existing FFmpeg executables and
pinned external ncnn/compiler sources described in `poc/ncnn-rife/README.md`.
The installer-size recommendation is about 500MB including runtime and models.
FFmpeg remains separate and replaceable. See `THIRD_PARTY_NOTICES.md` and `licenses/`.
Real-hardware verification covers only the development PC; other GPU vendors and
multi-GPU systems follow `HARDWARE-TESTS.md`. The minimal optional Torch model
definition used only for model export is isolated under `poc/ncnn-rife/baseline`.

Time estimates show one approximate duration and a completion clock. Decode-ahead overlap and completed jobs calibrate the prediction; uncertainty remains in the tooltip. Verification only decodes the first, centered middle and final minute (overlaps merged, up to 180 seconds), plus global metadata checks. Results include inspected windows/frame count; errors elsewhere may be missed. Verification remains off by default in the GUI.
