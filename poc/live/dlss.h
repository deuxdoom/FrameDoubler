// Optional frame generation through NVIDIA DLSS Frame Generation (NGX feature 11).
// The driver's NGX core is loaded dynamically and called through its exported C entry
// points; the feature library (nvngx_dlssg.dll) comes from a folder next to the engine.
// DLSS-G is built for game engines, so a captured picture is given a flat depth buffer and a
// fixed camera. DLSS-G 310 estimates the picture motion itself: two different vector inputs
// (own block motion, hardware optical flow) gave byte-for-byte equal quality on sample 2, so
// by default no vectors are computed for it. When the driver, the GPU or any call refuses,
// the engine keeps its own interpolation.
#pragma once
#include "common.h"

namespace fdl {
class Gpu;

// Motion vectors handed to DLSS-G.
enum class DlssMotion { None, Own, Hardware, HardwareRequired };
DlssMotion parseDlssMotion(const std::string& text);   // none, own, auto, nvof
const char* dlssMotionName(DlssMotion motion);

struct DlssOptions {
    bool diagnostics = false;          // NGX log callback and per-step trace on stderr
    DlssMotion motion = DlssMotion::None;
    bool lite = false;                 // DLSSG.LiteCandidateBlendingEnabled (read at create by 310.9.16)
    float vectorScale = 1.f;           // diagnostics only: multiplies the vectors' normalisation (sign/scale checks)
};

class DlssGenerator {
public:
    ~DlssGenerator() { destroy(); }
    static bool runtimePresent(const std::wstring& directory);
    // Driver capability only (no feature, no textures): the largest multiplier DLSS-G allows on
    // the adapter (2 = one generated frame), 0 when frame generation is not available.
    static int probeMaxMultiplier(const LUID& adapter, const std::wstring& featureDirectory, const std::wstring& dataDirectory, std::string& error, bool diagnostics = false);
    // featureDirectory holds nvngx_dlssg.dll; dataDirectory must be writable (NGX logs).
    bool create(Gpu& gpu, const std::wstring& featureDirectory, const std::wstring& dataDirectory, std::string& error, const DlssOptions& options = {});
    // Generated frames the driver allows between two real ones (1 = 2x, 3 = 4x).
    int maxGenerated() const { return maxGenerated_; }
    // Generates the frame nearest to `phase` out of `generated` evenly spaced ones between
    // slot A and slot B into the engine's output texture. idA/idB identify the pictures in
    // the slots. With own motion the pair's field must already be computed (Gpu::motion).
    // The work is queued on the GPU: later work on the engine's context waits for it there,
    // the CPU does not.
    bool interpolate(Gpu& gpu, int slotA, int slotB, uint64_t idA, uint64_t idB, float phase, int generated, std::string& error);
    void destroy();
    // Camera motion for the following pairs: h (row-major 3x3) maps current-picture pixels to
    // previous-picture pixels (a camera turn, roll or zoom of the whole picture). It is given to
    // DLSS-G as clip-to-previous-clip matrices on the flat depth, with the vectors then holding
    // only what the camera does not explain. nullptr: a fixed camera. transposed: column-vector
    // matrices instead of DirectX row vectors (diagnostics).
    void setCameraMotion(const double* h, bool transposed = false);
    // The next pair primes a new sequence. Keep GPU allocations for the same size.
    void resetHistory();
    bool ready() const { return ready_; }
    std::string version() const { return version_; }
    // GPU time of the latest finished DLSS-G evaluation (falls back to CPU time until the
    // first GPU timestamps arrive).
    double lastMs() const { return lastMs_; }
    // Average GPU time of every finished evaluation so far, and how many were measured.
    double averageGpuMs() const;
    int gpuSamples() const;
    // True when the engine has to compute its block motion field for this generator.
    bool needsEngineMotion() const;
    bool hardwareMotion() const;
    std::string motionSource() const;  // "none", "own" or "nvof"

    // Without NGX: the D3D11 -> D3D12 -> D3D11 hand-over (shared textures, both fences,
    // command allocator reuse and GPU timestamps) on a plain copy. detail reports the result.
    static bool bridgeSelfTest(Gpu& gpu, std::string& detail);
private:
    struct State;
    State* state_ = nullptr;
    bool ready_ = false;
    int maxGenerated_ = 1;
    std::string version_;
    double lastMs_ = 0;
};

} // namespace fdl
