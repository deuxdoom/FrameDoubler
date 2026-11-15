#include "dlss.h"
#include "gpu.h"
#include "nvof.h"

#include <d3d11_4.h>
#include <d3d12.h>
#include <algorithm>
#include <atomic>
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

} // namespace

DlssMotion parseDlssMotion(const std::string& text) {
    if (text == "own") return DlssMotion::Own;
    if (text == "auto") return DlssMotion::Hardware;
    if (text == "nvof") return DlssMotion::HardwareRequired;
    return DlssMotion::None;
}

const char* dlssMotionName(DlssMotion motion) {
    switch (motion) {
    case DlssMotion::Own: return "own";
    case DlssMotion::Hardware: return "auto";
    case DlssMotion::HardwareRequired: return "nvof";
    default: return "none";
    }
}

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

    // D3D12 side. Command allocators are reused round-robin once the GPU has finished with
    // them; the CPU only waits when all of them are still in flight.
    static constexpr int kRing = 3;
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> allocators[kRing];
    uint64_t allocatorFence[kRing] = {};
    int current = 0;
    ComPtr<ID3D12GraphicsCommandList> list;
    ComPtr<ID3D12Fence> done;            // D3D12 work finished (shared: D3D11 waits on it on the GPU)
    ComPtr<ID3D11Fence> done11;
    ComPtr<ID3D12Fence> handoff12;       // D3D11 finished writing the inputs
    ComPtr<ID3D11Fence> handoff11;
    ComPtr<ID3D11Device5> device11;
    ComPtr<ID3D11DeviceContext4> context11;
    HANDLE event = nullptr;
    uint64_t doneValue = 0, handoffValue = 0;
    // GPU timestamps around each evaluation, one pair per allocator slot.
    ComPtr<ID3D12QueryHeap> queries;
    ComPtr<ID3D12Resource> queryReadback;
    double tickMs = 0;
    bool timed[kRing] = {};
    double gpuSum = 0, lastGpu = -1;
    int gpuCount = 0;
    // DLSSG.OutputDisableInterpolation: cleared before, copied per allocator slot after
    // each shown evaluation, read once that slot's fence has passed.
    ComPtr<ID3D12Resource> disable12, disableZero, disableReadback;
    bool disableUsed[kRing] = {};
    std::atomic<uint64_t> generatedCount{0}, cachedCount{0}, disableChecked{0}, disableSet{0};   // read by the status thread

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
    int source = 0;                      // 0 no vectors (zeros), 1 engine block motion, 2 hardware optical flow
    float vectorScale = 1.f;
    bool camera = false;
    float toPrevious[4][4]{}, fromPrevious[4][4]{};
    NvofFlow flow;
    float viewToClip[4][4]{}, clipToView[4][4]{}, identity[4][4]{};

    bool waitFor(uint64_t value, std::string& error) {
        if (done->GetCompletedValue() >= value) return true;
        if (FAILED(done->SetEventOnCompletion(value, event)) || WaitForSingleObject(event, 5000) != WAIT_OBJECT_0) { error = "GPU did not finish in time"; return false; }
        return true;
    }
    void collect() {
        const uint64_t completed = done->GetCompletedValue();
        if (disableReadback) for (int i = 0; i < kRing; ++i) {
            if (!disableUsed[i] || allocatorFence[i] > completed) continue;
            disableUsed[i] = false;
            D3D12_RANGE range{size_t(i) * 256, size_t(i) * 256 + 4}; void* mapped = nullptr;
            if (FAILED(disableReadback->Map(0, &range, &mapped))) continue;
            const bool set = static_cast<uint8_t*>(mapped)[range.Begin] != 0;
            D3D12_RANGE none{0, 0}; disableReadback->Unmap(0, &none);
            ++disableChecked; disableSet += set;
        }
        if (!queries) return;
        for (int i = 0; i < kRing; ++i) {
            if (!timed[i] || allocatorFence[i] > completed) continue;
            timed[i] = false;
            D3D12_RANGE range{size_t(i) * 16, size_t(i) * 16 + 16}; void* mapped = nullptr;
            if (FAILED(queryReadback->Map(0, &range, &mapped))) continue;
            uint64_t ticks[2]; memcpy(ticks, static_cast<uint8_t*>(mapped) + range.Begin, 16);
            D3D12_RANGE none{0, 0}; queryReadback->Unmap(0, &none);
            if (ticks[1] <= ticks[0]) continue;
            lastGpu = double(ticks[1] - ticks[0]) * tickMs;
            gpuSum += lastGpu; ++gpuCount;
        }
    }
    // Closes and queues the list. D3D11 work recorded afterwards waits for it on the GPU;
    // the CPU waits only when asked (creation, upload buffers) or when every allocator is busy.
    bool submit(std::string& error, bool cpuWait = false) {
        if (FAILED(list->Close())) { error = "command list close"; return false; }
        ID3D12CommandList* lists[] = {list.Get()};
        queue->ExecuteCommandLists(1, lists);
        if (FAILED(queue->Signal(done.Get(), ++doneValue))) { error = "fence"; return false; }
        allocatorFence[current] = doneValue;
        if (FAILED(context11->Wait(done11.Get(), doneValue))) { error = "shared fence wait (D3D11)"; return false; }
        if (cpuWait && !waitFor(doneValue, error)) return false;
        current = (current + 1) % kRing;
        if (!waitFor(allocatorFence[current], error)) return false;
        collect();
        if (FAILED(device->GetDeviceRemovedReason())) { error = "D3D12 device removed"; return false; }
        if (FAILED(allocators[current]->Reset()) || FAILED(list->Reset(allocators[current].Get(), nullptr))) { error = "command list reset"; return false; }
        return true;
    }
    // D3D11's queued writes come first; D3D12 starts only after they are done.
    bool handOver(std::string& error) {
        if (FAILED(context11->Signal(handoff11.Get(), ++handoffValue))) { error = "shared fence signal"; return false; }
        context11->Flush();
        if (FAILED(queue->Wait(handoff12.Get(), handoffValue))) { error = "shared fence wait"; return false; }
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
    // D3D12 device on the engine's adapter, queue, allocators, both fences, timestamps and the
    // shared pictures. No NGX call is made here.
    bool bridge(Gpu& gpu, std::string& error) {
        width = gpu.width(); height = gpu.height();
        ComPtr<IDXGIFactory4> factory; ComPtr<IDXGIAdapter1> adapter;
        const LUID luid = gpu.adapterLuid();
        if (FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory))) || FAILED(factory->EnumAdapterByLuid(luid, IID_PPV_ARGS(&adapter)))) { error = "graphics adapter not found"; return false; }
        if (FAILED(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device)))) { error = "Direct3D 12 is not available on this GPU"; return false; }
        D3D12_COMMAND_QUEUE_DESC queueDesc{};
        if (FAILED(device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&queue)))) { error = "Direct3D 12 queue"; return false; }
        for (auto& a : allocators) if (FAILED(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&a)))) { error = "Direct3D 12 command allocator"; return false; }
        if (FAILED(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocators[0].Get(), nullptr, IID_PPV_ARGS(&list)))) { error = "Direct3D 12 command list"; return false; }
        event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        ComPtr<ID3D11Device> d11 = gpu.device();
        ComPtr<ID3D11DeviceContext> c11 = gpu.context();
        if (FAILED(d11.As(&device11)) || FAILED(c11.As(&context11))) { error = "Direct3D 11.4 is required for DLSS frame generation"; return false; }
        // D3D11 -> D3D12: the inputs are written.
        HANDLE handle = nullptr;
        if (FAILED(device11->CreateFence(0, D3D11_FENCE_FLAG_SHARED, IID_PPV_ARGS(&handoff11))) ||
            FAILED(handoff11->CreateSharedHandle(nullptr, GENERIC_ALL, nullptr, &handle))) { error = "shared fence"; return false; }
        HRESULT hr = device->OpenSharedHandle(handle, IID_PPV_ARGS(&handoff12));
        CloseHandle(handle);
        if (FAILED(hr)) { error = "D3D12 could not open the shared fence"; return false; }
        // D3D12 -> D3D11: the generated picture is written.
        handle = nullptr;
        if (FAILED(device->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&done))) ||
            FAILED(device->CreateSharedHandle(done.Get(), nullptr, GENERIC_ALL, nullptr, &handle))) { error = "D3D12 shared fence"; return false; }
        hr = device11->OpenSharedFence(handle, IID_PPV_ARGS(&done11));
        CloseHandle(handle);
        if (FAILED(hr)) { error = "D3D11 could not open the D3D12 fence"; return false; }
        // Timestamps are diagnostics only: without them the generator still runs.
        UINT64 frequency = 0;
        D3D12_QUERY_HEAP_DESC qd{D3D12_QUERY_HEAP_TYPE_TIMESTAMP, UINT(2 * kRing), 0};
        D3D12_HEAP_PROPERTIES readbackHeap{D3D12_HEAP_TYPE_READBACK};
        D3D12_RESOURCE_DESC b{}; b.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER; b.Width = 16 * kRing; b.Height = 1; b.DepthOrArraySize = 1; b.MipLevels = 1;
        b.SampleDesc.Count = 1; b.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        if (FAILED(queue->GetTimestampFrequency(&frequency)) || !frequency || FAILED(device->CreateQueryHeap(&qd, IID_PPV_ARGS(&queries))) ||
            FAILED(device->CreateCommittedResource(&readbackHeap, D3D12_HEAP_FLAG_NONE, &b, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&queryReadback)))) {
            queries.Reset(); queryReadback.Reset();
        } else tickMs = 1000.0 / double(frequency);
        {
            // Diagnostics only: without these buffers the flag is simply not requested.
            D3D12_HEAP_PROPERTIES defaultHeap{D3D12_HEAP_TYPE_DEFAULT}, uploadHeap{D3D12_HEAP_TYPE_UPLOAD};
            D3D12_RESOURCE_DESC f = b; f.Width = 256; f.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
            D3D12_RESOURCE_DESC z = b; z.Width = 256;
            D3D12_RESOURCE_DESC r = b; r.Width = 256 * kRing;
            void* zero = nullptr;
            if (FAILED(device->CreateCommittedResource(&defaultHeap, D3D12_HEAP_FLAG_NONE, &f, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&disable12))) ||
                FAILED(device->CreateCommittedResource(&uploadHeap, D3D12_HEAP_FLAG_NONE, &z, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&disableZero))) ||
                FAILED(device->CreateCommittedResource(&readbackHeap, D3D12_HEAP_FLAG_NONE, &r, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&disableReadback))) ||
                FAILED(disableZero->Map(0, nullptr, &zero))) {
                disable12.Reset(); disableZero.Reset(); disableReadback.Reset();
            } else { memset(zero, 0, 256); disableZero->Unmap(0, nullptr); }
        }
        if (!share(d11.Get(), DXGI_FORMAT_R8G8B8A8_UNORM, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET, colour11, colour12, error) ||
            !share(d11.Get(), DXGI_FORMAT_R16G16_FLOAT, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS, motion11, motion12, error) ||
            !share(d11.Get(), DXGI_FORMAT_R8G8B8A8_UNORM, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_RENDER_TARGET, out11, out12, error))
            return false;
        if (FAILED(d11->CreateUnorderedAccessView(motion11.Get(), nullptr, &motionUav))) { error = "motion vector view"; return false; }
        const float zero[4] = {0, 0, 0, 0};
        // The only immediate-context call while creating: the caller does not hold the
        // GPU lock, so the renderer keeps presenting during NGX initialisation.
        { std::lock_guard<std::mutex> lock(gpu.lock()); context11->ClearUnorderedAccessViewFloat(motionUav.Get(), zero); }
        return true;
    }
    void beginTimer() { if (queries) list->EndQuery(queries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, UINT(current * 2)); }
    void endTimer() {
        if (!queries) return;
        list->EndQuery(queries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, UINT(current * 2 + 1));
        list->ResolveQueryData(queries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, UINT(current * 2), 2, queryReadback.Get(), UINT64(current) * 16);
        timed[current] = true;
    }
    // One DLSS-G evaluation of the picture currently in the shared colour texture.
    bool run(bool reset, int index, int generated, std::string& error) {
        const bool moving = camera && !reset;
        if (!handOver(error)) return false;
        const auto read = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, write = D3D12_RESOURCE_STATE_UNORDERED_ACCESS, common = D3D12_RESOURCE_STATE_COMMON;
        D3D12_RESOURCE_BARRIER enter[] = {barrier(colour12.Get(), common, read), barrier(motion12.Get(), common, read), barrier(out12.Get(), common, write)};
        list->ResourceBarrier(3, enter);
        // A priming (reset) evaluation's output is not shown, so its flag is not asked for.
        const bool flag = disable12 && !reset;
        if (flag) {
            auto clear = barrier(disable12.Get(), common, D3D12_RESOURCE_STATE_COPY_DEST);
            list->ResourceBarrier(1, &clear);
            list->CopyBufferRegion(disable12.Get(), 0, disableZero.Get(), 0, 4);
            auto writable = barrier(disable12.Get(), D3D12_RESOURCE_STATE_COPY_DEST, write);
            list->ResourceBarrier(1, &writable);
        }
        Parameters* p = parameters;
        setResource(p, "DLSSG.Backbuffer", colour12.Get()); setResource(p, "DLSSG.MVecs", motion12.Get()); setResource(p, "DLSSG.Depth", depth12.Get());
        setResource(p, "DLSSG.HUDLess", nullptr); setResource(p, "DLSSG.UI", nullptr); setResource(p, "DLSSG.UIAlpha", nullptr);
        setResource(p, "DLSSG.BidirectionalDistortionField", nullptr);
        setResource(p, "DLSSG.OutputInterpolated", out12.Get()); setResource(p, "DLSSG.OutputReal", nullptr); setResource(p, "DLSSG.OutputDisableInterpolation", flag ? disable12.Get() : nullptr);
        setUint(p, "DLSSG.MultiFrameCount", unsigned(generated)); setUint(p, "DLSSG.MultiFrameIndex", unsigned(index));
        setPointer(p, "DLSSG.CameraViewToClip", viewToClip); setPointer(p, "DLSSG.ClipToCameraView", clipToView);
        setPointer(p, "DLSSG.ClipToLensClip", identity);
        setPointer(p, "DLSSG.ClipToPrevClip", moving ? toPrevious : identity); setPointer(p, "DLSSG.PrevClipToClip", moving ? fromPrevious : identity);
        setFloat(p, "DLSSG.JitterOffsetX", 0); setFloat(p, "DLSSG.JitterOffsetY", 0);
        // Motion is stored in pixels (current picture to the previous one); these scales normalise it.
        setFloat(p, "DLSSG.MvecScaleX", vectorScale / float(width)); setFloat(p, "DLSSG.MvecScaleY", vectorScale / float(height));
        setFloat(p, "DLSSG.CameraPinholeOffsetX", 0); setFloat(p, "DLSSG.CameraPinholeOffsetY", 0);
        setFloat(p, "DLSSG.CameraPosX", 0); setFloat(p, "DLSSG.CameraPosY", 0); setFloat(p, "DLSSG.CameraPosZ", 0);
        setFloat(p, "DLSSG.CameraUpX", 0); setFloat(p, "DLSSG.CameraUpY", 1); setFloat(p, "DLSSG.CameraUpZ", 0);
        setFloat(p, "DLSSG.CameraRightX", 1); setFloat(p, "DLSSG.CameraRightY", 0); setFloat(p, "DLSSG.CameraRightZ", 0);
        setFloat(p, "DLSSG.CameraFwdX", 0); setFloat(p, "DLSSG.CameraFwdY", 0); setFloat(p, "DLSSG.CameraFwdZ", 1);
        setFloat(p, "DLSSG.CameraNear", 0.1f); setFloat(p, "DLSSG.CameraFar", 1000.f); setFloat(p, "DLSSG.CameraFOV", 1.5707963f);
        setFloat(p, "DLSSG.CameraAspectRatio", float(width) / float(height));
        setUint(p, "DLSSG.ColorBuffersHDR", 0); setUint(p, "DLSSG.DepthInverted", 0); setUint(p, "DLSSG.CameraMotionIncluded", moving ? 0 : 1);
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
        beginTimer();
        const unsigned result = evaluate(list.Get(), feature, p, nullptr);
        endTimer();
        trace(verbose, "evaluate: " + hex(result));
        D3D12_RESOURCE_BARRIER leave[] = {barrier(colour12.Get(), read, common), barrier(motion12.Get(), read, common), barrier(out12.Get(), write, common)};
        list->ResourceBarrier(3, leave);
        if (flag) {
            D3D12_RESOURCE_BARRIER uav{}; uav.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV; uav.UAV.pResource = disable12.Get();
            auto source = barrier(disable12.Get(), write, D3D12_RESOURCE_STATE_COPY_SOURCE);
            D3D12_RESOURCE_BARRIER toCopy[] = {uav, source};
            list->ResourceBarrier(2, toCopy);
            list->CopyBufferRegion(disableReadback.Get(), UINT64(current) * 256, disable12.Get(), 0, 4);
            auto back = barrier(disable12.Get(), D3D12_RESOURCE_STATE_COPY_SOURCE, common);
            list->ResourceBarrier(1, &back);
            disableUsed[current] = true;
        }
        if (!submit(error)) return false;
        if (result != kSuccess) { error = "DLSS frame generation evaluate " + hex(result); return false; }
        return true;
    }
    // Self-test stand-in for an evaluation: the D3D12 queue copies colour into out.
    bool copyRun(std::string& error) {
        if (!handOver(error)) return false;
        const auto common = D3D12_RESOURCE_STATE_COMMON;
        D3D12_RESOURCE_BARRIER enter[] = {barrier(colour12.Get(), common, D3D12_RESOURCE_STATE_COPY_SOURCE), barrier(out12.Get(), common, D3D12_RESOURCE_STATE_COPY_DEST)};
        list->ResourceBarrier(2, enter);
        beginTimer();
        list->CopyResource(out12.Get(), colour12.Get());
        endTimer();
        D3D12_RESOURCE_BARRIER leave[] = {barrier(colour12.Get(), D3D12_RESOURCE_STATE_COPY_SOURCE, common), barrier(out12.Get(), D3D12_RESOURCE_STATE_COPY_DEST, common)};
        list->ResourceBarrier(2, leave);
        return submit(error);
    }
    // Before anything the GPU may still use is released.
    void idle() { std::string ignored; if (done && doneValue) waitFor(doneValue, ignored); }
};

bool DlssGenerator::runtimePresent(const std::wstring& directory) {
    return !directory.empty() && GetFileAttributesW((directory + L"\\nvngx_dlssg.dll").c_str()) != INVALID_FILE_ATTRIBUTES &&
           GetFileAttributesW(corePath().c_str()) != INVALID_FILE_ATTRIBUTES;
}

int DlssGenerator::probeMaxMultiplier(const LUID& adapter, const std::wstring& featureDirectory, const std::wstring& dataDirectory, std::string& error, bool diagnostics) {
    trace(diagnostics, "probe: begin");
    if (!runtimePresent(featureDirectory)) { error = "DLSS frame generation runtime or NVIDIA driver component not found"; return 0; }
    std::error_code pathError;
    const std::wstring featurePath = std::filesystem::absolute(featureDirectory, pathError).wstring();
    const std::wstring dataPath = std::filesystem::absolute(dataDirectory.empty() ? featureDirectory : dataDirectory, pathError).wstring();
    if (!pathError) std::filesystem::create_directories(dataPath, pathError);
    if (pathError) { error = "DLSS directories: " + pathError.message(); return 0; }
    ComPtr<IDXGIFactory4> factory; ComPtr<IDXGIAdapter1> device12Adapter; ComPtr<ID3D12Device> device;
    if (FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory))) || FAILED(factory->EnumAdapterByLuid(adapter, IID_PPV_ARGS(&device12Adapter)))) { error = "graphics adapter not found"; return 0; }
    if (FAILED(D3D12CreateDevice(device12Adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device)))) { error = "Direct3D 12 is not available on this GPU"; return 0; }
    trace(diagnostics, "probe: D3D12 device ready");
    HMODULE core = LoadLibraryExW(corePath().c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!core) { error = "NVIDIA NGX core could not be loaded"; return 0; }
    trace(diagnostics, "probe: core loaded");
    auto init = reinterpret_cast<unsigned (*)(const char*, int, const char*, const wchar_t*, ID3D12Device*, int, const CommonInfo*)>(GetProcAddress(core, "NVSDK_NGX_D3D12_Init_ProjectID"));
    auto capabilities = reinterpret_cast<unsigned (*)(Parameters**)>(GetProcAddress(core, "NVSDK_NGX_D3D12_GetCapabilityParameters"));
    auto destroyParameters = reinterpret_cast<unsigned (*)(Parameters*)>(GetProcAddress(core, "NVSDK_NGX_D3D12_DestroyParameters"));
    auto shutdown = reinterpret_cast<unsigned (*)(ID3D12Device*)>(GetProcAddress(core, "NVSDK_NGX_D3D12_Shutdown1"));
    if (!init || !capabilities || !destroyParameters || !shutdown) { error = "the NVIDIA driver is too old for DLSS frame generation"; return 0; }
    const wchar_t* paths[] = {featurePath.c_str()};
    CommonInfo info{}; info.paths.path = paths; info.paths.length = 1;
    if (diagnostics) { info.logging.callback = &ngxLog; info.logging.minimum = 2; }
    trace(diagnostics, "probe: init");
    unsigned result = init(kProjectId, kEngineCustom, "2.0", dataPath.c_str(), device.Get(), kApiVersion, &info);
    trace(diagnostics, "probe: init " + hex(result));
    if (result != kSuccess) { error = "NGX initialisation " + hex(result); return 0; }
    Parameters* caps = nullptr;
    int multiplier = 0;
    result = capabilities(&caps);
    trace(diagnostics, "probe: capabilities " + hex(result));
    if (result != kSuccess || !caps) error = "NGX capability query " + hex(result);
    else {
        setUint(caps, "FrameDoubler.Probe.Uint", 0xA1B2C3D4u); setFloat(caps, "FrameDoubler.Probe.Float", 1.25f);
        unsigned probeUint = 0; float probeFloat = 0;
        getUint(caps, "FrameDoubler.Probe.Uint", &probeUint); getFloat(caps, "FrameDoubler.Probe.Float", &probeFloat);
        int available = 0; unsigned maximum = 0;
        if (probeUint != 0xA1B2C3D4u || probeFloat != 1.25f) error = "NGX parameter interface layout is not the expected one";
        else {
            getInt(caps, "FrameGeneration.Available", &available); getUint(caps, "DLSSG.MultiFrameCountMax", &maximum);
            if (available) multiplier = int(std::clamp<unsigned>(maximum, 1u, 5u)) + 1;
            else error = "DLSS frame generation is not available on this GPU";
        }
        trace(diagnostics, "probe: read, multiplier " + std::to_string(multiplier));
        destroyParameters(caps);
    }
    trace(diagnostics, "probe: shutdown");
    shutdown(device.Get());
    trace(diagnostics, "probe: done");
    return multiplier;
}

bool DlssGenerator::create(Gpu& gpu, const std::wstring& featureDirectory, const std::wstring& dataDirectory, std::string& error, const DlssOptions& options) {
    destroy();
    const bool diagnostics = options.diagnostics;
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
    trace(diagnostics, "create: " + std::to_string(gpu.width()) + "x" + std::to_string(gpu.height()) + ", motion " + dlssMotionName(options.motion) + (options.lite ? ", lite" : ""));
    std::string bridgeError;
    if (!s->bridge(gpu, bridgeError)) return fail(bridgeError);

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
    setUint(p, "DLSSG.Width", unsigned(s->width)); setUint(p, "DLSSG.Height", unsigned(s->height));
    setUint(p, "DLSSG.BackbufferFormat", unsigned(DXGI_FORMAT_R8G8B8A8_UNORM));
    setUint(p, "DLSSG.InternalWidth", unsigned(s->width)); setUint(p, "DLSSG.InternalHeight", unsigned(s->height)); setUint(p, "DLSSG.DynamicResolution", 0);
    // Public SDK parameter (nvsdk_ngx_defs_dlssg.h); runtimes that do not know it ignore it.
    setUint(p, "DLSSG.LiteCandidateBlendingEnabled", options.lite ? 1u : 0u);
    trace(diagnostics, "create feature: begin");
    result = s->createFeature(s->list.Get(), kFeatureFrameGeneration, p, &s->feature);
    trace(diagnostics, "create feature: " + hex(result));
    std::string submitError;
    if (!s->submit(submitError, true)) return fail(submitError);
    if (result != kSuccess || !s->feature) return fail("DLSS frame generation could not be created " + hex(result));

    // The flat depth buffer.
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
        if (!s->submit(submitError, true)) return fail(submitError);   // the upload buffer must outlive the copy
    }
    // A camera that does not move: all motion is in the vector buffer, but the projection
    // and its inverse still have to agree with each other.
    const float aspect = float(s->width) / float(s->height), nearPlane = 0.1f, farPlane = 1000.f;
    const float px = 1.f / aspect, pz = farPlane / (farPlane - nearPlane), pw = -nearPlane * pz;
    for (int i = 0; i < 4; ++i) s->identity[i][i] = 1;
    s->viewToClip[0][0] = px; s->viewToClip[1][1] = 1; s->viewToClip[2][2] = pz; s->viewToClip[2][3] = 1; s->viewToClip[3][2] = pw;
    s->clipToView[0][0] = 1 / px; s->clipToView[1][1] = 1; s->clipToView[2][3] = 1 / pw; s->clipToView[3][2] = 1; s->clipToView[3][3] = -pz / pw;

    s->source = options.motion == DlssMotion::Own ? 1 : 0;
    if (options.motion == DlssMotion::Hardware || options.motion == DlssMotion::HardwareRequired) {
        std::string flowError;
        if (s->flow.create(gpu, 1, flowError, false)) s->source = 2;
        else if (options.motion == DlssMotion::HardwareRequired) return fail(flowError);
        else { s->source = 1; trace(diagnostics, flowError + "; using own block motion"); }
    }
    version_ = "DLSS-G, up to " + std::to_string(maxGenerated_ + 1) + "x";
    if (s->source == 2) version_ += ", hardware optical flow";
    else if (s->source == 1) version_ += ", block motion vectors";
    if (options.lite) version_ += ", lite";
    s->vectorScale = options.vectorScale;
    if (options.vectorScale != 1.f) version_ += ", vector scale " + std::to_string(options.vectorScale);
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
        const bool continuous = s.haveHistory && s.fedId == idA;
        if (!continuous) {
            // DLSS-G keeps the previous picture itself. When it did not just receive A, hand A
            // over first as a new sequence; that evaluation's output is not used.
            c->CopyResource(s.colour11.Get(), gpu.frame(slotA));
            if (s.source != 0) { const float zero[4] = {0, 0, 0, 0}; c->ClearUnorderedAccessViewFloat(s.motionUav.Get(), zero); }
            ++s.frameId;
            if (!s.run(true, 1, generated, error)) { ready_ = false; return false; }
            s.haveHistory = true;
        }
        c->CopyResource(s.colour11.Get(), gpu.frame(slotB));
        if (s.source == 2) {
            if (!s.flow.estimate(gpu, slotA, slotB, !continuous, error)) { ready_ = false; return false; }
            s.flow.motionVectors(gpu, s.motionUav.Get());
        } else if (s.source == 1) gpu.motionVectors(slotB, s.motionUav.Get());
        // source 0: the vector texture was cleared at creation and is never written.
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
        // Queued after the GPU-side wait that submit() inserted, so it reads the finished picture.
        c->CopyResource(cached.Get(), s.out11.Get());
        s.generatedThrough = next;
        ++s.generatedCount;
    }
    if (!generating) ++s.cachedCount;
    if (!generating) trace(s.diagnostics && s.cacheHits++ < 2, "reuse generated frame: pair=" + std::to_string(idA) + "/" + std::to_string(idB) + ", index=" + std::to_string(index));
    c->CopyResource(gpu.output(), s.generatedFrames[size_t(index - 1)].Get());
    if (generating) {
        QueryPerformanceCounter(&end);
        s.collect();
        lastMs_ = s.lastGpu >= 0 ? s.lastGpu : double(end.QuadPart - begin.QuadPart) * 1000.0 / double(frequency.QuadPart);
    }
    return true;
}

namespace {
// Pixel homography -> clip-space matrix on the flat depth plane (z = 0.5), DirectX row vectors
// (clip' = clip * M). x/w and y/w follow the homography, z/w stays on the plane.
void cameraMatrix(const double* h, int width, int height, bool transposed, float out[4][4]) {
    const double N[9] = {2.0 / width, 0, -1, 0, -2.0 / height, 1, 0, 0, 1};
    const double Ni[9] = {width / 2.0, 0, width / 2.0, 0, -height / 2.0, height / 2.0, 0, 0, 1};
    double t[9] = {}, n[9] = {};
    for (int r = 0; r < 3; ++r) for (int c = 0; c < 3; ++c) for (int k = 0; k < 3; ++k) t[r * 3 + c] += h[r * 3 + k] * Ni[k * 3 + c];
    for (int r = 0; r < 3; ++r) for (int c = 0; c < 3; ++c) for (int k = 0; k < 3; ++k) n[r * 3 + c] += N[r * 3 + k] * t[k * 3 + c];
    for (double& v : n) v /= n[8];
    const double z = 0.5;
    double m[4][4] = {};
    m[0][0] = n[0]; m[1][0] = n[1]; m[3][0] = n[2];
    m[0][1] = n[3]; m[1][1] = n[4]; m[3][1] = n[5];
    m[0][3] = n[6]; m[1][3] = n[7]; m[3][3] = n[8];
    m[0][2] = z * n[6]; m[1][2] = z * n[7]; m[2][2] = n[8];
    for (int r = 0; r < 4; ++r) for (int c = 0; c < 4; ++c) out[r][c] = float(transposed ? m[c][r] : m[r][c]);
}
bool invert3(const double* a, double* out) {
    const double d = a[0] * (a[4] * a[8] - a[5] * a[7]) - a[1] * (a[3] * a[8] - a[5] * a[6]) + a[2] * (a[3] * a[7] - a[4] * a[6]);
    if (std::fabs(d) < 1e-15) return false;
    out[0] = (a[4] * a[8] - a[5] * a[7]) / d; out[1] = (a[2] * a[7] - a[1] * a[8]) / d; out[2] = (a[1] * a[5] - a[2] * a[4]) / d;
    out[3] = (a[5] * a[6] - a[3] * a[8]) / d; out[4] = (a[0] * a[8] - a[2] * a[6]) / d; out[5] = (a[2] * a[3] - a[0] * a[5]) / d;
    out[6] = (a[3] * a[7] - a[4] * a[6]) / d; out[7] = (a[1] * a[6] - a[0] * a[7]) / d; out[8] = (a[0] * a[4] - a[1] * a[3]) / d;
    return true;
}
} // namespace

void DlssGenerator::setCameraMotion(const double* h, bool transposed) {
    if (!state_) return;
    double inverse[9];
    state_->camera = h && invert3(h, inverse);
    if (!state_->camera) return;
    cameraMatrix(h, state_->width, state_->height, transposed, state_->toPrevious);
    cameraMatrix(inverse, state_->width, state_->height, transposed, state_->fromPrevious);
}

void DlssGenerator::resetHistory() {
    if (!state_) return;
    state_->haveHistory = false; state_->fedId = UINT64_MAX;
    state_->pairA = state_->pairB = UINT64_MAX; state_->generatedThrough = 0;
}

double DlssGenerator::averageGpuMs() const {
    if (!state_) return -1;
    state_->collect();
    return state_->gpuCount ? state_->gpuSum / state_->gpuCount : -1;
}
int DlssGenerator::gpuSamples() const { return state_ ? state_->gpuCount : 0; }
DlssGenerator::Counters DlssGenerator::counters() const {
    Counters c;
    if (state_) { c.generated = state_->generatedCount; c.cached = state_->cachedCount; c.checked = state_->disableChecked; c.disabled = state_->disableSet; }
    return c;
}
bool DlssGenerator::needsEngineMotion() const { return state_ && state_->source == 1; }
bool DlssGenerator::hardwareMotion() const { return state_ && state_->source == 2; }
std::string DlssGenerator::motionSource() const { return !state_ ? "" : state_->source == 2 ? "nvof" : state_->source == 1 ? "own" : "none"; }

void DlssGenerator::destroy() {
    ready_ = false;
    if (!state_) return;
    State* s = state_; state_ = nullptr;
    s->idle();
    if (s->feature && s->release) { trace(s->diagnostics, "release feature"); s->release(s->feature); }
    if (s->parameters && s->destroyParameters) { trace(s->diagnostics, "destroy parameters"); s->destroyParameters(s->parameters); }
    if (s->initialised && s->shutdown) { trace(s->diagnostics, "shutdown"); s->shutdown(s->device.Get()); }
    if (s->event) CloseHandle(s->event);
    // The NGX core stays loaded: unloading it while the driver still holds callbacks is unsafe.
    delete s;
}

bool DlssGenerator::bridgeSelfTest(Gpu& gpu, std::string& detail) {
    State s;
    std::string error;
    if (!s.bridge(gpu, error)) { detail = error; s.idle(); if (s.event) CloseHandle(s.event); return false; }
    const int w = s.width, h = s.height;
    std::vector<uint8_t> picture(size_t(w) * h * 4), result;
    bool ok = true;
    int mismatched = 0;
    // More rounds than allocators: each one reuses a slot only after the GPU released it.
    const int rounds = 2 * State::kRing + 1;
    for (int round = 0; round < rounds && ok; ++round) {
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x) {
                uint8_t* p = &picture[(size_t(y) * w + x) * 4];
                p[0] = uint8_t(x * 3 + round * 17); p[1] = uint8_t(y * 5 + round * 29); p[2] = uint8_t((x ^ y) + round * 41); p[3] = 255;
            }
        gpu.context()->UpdateSubresource(s.colour11.Get(), 0, nullptr, picture.data(), UINT(w * 4), 0);
        ok = s.copyRun(error);
        if (!ok) break;
        // No CPU wait here: the copy below is ordered after D3D12 by the GPU-side fence wait.
        gpu.context()->CopyResource(gpu.output(), s.out11.Get());
        ok = gpu.readback(gpu.output(), result);
        if (!ok) { error = "readback"; break; }
        if (result != picture) ++mismatched;
    }
    s.idle();
    s.collect();
    if (s.event) CloseHandle(s.event);
    detail = ok ? std::to_string(rounds) + " rounds through " + std::to_string(State::kRing) + " allocators, " + std::to_string(mismatched) + " mismatched, " +
                  std::to_string(s.gpuCount) + " GPU timings" : error;
    return ok && mismatched == 0 && (!s.queries || s.gpuCount > 0);
}

} // namespace fdl
