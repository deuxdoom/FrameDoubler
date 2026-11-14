"""Export the independent RIFE graph for development-only ORT comparisons.

Inputs stay at the source resolution, with the same 64/scale replicate padding
performed by the caller. The scalar time input is expanded inside the graph.
No model code is loaded from downloaded weights, and missing weights are an error.
"""
import argparse
import hashlib
import json
from pathlib import Path

import onnx
import torch

from baseline.weights import MODELS, models_dir
from export_model import ExportNet


class OnnxRife(ExportNet):
    def forward(self, first, second, timestep):
        return super().forward(first, second, timestep.expand_as(first[:, :1]))


def load(model_name, scale):
    weights = models_dir() / f"rife-{model_name}" / "flownet.pkl"
    if not weights.is_file():
        raise FileNotFoundError(weights)
    digest = hashlib.sha256(weights.read_bytes()).hexdigest()
    if digest != MODELS[model_name].sha256:
        raise RuntimeError("Official weight checksum mismatch")
    model = OnnxRife().eval()
    model.scale = scale
    state = {key.removeprefix("module."): value for key, value in
             torch.load(weights, weights_only=True, map_location="cpu").items()}
    missing, unused = model.load_state_dict(state, strict=False)
    if missing:
        raise RuntimeError(f"Missing model keys: {missing}")
    return model, digest, unused


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", choices=MODELS, required=True)
    parser.add_argument("--scale", type=float, choices=(.5, 1.), required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    torch.set_num_threads(4)
    torch.manual_seed(1)
    model, digest, unused = load(args.model, args.scale)
    multiple = int(64 / args.scale)
    inputs = (torch.rand(1, 3, multiple, multiple), torch.rand(1, 3, multiple, multiple),
              torch.tensor([[[[.5]]]], dtype=torch.float32))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with torch.inference_mode():
        torch.onnx.export(model, inputs, str(args.output), dynamo=False, opset_version=18,
                          input_names=["in0", "in1", "time"], output_names=["out0"],
                          dynamic_axes={name: {2: "height", 3: "width"} for name in ("in0", "in1", "out0")})
    graph = onnx.load(args.output)
    onnx.checker.check_model(graph)
    operators = sorted({node.op_type for node in graph.graph.node})
    if any(node.domain not in ("", "ai.onnx") for node in graph.graph.node):
        raise RuntimeError("Unexpected nonstandard ONNX operator")
    provenance = {"model": args.model, "scale": args.scale, "source_sha256": digest,
                  "precision": "float32", "opset": 18, "torch": torch.__version__,
                  "onnx": onnx.__version__, "operators": operators, "ignored_training_keys": unused,
                  "output_sha256": hashlib.sha256(args.output.read_bytes()).hexdigest()}
    args.output.with_suffix(".json").write_text(json.dumps(provenance, indent=2), encoding="utf-8")
    print(json.dumps(provenance, indent=2))


if __name__ == "__main__":
    main()
