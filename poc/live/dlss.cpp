#include "dlss.h"
#include "gpu.h"
#include "nvof.h"

#include <d3d11_4.h>
#include <d3d12.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <filesystem>

namespace fdl {
namespace {

// NVSDK_NGX_Parameter is a C++ interface built with MSVC, which orders overloaded virtual
// functions in reverse declaration order inside each overload group. The engine is built
// with a different C++ ABI, so the table is called by index; create() confirms the order
// with a typed round trip before any value is trusted.
struct Parameters { void** table; };
enum { kSetPointer = 0, kSetD3d12 = 1, kSetInt = 3, kSetUint = 4, kSetFloat = 6, kSetUint64 = 7,
       kGetInt = 11, kGetUint = 12, kGetFloat = 14, kGetUint64 = 15 };
void setUint(Parameters* p, const char* n, unsigned v) { reinterpret_cast<void (*)(Parameters*, const char*, unsigned)>(p->table[kSetUint])(p, n, v); }
void setFloat(Parameters* p, const char* n, float v) { reinterpret_cast<void (*)(Parameters*, const char*, float)>(p->table[kSetFloat])(p, n, v); }
void setUint64(Parameters* p, const char* n, unsigned long long v) { reinterpret_cast<void (*)(Parameters*, const char*, unsigned long long)>(p->table[kSetUint64])(p, n, v); }
void setPointer(Parameters* p, const char* n, void* v) { reinterpret_cast<void (*)(Parameters*, const char*, void*)>(p->table[kSetPointer])(p, n, v); }
void setResource(Parameters* p, const char* n, ID3D12Resource* v) { reinterpret_cast<void (*)(Parameters*, const char*, ID3D12Resource*)>(p->table[kSetD3d12])(p, n, v); }
unsigned getUint(Parameters* p, const char* n, unsigned* v) { return reinterpret_cast<unsigned (*)(Parameters*, const char*, unsigned*)>(p->table[kGetUint])(p, n, v); }
unsigned getInt(Parameters* p, const char* n, int* v) { return reinterpret_cast<unsigned (*)(Parameters*, const char*, int*)>(p->table[kGetInt])(p, n, v); }
unsigned getFloat(Parameters* p, const char* n, float* v) { return reinterpret_cast<unsigned (*)(Parameters*, const char*, float*)>(p->table[kGetFloat])(p, n, v); }
unsigned getUint64(Parameters* p, const char* n, unsigned long long* v) { return reinterpret_cast<unsigned (*)(Parameters*, const char*, unsigned long long*)>(p->table[kGetUint64])(p, n, v); }

// Public NGX values (nvsdk_ngx_defs.h, nvsdk_ngx_defs_dlssg.h).
constexpr unsigned kSuccess = 1;
constexpr int kFeatureFrameGeneration = 11, kEngineCustom = 0, kApiVersion = 0x15;
constexpr unsigned kFlagBackbuffer = 1, kFlagMotion = 2, kFlagDepth = 4, kFlagHudless = 8, kFlagUi = 16, kFlagDistortion = 32, kFlagUiAlpha = 128;
struct PathList { const wchar_t* const* path; unsigned length; };
struct Logging { void (*callback)(const char*, int, int); int minimum; bool disableOtherSinks; };
struct CommonInfo { PathList paths; void* internal; Logging logging; };
static_assert(sizeof(CommonInfo) == 40 && offsetof(CommonInfo, logging) == 24);
const char* kProjectId = "6b1f6d0e-52a4-4c0b-9f6a-3d6c1f0b7a21"; // FrameDoubler's own identifier (custom engine)

std::wstring corePath() {
    wchar_t buffer[MAX_PATH]{};
    struct { const wchar_t* key; const wchar_t* value; } places[] = {
        {L"SYSTEM\\CurrentControlSet\\Services\\nvlddmkm\\NGXCore", L"NGXPath"},
        {L"SOFTWARE\\NVIDIA Corporation\\Global\\NGXCore", L"FullPath"}};
    for (auto& place : places) {
        HKEY key = nullptr; DWORD size = sizeof buffer;
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, place.key, 0, KEY_READ, &key) != ERROR_SUCCESS) continue;
        const LONG result = RegQueryValueExW(key, place.value, nullptr, nullptr, reinterpret_cast<LPBYTE>(buffer), &size);
        RegCloseKey(key);
        if (result != ERROR_SUCCESS) continue;
        const std::wstring path = std::wstring(buffer) + L"\\_nvngx.dll";
        if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES) return path;
    }
    GetSystemDirectoryW(buffer, MAX_PATH);
    return std::wstring(buffer) + L"\\_nvngx.dll";
}

std::string hex(unsigned value) { char text[16]; snprintf(text, sizeof text, "0x%08X", value); return text; }

void trace(bool enabled, const std::string& message) {
    if (!enabled) return;
    static std::mutex mutex;
    std::lock_guard<std::mutex> lock(mutex);
    fprintf(stderr, "[dlss] %s\n", message.c_str());
    fflush(stderr); // Keep the last completed stage even if a driver call crashes.
}

void ngxLog(const char* message, int, int) { if (message) trace(true, std::string("NGX: ") + message); }

uint16_t half(float v) {
    uint32_t bits; memcpy(&bits, &v, 4);
    const uint32_t sign = (bits >> 16) & 0x8000; const int exponent = int((bits >> 23) & 0xff) - 127 + 15;
    if (v == 0 || exponent <= 0) return uint16_t(sign);
    if (exponent >= 31) return uint16_t(sign | 0x7c00);
    return uint16_t(sign | (exponent << 10) | ((bits >> 13) & 0x3ff));
}

} // namespace

struct DlssGenerator::State {
    HMODULE core = nullptr;
    // Driver export, NOT the SDK's Init_with_ProjectID wrapper: the last two arguments
    // are SDK version then common info. Using the wrapper's order dereferences 0x15.
    unsigned (*init)(const char*, int, const char*, const wchar_t*, ID3D12Device*, int, const CommonInfo*) = nullptr;
    unsigned (*allocate)(Parameters**) = nullptr;
    unsigned (*capabilities)(Parameters**) = nullptr;
    unsigned (*destroyParameters)(Parameters*) = nullptr;
    unsigned (*createFeature)(ID3D12GraphicsCommandList*, int, Parameters*, void**) = nullptr;
    unsigned (*evaluate)(ID3D12GraphicsCommandList*, void*, Parameters*, void*) = nullptr;
    unsigned (*release)(void*) = nullptr;
    unsigned (*shutdown)(ID3D12Device*) = nullptr;
    bool initialised = false;
    bool diagnostics = false;
    unsigned evaluations = 0;
    Parameters* parameters = nullptr;
    void* feature = nullptr;

    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> list;
    ComPtr<ID3D12Fence> done;            // D3D12 work finished (waited on the CPU)
    ComPtr<ID3D12Fence> handoff12;       // D3D11 finished writing the inputs
    ComPtr<ID3D11Fence> handoff11;
    ComPtr<ID3D11Device5> device11;
    ComPtr<ID3D11DeviceContext4> context11;
    HANDLE event = nullptr;
    uint64_t doneValue = 0, handoffValue = 0;

    // Shared pictures: written by D3D11, read by DLSS-G (colour, motion) and the reverse (out).
    ComPtr<ID3D11Texture2D> colour11, motion11, out11;
    ComPtr<ID3D11UnorderedAccessView> motionUav;
    ComPtr<ID3D12Resource> colour12, motion12, out12, depth12;
    int width = 0, height = 0;
    uint64_t fedId = UINT64_MAX;         // picture DLSS-G last received as its current frame
    bool haveHistory = false;
    uint64_t frameId = 0;
    // DLSS-G has temporal history. Re-evaluating the same subframe is not a stateless
    // interpolation: keep each generated result for the current pair instead.
    uint64_t pairA = UINT64_MAX, pairB = UINT64_MAX;
    int pairGenerated = 0, generatedThrough = 0;
    std::vector<ComPtr<ID3D11Texture2D>> generatedFrames;
    unsigned cacheHits = 0;
    NvofFlow flow;
    float viewToClip[4][4]{}, clipToView[4][4]{}, identity[4][4]{};

    bool submit(std::string& error) {
        if (FAILED(list->Close())) { error = "command list close"; return false; }
        ID3D12CommandList* lists[] = {list.Get()};
        queue->ExecuteCommandLists(1, lists);
        if (FAILED(queue->Signal(done.Get(), ++doneValue)) || FAILED(done->SetEventOnCompletion(doneValue, event))) { error = "fence"; return false; }
        if (WaitForSingleObject(event, 5000) != WAIT_OBJECT_0) { error = "GPU did not finish in time"; return false; }
        if (FAILED(device->GetDeviceRemovedReason())) { error = "D3D12 device removed"; return false; }
        if (FAILED(allocator->Reset()) || FAILED(list->Reset(allocator.Get(), nullptr))) { error = "command list reset"; return false; }
        return true;
    }
    bool share(ID3D11Device* device11Raw, DXGI_FORMAT format, UINT bind, ComPtr<ID3D11Texture2D>& texture11, ComPtr<ID3D12Resource>& texture12, std::string& error) {
        D3D11_TEXTURE2D_DESC d{UINT(width), UINT(height), 1, 1, format, {1, 0}, D3D11_USAGE_DEFAULT, bind, 0,
                               D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED};
        if (FAILED(device11Raw->CreateTexture2D(&d, nullptr, &texture11))) { error = "shared texture"; return false; }
        ComPtr<IDXGIResource1> resource; HANDLE handle = nullptr;
        if (FAILED(texture11.As(&resource)) || FAILED(resource->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &handle))) { error = "shared handle"; return false; }
        const HRESULT hr = device->OpenSharedHandle(handle, IID_PPV_ARGS(&texture12));
        CloseHandle(handle);
        if (FAILED(hr)) { error = "D3D12 could not open the shared texture"; return false; }
        return true;
    }
    static D3D12_RESOURCE_BARRIER barrier(ID3D12Resource* r, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
        D3D12_RESOURCE_BARRIER b{}; b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition.pResource = r; b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        b.Transition.StateBefore = before; b.Transition.StateAfter = after; return b;
    }
    // One DLSS-G evaluation of the picture currently in the shared colour texture.
    bool run(bool reset, int index, int generated, std::string& error) {
        // D3D11 has queued its writes; D3D12 starts only after they are done.
        if (FAILED(context11->Signal(handoff11.Get(), ++handoffValue))) { error = "shared fence signal"; return false; }
        context11->Flush();
        if (FAILED(queue->Wait(handoff12.Get(), handoffValue))) { error = "shared fence wait"; return false; }
        const auto read = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, write = D3D12_RESOURCE_STATE_UNORDERED_ACCESS, common = D3D12_RESOURCE_STATE_COMMON;
        D3D12_RESOURCE_BARRIER enter[] = {barrier(colour12.Get(), common, read), barrier(motion12.Get(), common, read), barrier(out12.Get(), common, write)};
        list->ResourceBarrier(3, enter);
        Parameters* p = parameters;
        setResource(p, "DLSSG.Backbuffer", colour12.Get()); setResource(p, "DLSSG.MVecs", motion12.Get()); setResource(p, "DLSSG.Depth", depth12.Get());
        setResource(p, "DLSSG.HUDLess", nullptr); setResource(p, "DLSSG.UI", nullptr); setResource(p, "DLSSG.UIAlpha", nullptr);
        setResource(p, "DLSSG.BidirectionalDistortionField", nullptr);
        setResource(p, "DLSSG.OutputInterpolated", out12.Get()); setResource(p, "DLSSG.OutputReal", nullptr); setResource(p, "DLSSG.OutputDisableInterpolation", nullptr);
        setUint(p, "DLSSG.MultiFrameCount", unsigned(generated)); setUint(p, "DLSSG.MultiFrameIndex", unsigned(index));
        setPointer(p, "DLSSG.CameraViewToClip", viewToClip); setPointer(p, "DLSSG.ClipToCameraView", clipToView);
        setPointer(p, "DLSSG.ClipToLensClip", identity); setPointer(p, "DLSSG.ClipToPrevClip", identity); setPointer(p, "DLSSG.PrevClipToClip", identity);
        setFloat(p, "DLSSG.JitterOffsetX", 0); setFloat(p, "DLSSG.JitterOffsetY", 0);
        // Motion is stored in pixels (current picture to the previous one); these scales normalise it.
        setFloat(p, "DLSSG.MvecScaleX", 1.f / float(width)); setFloat(p, "DLSSG.MvecScaleY", 1.f / float(height));
        setFloat(p, "DLSSG.CameraPinholeOffsetX", 0); setFloat(p, "DLSSG.CameraPinholeOffsetY", 0);
        setFloat(p, "DLSSG.CameraPosX", 0); setFloat(p, "DLSSG.CameraPosY", 0); setFloat(p, "DLSSG.CameraPosZ", 0);
        setFloat(p, "DLSSG.CameraUpX", 0); setFloat(p, "DLSSG.CameraUpY", 1); setFloat(p, "DLSSG.CameraUpZ", 0);
        setFloat(p, "DLSSG.CameraRightX", 1); setFloat(p, "DLSSG.CameraRightY", 0); setFloat(p, "DLSSG.CameraRightZ", 0);
        setFloat(p, "DLSSG.CameraFwdX", 0); setFloat(p, "DLSSG.CameraFwdY", 0); setFloat(p, "DLSSG.CameraFwdZ", 1);
        setFloat(p, "DLSSG.CameraNear", 0.1f); setFloat(p, "DLSSG.CameraFar", 1000.f); setFloat(p, "DLSSG.CameraFOV", 1.5707963f);
        setFloat(p, "DLSSG.CameraAspectRatio", float(width) / float(height));
        setUint(p, "DLSSG.ColorBuffersHDR", 0); setUint(p, "DLSSG.DepthInverted", 0); setUint(p, "DLSSG.CameraMotionIncluded", 1);
        setUint(p, "DLSSG.Reset", reset ? 1 : 0); setUint(p, "DLSSG.AutomodeOverrideReset", 0); setUint(p, "DLSSG.NotRenderingGameFrames", 0);
        setUint(p, "DLSSG.OrthoProjection", 0); setFloat(p, "DLSSG.MvecInvalidValue", 3.402823466e38f); setUint(p, "DLSSG.MvecDilated", 0);
        setUint(p, "DLSSG.MenuDetectionEnabled", 0);
        for (const char* rect : {"DLSSG.MVecsSubrect", "DLSSG.DepthSubrect", "DLSSG.InputBackbufferSubrect", "DLSSG.OutputInterpolatedSubrect"}) {
            const std::string name = rect;
            setUint(p, (name + "BaseX").c_str(), 0); setUint(p, (name + "BaseY").c_str(), 0);
            setUint(p, (name + "Width").c_str(), unsigned(width)); setUint(p, (name + "Height").c_str(), unsigned(height));
        }
        setFloat(p, "DLSSG.MinRelativeLinearDepthObjectSeparation", 40.f);
        setUint64(p, "DLSSG.BackbufferFrameID", frameId);
        const bool verbose = diagnostics && evaluations++ < 4;
        trace(verbose, "evaluate: begin, frame=" + std::to_string(frameId) + ", reset=" + std::to_string(reset) + ", index=" + std::to_string(index));
        const unsigned result = evaluate(list.Get(), feature, p, nullptr);
        trace(verbose, "evaluate: " + hex(result));
        D3D12_RESOURCE_BARRIER leave[] = {barrier(colour12.Get(), read, common), barrier(motion12.Get(), read, common), barrier(out12.Get(), write, common)};
        list->ResourceBarrier(3, leave);
        if (!submit(error)) return false;
        if (result != kSuccess) { error = "DLSS frame generation evaluate " + hex(result); return false; }
        return true;
    }
};

bool DlssGenerator::runtimePresent(const std::wstring& directory) {
    return !directory.empty() && GetFileAttributesW((directory + L"\\nvngx_dlssg.dll").c_str()) != INVALID_FILE_ATTRIBUTES &&
           GetFileAttributesW(corePath().c_str()) != INVALID_FILE_ATTRIBUTES;
}

bool DlssGenerator::create(Gpu& gpu, const std::wstring& featureDirectory, const std::wstring& dataDirectory, std::string& error, bool diagnostics, int motionMode) {
    destroy();
    if (!runtimePresent(featureDirectory)) { error = "DLSS frame generation runtime or NVIDIA driver component not found"; return false; }
    if (dataDirectory.empty()) { error = "DLSS needs a writable data directory (CLI: --dlss-data DIR)"; return false; }
    std::error_code pathError;
    const std::wstring featurePath = std::filesystem::absolute(featureDirectory, pathError).wstring();
    if (pathError) { error = "DLSS runtime directory: " + pathError.message(); return false; }
    const std::wstring dataPath = std::filesystem::absolute(dataDirectory, pathError).wstring();
    if (!pathError) std::filesystem::create_directories(dataPath, pathError);
    if (pathError) { error = "DLSS data directory: " + pathError.message(); return false; }
    auto s = std::make_unique<State>();
    s->diagnostics = diagnostics;
    state_ = s.get();
    auto fail = [&](const std::string& message) { error = message; trace(diagnostics, "failed: " + message); s.release(); destroy(); return false; };
    s->width = gpu.width(); s->height = gpu.height();
    trace(diagnostics, "create: " + std::to_string(s->width) + "x" + std::to_string(s->height));

    // D3D12 device on the adapter the engine already uses.
    ComPtr<IDXGIFactory4> factory; ComPtr<IDXGIAdapter1> adapter;
    const LUID luid = gpu.adapterLuid();
    if (FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory))) || FAILED(factory->EnumAdapterByLuid(luid, IID_PPV_ARGS(&adapter)))) return fail("graphics adapter not found");
    if (FAILED(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&s->device)))) return fail("Direct3D 12 is not available on this GPU");
    D3D12_COMMAND_QUEUE_DESC queueDesc{};
    if (FAILED(s->device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&s->queue))) ||
        FAILED(s->device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&s->allocator))) ||
        FAILED(s->device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, s->allocator.Get(), nullptr, IID_PPV_ARGS(&s->list))) ||
        FAILED(s->device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&s->done)))) return fail("Direct3D 12 command objects");
    s->event = CreateEventW(nullptr, FALSE, FALSE, nullptr);

    // Fence that orders D3D11's writes before D3D12's reads.
    ComPtr<ID3D11Device> device11 = gpu.device();
    ComPtr<ID3D11DeviceContext> context11 = gpu.context();
    if (FAILED(device11.As(&s->device11)) || FAILED(context11.As(&s->context11))) return fail("Direct3D 11.4 is required for DLSS frame generation");
    HANDLE fenceHandle = nullptr;
    if (FAILED(s->device11->CreateFence(0, D3D11_FENCE_FLAG_SHARED, IID_PPV_ARGS(&s->handoff11))) ||
        FAILED(s->handoff11->CreateSharedHandle(nullptr, GENERIC_ALL, nullptr, &fenceHandle))) return fail("shared fence");
    const HRESULT fenceResult = s->device->OpenSharedHandle(fenceHandle, IID_PPV_ARGS(&s->handoff12));
    CloseHandle(fenceHandle);
    if (FAILED(fenceResult)) return fail("D3D12 could not open the shared fence");

    // NGX core from the driver.
    const std::wstring core = corePath();
    trace(diagnostics, "load core: " + utf8(core));
    s->core = LoadLibraryExW(core.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!s->core) return fail("NVIDIA NGX core could not be loaded");
    auto bind = [&](auto& target, const char* name) { target = reinterpret_cast<std::remove_reference_t<decltype(target)>>(GetProcAddress(s->core, name)); return target != nullptr; };
    if (!bind(s->init, "NVSDK_NGX_D3D12_Init_ProjectID") || !bind(s->allocate, "NVSDK_NGX_D3D12_AllocateParameters") ||
        !bind(s->capabilities, "NVSDK_NGX_D3D12_GetCapabilityParameters") || !bind(s->destroyParameters, "NVSDK_NGX_D3D12_DestroyParameters") ||
        !bind(s->createFeature, "NVSDK_NGX_D3D12_CreateFeature") || !bind(s->evaluate, "NVSDK_NGX_D3D12_EvaluateFeature") ||
        !bind(s->release, "NVSDK_NGX_D3D12_ReleaseFeature") || !bind(s->shutdown, "NVSDK_NGX_D3D12_Shutdown1")) return fail("the NVIDIA driver is too old for DLSS frame generation");
    const wchar_t* paths[] = {featurePath.c_str()};
    CommonInfo info{}; info.paths.path = paths; info.paths.length = 1;
    if (diagnostics) { info.logging.callback = &ngxLog; info.logging.minimum = 2; }
    trace(diagnostics, "runtime: " + utf8(featurePath) + ", data: " + utf8(dataPath));
    trace(diagnostics, "init: begin, SDK=" + hex(kApiVersion));
    unsigned result = s->init(kProjectId, kEngineCustom, "2.0", dataPath.c_str(), s->device.Get(), kApiVersion, &info);
    trace(diagnostics, "init: " + hex(result));
    if (result != kSuccess) return fail("NGX initialisation " + hex(result));
    s->initialised = true;

    Parameters* caps = nullptr;
    trace(diagnostics, "capabilities: begin");
    result = s->capabilities(&caps);
    trace(diagnostics, "capabilities: " + hex(result));
    if (result != kSuccess || !caps) return fail("NGX capability query " + hex(result));
    // Typed round trip checks the known ABI before feature parameters are populated.
    trace(diagnostics, "parameter ABI: begin");
    setUint(caps, "FrameDoubler.Probe.Uint", 0xA1B2C3D4u); setFloat(caps, "FrameDoubler.Probe.Float", 1.25f); setUint64(caps, "FrameDoubler.Probe.Uint64", 0x1122334455667788ull);
    unsigned probeUint = 0; float probeFloat = 0; unsigned long long probeUint64 = 0;
    getUint(caps, "FrameDoubler.Probe.Uint", &probeUint); getFloat(caps, "FrameDoubler.Probe.Float", &probeFloat); getUint64(caps, "FrameDoubler.Probe.Uint64", &probeUint64);
    if (probeUint != 0xA1B2C3D4u || probeFloat != 1.25f || probeUint64 != 0x1122334455667788ull) { s->destroyParameters(caps); return fail("NGX parameter interface layout is not the expected one"); }
    trace(diagnostics, "parameter ABI: passed");
    int available = 0, needsDriver = 0; unsigned maximum = 0;
    getInt(caps, "FrameGeneration.Available", &available); getInt(caps, "FrameGeneration.NeedsUpdatedDriver", &needsDriver);
    getUint(caps, "DLSSG.MultiFrameCountMax", &maximum);
    s->destroyParameters(caps);
    trace(diagnostics, "available=" + std::to_string(available) + ", needsDriver=" + std::to_string(needsDriver) + ", maxGenerated=" + std::to_string(maximum));
    if (!available) return fail(needsDriver ? "DLSS frame generation needs a newer NVIDIA driver" : "DLSS frame generation is not available on this GPU");
    maxGenerated_ = int(std::clamp<unsigned>(maximum, 1u, 5u));

    trace(diagnostics, "allocate parameters: begin");
    result = s->allocate(&s->parameters);
    trace(diagnostics, "allocate parameters: " + hex(result));
    if (result != kSuccess || !s->parameters) return fail("NGX parameters " + hex(result));
    Parameters* p = s->parameters;
    setUint(p, "DLSSG.ResourceAlwaysProvidedFlags", kFlagBackbuffer | kFlagMotion | kFlagDepth);
    setUint(p, "DLSSG.ResourceNeverProvidedFlags", kFlagHudless | kFlagUi | kFlagUiAlpha | kFlagDistortion);
    setUint(p, "CreationNodeMask", 1); setUint(p, "VisibilityNodeMask", 1);
    setUint(p, "Width", unsigned(s->width)); setUint(p, "Height", unsigned(s->height));
    setUint(p, "DLSSG.BackbufferFormat", unsigned(DXGI_FORMAT_R8G8B8A8_UNORM));
    setUint(p, "DLSSG.InternalWidth", unsigned(s->width)); setUint(p, "DLSSG.InternalHeight", unsigned(s->height)); setUint(p, "DLSSG.DynamicResolution", 0);
    trace(diagnostics, "create feature: begin");
    result = s->createFeature(s->list.Get(), kFeatureFrameGeneration, p, &s->feature);
    trace(diagnostics, "create feature: " + hex(result));
    std::string submitError;
    if (!s->submit(submitError)) return fail(submitError);
    if (result != kSuccess || !s->feature) return fail("DLSS frame generation could not be created " + hex(result));

    // Shared pictures and the flat depth buffer.
    if (!s->share(device11.Get(), DXGI_FORMAT_R8G8B8A8_UNORM, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET, s->colour11, s->colour12, error) ||
        !s->share(device11.Get(), DXGI_FORMAT_R16G16_FLOAT, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS, s->motion11, s->motion12, error) ||
        !s->share(device11.Get(), DXGI_FORMAT_R8G8B8A8_UNORM, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_RENDER_TARGET, s->out11, s->out12, error))
        return fail(error);
    if (FAILED(device11->CreateUnorderedAccessView(s->motion11.Get(), nullptr, &s->motionUav))) return fail("motion vector view");
    {
        D3D12_HEAP_PROPERTIES heap{D3D12_HEAP_TYPE_DEFAULT};
        D3D12_RESOURCE_DESC d{}; d.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D; d.Width = UINT64(s->width); d.Height = UINT(s->height);
        d.DepthOrArraySize = 1; d.MipLevels = 1; d.Format = DXGI_FORMAT_R32_FLOAT; d.SampleDesc.Count = 1;
        if (FAILED(s->device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &d, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&s->depth12)))) return fail("depth texture");
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint; UINT64 total = 0;
        s->device->GetCopyableFootprints(&d, 0, 1, 0, &footprint, nullptr, nullptr, &total);
        D3D12_HEAP_PROPERTIES uploadHeap{D3D12_HEAP_TYPE_UPLOAD};
        D3D12_RESOURCE_DESC b{}; b.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER; b.Width = total; b.Height = 1; b.DepthOrArraySize = 1; b.MipLevels = 1;
        b.SampleDesc.Count = 1; b.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ComPtr<ID3D12Resource> upload;
        if (FAILED(s->device->CreateCommittedResource(&uploadHeap, D3D12_HEAP_FLAG_NONE, &b, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&upload)))) return fail("depth upload");
        BYTE* mapped = nullptr;
        if (FAILED(upload->Map(0, nullptr, reinterpret_cast<void**>(&mapped)))) return fail("depth upload map");
        for (UINT y = 0; y < UINT(s->height); ++y) {
            float* row = reinterpret_cast<float*>(mapped + footprint.Offset + size_t(y) * footprint.Footprint.RowPitch);
            for (int x = 0; x < s->width; ++x) row[x] = 0.5f;   // one flat plane
        }
        upload->Unmap(0, nullptr);
        D3D12_TEXTURE_COPY_LOCATION source{upload.Get(), D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT}; source.PlacedFootprint = footprint;
        D3D12_TEXTURE_COPY_LOCATION target{s->depth12.Get(), D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX};
        s->list->CopyTextureRegion(&target, 0, 0, 0, &source, nullptr);
        auto ready = State::barrier(s->depth12.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        s->list->ResourceBarrier(1, &ready);
        if (!s->submit(submitError)) return fail(submitError);
    }
    // A camera that does not move: all motion is in the vector buffer, but the projection
    // and its inverse still have to agree with each other.
    const float aspect = float(s->width) / float(s->height), nearPlane = 0.1f, farPlane = 1000.f;
    const float px = 1.f / aspect, pz = farPlane / (farPlane - nearPlane), pw = -nearPlane * pz;
    for (int i = 0; i < 4; ++i) s->identity[i][i] = 1;
    s->viewToClip[0][0] = px; s->viewToClip[1][1] = 1; s->viewToClip[2][2] = pz; s->viewToClip[2][3] = 1; s->viewToClip[3][2] = pw;
    s->clipToView[0][0] = 1 / px; s->clipToView[1][1] = 1; s->clipToView[2][3] = 1 / pw; s->clipToView[3][2] = 1; s->clipToView[3][3] = -pz / pw;

    if(motionMode!=1) {
        std::string flowError;
        if(!s->flow.create(gpu,1,flowError,false)) {
            if(motionMode==2)return fail(flowError);
            trace(diagnostics,flowError+"; using own block motion");
        }
    }
    version_ = "DLSS-G, up to " + std::to_string(maxGenerated_ + 1) + "x";
    if(s->flow.ready())version_ += ", hardware optical flow";
    s.release();
    ready_ = true;
    trace(diagnostics, "ready: " + version_);
    return true;
}

bool DlssGenerator::interpolate(Gpu& gpu, int slotA, int slotB, uint64_t idA, uint64_t idB, float phase, int generated, std::string& error) {
    if (!ready_ || !state_) { error = "DLSS frame generation is not ready"; return false; }
    State& s = *state_;
    generated = std::clamp(generated, 1, maxGenerated_);
    const int index = std::clamp(int(std::lround(phase * float(generated + 1))), 1, generated);
    ID3D11DeviceContext* c = gpu.context();
    LARGE_INTEGER frequency, begin, end; QueryPerformanceFrequency(&frequency); QueryPerformanceCounter(&begin);
    const bool newPair = s.pairA != idA || s.pairB != idB || s.pairGenerated != generated;
    if (newPair) {
        // A changed request with the same last picture must rebuild the pair's history.
        if (s.fedId == idB) { s.haveHistory = false; s.fedId = UINT64_MAX; }
        s.pairA = idA; s.pairB = idB; s.pairGenerated = generated; s.generatedThrough = 0;
        s.generatedFrames.resize(size_t(generated));
    }
    if (s.fedId != idB) {
        const bool continuous=s.haveHistory && s.fedId==idA;
        if (!s.haveHistory || s.fedId != idA) {
            // DLSS-G keeps the previous picture itself. When it did not just receive A, hand A
            // over first as a new sequence; that evaluation's output is not used.
            c->CopyResource(s.colour11.Get(), gpu.frame(slotA));
            const float zero[4] = {0, 0, 0, 0};
            c->ClearUnorderedAccessViewFloat(s.motionUav.Get(), zero);
            ++s.frameId;
            if (!s.run(true, 1, generated, error)) { ready_ = false; return false; }
            s.haveHistory = true;
        }
        c->CopyResource(s.colour11.Get(), gpu.frame(slotB));
        if(s.flow.ready()) {
            if(!s.flow.estimate(gpu,slotA,slotB,!continuous,error)){ready_=false;return false;}
            s.flow.motionVectors(gpu,s.motionUav.Get());
        } else gpu.motionVectors(slotB, s.motionUav.Get());
        ++s.frameId;
        s.fedId = idB;
    }
    const bool generating = index > s.generatedThrough;
    // Emit missing subframes in order, even if the display first asks for a later phase.
    // Earlier/repeated requests only copy a cached texture, without advancing NGX history.
    while (s.generatedThrough < index) {
        const int next = s.generatedThrough + 1;
        auto& cached = s.generatedFrames[size_t(next - 1)];
        if (!cached) {
            D3D11_TEXTURE2D_DESC desc{}; s.out11->GetDesc(&desc);
            desc.BindFlags = 0; desc.MiscFlags = 0;
            const HRESULT hr = gpu.device()->CreateTexture2D(&desc, nullptr, &cached);
            if (FAILED(hr)) { error = "DLSS generated frame cache " + hresult(hr); ready_ = false; return false; }
        }
        if (!s.run(false, next, generated, error)) { ready_ = false; return false; }
        c->CopyResource(cached.Get(), s.out11.Get());
        s.generatedThrough = next;
    }
    if (!generating) trace(s.diagnostics && s.cacheHits++ < 2, "reuse generated frame: pair=" + std::to_string(idA) + "/" + std::to_string(idB) + ", index=" + std::to_string(index));
    c->CopyResource(gpu.output(), s.generatedFrames[size_t(index - 1)].Get());
    if (generating) {
        QueryPerformanceCounter(&end);
        lastMs_ = double(end.QuadPart - begin.QuadPart) * 1000.0 / double(frequency.QuadPart);
    }
    return true;
}

void DlssGenerator::resetHistory() {
    if (!state_) return;
    state_->haveHistory = false; state_->fedId = UINT64_MAX;
    state_->pairA = state_->pairB = UINT64_MAX; state_->generatedThrough = 0;
}

bool DlssGenerator::hardwareMotion() const {return state_ && state_->flow.ready();}

void DlssGenerator::destroy() {
    ready_ = false;
    if (!state_) return;
    State* s = state_; state_ = nullptr;
    if (s->feature && s->release) { trace(s->diagnostics, "release feature"); s->release(s->feature); }
    if (s->parameters && s->destroyParameters) { trace(s->diagnostics, "destroy parameters"); s->destroyParameters(s->parameters); }
    if (s->initialised && s->shutdown) { trace(s->diagnostics, "shutdown"); s->shutdown(s->device.Get()); }
    if (s->event) CloseHandle(s->event);
    // The NGX core stays loaded: unloading it while the driver still holds callbacks is unsafe.
    delete s;
}

} // namespace fdl
