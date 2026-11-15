// Optional driver-provided hardware optical flow. No CUDA or redistributable DLL.
#pragma once
#include "common.h"
#include <d3d11.h>

namespace fdl {
class Gpu;
class NvofFlow {
public:
    NvofFlow() = default;
    NvofFlow(const NvofFlow&) = delete;
    NvofFlow& operator=(const NvofFlow&) = delete;
    ~NvofFlow();
    bool create(Gpu& gpu, int preset, std::string& error, bool bidirectional = true, bool edgeAegis = false);
    void destroy();
    bool ready() const { return state_ != nullptr; }
    // Always full-size pictures; only the hardware vector grid is coarser.
    bool estimate(Gpu& gpu, int slotA, int slotB, bool reset, std::string& error);
    void motionVectors(Gpu& gpu, ID3D11UnorderedAccessView* target);
    void interpolate(Gpu& gpu, int slotA, int slotB, float phase);
    bool readBackward(Gpu& gpu, std::vector<float>& xy, int& width, int& height);
    int grid() const;
private:
    struct State;
    State* state_ = nullptr;
};
} // namespace fdl
