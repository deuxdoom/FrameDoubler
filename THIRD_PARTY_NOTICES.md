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

This is a local test candidate. Verification on other physical hardware and the
exact corresponding-source/dependency/build-recipe review remain required before
a public release. See `app/HARDWARE-TESTS.md` and `PROGRESS.md` in the repository.


## Practical-RIFE notice

```text
MIT License

Copyright (c) 2021 hzwer

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Development-only comparison tools

The optional Python/Torch/pnnx tools under `poc/ncnn-rife` are not bundled.
Their minimal model definition was retained from this project solely for model export and numerical comparison.
The retired Python/Qt app and its tests/build tools were moved outside the repository; see `app/CLEANUP.md`.
