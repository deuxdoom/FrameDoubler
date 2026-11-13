"""RIFE v4.25 / v4.26 backend on PyTorch (CUDA).

The network layout has to match the official Practical-RIFE weights
(https://github.com/hzwer/Practical-RIFE, MIT License, Copyright (c) 2021 hzwer).
This is an inference-only implementation; see THIRD_PARTY_NOTICES.md.
"""

from dataclasses import dataclass
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

from framedoubler.interp.base import Interpolator
from framedoubler.models import DEFAULT_MODEL, ensure_model


def _conv(in_planes: int, out_planes: int, stride: int) -> nn.Sequential:
    return nn.Sequential(nn.Conv2d(in_planes, out_planes, 3, stride, 1), nn.LeakyReLU(0.2, True))


class _Head(nn.Module):
    def __init__(self):
        super().__init__()
        self.cnn0 = nn.Conv2d(3, 16, 3, 2, 1)
        self.cnn1 = nn.Conv2d(16, 16, 3, 1, 1)
        self.cnn2 = nn.Conv2d(16, 16, 3, 1, 1)
        self.cnn3 = nn.ConvTranspose2d(16, 4, 4, 2, 1)
        self.relu = nn.LeakyReLU(0.2, True)

    def forward(self, x):
        x = self.relu(self.cnn0(x))
        x = self.relu(self.cnn1(x))
        x = self.relu(self.cnn2(x))
        return self.cnn3(x)


class _ResConv(nn.Module):
    def __init__(self, c: int):
        super().__init__()
        self.conv = nn.Conv2d(c, c, 3, 1, 1)
        self.beta = nn.Parameter(torch.ones((1, c, 1, 1)))
        self.relu = nn.LeakyReLU(0.2, True)

    def forward(self, x):
        return self.relu(self.conv(x) * self.beta + x)


class _IFBlock(nn.Module):
    def __init__(self, in_planes: int, c: int):
        super().__init__()
        self.conv0 = nn.Sequential(_conv(in_planes, c // 2, 2), _conv(c // 2, c, 2))
        self.convblock = nn.Sequential(*[_ResConv(c) for _ in range(8)])
        self.lastconv = nn.Sequential(nn.ConvTranspose2d(c, 4 * 13, 4, 2, 1), nn.PixelShuffle(2))

    def forward(self, x, flow, scale):
        x = F.interpolate(x, scale_factor=1.0 / scale, mode="bilinear", align_corners=False)
        if flow is not None:
            flow = F.interpolate(flow, scale_factor=1.0 / scale, mode="bilinear", align_corners=False) / scale
            x = torch.cat((x, flow), 1)
        out = self.lastconv(self.convblock(self.conv0(x)))
        out = F.interpolate(out, scale_factor=scale, mode="bilinear", align_corners=False)
        return out[:, :4] * scale, out[:, 4:5], out[:, 5:]


class _IFNet(nn.Module):
    def __init__(self):
        super().__init__()
        self.block0 = _IFBlock(7 + 8, c=192)
        self.block1 = _IFBlock(8 + 4 + 8 + 8, c=128)
        self.block2 = _IFBlock(8 + 4 + 8 + 8, c=96)
        self.block3 = _IFBlock(8 + 4 + 8 + 8, c=64)
        self.block4 = _IFBlock(8 + 4 + 8 + 8, c=32)
        self.encode = _Head()
        self._grid: tuple[tuple, torch.Tensor] | None = None

    def _warp(self, x, flow):
        _, _, h, w = flow.shape
        key = (h, w, flow.device, flow.dtype)
        if self._grid is None or self._grid[0] != key:
            xs = torch.linspace(-1.0, 1.0, w, device=flow.device, dtype=flow.dtype).view(1, 1, 1, w).expand(1, 1, h, w)
            ys = torch.linspace(-1.0, 1.0, h, device=flow.device, dtype=flow.dtype).view(1, 1, h, 1).expand(1, 1, h, w)
            self._grid = (key, torch.cat((xs, ys), 1))
        norm = torch.cat((flow[:, 0:1] / ((w - 1) / 2.0), flow[:, 1:2] / ((h - 1) / 2.0)), 1)
        grid = (self._grid[1] + norm).permute(0, 2, 3, 1)
        return F.grid_sample(x, grid, mode="bilinear", padding_mode="border", align_corners=True)

    def forward(self, img0, img1, timestep: float, scale: float):
        scales = [16 / scale, 8 / scale, 4 / scale, 2 / scale, 1 / scale]
        t = torch.full_like(img0[:, :1], timestep)
        f0, f1 = self.encode(img0), self.encode(img1)
        flow = mask = feat = None
        w0, w1 = img0, img1
        for block, s in zip((self.block0, self.block1, self.block2, self.block3, self.block4), scales):
            if flow is None:
                flow, mask, feat = block(torch.cat((img0, img1, f0, f1, t), 1), None, s)
            else:
                wf0 = self._warp(f0, flow[:, :2])
                wf1 = self._warp(f1, flow[:, 2:4])
                delta, mask, feat = block(torch.cat((w0, w1, wf0, wf1, t, mask, feat), 1), flow, s)
                flow = flow + delta
            w0 = self._warp(img0, flow[:, :2])
            w1 = self._warp(img1, flow[:, 2:4])
        m = torch.sigmoid(mask)
        return w0 * m + w1 * (1 - m)


@dataclass(frozen=True)
class _Prepared:
    tensor: torch.Tensor  # 1x3xHxW, padded, values in [0, 1]
    height: int
    width: int
    dtype: np.dtype


class RifeInterpolator(Interpolator):
    def __init__(self, model: str = DEFAULT_MODEL, scale: float | None = None,
                 half: bool = True, device: str = "cuda", weights: Path | None = None):
        """`scale` is the resolution factor for motion estimation; None picks 0.5
        for frames taller than 1440 lines and 1.0 otherwise, as the RIFE authors advise."""
        if device == "cuda" and not torch.cuda.is_available():
            raise RuntimeError("CUDA is not available. FrameDoubler needs an NVIDIA GPU.")
        path = weights or ensure_model(model)
        state = torch.load(path, map_location="cpu", weights_only=True)
        state = {k.replace("module.", ""): v for k, v in state.items()}
        net = _IFNet()
        missing, _ = net.load_state_dict(state, strict=False)  # training-only keys are ignored
        if missing:
            raise RuntimeError(f"{path} does not match the RIFE v4.25/v4.26 layout")
        self._net = net.eval().to(device)
        self._dtype = torch.float16 if half else torch.float32
        if half:
            self._net.half()
        self._device = device
        self._scale = scale

    def _scale_for(self, height: int) -> float:
        return self._scale if self._scale is not None else (0.5 if height > 1440 else 1.0)

    def prepare(self, frame: np.ndarray) -> _Prepared:
        h, w = frame.shape[:2]
        peak = float(np.iinfo(frame.dtype).max)
        source = frame if frame.dtype == np.uint8 else frame.astype(np.int32)  # torch has no uint16
        t = torch.from_numpy(source).to(self._device).permute(2, 0, 1).unsqueeze(0)
        # Scale in float32 first: 16-bit sample values overflow half precision.
        t = (t.to(torch.float32) / peak).to(self._dtype)
        multiple = int(64 / self._scale_for(h))
        pad_h, pad_w = (-h) % multiple, (-w) % multiple
        if pad_h or pad_w:
            t = F.pad(t, (0, pad_w, 0, pad_h), mode="replicate")
        return _Prepared(t, h, w, frame.dtype)

    @torch.inference_mode()
    def interpolate(self, a: _Prepared, b: _Prepared, step: float) -> np.ndarray:
        out = self._net(a.tensor, b.tensor, float(step), self._scale_for(a.height))
        out = out[0, :, :a.height, :a.width].float().clamp_(0.0, 1.0)
        peak = float(np.iinfo(a.dtype).max)
        out = (out * peak + 0.5).permute(1, 2, 0).contiguous()
        if a.dtype == np.uint8:
            return out.to(torch.uint8).cpu().numpy()
        return out.to(torch.int32).cpu().numpy().astype(a.dtype)
