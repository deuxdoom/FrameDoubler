# Third-Party Notices

FrameDoubler is released under the MIT License (see `LICENSE`). It depends on the
third-party components listed below. None of them is stored in this repository;
they are downloaded during setup. A packaged release that bundles any of them must
ship the corresponding license texts.

| Component | Use | License | How it is used | Obligations when redistributed |
|---|---|---|---|---|
| [Practical-RIFE](https://github.com/hzwer/Practical-RIFE) (code and model weights) | The network layout in FrameDoubler's RIFE backend matches the official weights; the weights are downloaded by the user. | MIT, Copyright (c) 2021 hzwer | Independent inference-only implementation of the same architecture; weights loaded at runtime | Keep the copyright and permission notice |
| [PyTorch](https://pytorch.org) | Inference runtime | BSD-3-Clause | Python dependency | Keep the license notice |
| NVIDIA CUDA runtime libraries (bundled in PyTorch CUDA wheels) | GPU execution | NVIDIA proprietary redistributable license terms | Installed with PyTorch | Review NVIDIA's redistribution terms before bundling in a package |
| [NumPy](https://numpy.org) | Array handling | BSD-3-Clause | Python dependency | Keep the license notice |
| [FFmpeg](https://ffmpeg.org) (BtbN LGPL build) | Decoding, encoding (NVENC), muxing | LGPL-3.0-or-later for this build | Run as a separate executable; not linked into FrameDoubler | Ship the LGPL text, state the FFmpeg version and where its source can be obtained, and do not restrict replacing the executable |
| [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) | GUI icons (planned, Phase 5) | MIT, Copyright (c) Microsoft Corporation | Recoloured SVG assets | Keep the copyright and permission notice |
| [PySide6 / Qt 6](https://doc.qt.io/qtforpython-6/) | GUI toolkit (planned, Phase 5, not yet confirmed) | LGPL-3.0 | Python dependency, dynamically loaded | Ship the LGPL text and allow replacing the Qt libraries |

## Practical-RIFE notice

```
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

## Reference projects

Video2X and Flowframes were studied for architecture and known limitations only.
No source code from either project is included in FrameDoubler.
