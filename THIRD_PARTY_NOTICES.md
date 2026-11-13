# Third-Party Notices

FrameDoubler is released under the MIT License (see `LICENSE`). It depends on the
third-party components listed below. Runtime libraries and model weights are
downloaded during setup or first use; selected icon assets are included. A packaged release that bundles any of them must
ship the corresponding license texts.

| Component | Use | License | How it is used | Obligations when redistributed |
|---|---|---|---|---|
| [Practical-RIFE](https://github.com/hzwer/Practical-RIFE) (code and model weights) | The network layout in FrameDoubler's RIFE backend matches the official weights; the weights are downloaded by the user. | MIT, Copyright (c) 2021 hzwer | Independent inference-only implementation of the same architecture; weights loaded at runtime | Keep the copyright and permission notice |
| [PyTorch](https://pytorch.org) | Inference runtime | BSD-3-Clause | Python dependency | Keep the license notice |
| NVIDIA CUDA runtime libraries (bundled in PyTorch CUDA wheels) | GPU execution | NVIDIA proprietary redistributable license terms | Installed with PyTorch | Review NVIDIA's redistribution terms before bundling in a package |
| [NumPy](https://numpy.org) | Array handling | BSD-3-Clause | Python dependency | Keep the license notice |
| [FFmpeg](https://ffmpeg.org) (BtbN LGPL build) | Decoding, encoding (NVENC), muxing | LGPL-3.0-or-later for this build | Run as a separate executable; not linked into FrameDoubler | Ship the LGPL text, state the FFmpeg version and where its source can be obtained, and do not restrict replacing the executable |
| [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) | GUI checkmark | MIT, Copyright (c) 2020 Microsoft Corporation | Recoloured SVG asset; notice in `assets/icons/LICENSE-fluent.txt` | Keep the copyright and permission notice |
| [PySide6 Essentials / Qt 6](https://doc.qt.io/qtforpython-6/) and Shiboken6 | GUI toolkit, Qt Widgets/SVG; tested with 6.12.0 on Python 3.14 | LGPL-3.0 (community distribution; see included Qt module notices) | Python dependency, dynamically loaded; no GPL-only Qt modules used | Ship the LGPL text and module notices, provide corresponding source location, and allow replacing the Qt libraries |

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

## Local package candidates

The folder package includes Python (PSF license), the runtime libraries above
and their transitive dependencies. `licenses/` contains license/notice files
copied from the build environment, the Python license, LGPL/GPL texts and NVIDIA
CUDA/cuDNN terms. `manifest.json` records component versions, legal download
sources and file hashes. An inventory entry may describe a build dependency
rather than a bundled runtime component; it is not a claim that all components
listed there are shipped.

PyInstaller 6.22.3 and its hooks are build tools. Its GPL exception permits the
resulting application to retain its own license; see the
[PyInstaller license](https://pyinstaller.org/en/stable/license.html). No modified
PyInstaller source is shipped. Python dependencies collected by the packaging
hooks retain their respective licenses and notices.

The local candidate is not a completed redistribution audit. Exact Qt/FFmpeg
corresponding sources/build configurations and NVIDIA DLL redistribution terms
must be checked before publishing. See [distribution status](docs/distribution.md).
Qt libraries live under `_internal/PySide6`, and FFmpeg under `tools/ffmpeg/bin`;
neither is hidden in a self-extracting executable or protected against replacement.
