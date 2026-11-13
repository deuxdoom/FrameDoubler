// Independent FrameDoubler inference PoC. Tencent ncnn APIs and our own warp layer.
// stdin: 'P', float timestep, two packed RGB8 frames; 'Q' exits.
// stdout: three double durations (prepare/extract/postprocess), packed RGB8 result.
#include <c_api.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
#include <fcntl.h>
#include <io.h>

using Clock = std::chrono::steady_clock;
extern "C" void fd_register_warp(ncnn_net_t net);
extern "C" int fd_audit_layers(ncnn_net_t net);
double elapsed(Clock::time_point start) { return std::chrono::duration<double>(Clock::now() - start).count(); }
void checked(int value, const char* what) { if (value != 0) throw std::runtime_error(what); }
void read_exact(void* target, size_t bytes) {
    if (std::fread(target, 1, bytes, stdin) != bytes) throw std::runtime_error("truncated input");
}
struct Tensor {
    ncnn_mat_t value;
    Tensor(int w, int h, int c) : value(ncnn_mat_create_3d(w, h, c, nullptr)) {
        if (!ncnn_mat_get_data(value)) throw std::runtime_error("tensor allocation failed");
    }
    ~Tensor() { ncnn_mat_destroy(value); }
    float* channel(int index) { return static_cast<float*>(ncnn_mat_get_channel_data(value, index)); }
    Tensor(const Tensor&) = delete;
    Tensor& operator=(const Tensor&) = delete;
};

int main(int argc, char** argv) {
    ncnn_net_t net = nullptr;
    try {
        if (argc < 6 || argc > 8) throw std::runtime_error("usage: rife-poc MODEL_DIR WIDTH HEIGHT fp16|fp32 gpu|cpu [SCALE] [sequence]");
        const std::filesystem::path model(argv[1]);
        const int width = std::stoi(argv[2]), height = std::stoi(argv[3]);
        if (width < 2 || height < 2 || width > 8192 || height > 8192) throw std::runtime_error("invalid dimensions");
        const bool half = std::string(argv[4]) == "fp16";
        const bool gpu = std::string(argv[5]) == "gpu";
        const float scale = argc >= 7 ? std::stof(argv[6]) : (height > 1440 ? .5f : 1.f);
        if (scale != .5f && scale != 1.f) throw std::runtime_error("unsupported scale");
        const int alignment = static_cast<int>(64 / scale);
        const int padded_w = (width + alignment - 1) / alignment * alignment;
        const int padded_h = (height + alignment - 1) / alignment * alignment;
        const bool sequence = argc == 8 && std::string(argv[7]) == "sequence";
        _setmode(_fileno(stdin), _O_BINARY);
        _setmode(_fileno(stdout), _O_BINARY);
        const auto startup = Clock::now();
        net = ncnn_net_create();
        fd_register_warp(net);
        auto option = ncnn_net_get_option(net);
        ncnn_option_set_num_threads(option, 4);
        ncnn_option_set_use_vulkan_compute(option, gpu);
        ncnn_option_set_use_fp16_packed(option, half);
        ncnn_option_set_use_fp16_storage(option, half);
        // Keep arithmetic in FP32, with optional FP16 weights/activation storage.
        ncnn_option_set_use_fp16_arithmetic(option, false);
        // get_option borrows the Net-owned option; it must not be destroyed.
        if (gpu) ncnn_net_set_vulkan_device(net, 0);
        const auto scaled_param = model / "rife-s05.ncnn.param";
        const auto param = scale == .5f && std::filesystem::exists(scaled_param)
                         ? scaled_param : model / "rife.ncnn.param";
        checked(ncnn_net_load_param_w(net, param.c_str()), "load param failed");
        checked(ncnn_net_load_model_w(net, (model / "rife.ncnn.bin").c_str()), "load weights failed");
        option = ncnn_net_get_option(net);
        const bool actual_gpu = ncnn_option_get_use_vulkan_compute(option);
        if (gpu && !actual_gpu) throw std::runtime_error("Vulkan was requested but disabled");
        if (gpu && fd_audit_layers(net)) throw std::runtime_error("unexpected CPU fallback in graph");
        Tensor a(padded_w, padded_h, 3), b(padded_w, padded_h, 3), time(padded_w, padded_h, 1);
        std::vector<unsigned char> first(static_cast<size_t>(width) * height * 3), second(first.size()), output(first.size());
        float* planes_a[3] = {a.channel(0), a.channel(1), a.channel(2)};
        float* planes_b[3] = {b.channel(0), b.channel(1), b.channel(2)};
        std::cerr << "READY vulkan=" << actual_gpu << " startup_s=" << elapsed(startup)
                  << " padded=" << padded_w << "x" << padded_h << "\n" << std::flush;
        if (sequence) read_exact(first.data(), first.size());
        while (true) {
            float step = .5f;
            if (sequence) {
                const size_t count = std::fread(second.data(), 1, second.size(), stdin);
                if (count == 0) {
                    std::fwrite(first.data(), 1, first.size(), stdout);
                    std::fwrite(first.data(), 1, first.size(), stdout);
                    break;
                }
                if (count != second.size()) throw std::runtime_error("truncated sequence frame");
            } else {
                int command = std::fgetc(stdin);
                if (command == EOF || command == 'Q') break;
                if (command != 'P') throw std::runtime_error("unknown command");
                read_exact(&step, sizeof(step));
                read_exact(first.data(), first.size()); read_exact(second.data(), second.size());
            }
            if (!(step > 0 && step < 1)) throw std::runtime_error("invalid timestep");
            auto start = Clock::now();
            for (int y = 0; y < padded_h; ++y) for (int x = 0; x < padded_w; ++x) {
                const size_t src = (static_cast<size_t>(std::min(y, height - 1)) * width + std::min(x, width - 1)) * 3;
                const size_t dst = static_cast<size_t>(y) * padded_w + x;
                for (int channel = 0; channel < 3; ++channel) {
                    planes_a[channel][dst] = first[src + channel] / 255.f;
                    planes_b[channel][dst] = second[src + channel] / 255.f;
                }
            }
            ncnn_mat_fill_float(time.value, step);
            double durations[3] = {elapsed(start), 0, 0};
            auto extractor = ncnn_extractor_create(net);
            start = Clock::now();
            checked(ncnn_extractor_input(extractor, "in0", a.value), "input A failed");
            checked(ncnn_extractor_input(extractor, "in1", b.value), "input B failed");
            checked(ncnn_extractor_input(extractor, "in2", time.value), "input time failed");
            ncnn_mat_t result = nullptr;
            const int code = ncnn_extractor_extract(extractor, "out0", &result);
            ncnn_extractor_destroy(extractor);
            checked(code, "inference failed");
            durations[1] = elapsed(start);
            start = Clock::now();
            if (ncnn_mat_get_w(result) != padded_w || ncnn_mat_get_h(result) != padded_h || ncnn_mat_get_c(result) != 3
                    || ncnn_mat_get_elemsize(result) != 4 || ncnn_mat_get_elempack(result) != 1) {
                ncnn_mat_destroy(result);
                throw std::runtime_error("unexpected output shape/type");
            }
            for (int channel = 0; channel < 3; ++channel) {
                const auto* data = static_cast<const float*>(ncnn_mat_get_channel_data(result, channel));
                for (int y = 0; y < height; ++y) for (int x = 0; x < width; ++x) {
                    const float value = std::clamp(data[static_cast<size_t>(y) * padded_w + x], 0.f, 1.f);
                    if (!std::isfinite(value)) throw std::runtime_error("non-finite output");
                    output[(static_cast<size_t>(y) * width + x) * 3 + channel] = static_cast<unsigned char>(value * 255 + .5f);
                }
            }
            ncnn_mat_destroy(result);
            durations[2] = elapsed(start);
            if (sequence) {
                if (std::fwrite(first.data(), 1, first.size(), stdout) != first.size()) throw std::runtime_error("output failed");
            } else if (std::fwrite(durations, 1, sizeof(durations), stdout) != sizeof(durations)) throw std::runtime_error("output failed");
            if (
                std::fwrite(output.data(), 1, output.size(), stdout) != output.size()) throw std::runtime_error("output failed");
            std::fflush(stdout);
            if (sequence) first.swap(second);
        }
        ncnn_net_destroy(net);
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "ERROR: " << error.what() << "\n";
        if (net) ncnn_net_destroy(net);
        return 1;
    }
}
