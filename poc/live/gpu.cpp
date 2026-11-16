#include "gpu.h"
#include "shaders.h"

#include <d3d11_4.h>
#include <d3dcompiler.h>

#include <algorithm>
#include <cmath>
#include <cstring>

namespace fdl {
namespace {
int groups(int value, int size) { return (value + size - 1) / size; }
void unbindCompute(ID3D11DeviceContext* c) {
    ID3D11ShaderResourceView* srv[4] = {};
    ID3D11UnorderedAccessView* uav[3] = {};
    c->CSSetShaderResources(0, 4, srv);
    c->CSSetUnorderedAccessViews(0, 3, uav, nullptr);
}
} // namespace

// ---------------------------------------------------------------- GpuTimer
void GpuTimer::create(ID3D11Device* device) {
    for (auto& set : sets_) {
        D3D11_QUERY_DESC d{D3D11_QUERY_TIMESTAMP_DISJOINT, 0};
        device->CreateQuery(&d, &set.disjoint);
        d.Query = D3D11_QUERY_TIMESTAMP;
        for (auto& stage : set.marks) for (auto& q : stage) device->CreateQuery(&d, &q);
    }
}
void GpuTimer::begin(ID3D11DeviceContext* c) {
    Set& set = sets_[write_];
    active_ = !set.pending && set.disjoint;
    if (!active_) return;
    std::fill(std::begin(set.used), std::end(set.used), false);
    c->Begin(set.disjoint.Get());
}
void GpuTimer::mark(ID3D11DeviceContext* c, GpuStage stage, bool start) {
    if (!active_) return;
    Set& set = sets_[write_];
    c->End(set.marks[stage][start ? 0 : 1].Get());
    if (!start) set.used[stage] = true;
}
void GpuTimer::end(ID3D11DeviceContext* c) {
    if (!active_) return;
    Set& set = sets_[write_];
    c->End(set.disjoint.Get());
    set.pending = true;
    write_ = (write_ + 1) % 6;
    active_ = false;
}
void GpuTimer::collect(ID3D11DeviceContext* c) {
    for (auto& set : sets_) {
        if (!set.pending) continue;
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT dj{};
        if (c->GetData(set.disjoint.Get(), &dj, sizeof dj, D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) continue;
        set.pending = false;
        if (dj.Disjoint || !dj.Frequency) continue;
        std::lock_guard<std::mutex> lock(mutex_);
        for (int s = 0; s < StageCount; ++s) {
            if (!set.used[s]) continue;
            UINT64 t0 = 0, t1 = 0;
            if (c->GetData(set.marks[s][0].Get(), &t0, sizeof t0, D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) continue;
            if (c->GetData(set.marks[s][1].Get(), &t1, sizeof t1, D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) continue;
            if (t1 < t0) continue;
            sum_[s] += double(t1 - t0) * 1000.0 / double(dj.Frequency); ++count_[s];
        }
    }
}
void GpuTimer::take(double out[StageCount]) {
    std::lock_guard<std::mutex> lock(mutex_);
    for (int s = 0; s < StageCount; ++s) { out[s] = count_[s] ? sum_[s] / count_[s] : -1; sum_[s] = 0; count_[s] = 0; }
}

// ---------------------------------------------------------------- Gpu
bool Gpu::compile(const char* source, const char* profile, ComPtr<ID3DBlob>& blob, std::string& error) {
    ComPtr<ID3DBlob> messages;
    HRESULT hr = D3DCompile(source, strlen(source), nullptr, nullptr, nullptr, "main", profile,
                            D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &blob, &messages);
    if (FAILED(hr)) {
        error = std::string("shader compile failed: ") + (messages ? static_cast<const char*>(messages->GetBufferPointer()) : hresult(hr).c_str());
        return false;
    }
    return true;
}

bool Gpu::create(std::string& error) {
    HRESULT hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory_));
    if (FAILED(hr)) { error = "DXGI factory " + hresult(hr); return false; }
    ComPtr<IDXGIAdapter1> adapter;
    ComPtr<IDXGIFactory6> factory6;
    if (SUCCEEDED(factory_.As(&factory6)))
        factory6->EnumAdapterByGpuPreference(0, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&adapter));
    if (!adapter) {
        ComPtr<IDXGIFactory1> f1; factory_.As(&f1);
        if (f1) f1->EnumAdapters1(0, &adapter);
    }
    if (!adapter) { error = "no graphics adapter"; return false; }
    DXGI_ADAPTER_DESC1 desc{}; adapter->GetDesc1(&desc);
    adapterName_ = utf8(desc.Description); luid_ = desc.AdapterLuid;
    const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    D3D_FEATURE_LEVEL got{};
    hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
                           levels, 2, D3D11_SDK_VERSION, &device_, &got, &context_);
    if (FAILED(hr))
        hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                               levels, 2, D3D11_SDK_VERSION, &device_, &got, &context_);
    if (FAILED(hr)) { error = "Direct3D 11 device " + hresult(hr); return false; }
    // The encoder (Media Foundation) and CUDA interop touch the device from other threads.
    ComPtr<ID3D11Multithread> multithread;
    if (SUCCEEDED(context_.As(&multithread))) multithread->SetMultithreadProtected(TRUE);
    ComPtr<IDXGIDevice1> dxgiDevice;
    if (SUCCEEDED(device_.As(&dxgiDevice))) dxgiDevice->SetMaximumFrameLatency(1);
    ComPtr<IDXGIFactory5> factory5;
    BOOL allowTearing = FALSE;
    if (SUCCEEDED(factory_.As(&factory5)) && SUCCEEDED(factory5->CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING, &allowTearing, sizeof allowTearing)))
        tearing_ = allowTearing != FALSE;

    struct Cs { const char* src; ComPtr<ID3D11ComputeShader>* out; };
    Cs cs[] = {{shaders::kConvert, &convert_}, {shaders::kLumaFromRgb, &lumaFromRgb_}, {shaders::kLumaHalve, &lumaHalve_},
               {shaders::kMotion, &motion_}, {shaders::kMedian, &median_}, {shaders::kInterpolate, &interpolate_},
               {shaders::kAntiAlias, &antiAlias_}, {shaders::kMotionVectors, &motionVectors_}};
    for (auto& s : cs) {
        ComPtr<ID3DBlob> blob;
        if (!compile(s.src, "cs_5_0", blob, error)) return false;
        if (FAILED(device_->CreateComputeShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, s.out->GetAddressOf()))) { error = "compute shader"; return false; }
    }
    ComPtr<ID3DBlob> blob;
    if (!compile(shaders::kPresentVs, "vs_5_0", blob, error)) return false;
    device_->CreateVertexShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &fullscreen_);
    struct Ps { const char* src; ComPtr<ID3D11PixelShader>* out; };
    Ps ps[] = {{shaders::kPresentPs, &present_}, {shaders::kToLumaPs, &toLuma_}, {shaders::kToChromaPs, &toChroma_}};
    for (auto& s : ps) {
        ComPtr<ID3DBlob> b;
        if (!compile(s.src, "ps_5_0", b, error)) return false;
        if (FAILED(device_->CreatePixelShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, s.out->GetAddressOf()))) { error = "pixel shader"; return false; }
    }
    D3D11_SAMPLER_DESC sd{};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    device_->CreateSamplerState(&sd, &linear_);
    D3D11_BUFFER_DESC bd{256, D3D11_USAGE_DYNAMIC, D3D11_BIND_CONSTANT_BUFFER, D3D11_CPU_ACCESS_WRITE, 0, 0};
    device_->CreateBuffer(&bd, nullptr, &constants_);
    timer.create(device_.Get());
    return true;
}

bool Gpu::image(Image& out, int w, int h, DXGI_FORMAT format, bool uav, std::string& error, bool renderTarget) {
    D3D11_TEXTURE2D_DESC d{};
    d.Width = UINT(std::max(1, w)); d.Height = UINT(std::max(1, h)); d.MipLevels = 1; d.ArraySize = 1; d.Format = format;
    d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT;
    d.BindFlags = D3D11_BIND_SHADER_RESOURCE | (uav ? D3D11_BIND_UNORDERED_ACCESS : 0) | (renderTarget ? D3D11_BIND_RENDER_TARGET : 0);
    out = {};
    HRESULT hr = device_->CreateTexture2D(&d, nullptr, &out.texture);
    if (SUCCEEDED(hr)) hr = device_->CreateShaderResourceView(out.texture.Get(), nullptr, &out.srv);
    if (SUCCEEDED(hr) && uav) hr = device_->CreateUnorderedAccessView(out.texture.Get(), nullptr, &out.uav);
    if (SUCCEEDED(hr) && renderTarget) hr = device_->CreateRenderTargetView(out.texture.Get(), nullptr, &out.rtv);
    if (FAILED(hr)) { error = "texture " + std::to_string(w) + "x" + std::to_string(h) + " " + hresult(hr); return false; }
    return true;
}

bool Gpu::configure(int width, int height, std::string& error) {
    width_ = width; height_ = height;
    // Frames are also render targets: SMAA's last pass draws into them.
    for (auto& f : frames_) if (!image(f, width, height, DXGI_FORMAT_R8G8B8A8_UNORM, true, error, true)) return false;
    smaa_.configure(device_.Get(), width, height);
    luma16Upload_.Reset(); chroma16Upload_.Reset(); luma16UploadSrv_.Reset(); chroma16UploadSrv_.Reset();
    if (!image(staging_, width, height, DXGI_FORMAT_R8G8B8A8_UNORM, true, error)) return false;
    if (!image(output_, width, height, DXGI_FORMAT_R8G8B8A8_UNORM, true, error)) return false;
    Image upload;
    if (!image(upload, width, height, DXGI_FORMAT_R8_UNORM, false, error)) return false;
    lumaUpload_ = upload.texture; lumaUploadSrv_ = upload.srv;
    if (!image(upload, (width + 1) / 2, (height + 1) / 2, DXGI_FORMAT_R8G8_UNORM, false, error)) return false;
    chromaUpload_ = upload.texture; chromaUploadSrv_ = upload.srv;
    if (!image(upload, (width + 1) / 2, height, DXGI_FORMAT_R8G8B8A8_UNORM, false, error)) return false;
    packedUpload_ = upload.texture; packedUploadSrv_ = upload.srv;
    if (!image(upload, width, height, DXGI_FORMAT_B8G8R8A8_UNORM, false, error)) return false;
    rgbUpload_ = upload.texture; rgbUploadSrv_ = upload.srv;
    // Pyramid depth: halve until the coarsest level is at most 320 pixels wide.
    levels_ = 2;
    while ((width >> levels_) > 320 && levels_ < 6) ++levels_;
    for (auto& p : pyramids_) {
        p.assign(size_t(levels_), {});
        for (int k = 1; k <= levels_; ++k) if (!image(p[size_t(k - 1)], width >> k, height >> k, DXGI_FORMAT_R8_UNORM, true, error)) return false;
    }
    scratch_.assign(size_t(levels_) * 2, {});
    for (int k = 1; k <= levels_; ++k) {
        const int gw = groups(width >> k, 8), gh = groups(height >> k, 8);
        for (int copy = 0; copy < 2; ++copy) {
            Field& f = scratch_[size_t((k - 1) * 2 + copy)];
            if (!image(f.vectors, gw, gh, DXGI_FORMAT_R32G32_FLOAT, true, error)) return false;
            if (!image(f.cost, gw, gh, DXGI_FORMAT_R32_FLOAT, true, error)) return false;
        }
    }
    for (int s = 0; s < kSlots; ++s) {
        const int gw = groups(width >> 1, 8), gh = groups(height >> 1, 8);
        if (!image(fields_[s].vectors, gw, gh, DXGI_FORMAT_R32G32_FLOAT, true, error)) return false;
        if (!image(fields_[s].cost, gw, gh, DXGI_FORMAT_R32_FLOAT, true, error)) return false;
        D3D11_BUFFER_DESC bd{16, D3D11_USAGE_DEFAULT, D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE, 0, D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS, 0};
        if (FAILED(device_->CreateBuffer(&bd, nullptr, &totals_[s]))) { error = "totals buffer"; return false; }
        D3D11_UNORDERED_ACCESS_VIEW_DESC ud{}; ud.Format = DXGI_FORMAT_R32_TYPELESS; ud.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
        ud.Buffer.NumElements = 4; ud.Buffer.Flags = D3D11_BUFFER_UAV_FLAG_RAW;
        device_->CreateUnorderedAccessView(totals_[s].Get(), &ud, &totalsUav_[s]);
        D3D11_SHADER_RESOURCE_VIEW_DESC sd{}; sd.Format = DXGI_FORMAT_R32_TYPELESS; sd.ViewDimension = D3D11_SRV_DIMENSION_BUFFEREX;
        sd.BufferEx.NumElements = 4; sd.BufferEx.Flags = D3D11_BUFFEREX_SRV_FLAG_RAW;
        device_->CreateShaderResourceView(totals_[s].Get(), &sd, &totalsSrv_[s]);
    }
    readback_.Reset();
    return true;
}

template <class T> void Gpu::constants(const T& value) {
    static_assert(sizeof(T) <= 256 && sizeof(T) % 16 == 0, "constant block");
    D3D11_MAPPED_SUBRESOURCE m{};
    if (SUCCEEDED(context_->Map(constants_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &m))) { memcpy(m.pData, &value, sizeof(T)); context_->Unmap(constants_.Get(), 0); }
    ID3D11Buffer* b = constants_.Get();
    context_->CSSetConstantBuffers(0, 1, &b);
    context_->PSSetConstantBuffers(0, 1, &b);
}

void Gpu::dispatch(ID3D11ComputeShader* shader, int x, int y, int groupX, int groupY) {
    context_->CSSetShader(shader, nullptr, 0);
    ID3D11SamplerState* s = linear_.Get();
    context_->CSSetSamplers(0, 1, &s);
    context_->Dispatch(UINT(groups(x, groupX)), UINT(groups(y, groupY)), 1);
    unbindCompute(context_.Get());
}

void Gpu::antiAliasInto(ID3D11ShaderResourceView* source, ID3D11UnorderedAccessView* target, int aa) {
    // Strength 1 low, 2 medium, 3 high: edge sensitivity, search length and sub-pixel blend.
    const int level = std::clamp(aa, 1, 3);
    const uint32_t steps[] = {6, 9, 12};
    const float edgeMin[] = {0.0833f, 0.0625f, 0.0312f}, edgeRel[] = {0.25f, 0.166f, 0.125f}, subpixel[] = {0.35f, 0.55f, 0.75f};
    struct { uint32_t w, h, steps, pad0; float edgeMin, edgeRel, subpixel, pad1; } c{
        uint32_t(width_), uint32_t(height_), steps[level - 1], 0, edgeMin[level - 1], edgeRel[level - 1], subpixel[level - 1], 0};
    constants(c);
    context_->CSSetShaderResources(0, 1, &source);
    context_->CSSetUnorderedAccessViews(0, 1, &target, nullptr);
    dispatch(antiAlias_.Get(), width_, height_, 16, 8);
}

void Gpu::antiAliasSlot(ID3D11ShaderResourceView* source, int slot, int aa, int method) {
    if (method == AaSmaa) {
        std::string error;
        if (smaa_.apply(context_.Get(), source, frames_[slot].rtv.Get(), aa, error)) { aaUsed_ = AaSmaa; return; }
        std::lock_guard<std::mutex> lock(aaErrorMutex_);
        aaError_ = error;
    }
    antiAliasInto(source, frames_[slot].uav.Get(), aa);
    aaUsed_ = AaEdge;
}

bool Gpu::prepareP010(std::string& error) {
    Image upload;
    if (!luma16Upload_) {
        if (!image(upload, width_, height_, DXGI_FORMAT_R16_UNORM, false, error)) return false;
        luma16Upload_ = upload.texture; luma16UploadSrv_ = upload.srv;
    }
    if (!chroma16Upload_) {
        if (!image(upload, (width_ + 1) / 2, (height_ + 1) / 2, DXGI_FORMAT_R16G16_UNORM, false, error)) return false;
        chroma16Upload_ = upload.texture; chroma16UploadSrv_ = upload.srv;
    }
    return true;
}

void Gpu::upload(int slot, const uint8_t* luma, int lumaPitch, const uint8_t* chroma, int chromaPitch,
                 PixelFormat format, const ColorSetup& color, int aa, int method) {
    const bool ten = format == PixelFormat::P010;
    // Sessions prepare these before capture starts; standalone GPU self-tests use this path.
    if (ten) { std::string error; if (!prepareP010(error)) return; }
    if (format == PixelFormat::NV12) {
        context_->UpdateSubresource(lumaUpload_.Get(), 0, nullptr, luma, UINT(lumaPitch), 0);
        context_->UpdateSubresource(chromaUpload_.Get(), 0, nullptr, chroma, UINT(chromaPitch), 0);
    } else if (ten) {
        context_->UpdateSubresource(luma16Upload_.Get(), 0, nullptr, luma, UINT(lumaPitch), 0);
        context_->UpdateSubresource(chroma16Upload_.Get(), 0, nullptr, chroma, UINT(chromaPitch), 0);
    } else if (format == PixelFormat::RGB32) {
        context_->UpdateSubresource(rgbUpload_.Get(), 0, nullptr, luma, UINT(lumaPitch), 0);
    } else {
        context_->UpdateSubresource(packedUpload_.Get(), 0, nullptr, luma, UINT(lumaPitch), 0);
    }
    const double kr = color.bt2020 ? 0.2627 : color.bt709 ? 0.2126 : 0.299, kb = color.bt2020 ? 0.0593 : color.bt709 ? 0.0722 : 0.114, kg = 1 - kr - kb;
    // 10-bit codes: limited range is 64..940 (luma) and 64..960 around 512 (chroma).
    const float yOffset = color.limited ? (ten ? 64.f / 1023 : 16.f / 255) : 0.f, yScale = color.limited ? (ten ? 1023.f / 876 : 255.f / 219) : 1.f;
    const float cScale = color.limited ? (ten ? 1023.f / 896 : 255.f / 224) : 1.f, cOffset = ten ? 512.f / 1023 : 128.f / 255;
    struct { uint32_t w, h, format, pad0; float yOffset, yScale, cScale, cOffset; float rv, gu, gv, bu; uint32_t transfer, primaries; float peak, pad1; } c{
        uint32_t(width_), uint32_t(height_), ten ? 4u : format == PixelFormat::NV12 ? 0u : format == PixelFormat::UYVY ? 2u : format == PixelFormat::RGB32 ? 3u : 1u, 0,
        yOffset, yScale, cScale, cOffset,
        float(2 * (1 - kr)), float(-2 * kb * (1 - kb) / kg), float(-2 * kr * (1 - kr) / kg), float(2 * (1 - kb)),
        uint32_t(std::clamp(color.transfer, 0, 2)), color.bt2020 ? 1u : 0u, 1000.f, 0.f};
    constants(c);
    ID3D11ShaderResourceView* srv[6] = {lumaUploadSrv_.Get(), chromaUploadSrv_.Get(), packedUploadSrv_.Get(), rgbUploadSrv_.Get(), luma16UploadSrv_.Get(), chroma16UploadSrv_.Get()};
    context_->CSSetShaderResources(0, 6, srv);
    Image& before = keepOriginals_ && originals_[slot].uav ? originals_[slot] : staging_;
    ID3D11UnorderedAccessView* target = aa > 0 ? before.uav.Get() : frames_[slot].uav.Get();
    context_->CSSetUnorderedAccessViews(0, 1, &target, nullptr);
    dispatch(convert_.Get(), width_, height_, 16, 8);
    ID3D11ShaderResourceView* none[6] = {};
    context_->CSSetShaderResources(0, 6, none);
    if (aa > 0) antiAliasSlot(before.srv.Get(), slot, aa, method);
    originalValid_[slot] = aa > 0 && &before != &staging_;
}

void Gpu::uploadRgba(int slot, const uint8_t* rgba, int pitch, int aa, int method) {
    ID3D11Texture2D* target = aa > 0 ? staging_.texture.Get() : frames_[slot].texture.Get();
    context_->UpdateSubresource(target, 0, nullptr, rgba, UINT(pitch), 0);
    if (aa > 0) antiAliasSlot(staging_.srv.Get(), slot, aa, method);
}

void Gpu::pyramid(int slot) {
    auto& p = pyramids_[slot];
    for (int k = 1; k <= levels_; ++k) {
        struct { uint32_t ow, oh, iw, ih; } c{uint32_t(width_ >> k), uint32_t(height_ >> k),
                                             uint32_t(k == 1 ? width_ : width_ >> (k - 1)), uint32_t(k == 1 ? height_ : height_ >> (k - 1))};
        constants(c);
        ID3D11ShaderResourceView* src = k == 1 ? frames_[slot].srv.Get() : p[size_t(k - 2)].srv.Get();
        ID3D11UnorderedAccessView* dst = p[size_t(k - 1)].uav.Get();
        context_->CSSetShaderResources(0, 1, &src);
        context_->CSSetUnorderedAccessViews(0, 1, &dst, nullptr);
        dispatch(k == 1 ? lumaFromRgb_.Get() : lumaHalve_.Get(), width_ >> k, height_ >> k, 16, 8);
    }
}

void Gpu::motion(int slotA, int slotB) {
    const UINT zero[4] = {0, 0, 0, 0};
    context_->ClearUnorderedAccessViewUint(totalsUav_[slotB].Get(), zero);
    for (int k = levels_; k >= 1; --k) {
        const int lw = width_ >> k, lh = height_ >> k;
        const int gw = groups(lw, 8), gh = groups(lh, 8);
        const int pgw = k < levels_ ? groups(width_ >> (k + 1), 8) : 1, pgh = k < levels_ ? groups(height_ >> (k + 1), 8) : 1;
        Field& raw = scratch_[size_t((k - 1) * 2)];
        Field& smooth = scratch_[size_t((k - 1) * 2 + 1)];
        auto search = [&](uint32_t mode, ID3D11ShaderResourceView* predictors) {
            struct { uint32_t lw, lh, gw, gh, pgw, pgh, mode, finest; float scale, lambda; int32_t radius; uint32_t pad; } c{
                uint32_t(lw), uint32_t(lh), uint32_t(gw), uint32_t(gh), uint32_t(pgw), uint32_t(pgh),
                mode, k == 1 ? 1u : 0u, float(1 << k), 0.004f, 12, 0};
            constants(c);
            ID3D11ShaderResourceView* srv[3] = {pyramids_[slotA][size_t(k - 1)].srv.Get(), pyramids_[slotB][size_t(k - 1)].srv.Get(), predictors};
            ID3D11UnorderedAccessView* uav[3] = {raw.vectors.uav.Get(), (k == 1 ? fields_[slotB].cost : raw.cost).uav.Get(), totalsUav_[slotB].Get()};
            context_->CSSetShaderResources(0, 3, srv);
            context_->CSSetUnorderedAccessViews(0, 3, uav, nullptr);
            dispatch(motion_.Get(), gw, gh, 8, 8);
        };
        auto median = [&](ID3D11UnorderedAccessView* out) {
            struct { uint32_t gw, gh, p0, p1; } m{uint32_t(gw), uint32_t(gh), 0, 0};
            constants(m);
            ID3D11ShaderResourceView* in = raw.vectors.srv.Get();
            context_->CSSetShaderResources(0, 1, &in);
            context_->CSSetUnorderedAccessViews(0, 1, &out, nullptr);
            dispatch(median_.Get(), gw, gh, 8, 8);
        };
        // Search (full on the coarsest level, from the parent otherwise), smooth, then let
        // each block try its neighbours' vectors (propagation) and smooth again.
        search(k == levels_ ? 0u : 1u, k < levels_ ? scratch_[size_t(k * 2 + 1)].vectors.srv.Get() : nullptr);
        median(smooth.vectors.uav.Get());
        search(2u, smooth.vectors.srv.Get());
        median((k == 1 ? fields_[slotB].vectors : smooth.vectors).uav.Get());
    }
}

void Gpu::motionVectors(int slotB, ID3D11UnorderedAccessView* target) {
    const int gw = groups(width_ >> 1, 8), gh = groups(height_ >> 1, 8);
    struct { uint32_t w, h, gw, gh; float blockFull, pad[3]; } c{uint32_t(width_), uint32_t(height_), uint32_t(gw), uint32_t(gh), 16.f, {0, 0, 0}};
    constants(c);
    ID3D11ShaderResourceView* srv = fields_[slotB].vectors.srv.Get();
    context_->CSSetShaderResources(0, 1, &srv);
    context_->CSSetUnorderedAccessViews(0, 1, &target, nullptr);
    dispatch(motionVectors_.Get(), width_, height_, 16, 8);
}

void Gpu::interpolate(int slotA, int slotB, float phase, const double* cameraBtoA) {
    const int gw = groups(width_ >> 1, 8), gh = groups(height_ >> 1, 8);
    struct { uint32_t w, h, gw, gh; float phase, blockFull, sharpness, cutCost; float camera[12]; } c{
        uint32_t(width_), uint32_t(height_), uint32_t(gw), uint32_t(gh), phase, 16.f, 150.f, 0.10f, {}};
    if (cameraBtoA) {
        for (int r = 0; r < 3; ++r) for (int k = 0; k < 3; ++k) c.camera[r * 4 + k] = float(cameraBtoA[r * 3 + k] / cameraBtoA[8]);
        c.camera[11] = 1;
    }
    constants(c);
    ID3D11ShaderResourceView* srv[4] = {frames_[slotA].srv.Get(), frames_[slotB].srv.Get(), fields_[slotB].vectors.srv.Get(), totalsSrv_[slotB].Get()};
    ID3D11UnorderedAccessView* uav = output_.uav.Get();
    context_->CSSetShaderResources(0, 4, srv);
    context_->CSSetUnorderedAccessViews(0, 1, &uav, nullptr);
    dispatch(interpolate_.Get(), width_, height_, 16, 8);
}

bool Gpu::attach(HWND hwnd, std::string& error) {
    detach();
    RECT r{}; GetClientRect(hwnd, &r);
    DXGI_SWAP_CHAIN_DESC1 d{};
    d.Width = UINT(std::max<LONG>(8, r.right - r.left)); d.Height = UINT(std::max<LONG>(8, r.bottom - r.top));
    d.Format = DXGI_FORMAT_R8G8B8A8_UNORM; d.SampleDesc.Count = 1;
    d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; d.BufferCount = 3;
    d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD; d.Scaling = DXGI_SCALING_STRETCH; d.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    d.Flags = tearing_ ? DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0;
    HRESULT hr = factory_->CreateSwapChainForHwnd(device_.Get(), hwnd, &d, nullptr, nullptr, &swapchain_);
    if (FAILED(hr)) { error = "swap chain " + hresult(hr); return false; }
    factory_->MakeWindowAssociation(hwnd, DXGI_MWA_NO_ALT_ENTER);
    hwnd_ = hwnd; backWidth_ = d.Width; backHeight_ = d.Height;
    ComPtr<ID3D11Texture2D> back;
    swapchain_->GetBuffer(0, IID_PPV_ARGS(&back));
    device_->CreateRenderTargetView(back.Get(), nullptr, &backbuffer_);
    return true;
}

void Gpu::keepOriginals(bool keep, std::string& error) {
    if (keep && !keepOriginals_)
        for (auto& o : originals_) if (!o.uav && !image(o, width_, height_, DXGI_FORMAT_R8G8B8A8_UNORM, true, error)) return;
    if (!keep) for (auto& v : originalValid_) v = false;
    keepOriginals_ = keep;
}

void Gpu::detach() { backbuffer_.Reset(); swapchain_.Reset(); hwnd_ = nullptr; }

bool Gpu::present(ID3D11ShaderResourceView* source, ID3D11ShaderResourceView* original, int sync, int aspect) {
    if (!swapchain_) return true;
    if (!IsWindow(hwnd_)) return false;
    RECT r{}; GetClientRect(hwnd_, &r);
    const UINT w = UINT(std::max<LONG>(8, r.right - r.left)), h = UINT(std::max<LONG>(8, r.bottom - r.top));
    if (w != backWidth_ || h != backHeight_) {
        backbuffer_.Reset();
        context_->OMSetRenderTargets(0, nullptr, nullptr);
        context_->Flush();
        if (FAILED(swapchain_->ResizeBuffers(0, w, h, DXGI_FORMAT_UNKNOWN, tearing_ ? DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0))) return false;
        backWidth_ = w; backHeight_ = h;
        ComPtr<ID3D11Texture2D> back;
        swapchain_->GetBuffer(0, IID_PPV_ARGS(&back));
        device_->CreateRenderTargetView(back.Get(), nullptr, &backbuffer_);
    }
    const float black[4] = {0, 0, 0, 1};
    context_->ClearRenderTargetView(backbuffer_.Get(), black);
    const double sx = double(backWidth_) / width_, sy = double(backHeight_) / height_;
    const double scale = aspect == 1 ? std::max(sx, sy) : std::min(sx, sy);
    float vw = float(width_ * scale), vh = float(height_ * scale);
    if (aspect == 2) { vw = float(backWidth_); vh = float(backHeight_); }
    // A rounded DIP-to-pixel host can differ from the source aspect by one pixel.
    // Use its full surface in that case, without a thin artificial letterbox edge.
    if (std::abs(vw - float(backWidth_)) <= 1.5f && std::abs(vh - float(backHeight_)) <= 1.5f) {
        vw = float(backWidth_); vh = float(backHeight_);
    }
    D3D11_VIEWPORT vp{float((backWidth_ - vw) / 2), float((backHeight_ - vh) / 2), vw, vh, 0, 1};
    struct { float tx, ty, downscale, split, viewWidth, p0, p1, p2; } c{1.f / width_, 1.f / height_, float(std::max(double(width_) / vw, double(height_) / vh)),
                                                                     original ? 1.f : 0.f, vw, 0, 0, 0};
    constants(c);
    ID3D11RenderTargetView* rtv = backbuffer_.Get();
    context_->OMSetRenderTargets(1, &rtv, nullptr);
    context_->RSSetViewports(1, &vp);
    context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context_->IASetInputLayout(nullptr);
    context_->VSSetShader(fullscreen_.Get(), nullptr, 0);
    context_->PSSetShader(present_.Get(), nullptr, 0);
    ID3D11SamplerState* s = linear_.Get();
    context_->PSSetSamplers(0, 1, &s);
    ID3D11ShaderResourceView* views[2] = {source, original ? original : source};
    context_->PSSetShaderResources(0, 2, views);
    context_->Draw(3, 0);
    ID3D11ShaderResourceView* none[2] = {};
    context_->PSSetShaderResources(0, 2, none);
    context_->OMSetRenderTargets(0, nullptr, nullptr);
    // The present stage measures the drawing only; Present() may wait for the compositor.
    timer.mark(context_.Get(), StagePresent, false);
    HRESULT hr = sync == 1 ? swapchain_->Present(1, 0) : swapchain_->Present(0, sync == 2 && tearing_ ? DXGI_PRESENT_ALLOW_TEARING : 0);
    return SUCCEEDED(hr) || hr == DXGI_STATUS_OCCLUDED;
}

Gpu::DisplayStats Gpu::displayStats() {
    DisplayStats d;
    if (!swapchain_) return d;
    UINT last = 0;
    DXGI_FRAME_STATISTICS s{};
    if (FAILED(swapchain_->GetLastPresentCount(&last)) || FAILED(swapchain_->GetFrameStatistics(&s))) return d;
    static const int64_t frequency = [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return f.QuadPart; }();
    d.valid = true; d.lastPresent = last; d.shownPresent = s.PresentCount; d.shownRefresh = s.PresentRefreshCount;
    d.shownTime = int64_t((__int128)s.SyncQPCTime.QuadPart * 10000000 / frequency);
    return d;
}

bool Gpu::toNv12(ID3D11ShaderResourceView* source, ID3D11Texture2D* nv12) {
    // Plane views are created once per encoder texture (the recorder reuses a fixed pool).
    Nv12Targets* targets = nullptr;
    for (auto& t : nv12Targets_) if (t.texture.Get() == nv12) { targets = &t; break; }
    if (!targets) {
        if (nv12Targets_.size() >= 32) nv12Targets_.clear();
        Nv12Targets t; t.texture = nv12;
        D3D11_RENDER_TARGET_VIEW_DESC rd{};
        rd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
        rd.Format = DXGI_FORMAT_R8_UNORM;
        if (FAILED(device_->CreateRenderTargetView(nv12, &rd, &t.luma))) return false;
        rd.Format = DXGI_FORMAT_R8G8_UNORM;
        if (FAILED(device_->CreateRenderTargetView(nv12, &rd, &t.chroma))) return false;
        nv12Targets_.push_back(std::move(t));
        targets = &nv12Targets_.back();
    }
    ID3D11RenderTargetView* luma = targets->luma.Get();
    ID3D11RenderTargetView* chroma = targets->chroma.Get();
    context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context_->IASetInputLayout(nullptr);
    context_->VSSetShader(fullscreen_.Get(), nullptr, 0);
    ID3D11SamplerState* s = linear_.Get();
    context_->PSSetSamplers(0, 1, &s);
    context_->PSSetShaderResources(0, 1, &source);
    D3D11_VIEWPORT vp{0, 0, float(width_), float(height_), 0, 1};
    ID3D11RenderTargetView* rtv = luma;
    context_->OMSetRenderTargets(1, &rtv, nullptr);
    context_->RSSetViewports(1, &vp);
    context_->PSSetShader(toLuma_.Get(), nullptr, 0);
    context_->Draw(3, 0);
    vp.Width = float(width_ / 2); vp.Height = float(height_ / 2);
    rtv = chroma;
    context_->OMSetRenderTargets(1, &rtv, nullptr);
    context_->RSSetViewports(1, &vp);
    context_->PSSetShader(toChroma_.Get(), nullptr, 0);
    context_->Draw(3, 0);
    ID3D11ShaderResourceView* none = nullptr;
    context_->PSSetShaderResources(0, 1, &none);
    context_->OMSetRenderTargets(0, nullptr, nullptr);
    return true;
}

bool Gpu::readField(int slotB, std::vector<float>& xy, int& gridWidth, int& gridHeight) {
    D3D11_TEXTURE2D_DESC d{};
    fields_[slotB].vectors.texture->GetDesc(&d);
    d.Usage = D3D11_USAGE_STAGING; d.BindFlags = 0; d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging;
    if (FAILED(device_->CreateTexture2D(&d, nullptr, &staging))) return false;
    context_->CopyResource(staging.Get(), fields_[slotB].vectors.texture.Get());
    D3D11_MAPPED_SUBRESOURCE m{};
    if (FAILED(context_->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &m))) return false;
    gridWidth = int(d.Width); gridHeight = int(d.Height);
    xy.resize(size_t(d.Width) * d.Height * 2);
    for (UINT y = 0; y < d.Height; ++y) memcpy(xy.data() + size_t(y) * d.Width * 2, static_cast<uint8_t*>(m.pData) + size_t(y) * m.RowPitch, size_t(d.Width) * 8);
    context_->Unmap(staging.Get(), 0);
    return true;
}

void Gpu::queueFieldRead(int slotB) {
    if (!fieldStaging_[slotB]) {
        D3D11_TEXTURE2D_DESC d{};
        fields_[slotB].vectors.texture->GetDesc(&d);
        d.Usage = D3D11_USAGE_STAGING; d.BindFlags = 0; d.CPUAccessFlags = D3D11_CPU_ACCESS_READ; d.MiscFlags = 0;
        if (FAILED(device_->CreateTexture2D(&d, nullptr, &fieldStaging_[slotB]))) return;
    }
    context_->CopyResource(fieldStaging_[slotB].Get(), fields_[slotB].vectors.texture.Get());
    fieldQueued_[slotB] = true;
}

bool Gpu::takeField(int slotB, std::vector<float>& xy, int& gridWidth, int& gridHeight) {
    if (!fieldQueued_[slotB] || !fieldStaging_[slotB]) return false;
    fieldQueued_[slotB] = false;
    D3D11_TEXTURE2D_DESC d{};
    fieldStaging_[slotB]->GetDesc(&d);
    D3D11_MAPPED_SUBRESOURCE m{};
    if (FAILED(context_->Map(fieldStaging_[slotB].Get(), 0, D3D11_MAP_READ, 0, &m))) return false;
    gridWidth = int(d.Width); gridHeight = int(d.Height);
    xy.resize(size_t(d.Width) * d.Height * 2);
    for (UINT y = 0; y < d.Height; ++y) memcpy(xy.data() + size_t(y) * d.Width * 2, static_cast<uint8_t*>(m.pData) + size_t(y) * m.RowPitch, size_t(d.Width) * 8);
    context_->Unmap(fieldStaging_[slotB].Get(), 0);
    return true;
}

bool Gpu::readback(ID3D11Texture2D* source, std::vector<uint8_t>& rgba) {
    if (!readback_) {
        D3D11_TEXTURE2D_DESC d{};
        source->GetDesc(&d);
        d.Usage = D3D11_USAGE_STAGING; d.BindFlags = 0; d.CPUAccessFlags = D3D11_CPU_ACCESS_READ; d.MiscFlags = 0;
        if (FAILED(device_->CreateTexture2D(&d, nullptr, &readback_))) return false;
    }
    context_->CopyResource(readback_.Get(), source);
    D3D11_MAPPED_SUBRESOURCE m{};
    if (FAILED(context_->Map(readback_.Get(), 0, D3D11_MAP_READ, 0, &m))) return false;
    rgba.resize(size_t(width_) * height_ * 4);
    for (int y = 0; y < height_; ++y)
        memcpy(rgba.data() + size_t(y) * width_ * 4, static_cast<const uint8_t*>(m.pData) + size_t(y) * m.RowPitch, size_t(width_) * 4);
    context_->Unmap(readback_.Get(), 0);
    return true;
}

} // namespace fdl
