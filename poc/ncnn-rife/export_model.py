"""Development-only conversion of the project's MIT RIFE network and official weights.

No Video2X/librife source or model assets are used in the generated deployment.
Torch and pnnx are needed here, never by the C++ inference executable.
"""

import argparse
import hashlib
import json
from pathlib import Path

import torch
import torch.nn.functional as F

from baseline.rife import _IFNet
from baseline.weights import MODELS, ensure_model


class Warp(torch.nn.Module):
    def forward(self, image, flow):
        height, width = image.shape[-2:]
        xs = torch.linspace(-1., 1., width, dtype=image.dtype, device=image.device).view(1, 1, 1, width)
        ys = torch.linspace(-1., 1., height, dtype=image.dtype, device=image.device).view(1, 1, height, 1)
        grid = torch.cat((xs.expand(1, 1, height, width), ys.expand(1, 1, height, width)), 1)
        norm = torch.cat((flow[:, :1] * (2. / (width - 1)), flow[:, 1:] * (2. / (height - 1))), 1)
        grid = (grid + norm).permute(0, 2, 3, 1)
        return F.grid_sample(image, grid, mode="bilinear", padding_mode="border", align_corners=True)


class ExportNet(_IFNet):
    def __init__(self):
        super().__init__()
        self.sampler = Warp()
        self.pre_resize = False

    def _warp(self, image, flow):
        return self.sampler(image, flow)

    def _block(self, block, parts, flow, scale):
        if not self.pre_resize:
            return block(torch.cat(parts, 1), flow, scale)
        # Bilinear resize is channel-independent: resizing before concatenation
        # avoids allocating a full-resolution 15/24-channel intermediate.
        small = [F.interpolate(p, scale_factor=1. / scale, mode="bilinear", align_corners=False) for p in parts]
        if flow is not None:
            small.append(F.interpolate(flow, scale_factor=1. / scale, mode="bilinear", align_corners=False) / scale)
        output = block.lastconv(block.convblock(block.conv0(torch.cat(small, 1))))
        output = F.interpolate(output, scale_factor=scale, mode="bilinear", align_corners=False)
        return output[:, :4] * scale, output[:, 4:5], output[:, 5:]

    def forward(self, first, second, time_image):
        scales = [value / self.scale for value in (16, 8, 4, 2, 1)]
        f0, f1 = self.encode(first), self.encode(second)
        flow = mask = feat = None
        left, right = first, second
        for block, scale in zip((self.block0, self.block1, self.block2, self.block3, self.block4), scales):
            if flow is None:
                flow, mask, feat = self._block(block, (first, second, f0, f1, time_image), None, scale)
            else:
                features0 = self._warp(f0, flow[:, :2])
                features1 = self._warp(f1, flow[:, 2:4])
                delta, mask, feat = self._block(block, (left, right, features0, features1,
                                                       time_image, mask, feat), flow, scale)
                flow = flow + delta
            left = self._warp(first, flow[:, :2])
            right = self._warp(second, flow[:, 2:4])
        weight = torch.sigmoid(mask)
        return left * weight + right * (1 - weight)


def main():
    import pnnx
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", choices=MODELS, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--scale", type=float, choices=(.5, 1.), default=1.)
    parser.add_argument("--pre-resize", action="store_true")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    path = ensure_model(args.model)
    actual = hashlib.sha256(path.read_bytes()).hexdigest()
    if actual != MODELS[args.model].sha256:
        raise RuntimeError("Official weight checksum mismatch")
    model = ExportNet().eval()
    model.scale = args.scale
    model.pre_resize = args.pre_resize
    state = {k.removeprefix("module."): v for k, v in torch.load(path, weights_only=True, map_location="cpu").items()}
    missing, unused = model.load_state_dict(state, strict=False)
    if missing:
        raise RuntimeError(str(missing))
    torch.set_num_threads(4)
    torch.manual_seed(1)
    def inputs(size, step):
        return (torch.rand(1, 3, size, size), torch.rand(1, 3, size, size),
                torch.full((1, 1, size, size), step))
    base = int(64 / args.scale)
    first, second = inputs(base, .5), inputs(base * 2, .25)
    pnnx.export(model, (args.output / "rife.pt").as_posix(), inputs=first, inputs2=second,
                check_trace=False, fp16=True, moduleop=["Warp"])
    parameter = args.output / "rife.ncnn.param"
    lines = parameter.read_text().splitlines()
    lines = [line.replace("Warp", "FDWarp", 1) if line.split() and line.split()[0] == "Warp" else line for line in lines]
    parameter.write_text("\n".join(lines) + "\n")
    (args.output / "provenance.json").write_text(json.dumps({
        "model": args.model, "source_sha256": actual, "source": "Practical-RIFE official MIT weights",
        "scale": args.scale,
        "pre_resize": args.pre_resize,
        "ignored_training_keys": unused, "pnnx": "20261020", "torch": torch.__version__,
    }, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
