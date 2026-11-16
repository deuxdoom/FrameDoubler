#include "smaa.h"
#include "shaders.h"

#include <d3dcompiler.h>
#include <algorithm>
#include <cstring>

// The reference's precomputed tables (MIT, unmodified): 160x560 RG8 area and 64x16 R8 search.
#include "third_party/smaa/AreaTex.h"
#include "third_party/smaa/SearchTex.h"

namespace fdl {
namespace {
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wc23-extensions"
const char kReference[] = {
#embed "third_party/smaa/SMAA.hlsl"
};
#pragma clang diagnostic pop

// kSmaaEntry includes "SMAA.hlsl"; it is served from the embedded copy, not from disk.
struct Reference : ID3DInclude {
    HRESULT __stdcall Open(D3D_INCLUDE_TYPE, LPCSTR name, LPCVOID, LPCVOID* data, UINT* bytes) override {
        if (std::strcmp(name, "SMAA.hlsl") != 0) return E_FAIL;
        *data = kReference; *bytes = UINT(sizeof kReference);
        return S_OK;
    }
    HRESULT __stdcall Close(LPCVOID) override { return S_OK; }
};

const char* const kPresets[] = {"SMAA_PRESET_MEDIUM", "SMAA_PRESET_HIGH", "SMAA_PRESET_ULTRA"};

bool compile(const char* entry, const char* profile, int preset, ComPtr<ID3DBlob>& blob, std::string& error) {
    Reference include;
    D3D_SHADER_MACRO macros[] = {{kPresets[preset], "1"}, {nullptr, nullptr}};
    ComPtr<ID3DBlob> messages;
    const HRESULT hr = D3DCompile(shaders::kSmaaEntry, strlen(shaders::kSmaaEntry), "smaa-entry.hlsl", macros, &include, entry, profile,
                                  D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &blob, &messages);
    if (FAILED(hr)) {
        error = std::string("SMAA shader ") + entry + ": " + (messages ? static_cast<const char*>(messages->GetBufferPointer()) : hresult(hr));
        return false;
    }
    return true;
}
} // namespace

void Smaa::configure(ID3D11Device* device, int width, int height) {
    if (device_.Get() != device) {
        edgeVs_.Reset(); blendVs_.Reset(); blendPs_.Reset();
        for (int p = 0; p < 3; ++p) { weightVs_[p].Reset(); edgePs_[p].Reset(); weightPs_[p].Reset(); }
        area_.Reset(); search_.Reset(); areaView_.Reset(); searchView_.Reset(); linear_.Reset(); point_.Reset();
    }
    device_ = device; width_ = width; height_ = height;
    edges_.Reset(); weights_.Reset(); edgesTarget_.Reset(); weightsTarget_.Reset(); edgesView_.Reset(); weightsView_.Reset(); metrics_.Reset();
    resourcesReady_ = false;
}

bool Smaa::resources(std::string& error) {
    if (resourcesReady_) return true;
    if (!device_ || width_ <= 0 || height_ <= 0) { error = "SMAA is not configured"; return false; }
    auto target = [&](DXGI_FORMAT format, ComPtr<ID3D11Texture2D>& texture, ComPtr<ID3D11RenderTargetView>& rtv, ComPtr<ID3D11ShaderResourceView>& srv) {
        D3D11_TEXTURE2D_DESC d{}; d.Width = UINT(width_); d.Height = UINT(height_); d.MipLevels = 1; d.ArraySize = 1; d.Format = format;
        d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        HRESULT hr = device_->CreateTexture2D(&d, nullptr, &texture);
        if (SUCCEEDED(hr)) hr = device_->CreateRenderTargetView(texture.Get(), nullptr, &rtv);
        if (SUCCEEDED(hr)) hr = device_->CreateShaderResourceView(texture.Get(), nullptr, &srv);
        if (FAILED(hr)) error = "SMAA texture " + hresult(hr);
        return SUCCEEDED(hr);
    };
    if (!target(DXGI_FORMAT_R8G8_UNORM, edges_, edgesTarget_, edgesView_) || !target(DXGI_FORMAT_R8G8B8A8_UNORM, weights_, weightsTarget_, weightsView_)) return false;
    auto table = [&](int w, int h, int pitch, DXGI_FORMAT format, const unsigned char* bytes, ComPtr<ID3D11Texture2D>& texture, ComPtr<ID3D11ShaderResourceView>& srv) {
        if (texture) return true;
        D3D11_TEXTURE2D_DESC d{}; d.Width = UINT(w); d.Height = UINT(h); d.MipLevels = 1; d.ArraySize = 1; d.Format = format;
        d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_IMMUTABLE; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data{bytes, UINT(pitch), 0};
        HRESULT hr = device_->CreateTexture2D(&d, &data, &texture);
        if (SUCCEEDED(hr)) hr = device_->CreateShaderResourceView(texture.Get(), nullptr, &srv);
        if (FAILED(hr)) error = "SMAA table " + hresult(hr);
        return SUCCEEDED(hr);
    };
    if (!table(AREATEX_WIDTH, AREATEX_HEIGHT, AREATEX_PITCH, DXGI_FORMAT_R8G8_UNORM, areaTexBytes, area_, areaView_) ||
        !table(SEARCHTEX_WIDTH, SEARCHTEX_HEIGHT, SEARCHTEX_PITCH, DXGI_FORMAT_R8_UNORM, searchTexBytes, search_, searchView_)) return false;
    if (!linear_) {
        D3D11_SAMPLER_DESC s{}; s.Filter = D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;
        s.AddressU = s.AddressV = s.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP; s.MaxLOD = D3D11_FLOAT32_MAX;
        device_->CreateSamplerState(&s, &linear_);
        s.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
        device_->CreateSamplerState(&s, &point_);
        if (!linear_ || !point_) { error = "SMAA samplers"; return false; }
    }
    const float metrics[4] = {1.f / float(width_), 1.f / float(height_), float(width_), float(height_)};
    D3D11_BUFFER_DESC bd{16, D3D11_USAGE_IMMUTABLE, D3D11_BIND_CONSTANT_BUFFER, 0, 0, 0};
    D3D11_SUBRESOURCE_DATA md{metrics, 0, 0};
    if (FAILED(device_->CreateBuffer(&bd, &md, &metrics_))) { error = "SMAA constants"; return false; }
    resourcesReady_ = true;
    return true;
}

bool Smaa::prepare(int strength, std::string& error) {
    if (!device_) { error = "SMAA is not configured"; return false; }
    const int p = std::clamp(strength, 1, 3) - 1;
    ComPtr<ID3DBlob> blob;
    // The edge vertex shader and the blending pass do not depend on the preset.
    if (!edgeVs_) { if (!compile("EdgeVS", "vs_5_0", 1, blob, error) || FAILED(device_->CreateVertexShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &edgeVs_))) return false; }
    if (!blendVs_) { if (!compile("BlendVS", "vs_5_0", 1, blob, error) || FAILED(device_->CreateVertexShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &blendVs_))) return false; }
    if (!blendPs_) { if (!compile("BlendPS", "ps_5_0", 1, blob, error) || FAILED(device_->CreatePixelShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &blendPs_))) return false; }
    if (!edgePs_[p]) { if (!compile("EdgePS", "ps_5_0", p, blob, error) || FAILED(device_->CreatePixelShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &edgePs_[p]))) return false; }
    if (!weightVs_[p]) { if (!compile("WeightVS", "vs_5_0", p, blob, error) || FAILED(device_->CreateVertexShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &weightVs_[p]))) return false; }
    if (!weightPs_[p]) { if (!compile("WeightPS", "ps_5_0", p, blob, error) || FAILED(device_->CreatePixelShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &weightPs_[p]))) return false; }
    return true;
}

bool Smaa::apply(ID3D11DeviceContext* c, ID3D11ShaderResourceView* source, ID3D11RenderTargetView* target, int strength, std::string& error) {
    if (!resources(error) || !prepare(strength, error)) return false;
    const int p = std::clamp(strength, 1, 3) - 1;
    const float zero[4] = {0, 0, 0, 0};
    D3D11_VIEWPORT viewport{0, 0, float(width_), float(height_), 0, 1};
    ID3D11Buffer* metrics = metrics_.Get();
    ID3D11SamplerState* samplers[2] = {linear_.Get(), point_.Get()};
    c->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    c->IASetInputLayout(nullptr);
    c->RSSetViewports(1, &viewport);
    c->VSSetConstantBuffers(0, 1, &metrics);
    c->PSSetConstantBuffers(0, 1, &metrics);
    c->PSSetSamplers(0, 2, samplers);
    // 1. Luma edges; pixels without an edge are discarded and keep the cleared zero.
    c->ClearRenderTargetView(edgesTarget_.Get(), zero);
    ID3D11RenderTargetView* rtv = edgesTarget_.Get();
    c->OMSetRenderTargets(1, &rtv, nullptr);
    c->VSSetShader(edgeVs_.Get(), nullptr, 0);
    c->PSSetShader(edgePs_[p].Get(), nullptr, 0);
    c->PSSetShaderResources(0, 1, &source);
    c->Draw(3, 0);
    // 2. Blending weights from the edges and the reference tables.
    c->ClearRenderTargetView(weightsTarget_.Get(), zero);
    rtv = weightsTarget_.Get();
    c->OMSetRenderTargets(1, &rtv, nullptr);
    ID3D11ShaderResourceView* weightInputs[3] = {edgesView_.Get(), areaView_.Get(), searchView_.Get()};
    c->PSSetShaderResources(0, 3, weightInputs);
    c->VSSetShader(weightVs_[p].Get(), nullptr, 0);
    c->PSSetShader(weightPs_[p].Get(), nullptr, 0);
    c->Draw(3, 0);
    // 3. Neighbourhood blending into the frame.
    c->OMSetRenderTargets(1, &target, nullptr);
    ID3D11ShaderResourceView* blendInputs[3] = {source, weightsView_.Get(), nullptr};
    c->PSSetShaderResources(0, 3, blendInputs);
    c->VSSetShader(blendVs_.Get(), nullptr, 0);
    c->PSSetShader(blendPs_.Get(), nullptr, 0);
    c->Draw(3, 0);
    ID3D11ShaderResourceView* none[3] = {};
    ID3D11Buffer* noBuffer = nullptr;
    c->PSSetShaderResources(0, 3, none);
    c->OMSetRenderTargets(0, nullptr, nullptr);
    c->VSSetConstantBuffers(0, 1, &noBuffer);
    return true;
}

} // namespace fdl
