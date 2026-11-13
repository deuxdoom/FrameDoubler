# FrameDoubler native package notices

FrameDoubler is MIT licensed. This package uses the independently authored
C#/.NET 10 application and C++ ncnn/Vulkan engine. Python, PyTorch, Qt/PySide,
CUDA Toolkit and GPU drivers are not included. Video2X/Flowframes code or model
assets are not included; technical requirements were analyzed as references.

Bundled components and notice locations:

- FrameDoubler: MIT, `licenses/FrameDoubler-MIT.txt`.
- Practical-RIFE official weights, independently converted: MIT, `licenses/Practical-RIFE-MIT.txt`.
- Tencent ncnn 20261020: BSD-3-Clause and its listed components, `licenses/ncnn.txt`.
- glslang pinned by the ncnn build: `licenses/glslang.txt` and `licenses/glslang/`.
- LLVM/MinGW C++ and C runtime support linked into the engine: Apache-2.0 with
  LLVM exceptions and the MinGW runtime notices in `licenses/`.
- Microsoft .NET / Windows Desktop runtime: MIT and third-party notices from
  the exact restored runtime packages, under `licenses/dotnet/`.
- FFmpeg: the existing BtbN LGPL-3.0 build, separate replaceable executables in
  `ffmpeg/`. See `licenses/FFmpeg-LGPL.txt`, `ffmpeg-version.txt` and `ffmpeg-buildconf.txt`.

FFmpeg source commit: [d838f9f9ae](https://github.com/FFmpeg/FFmpeg/tree/d838f9f9ae).
Build provider: [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds).
The full build configuration and binary hashes are preserved with this package.
Encoder availability is tested at runtime; compiled-in support does not imply
that a matching device or driver is installed. OpenH264, Kvazaar and SVT-AV1
notices are included separately when fetched by the package build.

Real-hardware verification so far covers only the development PC. Other GPU
vendors and multi-GPU systems are untested; see `HARDWARE-TESTS.md`.
