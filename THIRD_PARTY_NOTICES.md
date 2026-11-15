# FrameDoubler native package notices

## Pretendard JP Variable

The app embeds the unmodified Pretendard JP Variable font by Kil Hyung-jin under
the SIL Open Font License 1.1. Copyright notices and the full license are in
`app/FrameDoubler.App/Assets/Fonts/OFL.txt` in the source tree and
`licenses/PretendardJP-OFL.txt` in the native package.
Source: <https://github.com/orioncactus/pretendard>.

FrameDoubler is MIT licensed. This package uses the independently authored
C#/.NET 10 application and C++ ncnn/Vulkan engine. Python, PyTorch, Qt/PySide,
CUDA Toolkit and GPU drivers are not included. Video2X/Flowframes code or model
assets are not included; technical requirements were analyzed as references.

Bundled components and notice locations:

- FrameDoubler: MIT, `licenses/FrameDoubler-MIT.txt`.
- Practical-RIFE official weights, independently converted: MIT, `licenses/Practical-RIFE-MIT.txt`.
- ONNX Runtime C API declarations used by the optional native loader: MIT, `licenses/ONNXRuntime-MIT.txt`. The default package also contains the independently converted FP32 ONNX model data, with shared external weights.
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
vendors and multi-GPU systems are untested; see `app/HARDWARE-TESTS.md` in the
repository.


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

## Development-only model export

The optional Python/Torch/pnnx model export tool under `poc/ncnn-rife` is not bundled.
Its minimal model definition converts the official weights to ncnn or ONNX.
The ONNX export tools additionally use ONNX (Apache-2.0) and ONNX Script (MIT).
Development comparisons use ONNX Runtime (MIT). Torch, Python, ONNX and ONNX Script
are development dependencies and are not included in the native package.

## Optional downloaded GPU runtime

The optional add-on downloads pinned official PyPI wheels only after the user
chooses to install it. ONNX Runtime is MIT licensed; the TensorRT-RTX EP plugin
is Apache-2.0, while its included NVIDIA runtime has a separate
[NVIDIA software license agreement](https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/reference/sla.html).
These runtime DLLs are not in the default ZIP. Their license notices, distribution
metadata, source URLs and fixed SHA-256 values are retained under `accel/rtx/`.
The app does not install Python from those wheels. The official distributions are
[ONNX Runtime](https://pypi.org/project/onnxruntime/1.32.0/) and
[TensorRT RTX EP](https://pypi.org/project/onnxruntime-ep-nv-tensorrt-rtx-cu13/0.5.0/).

## NVIDIA DLSS Frame Generation library

`engine/dlss/nvngx_dlssg.dll` is NVIDIA's unmodified DLSS Frame Generation feature library from
the official https://github.com/NVIDIA/DLSS repository (`lib/Windows_x86_64/rel`). It is NVIDIA
proprietary software under the NVIDIA RTX SDKs License in `licenses/nvidia-dlss/LICENSE.txt` and
is not covered by FrameDoubler's MIT license. FrameDoubler loads the NGX core that is part of
the installed NVIDIA driver and calls its public entry points; no NVIDIA file is modified and
no hardware check is bypassed, so the feature runs only where the driver offers it and only up
to the multiplier the driver reports. Otherwise installed NVIDIA VFX is used, or originals are displayed when neither can run.
Deleting `engine/dlss/` removes the library.

## NVIDIA Optical Flow interface headers (optional build dependency)

The optional hardware optical-flow path uses NVIDIA Optical Flow SDK interface headers
`nvOpticalFlowCommon.h` and `nvOpticalFlowD3D11.h`, copyright (c) 2018-2023 and 2019-2023
NVIDIA Corporation respectively. Each header is MIT licensed, with the same permission and
warranty text reproduced above. The complete copyright and permission notices remain in
the original headers. The optical-flow DLL is loaded from the installed display driver;
no SDK runtime, sample binaries or CUDA Toolkit is bundled.

## NVIDIA Video Effects runtime (frame generation)

The package does not contain NVIDIA's Video Effects runtime. When the user asks for it in
Settings > Capture and accepts NVIDIA's license, FrameDoubler downloads the official
`nvidia-vfx` 0.2.0.0 package from https://pypi.nvidia.com/nvidia-vfx/ , checks its size and
SHA-256, and keeps five unmodified DLLs of the Video Effects SDK runtime 1.3.0.0
(`NVVideoEffects.dll`, `nvVFXVideoFrameGeneration.dll`, `NVCVImage.dll`, `nvngxruntime.dll`,
`cudart64_12.dll`) with NVIDIA's license texts in `accel/nvvfx/`. They are NVIDIA proprietary
software and are not covered by FrameDoubler's MIT license. FrameDoubler loads them through the
public C API only. If neither DLSS nor VFX can run, live capture displays the original pictures.
The Remove button in the same place deletes them. The runtime needs the Microsoft Visual C++
runtime (MSVCP140/VCRUNTIME140), which is not included.
