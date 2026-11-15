# FrameDoubler native package notices

## Pretendard JP Variable

The app embeds the unmodified Pretendard JP Variable font by Kil Hyung-jin under
the SIL Open Font License 1.1. The font needs no system installation. The full
copyright notices and license are in `licenses/PretendardJP-OFL.txt`.
Source: <https://github.com/orioncactus/pretendard>.

FrameDoubler is MIT licensed. This package uses the independently authored
C#/.NET 10 application and C++ ncnn/Vulkan engine. Python, PyTorch, Qt/PySide,
CUDA Toolkit and GPU drivers are not included. Video2X/Flowframes code or model
assets are not included; technical requirements were analyzed as references.

Bundled components and notice locations:

- FrameDoubler: MIT, `licenses/FrameDoubler-MIT.txt`.
- Practical-RIFE official weights, independently converted: MIT, `licenses/Practical-RIFE-MIT.txt`.
- ONNX Runtime C API declarations used by the optional native loader: MIT, `licenses/ONNXRuntime-MIT.txt`. Independently converted FP32 ONNX graphs share external weights under `models/onnx/`.
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

## Optional downloaded GPU runtime

Settings → Advanced → GPU acceleration add-on downloads official PyPI wheels only
with user consent. ONNX Runtime is MIT licensed; the TensorRT RTX EP plugin is
Apache-2.0. The included NVIDIA runtime has a separate NVIDIA Software License
Agreement: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/reference/sla.html

The default ZIP contains the loader and model data, without these runtime DLLs.
Downloaded licenses, distribution metadata, source URLs and fixed SHA-256 values
are retained under accel/rtx/. Python is not installed. Official distributions:
https://pypi.org/project/onnxruntime/1.32.0/
https://pypi.org/project/onnxruntime-ep-nv-tensorrt-rtx-cu13/0.5.0/

## NVIDIA DLSS Frame Generation library

`engine/dlss/nvngx_dlssg.dll` is NVIDIA's unmodified DLSS Frame Generation feature library from
the official https://github.com/NVIDIA/DLSS repository (`lib/Windows_x86_64/rel`). It is NVIDIA
proprietary software under the NVIDIA RTX SDKs License in `licenses/nvidia-dlss/LICENSE.txt` and
is not covered by FrameDoubler's MIT license. FrameDoubler loads the NGX core that is part of
the installed NVIDIA driver and calls its public entry points; no NVIDIA file is modified and
no hardware check is bypassed, so the feature runs only where the driver offers it and only up
to the multiplier the driver reports. Otherwise FrameDoubler's own interpolation is used.
Deleting `engine/dlss/` removes the library.

## NVIDIA Optical Flow interface headers

The optional hardware optical-flow path uses `nvOpticalFlowCommon.h` and
`nvOpticalFlowD3D11.h` from the official NVIDIA Optical Flow SDK. The driver DLL is
loaded from the installed display driver and is not bundled. No CUDA Toolkit or
SDK sample binaries are included. The following license covers these headers only:

```
Copyright (c) 2018-2023 NVIDIA Corporation (nvOpticalFlowCommon.h)
Copyright (c) 2019-2023 NVIDIA Corporation (nvOpticalFlowD3D11.h)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

## NVIDIA Video Effects runtime (frame generation)

The package does not contain NVIDIA's Video Effects runtime. When the user asks for it in
Settings > Capture and accepts NVIDIA's license, FrameDoubler downloads the official
`nvidia-vfx` 0.2.0.0 package from https://pypi.nvidia.com/nvidia-vfx/ , checks its size and
SHA-256, and keeps five unmodified DLLs of the Video Effects SDK runtime 1.3.0.0
(`NVVideoEffects.dll`, `nvVFXVideoFrameGeneration.dll`, `NVCVImage.dll`, `nvngxruntime.dll`,
`cudart64_12.dll`) with NVIDIA's license texts in `accel/nvvfx/`. They are NVIDIA proprietary
software and are not covered by FrameDoubler's MIT license. FrameDoubler loads them through the
public C API only and falls back to its own interpolation when they are missing or cannot run.
The Remove button in the same place deletes them. The runtime needs the Microsoft Visual C++
runtime (MSVCP140/VCRUNTIME140), which is not included.

Reference: Veyra-NRVideo documents the same public VFX API and separate, unmodified
runtime packaging in its [NVIDIA Video Frame Generation notice](https://github.com/Likely7/Veyra-NRVideo/blob/main/THIRD_PARTY_NOTICES.md#nvidia-video-frame-generation-local-integration-2026-10-03).
This reference does not relicense NVIDIA's runtime under FrameDoubler's MIT license
or Veyra's GPL-3.0 license. NVIDIA's original terms and third-party notices remain applicable.
