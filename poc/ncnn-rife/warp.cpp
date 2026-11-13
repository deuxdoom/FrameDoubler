// Independently authored border-clamped bilinear sampling in pixel coordinates.
// This implements the mathematical warp used by the project's MIT RIFE model.
#include <c_api.h>
#include <net.h>
#include <pipeline.h>
#include <command.h>
#include <string>
#include <vector>
#include <iostream>
#include <algorithm>
#include <cmath>

namespace {
class PixelWarp final : public ncnn::Layer {
    ncnn::Pipeline* pipeline = nullptr;
public:
    PixelWarp() {
        one_blob_only = false;
        support_vulkan = true;
        support_packing = true;
        support_fp16_storage = true;
    }
    int create_pipeline(const ncnn::Option& option) override {
        if (!option.use_vulkan_compute) return 0;
        const bool half = option.use_fp16_storage;
        std::string shader = "#version 450\n";
        if (half) shader += "#extension GL_EXT_shader_16bit_storage : require\n#extension GL_EXT_shader_explicit_arithmetic_types_float16 : require\n";
        shader += half ? "#define SAMPLE float16_t\n" : "#define SAMPLE float\n";
        shader += R"glsl(
layout(local_size_x=8, local_size_y=8, local_size_z=1) in;
layout(binding=0) readonly buffer Source { SAMPLE src[]; };
layout(binding=1) readonly buffer Displacement { SAMPLE flow[]; };
layout(binding=2) writeonly buffer Destination { SAMPLE dst[]; };
layout(push_constant) uniform parameter { int width; int height; int channels; int srcstep; int flowstep; int dststep; int pack; } shape;
void main() {
    int x = int(gl_GlobalInvocationID.x);
    int y = int(gl_GlobalInvocationID.y);
    int channel = int(gl_GlobalInvocationID.z);
    if (x >= shape.width || y >= shape.height || channel >= shape.channels) return;
    int position = y * shape.width + x;
    float sx = clamp(float(x) + float(flow[position]), 0.0, float(shape.width - 1));
    float sy = clamp(float(y) + float(flow[shape.flowstep + position]), 0.0, float(shape.height - 1));
    int x0 = int(floor(sx)); int y0 = int(floor(sy));
    int x1 = min(x0 + 1, shape.width - 1); int y1 = min(y0 + 1, shape.height - 1);
    float fx = sx - float(x0); float fy = sy - float(y0);
    int start = channel * shape.srcstep * shape.pack;
    for (int lane = 0; lane < shape.pack; ++lane) {
        float upper = mix(float(src[start + (y0 * shape.width + x0) * shape.pack + lane]), float(src[start + (y0 * shape.width + x1) * shape.pack + lane]), fx);
        float lower = mix(float(src[start + (y1 * shape.width + x0) * shape.pack + lane]), float(src[start + (y1 * shape.width + x1) * shape.pack + lane]), fx);
        dst[(channel * shape.dststep + position) * shape.pack + lane] = SAMPLE(mix(upper, lower, fy));
    }
}
)glsl";
        std::vector<uint32_t> spirv;
        if (ncnn::compile_spirv_module(shader.c_str(), option, spirv) != 0) return -1;
        pipeline = new ncnn::Pipeline(vkdev);
        pipeline->set_local_size_xyz(8, 8, 1);
        return pipeline->create(spirv.data(), spirv.size() * sizeof(uint32_t), {});
    }
    int destroy_pipeline(const ncnn::Option&) override {
        delete pipeline;
        pipeline = nullptr;
        return 0;
    }
    int forward(const std::vector<ncnn::Mat>& inputs, std::vector<ncnn::Mat>& outputs, const ncnn::Option& option) const override {
        const auto& image = inputs.at(0);
        const auto& flow = inputs.at(1);
        if (flow.elempack != 1 || flow.c != 2 || image.w != flow.w || image.h != flow.h
                || image.elemsize != size_t(4 * image.elempack) || flow.elemsize != 4) return -1;
        auto& result = outputs.at(0);
        result.create(image.w, image.h, image.c, image.elemsize, image.elempack, option.blob_allocator);
        if (result.empty()) return -100;
        const float* dx = flow.channel(0);
        const float* dy = flow.channel(1);
        const int pack = image.elempack;
        for (int channel = 0; channel < image.c; ++channel) {
            const float* source = image.channel(channel);
            float* dest = result.channel(channel);
            for (int y = 0; y < image.h; ++y) for (int x = 0; x < image.w; ++x) {
                const int position = y * image.w + x;
                float sx = std::clamp(float(x) + dx[position], 0.f, float(image.w - 1));
                float sy = std::clamp(float(y) + dy[position], 0.f, float(image.h - 1));
                int x0 = int(std::floor(sx)), y0 = int(std::floor(sy));
                int x1 = std::min(x0 + 1, image.w - 1), y1 = std::min(y0 + 1, image.h - 1);
                float fx = sx - x0, fy = sy - y0;
                for (int lane = 0; lane < pack; ++lane) {
                    float upper = source[(y0 * image.w + x0) * pack + lane] * (1-fx) + source[(y0 * image.w + x1) * pack + lane] * fx;
                    float lower = source[(y1 * image.w + x0) * pack + lane] * (1-fx) + source[(y1 * image.w + x1) * pack + lane] * fx;
                    dest[position * pack + lane] = upper * (1-fy) + lower * fy;
                }
            }
        }
        return 0;
    }
    int forward(const std::vector<ncnn::VkMat>& inputs, std::vector<ncnn::VkMat>& outputs,
                ncnn::VkCompute& commands, const ncnn::Option& option) const override {
        const auto& image = inputs.at(0);
        const auto& flow = inputs.at(1);
        if (flow.elempack != 1 || flow.c != 2 || image.w != flow.w || image.h != flow.h) return -1;
        auto& output = outputs.at(0);
        output.create(image.w, image.h, image.c, image.elemsize, image.elempack, option.blob_vkallocator);
        if (output.empty()) return -100;
        std::vector<ncnn::vk_constant_type> dimensions(7);
        dimensions[0].i = image.w; dimensions[1].i = image.h; dimensions[2].i = image.c;
        dimensions[3].i = static_cast<int>(image.cstep);
        dimensions[4].i = static_cast<int>(flow.cstep);
        dimensions[5].i = static_cast<int>(output.cstep);
        dimensions[6].i = image.elempack;
        commands.record_pipeline(pipeline, {image, flow, output}, dimensions, output);
        return 0;
    }
};
ncnn::Layer* create_warp(void*) { return new PixelWarp; }
}

extern "C" void fd_register_warp(ncnn_net_t wrapper) {
    auto* network = static_cast<ncnn::Net*>(wrapper->pthis);
    network->register_custom_layer("FDWarp", create_warp);
}

extern "C" int fd_audit_layers(ncnn_net_t wrapper) {
    const auto* network = static_cast<ncnn::Net*>(wrapper->pthis);
    int gpu = 0, structural = 0, fallback = 0;
    for (const auto* layer : network->layers()) {
        if (layer->support_vulkan) { ++gpu; continue; }
        if (layer->type == "Input" || layer->type == "Split" || layer->type == "MemoryData") {
            ++structural; continue;
        }
        std::cerr << "CPU_FALLBACK " << layer->type << " " << layer->name << "\n";
        ++fallback;
    }
    std::cerr << "LAYER_AUDIT gpu=" << gpu << " structural=" << structural << " fallback=" << fallback << "\n";
    return fallback;
}
