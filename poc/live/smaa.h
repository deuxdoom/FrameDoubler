// SMAA 1x anti-aliasing for the live engine: luma edges, blending weights from the reference
// area and search tables, then neighbourhood blending. The unmodified SMAA reference (Jimenez,
// Echevarria, Masia, Navarro, Gutierrez; MIT) is in third_party/smaa. Its passes are pixel
// shaders drawn into render targets, as the reference is written (the edge pass discards).
#pragma once
#include "common.h"
#include <d3d11.h>

namespace fdl {

class Smaa {
public:
    // Picture size; textures and shaders are created on first use (about 50 MB at 4K).
    void configure(ID3D11Device* device, int width, int height);
    // source: gamma-encoded RGBA (a UNORM, not sRGB, view); target: a render target of the
    // configured size. strength 1..3 selects the reference presets MEDIUM, HIGH and ULTRA.
    bool apply(ID3D11DeviceContext* c, ID3D11ShaderResourceView* source, ID3D11RenderTargetView* target, int strength, std::string& error);
    // Compiles one preset ahead of use (self-test and timing).
    bool prepare(int strength, std::string& error);
private:
    bool resources(std::string& error);
    ComPtr<ID3D11Device> device_;
    int width_ = 0, height_ = 0;
    bool resourcesReady_ = false;
    ComPtr<ID3D11Texture2D> edges_, weights_, area_, search_;
    ComPtr<ID3D11RenderTargetView> edgesTarget_, weightsTarget_;
    ComPtr<ID3D11ShaderResourceView> edgesView_, weightsView_, areaView_, searchView_;
    ComPtr<ID3D11SamplerState> linear_, point_;
    ComPtr<ID3D11Buffer> metrics_;
    ComPtr<ID3D11VertexShader> edgeVs_, blendVs_, weightVs_[3];
    ComPtr<ID3D11PixelShader> edgePs_[3], weightPs_[3], blendPs_;
};

} // namespace fdl
