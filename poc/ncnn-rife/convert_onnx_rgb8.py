"""Move RGB8 packing/normalization into an otherwise FP32 ONNX graph.

Spatial resolution, FP32 network/warp arithmetic and the caller's replicate
padding remain unchanged. This is a model conversion tool, not an app backend.
"""
import argparse
import copy
import json
from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    model = onnx.load(args.input)
    graph = model.graph
    nodes, replacements = [], {}
    for item in graph.input[:2]:
        name = item.name
        shape = item.type.tensor_type.shape
        old = copy.deepcopy(shape.dim)
        del shape.dim[:]
        shape.dim.extend([old[0], old[2], old[3], old[1]])
        item.type.tensor_type.elem_type = TensorProto.UINT8
        nodes.extend([
            helper.make_node("Cast", [name], [name+"_raw_float_nhwc"], to=TensorProto.FLOAT),
            helper.make_node("Transpose", [name+"_raw_float_nhwc"], [name+"_raw_float"], perm=[0, 3, 1, 2]),
            helper.make_node("Div", [name+"_raw_float", "fd_rgb_peak"], [name+"_normalized"]),
        ])
        replacements[name] = name+"_normalized"
    graph.initializer.append(numpy_helper.from_array(np.array(255, np.float32), "fd_rgb_peak"))
    for original in graph.node:
        node = copy.deepcopy(original)
        for i, value in enumerate(node.input):
            node.input[i] = replacements.get(value, value)
        nodes.append(node)
    output = graph.output[0]
    name = output.name
    for node in nodes:
        for i, value in enumerate(node.output):
            if value == name:
                node.output[i] = "fd_output_float"
    for name_, value in (("fd_zero", 0.), ("fd_one", 1.), ("fd_half", .5)):
        graph.initializer.append(numpy_helper.from_array(np.array(value, np.float32), name_))
    nodes.extend([
        helper.make_node("Clip", ["fd_output_float", "fd_zero", "fd_one"], ["fd_clamped"]),
        helper.make_node("Mul", ["fd_clamped", "fd_rgb_peak"], ["fd_scaled"]),
        helper.make_node("Add", ["fd_scaled", "fd_half"], ["fd_rounded"]),
        helper.make_node("Transpose", ["fd_rounded"], ["fd_output_nhwc_float"], perm=[0, 2, 3, 1]),
        helper.make_node("Cast", ["fd_output_nhwc_float"], [name], to=TensorProto.UINT8),
    ])
    shape = output.type.tensor_type.shape
    old = copy.deepcopy(shape.dim)
    del shape.dim[:]
    shape.dim.extend([old[0], old[2], old[3], old[1]])
    output.type.tensor_type.elem_type = TensorProto.UINT8
    del graph.node[:]
    graph.node.extend(nodes)
    used = {name for node in graph.node for name in node.input}
    kept = [item for item in graph.initializer if item.name in used]
    del graph.initializer[:]
    graph.initializer.extend(kept)
    onnx.checker.check_model(model)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    onnx.save(model, args.output)
    args.output.with_suffix(".variant.json").write_text(json.dumps({"source": str(args.input),
        "rgb8_io": True, "network_and_warp": "float32", "spatial_resolution": "unchanged"}, indent=2), encoding="utf-8")
    print(args.output)


if __name__ == "__main__":
    main()
