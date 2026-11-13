# FrameDoubler native Windows application

C#/.NET 10 + independently authored C++ ncnn/Vulkan. Windows 11 x64.
Intel/AMD CPUs and NVIDIA/AMD/Intel Vulkan GPUs are the support targets.
Actual physical validation currently covers i7-14700KF + RTX 4080 SUPER only;
see `HARDWARE-TESTS.md` in the repository for the matrix and procedure.

Run `FrameDoubler.exe`. Add/drop videos, review file and estimated motion fps,
select an output folder/preset and convert. Existing files are protected.
The device menu supports automatic choice, individual Vulkan devices and CPU.
The encoder menu contains only combinations that passed an actual startup test.
NVENC/AMF/QSV and software H.264/HEVC/AV1 are considered independently of the
inference GPU. CPU/software fallbacks are shown and can be much slower.

No Python, PyTorch, CUDA Toolkit, separate SDK or separately installed .NET is
required by the self-contained package. GPU drivers are provided by the operating
system/hardware vendor and are not bundled. Vulkan unavailable: use CPU inference.
HDR is explicitly unsupported; 8-bit and 10-bit SDR paths are implemented.

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
timing/audio/color errors stop finalization. Required final-source holds are
reported separately from residual moving duplicates.

Native settings use `%APPDATA%/FrameDoubler/native-settings.json`; legacy settings
are read for initial migration and not overwritten. Minimize to the tray to keep
working; close/Exit cancels active work and exits after cleanup. The Updates button
checks stable releases and offers installation only for `FrameDoubler.Native.zip`
with a GitHub SHA-256 digest and a matching native manifest. Installation verifies
every file, preserves the previous directory and restores it if swapping fails.
Legacy Torch assets cannot be installed. Local staging/swap tests passed; an actual
published native-release update has not been tested.

Development:

```powershell
dotnet run --project app/FrameDoubler.Tests/FrameDoubler.Tests.csproj
dotnet run --project app/FrameDoubler.Tests/FrameDoubler.Tests.csproj -- --integration --pipeline
dotnet run --project app/FrameDoubler.Tests/FrameDoubler.Tests.csproj -- --long
dotnet run --project app/FrameDoubler.Tests/FrameDoubler.Tests.csproj -- --update-package F:\Temp\FrameDoubler\FrameDoubler.Native.zip
pwsh scripts/build-native.ps1
```

The build uses the existing development environment, converted MIT models and
pinned external ncnn/compiler sources described in `poc/ncnn-rife/README.md`.
The installer-size recommendation is about 500MB including runtime and models.
FFmpeg remains separate and replaceable. See `THIRD_PARTY_NOTICES.md` and `licenses/`.
Local test builds are not a claim that all hardware or public-release obligations
have been verified. Performance relative to CUDA and GPU zero-copy work remain
recorded in `PROGRESS.md`; Python/PyTorch is retained as a comparison baseline.
