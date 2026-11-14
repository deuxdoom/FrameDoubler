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
#include <thread>
#include <future>
#include <atomic>
#include <windows.h>
#include "hardware.h"
#include "gpu_frames.h"

using Clock = std::chrono::steady_clock;
extern "C" void fd_register_warp(ncnn_net_t net);
extern "C" int fd_audit_layers(ncnn_net_t net);
double elapsed(Clock::time_point start) { return std::chrono::duration<double>(Clock::now() - start).count(); }
void checked(int value, const char* what) { if (value != 0) throw std::runtime_error(what); }
void read_exact(void* target, size_t bytes) {
    if (std::fread(target, 1, bytes, stdin) != bytes) throw std::runtime_error("truncated input");
}
// Keep individual pixel operations unchanged. A few independent row ranges avoid
// a serial RGB conversion bottleneck without oversubscribing concurrent engines.
template<class Work> void parallel_rows(int width,int height,Work work) {
    const unsigned count = width * height >= 1000000
        ? std::min(4u,std::max(1u,std::thread::hardware_concurrency()/4)) : 1;
    std::vector<std::future<void>> jobs;
    for(unsigned i=1;i<count;++i)jobs.push_back(std::async(std::launch::async,[&,i]{
        work(height*i/count,height*(i+1)/count);
    }));
    work(0,height/count);
    for(auto& job:jobs)job.get();
}
void unpack_row(const unsigned char* __restrict source, float* __restrict red,
                float* __restrict green, float* __restrict blue, int width) {
    constexpr float norm = 1.f / 255.f;
    for (int x = 0; x < width; ++x) {
        red[x] = source[x * 3] * norm;
        green[x] = source[x * 3 + 1] * norm;
        blue[x] = source[x * 3 + 2] * norm;
    }
}
bool pack_row(const float* __restrict red, const float* __restrict green, const float* __restrict blue,
              unsigned char* __restrict dest, int width) {
    bool finite = true;
    for (int x = 0; x < width; ++x) {
        bool r = std::isfinite(red[x]), g = std::isfinite(green[x]), b = std::isfinite(blue[x]);
        finite &= r & g & b;
        dest[x * 3] = static_cast<unsigned char>(std::clamp(r ? red[x] : 0.f, 0.f, 1.f) * 255 + .5f);
        dest[x * 3 + 1] = static_cast<unsigned char>(std::clamp(g ? green[x] : 0.f, 0.f, 1.f) * 255 + .5f);
        dest[x * 3 + 2] = static_cast<unsigned char>(std::clamp(b ? blue[x] : 0.f, 0.f, 1.f) * 255 + .5f);
    }
    return finite;
}
void unpack_row16(const unsigned short* source, float* red, float* green, float* blue, int width) {
    constexpr float norm = 1.f / 65535.f;
    for(int x=0;x<width;++x){red[x]=source[x*3]*norm;green[x]=source[x*3+1]*norm;blue[x]=source[x*3+2]*norm;}
}
bool pack_row16(const float* red,const float* green,const float* blue,unsigned short* dest,int width) {
    bool finite=true;
    for(int x=0;x<width;++x){
        bool r=std::isfinite(red[x]),g=std::isfinite(green[x]),b=std::isfinite(blue[x]);finite &= r&g&b;
        dest[x*3]=static_cast<unsigned short>(std::clamp(r?red[x]:0.f,0.f,1.f)*65535+.5f);
        dest[x*3+1]=static_cast<unsigned short>(std::clamp(g?green[x]:0.f,0.f,1.f)*65535+.5f);
        dest[x*3+2]=static_cast<unsigned short>(std::clamp(b?blue[x]:0.f,0.f,1.f)*65535+.5f);
    }
    return finite;
}
struct Tensor {
    ncnn_mat_t value;
    Tensor(int w, int h, int c) : value(ncnn_mat_create_3d(w, h, c, nullptr)) {
        if (!ncnn_mat_get_data(value)) throw std::runtime_error("tensor allocation failed");
    }
    ~Tensor() { ncnn_mat_destroy(value); }
    float* channel(int index) { return static_cast<float*>(ncnn_mat_get_channel_data(value, index)); }
    void reset(int w,int h,int c){ncnn_mat_destroy(value);value=ncnn_mat_create_3d(w,h,c,nullptr);if(!ncnn_mat_get_data(value))throw std::runtime_error("tensor allocation failed");}
    Tensor(const Tensor&) = delete;
    Tensor& operator=(const Tensor&) = delete;
};

int wmain(int argc, wchar_t** wide) {
    std::vector<std::string> arguments;
    for(int i=0;i<argc;i++){
        int size=WideCharToMultiByte(CP_UTF8,0,wide[i],-1,nullptr,0,nullptr,nullptr);
        std::string value(size,'\0');
        WideCharToMultiByte(CP_UTF8,0,wide[i],-1,value.data(),size,nullptr,nullptr);
        value.pop_back();arguments.push_back(value);
    }
    std::vector<char*> pointers;for(auto& value:arguments)pointers.push_back(value.data());
    char** argv=pointers.data();
    ncnn_net_t net = nullptr;
    FDGpuFrames* gpu_frames=nullptr;
    try {
        if (argc == 2 && std::string(argv[1]) == "--hardware") {
            std::cout << fd_hardware_json() << '\n';
            return 0;
        }
        if (argc < 6 || argc > 9) throw std::runtime_error("usage: rife-poc MODEL_DIR WIDTH HEIGHT fp16|fp32 auto|gpu:INDEX|cpu [SCALE] [rgb16] [sequence]");
        const std::filesystem::path model(wide[1]);
        const int width = std::stoi(argv[2]), height = std::stoi(argv[3]);
        if (width < 2 || height < 2 || width > 8192 || height > 8192) throw std::runtime_error("invalid dimensions");
        if (std::string(argv[4]) != "fp16" && std::string(argv[4]) != "fp32") throw std::runtime_error("invalid precision");
        bool half = std::string(argv[4]) == "fp16";
        const bool deep = argc >= 8 && std::string(argv[7]) == "rgb16";
        if (deep) half=false;
        const int device = fd_choose_device(argv[5], half);
        const bool gpu = device >= 0;
        const float scale = argc >= 7 ? std::stof(argv[6]) : (height > 1440 ? .5f : 1.f);
        if (scale != .5f && scale != 1.f) throw std::runtime_error("unsupported scale");
        const int alignment = static_cast<int>(64 / scale);
        const int padded_w = (width + alignment - 1) / alignment * alignment;
        const int padded_h = (height + alignment - 1) / alignment * alignment;
        const bool sequence = argc >= 8 && std::string(argv[argc-1]) == "sequence";
        _setmode(_fileno(stdin), _O_BINARY);
        _setmode(_fileno(stdout), _O_BINARY);
        const auto startup = Clock::now();
        net = ncnn_net_create();
        fd_register_warp(net);
        auto option = ncnn_net_get_option(net);
        ncnn_option_set_num_threads(option, std::max(1, std::min(32, int(std::thread::hardware_concurrency()))));
        ncnn_option_set_use_vulkan_compute(option, gpu);
        ncnn_option_set_use_fp16_packed(option, half);
        ncnn_option_set_use_fp16_storage(option, half);
        // Keep arithmetic in FP32, with optional FP16 weights/activation storage.
        ncnn_option_set_use_fp16_arithmetic(option, false);
        // get_option borrows the Net-owned option; it must not be destroyed.
        if (gpu) ncnn_net_set_vulkan_device(net, device);
        else std::cerr << "DEVICE cpu: inference can be much slower than a Vulkan GPU.\n";
        const auto scaled_param = model / "rife-s05.ncnn.param";
        const auto param = scale == .5f && std::filesystem::exists(scaled_param)
                         ? scaled_param : model / "rife.ncnn.param";
        checked(ncnn_net_load_param_w(net, param.c_str()), "load param failed");
        checked(ncnn_net_load_model_w(net, (model / "rife.ncnn.bin").c_str()), "load weights failed");
        option = ncnn_net_get_option(net);
        const bool actual_gpu = ncnn_option_get_use_vulkan_compute(option);
        if (gpu && !actual_gpu) throw std::runtime_error("Vulkan was requested but disabled");
        if (gpu && fd_audit_layers(net)) throw std::runtime_error("unexpected CPU fallback in graph");
        const char* gpu_rgb_override=std::getenv("FRAMEDOUBLER_GPU_RGB");
        bool gpu_rgb=gpu&&!half&&(gpu_rgb_override?std::string(gpu_rgb_override)=="1":width*height>=1000000);
        if(gpu_rgb)try{gpu_frames=fd_gpu_frames_create(net,width,height,padded_w,padded_h,deep);}
        catch(const std::exception& error){std::cerr<<"GPU_RGB fallback: "<<error.what()<<"\n";gpu_rgb=false;}
        Tensor a(gpu_rgb?1:padded_w, gpu_rgb?1:padded_h, 3), b(gpu_rgb?1:padded_w, gpu_rgb?1:padded_h, 3), time(gpu_rgb?1:padded_w, gpu_rgb?1:padded_h, 1);
        std::vector<unsigned char> first(static_cast<size_t>(width) * height * 3 * (deep?2:1)), second(first.size()), output(first.size());
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
            if(gpu_frames){
              double durations[3];bool converted=true;
              try{
                fd_gpu_frames_run(gpu_frames,first.data(),second.data(),step,output.data(),durations);
              }catch(const std::exception& error){
                std::cerr<<"GPU_RGB fallback: "<<error.what()<<"\n";fd_gpu_frames_destroy(gpu_frames);gpu_frames=nullptr;converted=false;
                a.reset(padded_w,padded_h,3);b.reset(padded_w,padded_h,3);time.reset(padded_w,padded_h,1);
                for(int c=0;c<3;c++){planes_a[c]=a.channel(c);planes_b[c]=b.channel(c);}
              }
              if(converted){
                if(sequence){if(std::fwrite(first.data(),1,first.size(),stdout)!=first.size())throw std::runtime_error("output failed");}
                else if(std::fwrite(durations,1,sizeof(durations),stdout)!=sizeof(durations))throw std::runtime_error("output failed");
                if(std::fwrite(output.data(),1,output.size(),stdout)!=output.size()||std::fflush(stdout))throw std::runtime_error("output failed");
                if(sequence)first.swap(second);continue;
              }
            }
            auto start = Clock::now();
            parallel_rows(width,height,[&](int begin,int end){for (int y = begin; y < end; ++y) {
                const size_t src = size_t(y) * width * 3 * (deep?2:1), dst = size_t(y) * padded_w;
                if(deep){
                    unpack_row16(reinterpret_cast<const unsigned short*>(first.data()+src),planes_a[0]+dst,planes_a[1]+dst,planes_a[2]+dst,width);
                    unpack_row16(reinterpret_cast<const unsigned short*>(second.data()+src),planes_b[0]+dst,planes_b[1]+dst,planes_b[2]+dst,width);
                }else{
                    unpack_row(first.data() + src, planes_a[0] + dst, planes_a[1] + dst, planes_a[2] + dst, width);
                    unpack_row(second.data() + src, planes_b[0] + dst, planes_b[1] + dst, planes_b[2] + dst, width);
                }
                for (int channel = 0; channel < 3; ++channel) {
                    std::fill(planes_a[channel] + dst + width, planes_a[channel] + dst + padded_w, planes_a[channel][dst + width - 1]);
                    std::fill(planes_b[channel] + dst + width, planes_b[channel] + dst + padded_w, planes_b[channel][dst + width - 1]);
                }
            }});
            for (int channel = 0; channel < 3; ++channel) for (int y = height; y < padded_h; ++y) {
                std::copy_n(planes_a[channel] + size_t(height - 1) * padded_w, padded_w, planes_a[channel] + size_t(y) * padded_w);
                std::copy_n(planes_b[channel] + size_t(height - 1) * padded_w, padded_w, planes_b[channel] + size_t(y) * padded_w);
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
            const float* channels[3] = {
                static_cast<const float*>(ncnn_mat_get_channel_data(result, 0)),
                static_cast<const float*>(ncnn_mat_get_channel_data(result, 1)),
                static_cast<const float*>(ncnn_mat_get_channel_data(result, 2))};
            std::atomic<bool> finite{true};
            parallel_rows(width,height,[&](int begin,int end){bool rows_finite=true;for (int y = begin; y < end; ++y) {
                const size_t offset = size_t(y) * padded_w;
                if(deep)rows_finite &= pack_row16(channels[0]+offset,channels[1]+offset,channels[2]+offset,
                    reinterpret_cast<unsigned short*>(output.data()+size_t(y)*width*6),width);
                else rows_finite &= pack_row(channels[0] + offset, channels[1] + offset, channels[2] + offset,
                                   output.data() + size_t(y) * width * 3, width);
            }if(!rows_finite)finite.store(false,std::memory_order_relaxed);});
            ncnn_mat_destroy(result);
            if (!finite) throw std::runtime_error("non-finite output");
            durations[2] = elapsed(start);
            if (sequence) {
                if (std::fwrite(first.data(), 1, first.size(), stdout) != first.size()) throw std::runtime_error("output failed");
            } else if (std::fwrite(durations, 1, sizeof(durations), stdout) != sizeof(durations)) throw std::runtime_error("output failed");
            if (
                std::fwrite(output.data(), 1, output.size(), stdout) != output.size()) throw std::runtime_error("output failed");
            std::fflush(stdout);
            if (sequence) first.swap(second);
        }
        fd_gpu_frames_destroy(gpu_frames);gpu_frames=nullptr;ncnn_net_destroy(net);
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "ERROR: " << error.what() << "\n";
        fd_gpu_frames_destroy(gpu_frames);if (net) ncnn_net_destroy(net);
        return 1;
    }
}
