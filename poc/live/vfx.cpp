#include "vfx.h"
#include "gpu.h"

#include <cstring>

namespace fdl {
namespace {
// Values of the public NvCVImage enumerations used here.
constexpr int kRgba = 6, kU8 = 1;
constexpr unsigned kChunky = 0, kGpuMemory = 1;
// The library fills these structures itself (InitFromD3D11Texture); the buffer is larger
// than the public structure so its exact size does not matter here.
struct alignas(16) ImageStorage { unsigned width, height; unsigned char rest[248]; };
const wchar_t* kModules[] = {L"cudart64_12.dll", L"NVCVImage.dll", L"nvngxruntime.dll", L"NVVideoEffects.dll", L"nvVFXVideoFrameGeneration.dll"};
} // namespace

struct VfxGenerator::Api {
    int (*getVersion)(unsigned*) = nullptr;
    int (*createEffect)(const char*, void**) = nullptr;
    void (*destroyEffect)(void*) = nullptr;
    int (*setU32)(void*, const char*, unsigned) = nullptr;
    int (*setF32)(void*, const char*, float) = nullptr;
    int (*setImage)(void*, const char*, void*) = nullptr;
    int (*setStream)(void*, const char*, void*) = nullptr;
    int (*streamCreate)(void**) = nullptr;
    int (*streamDestroy)(void*) = nullptr;
    int (*load)(void*) = nullptr;
    int (*run)(void*, int) = nullptr;
    int (*imageCreate)(unsigned, unsigned, int, int, unsigned, unsigned, unsigned, void**) = nullptr;
    void (*imageDestroy)(void*) = nullptr;
    void (*imageDealloc)(void*) = nullptr;
    int (*transfer)(const void*, void*, float, void*, void*) = nullptr;
    int (*fromD3D11)(void*, ID3D11Texture2D*) = nullptr;
    int (*map)(void*, void*) = nullptr;
    int (*unmap)(void*, void*) = nullptr;
    const char* (*errorText)(int) = nullptr;
    std::string describe(int status, const char* what) const {
        std::string text = errorText ? errorText(status) : "";
        return std::string(what) + " failed (" + std::to_string(status) + (text.empty() ? "" : ": " + text) + ")";
    }
};

bool VfxGenerator::runtimePresent(const std::wstring& directory) {
    for (const wchar_t* name : kModules)
        if (GetFileAttributesW((directory + L"\\" + name).c_str()) == INVALID_FILE_ATTRIBUTES) return false;
    return true;
}

bool VfxGenerator::create(Gpu& gpu, const std::wstring& directory, int quality, std::string& error) {
    destroy();
    if (!runtimePresent(directory)) { error = "NVIDIA VFX runtime not found"; return false; }
    // Load the runtime by full path in dependency order. Later by-name loads inside the
    // runtime then resolve to these modules.
    for (const wchar_t* name : kModules) {
        HMODULE m = LoadLibraryExW((directory + L"\\" + name).c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!m) { error = "cannot load " + utf8(name) + " (" + std::to_string(GetLastError()) + ")"; destroy(); return false; }
        modules_.push_back(m);
    }
    api_ = new Api();
    HMODULE effects = modules_[3], image = modules_[1];
    auto bind = [&](HMODULE m, const char* name, auto& fn) {
        fn = reinterpret_cast<std::remove_reference_t<decltype(fn)>>(reinterpret_cast<void*>(GetProcAddress(m, name)));
        if (!fn) error = std::string("missing ") + name;
        return fn != nullptr;
    };
    bool ok = bind(effects, "NvVFX_GetVersion", api_->getVersion) && bind(effects, "NvVFX_CreateEffect", api_->createEffect) &&
              bind(effects, "NvVFX_DestroyEffect", api_->destroyEffect) && bind(effects, "NvVFX_SetU32", api_->setU32) &&
              bind(effects, "NvVFX_SetF32", api_->setF32) && bind(effects, "NvVFX_SetImage", api_->setImage) &&
              bind(effects, "NvVFX_SetCudaStream", api_->setStream) && bind(effects, "NvVFX_CudaStreamCreate", api_->streamCreate) &&
              bind(effects, "NvVFX_CudaStreamDestroy", api_->streamDestroy) && bind(effects, "NvVFX_Load", api_->load) &&
              bind(effects, "NvVFX_Run", api_->run) && bind(image, "NvCVImage_Create", api_->imageCreate) &&
              bind(image, "NvCVImage_Destroy", api_->imageDestroy) && bind(image, "NvCVImage_Dealloc", api_->imageDealloc) &&
              bind(image, "NvCVImage_Transfer", api_->transfer) && bind(image, "NvCVImage_InitFromD3D11Texture", api_->fromD3D11) &&
              bind(image, "NvCVImage_MapResource", api_->map) && bind(image, "NvCVImage_UnmapResource", api_->unmap) &&
              bind(image, "NvCV_GetErrorStringFromCode", api_->errorText);
    if (!ok) { destroy(); return false; }
    unsigned v = 0;
    if (api_->getVersion(&v) == 0) version_ = std::to_string(v >> 24) + "." + std::to_string((v >> 16) & 0xff) + "." + std::to_string((v >> 8) & 0xff) + "." + std::to_string(v & 0xff);
    const unsigned w = unsigned(gpu.width()), h = unsigned(gpu.height());
    quality_ = std::max(0, std::min(2, quality));
    int s = api_->streamCreate(&stream_);
    if (s) { error = api_->describe(s, "CUDA stream"); destroy(); return false; }
    s = api_->createEffect("VideoFrameGeneration", &effect_);
    if (s) { error = api_->describe(s, "VideoFrameGeneration effect"); destroy(); return false; }
    struct { const char* key; unsigned value; } settings[] = {{"InputWidth", w}, {"InputHeight", h}, {"Mode", unsigned(quality_)}, {"AutomaticShotChangeDetectionEnabled", 1}};
    s = api_->setStream(effect_, "CudaStream", stream_);
    for (auto& p : settings) if (!s) s = api_->setU32(effect_, p.key, p.value);
    if (s) { error = api_->describe(s, "effect setup"); destroy(); return false; }
    s = api_->load(effect_);
    if (s) { error = api_->describe(s, "effect load"); destroy(); return false; }
    for (void** img : {&gpuA_, &gpuB_, &gpuOut_}) {
        s = api_->imageCreate(w, h, kRgba, kU8, kChunky, kGpuMemory, 32, img);
        if (s) { error = api_->describe(s, "GPU image"); destroy(); return false; }
    }
    s = api_->imageCreate(w, h, kRgba, kU8, kChunky, kGpuMemory, 32, &temp_);
    if (!s) s = api_->setImage(effect_, "DstImage0", gpuOut_);
    if (!s) s = api_->setU32(effect_, "FrameMultiplier", 0);   // explicit time steps
    if (s) { error = api_->describe(s, "output setup"); destroy(); return false; }
    // Register the engine's D3D11 frame slots and output with CUDA.
    for (int i = 0; i < 6; ++i) {
        auto* storage = new ImageStorage();
        memset(storage, 0, sizeof *storage);
        ID3D11Texture2D* texture = i < Gpu::kSlots ? gpu.frame(i) : gpu.output();
        s = api_->fromD3D11(storage, texture);
        if (s || storage->width != w || storage->height != h) {
            if (!s) api_->imageDealloc(storage);
            delete storage;
            error = s ? api_->describe(s, "D3D11 interop") : "D3D11 interop returned an unexpected image";
            destroy(); return false;
        }
        slotImages_[i] = storage;
    }
    ready_ = true;
    return true;
}

bool VfxGenerator::interpolate(Gpu& gpu, int slotA, int slotB, float phase, bool shotChange, std::string& error) {
    if (!ready_) { error = "not ready"; return false; }
    const int64_t start = now100ns();
    gpu.context()->Flush();
    void* a = slotImages_[slotA]; void* b = slotImages_[slotB]; void* out = slotImages_[5];
    int s = api_->map(a, stream_);
    const bool mappedA = s == 0;
    if (!s) s = api_->map(b, stream_);
    const bool mappedB = mappedA && s == 0;
    if (!s) s = api_->map(out, stream_);
    const bool mappedOut = mappedB && s == 0;
    if (!s) s = api_->transfer(a, gpuA_, 1.f, stream_, temp_);
    if (!s) s = api_->transfer(b, gpuB_, 1.f, stream_, temp_);
    if (!s) s = api_->setImage(effect_, "SrcImage0", gpuA_);
    if (!s) s = api_->setImage(effect_, "SrcImage1", gpuB_);
    if (!s) s = api_->setU32(effect_, "ShotChange", shotChange ? 1u : 0u);
    if (!s) s = api_->setF32(effect_, "Timestep", std::min(0.999f, std::max(0.001f, phase)));
    if (!s) s = api_->run(effect_, 0);
    if (!s) s = api_->transfer(gpuOut_, out, 1.f, stream_, temp_);
    // Unmapping waits for the stream, so the D3D11 side sees finished pictures.
    auto unmap = [&](void* image, bool mapped) { if (mapped) { int result = api_->unmap(image, stream_); if (!s) s = result; } };
    unmap(out, mappedOut); unmap(b, mappedB); unmap(a, mappedA);
    lastMs_ = double(now100ns() - start) / 1e4;
    if (s) { error = api_->describe(s, "frame generation"); return false; }
    return true;
}

void VfxGenerator::destroy() {
    ready_ = false;
    if (api_) {
        for (auto& img : slotImages_) if (img) { api_->imageDealloc(img); delete static_cast<ImageStorage*>(img); img = nullptr; }
        for (void** img : {&gpuA_, &gpuB_, &gpuOut_, &temp_}) if (*img) { api_->imageDestroy(*img); *img = nullptr; }
        if (effect_) { api_->destroyEffect(effect_); effect_ = nullptr; }
        if (stream_) { api_->streamDestroy(stream_); stream_ = nullptr; }
        delete api_; api_ = nullptr;
    }
    // Modules stay loaded: the CUDA runtime does not support being unloaded and reloaded
    // inside one process.
    modules_.clear();
}

} // namespace fdl
