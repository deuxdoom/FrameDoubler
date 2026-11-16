# FrameDoubler Windows application

C#/.NET 10 + independently authored C++ ncnn/Vulkan. Windows 11 x64.
Intel/AMD CPUs and NVIDIA/AMD/Intel Vulkan GPUs are the support targets.
Physical validation is limited to the hardware listed in `HARDWARE-TESTS.md`;
see that file for the matrix and procedure.

## Capture preview

A first launch opens on "Convert files"; switch to "Capture preview" in the title bar (the last
page is remembered and opens next time). The preview starts only when you press "Start preview";
opening the page lists the cards and keeps the last device and mode selected. Choose another card or exact mode as needed (NV12, P010, YUY2, UYVY, RGB32, RGB24, I420 or YV12;
compressed MJPG modes are not listed). Media Foundation
is preferred; devices exposed only through DirectShow are also listed. Selections keep the device
path as well as its name. A failed MF open tries only the same device and exact mode through
DirectShow; ambiguous identical names never switch to another card. DirectShow entries remain
visible when they expose additional usable modes. Ambiguous audio matches require manual selection. The picture is processed on the GPU.

The app includes Pretendard JP Variable for Latin, Hangul and Japanese text and file names; no font installation is needed.

Frame generation and duplicate removal are **off by default**. Enable either in the side panel
when wanted; saved choices are remembered. Actual game fps is shown only with duplicate removal on.

- Duplicate removal compares every captured picture with the last new one, so a 30 or 40 fps game
  inside a 60 Hz signal keeps only its real frames, each placed at the time it really changed.
- Frame generation fills the gaps: NVIDIA DLSS Frame Generation (RTX GPUs whose driver offers
  it, up to the multiplier the driver allows), NVIDIA VFX (RTX, after its runtime is downloaded
  in Settings > Capture). Automatic tries DLSS, then VFX. If neither can run, originals
  are displayed. Live capture does not use FrameDoubler's own interpolation.
  The default is 2×; VFX supports 3× and 4× too. DLSS uses those multipliers only when
  the driver reports multi-frame support; larger requests use VFX when installed or
  are reduced to the DLSS limit. These settings multiply the actual content rate: a 30 fps game
  becomes 60, 90 or 120 fps. With duplicate
  removal off they multiply the input signal rate. Frame generation off displays the latest
  picture directly. DLSS and VFX retain the source resolution and display originals and generated frames in
  content-time order. Detected hard scene cuts reset the history
  and show an original picture instead of generating across the cut.
- The Frame generation tab has optional experimental DLSS motion controls: hardware optical
  flow (NVOF) and Edge Aegis boundary refinement. Both default off and do not apply to VFX.
  They use extra GPU time; improvement of jelly artifacts depends on the scene. Changes briefly
  restart the preview and are locked during recording countdown, recording and saving. If NVOF
  is unavailable, DLSS keeps its internal motion estimation and the panel reports the fallback.
- Anti-aliasing (edge tracing or SMAA, low/medium/high) runs once per unique frame at the source
  size, before frame generation. If SMAA fails, edge tracing takes over and the panel reports why.
- P010 (10-bit) capture supports PQ/HLG HDR input, tone mapped to BT.709 SDR for display and
  recording using Hable with a 1000-nit reference. The Colour tab offers range, matrix (including
  BT.2020) and transfer (Auto/SDR/PQ/HLG). Auto follows the device's transfer metadata or assumes
  PQ for P010 without it; the automatic HDR matrix is BT.2020. Choose SDR if the card already
  tone maps the signal. Actual applied values and assumptions are shown in the panel.
- Display: automatic sync (newest frame without tearing), VSync or tearing allowed; fit, fill
  or stretch; split or original-only comparison; hold V to see the original; output cap.
- Sound from the card's audio input is played with a delay matched to the picture.
  Output can follow the Windows default device or stay pinned to one endpoint. Changing
  it reopens only the output; video and the recording's input audio continue. Failed output
  opens retry at most three times, then wait for a selection or device change.
- Video input waits up to five seconds for its first frame and three seconds after packets
  stop. Format changes or capture errors request a restart. Static pictures and silent audio
  remain valid while packets arrive; an audio input packet timeout leaves video running.
- Screenshots (PNG) and recordings (MP4, H.264/HEVC + AAC) go to Pictures\FrameDoubler and
  Videos\FrameDoubler by default. H.264 uses fragmented MP4; HEVC completes the file when
  recording stops. Recording starts after a three-second countdown; press again to cancel.
  Video uses a constant frame-rate grid, filling gaps with the previous picture.
  Change both folders
  under Settings → Capture. Shortcuts (F7 screenshot, F8 record, F11 full screen, F6 frame
  generation, F10 statistics, V original) can be changed under Settings → Shortcuts and,
  optionally, made global.

The preview occupies a 16:9 rectangle and double-clicking it toggles full screen.
The compact main window has a fixed size that fits the monitor's work area and Windows
scale; its controls scale separately from the native preview. Minimize and move remain available.
The two panels below show signal and processing time, and preview status: estimated picture
delay in milliseconds, measured GPU usage including other apps, and pictures per
second that changed from the previous picture, including zero. The graph shows the last
minute of that display rate. Tooltips explain the measurements and show long names and status messages.
The recording timer includes hours for long sessions. Only the development PC's capture
card has been tested; other cards and GPUs are unverified.

Capture recording requires a working Media Foundation hardware H.264/HEVC encoder; it does
not fall back to software encoding. Display latency runs from the capture callback to the
display refresh and excludes the card transfer and monitor processing. Vendor-only
DeckLink/Magewell SDK sources, compressed-only video modes and vendor-specific audio
paths are not included.

## Converting files

Run `FrameDoubler.exe` in `dist/FrameDoubler`. Click the drop area or use Ctrl+O
to select videos; dropping files works too. Videos with rotation metadata (portrait phone
recordings) are converted upright, as players show them, without a rotation tag in the output. Review file and estimated motion fps,
select an output folder/preset and convert. Queue items are numbered and can be
dragged to reorder; Convert all runs every remaining item in that order, Convert
selected only the selected ones (Ctrl/Shift multi-select). Existing files are protected.
Keyboard: Delete removes the selection, Ctrl+A selects all, Alt+Up/Alt+Down move the selection,
Ctrl+Enter converts all, Ctrl+Shift+Enter converts the selection and Enter (in the list) opens the
result. All keys, including Ctrl+O, can be changed under Settings → Shortcuts → Convert files.
Double-clicking a converted video in the list plays the result in the default player.
Four fixed export presets: Source (keep bitrate, default), Source (bitrate scales with fps), YouTube 4K, YouTube 1080p. Both Source modes keep the source size/codec when available and pass audio through. The second multiplies video bitrate by output/input file fps (an already-60fps capture does not double). YouTube uses H.264 High VBR at 45 Mbps for 4K and 10 Mbps for 1080p, plus AAC-LC 48 kHz (256 kbps when the source is at least 256 kbps; otherwise 192 kbps, including unknown source bitrate). HDR is tone-mapped to SDR at the same bitrate. The 4K/1080p sizes apply to the short side, so a portrait 1080×1920 video stays 1080p. Actual VBR average and file size may vary. Device/encoder/model/HUD options are under Settings → Advanced; activity logs are collapsible.
Each queued video shows a representative thumbnail (FFmpeg thumbnail filter).
The Settings button (gear) opens General, Window and tray, Advanced, Data and About pages:
update check at startup, close/minimize button actions, advanced device/encoder/model/HUD options, data folder,
time-estimate reset, working-file cleanup, version and device information.
The frameless, fixed-size window supports dragging and minimizing.
The device menu supports automatic choice, individual Vulkan devices and CPU.
The encoder menu contains only combinations that passed an actual startup test.
NVENC/AMF/QSV and software H.264/HEVC/AV1 are considered independently of the
inference GPU. CPU/software fallbacks are shown and can be much slower.
Interpolation and ordered encoding overlap. With sufficient GPU and available system
memory, up to three isolated inference workers run concurrently; large FP32
working sets use at most two, and CPU uses one.
Failed workers are retired individually before automatic CPU fallback is considered.
Time estimates measure the selected pool's throughput and keep separate profiles per worker count.
Large-frame RGB input/output conversion also uses a bounded number of CPU threads;
inference resolution and per-pixel calculations remain unchanged.
Large FP32 Vulkan jobs move RGB8/RGB16 conversion and timestep filling to GPU
shaders and reuse the previous GPU input when its bytes match. A failed GPU
conversion retries the same request through CPU conversion and Vulkan inference.
Smaller outputs are resized only by the encoder, after interpolation at the source resolution.
Both RIFE models use FP32 activation storage and arithmetic, including for 8-bit
video. FP16 storage capability does not enable a lower-precision path. Device
selection and worker limits account for the FP32 memory footprint; automatic
device selection falls back to CPU when GPU memory is insufficient, without reducing precision.

## Optional GPU acceleration

Settings → Advanced → GPU acceleration add-on can install or remove the optional
TensorRT RTX runtime. Installation asks for consent to the official PyPI download
(about 128 MB), license terms and about 310 MB of additional disk space. It checks
fixed SHA-256 values, the actual execution-provider device, and both models against
ncnn CPU inference before committing the installation. Python is not installed.

Auto uses an installed compatible add-on for RGB8 input, preserving source
resolution and FP32 network/warp arithmetic. The measured RTX policy uses one
worker; Vulkan's worker count is not reused. The default Vulkan selection remains
available, RGB16 uses ncnn FP32, and a failed add-on retries the request with ncnn.
Device matching is checked by actual adapter identifiers; ambiguous identical
adapters use ncnn. GPU memory admission keeps the conservative ncnn fallback budget.

The package includes shared FP32 model weights and the small standalone loader,
and downloads the runtime only when installed. Updates preserve `accel/` and restore
it on rollback. Provider/runtime fingerprints keep time-estimate histories separate.
License links and downloaded notices remain with the installed add-on.

CLI: `accel status`, `accel install --accept-licenses`, `accel remove`, and
`run ... --accelerator auto|vulkan|rtx`. The install command explicitly downloads the
runtime under its MIT / Apache-2.0 / NVIDIA runtime terms described by `accel status`.

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
named for that release (`FrameDoubler_v201.zip` for 2.0.1) from that exact tag, with a
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
dotnet run --project app/FrameDoubler.Tests/FrameDoubler.Tests.csproj -- --update-package dist/FrameDoubler_v201.zip
dotnet run --project app/FrameDoubler.Tests/FrameDoubler.Tests.csproj -- --launcher-package dist/FrameDoubler dist/FrameDoubler_v201.zip
pwsh scripts/setup-dev.ps1
pwsh scripts/build.ps1
```

`--installer` exercises the update installer with real processes and folder moves (locks,
rollback, cancellation, tampering); `--launcher-package` runs the packaged launchers from a
Hangul/space path without PATH or a global .NET and drives the real GUI update flow.
Build tools are kept in the project but not in the repository, and do not install Torch or Qt.
The build uses converted MIT models, the existing FFmpeg executables and
pinned external ncnn/compiler sources described in `poc/ncnn-rife/README.md`.
The extracted package is about 536MB; the version ZIP is about 264MB, including runtime and models.
FFmpeg remains separate and replaceable. See `THIRD_PARTY_NOTICES.md` and `licenses/`.
Real-hardware verification covers only the development PC; other GPU vendors and
multi-GPU systems follow `HARDWARE-TESTS.md`. The minimal optional Torch model
definition used only for model export is isolated under `poc/ncnn-rife/baseline`.

Time estimates show one approximate duration and a completion clock. Decode-ahead overlap and completed jobs calibrate the prediction; uncertainty remains in the tooltip. Verification only decodes the first, centered middle and final minute (overlaps merged, up to 180 seconds), plus global metadata checks. Results include inspected windows/frame count; errors elsewhere may be missed. Verification remains off by default in the GUI.
