// Optional frame generation through NVIDIA DLSS Frame Generation (NGX feature 11).
// The driver's NGX core is loaded dynamically and called through its exported C entry
// points; the feature library (nvngx_dlssg.dll) comes from a folder next to the engine.
// DLSS-G is built for game engines, so a captured picture is given a flat depth buffer, a
// fixed camera and hardware optical flow (or the engine's own block motion) as vectors. When the driver, the GPU
// or any call refuses, the engine keeps its own interpolation.
#pragma once
#include "common.h"

namespace fdl {
class Gpu;

class DlssGenerator {
public:
    ~DlssGenerator() { destroy(); }
    static bool runtimePresent(const std::wstring& directory);
    // featureDirectory holds nvngx_dlssg.dll; dataDirectory must be writable (NGX logs).
    // motionMode: 0 hardware when available, 1 own, 2 require hardware (comparison).
    bool create(Gpu& gpu, const std::wstring& featureDirectory, const std::wstring& dataDirectory, std::string& error, bool diagnostics = false, int motionMode = 0);
    // Generated frames the driver allows between two real ones (1 = 2x, 3 = 4x).
    int maxGenerated() const { return maxGenerated_; }
    // Generates the frame nearest to `phase` out of `generated` evenly spaced ones between
    // slot A and slot B into the engine's output texture. idA/idB identify the pictures in
    // the slots; the motion field of the pair must already be computed (Gpu::motion).
    bool interpolate(Gpu& gpu, int slotA, int slotB, uint64_t idA, uint64_t idB, float phase, int generated, std::string& error);
    void destroy();
    // The next pair primes a new sequence. Keep GPU allocations for the same size.
    void resetHistory();
    bool ready() const { return ready_; }
    std::string version() const { return version_; }
    double lastMs() const { return lastMs_; }
    bool hardwareMotion() const;
private:
    struct State;
    State* state_ = nullptr;
    bool ready_ = false;
    int maxGenerated_ = 1;
    std::string version_;
    double lastMs_ = 0;
};

} // namespace fdl
