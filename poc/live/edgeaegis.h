// Boundary-aware refinement of external motion vectors. Does not generate or blend pictures.
#pragma once
#include "common.h"
#include <d3d11.h>

namespace fdl {
class Gpu;
class EdgeAegis {
public:
    bool create(Gpu& gpu, std::string& error);
    void apply(Gpu& gpu, ID3D11ShaderResourceView* previous, ID3D11ShaderResourceView* current,
               ID3D11ShaderResourceView* backward, ID3D11ShaderResourceView* forward,
               int gridStep, ID3D11UnorderedAccessView* output);
    static bool selfTest(Gpu& gpu, std::string& detail);
private:
    ComPtr<ID3D11ComputeShader> select_, fill_;
    ComPtr<ID3D11Buffer> constants_;
    ComPtr<ID3D11SamplerState> sampler_;
    ComPtr<ID3D11Texture2D> selected_;
    ComPtr<ID3D11ShaderResourceView> selectedView_;
    ComPtr<ID3D11UnorderedAccessView> selectedWrite_;
};
}
