// Optional frame generation through NVIDIA's Video Effects runtime (VideoFrameGeneration).
// The runtime is loaded dynamically from a folder; when it is missing, the GPU is not an
// NVIDIA one, or any call fails, the engine keeps its own interpolation.
#pragma once
#include "common.h"

struct ID3D11Texture2D;

namespace fdl {
class Gpu;

class VfxGenerator {
public:
    ~VfxGenerator() { destroy(); }
    static bool runtimePresent(const std::wstring& directory);
    // quality: 0 low, 1 medium, 2 high (the effect's Mode).
    bool create(Gpu& gpu, const std::wstring& directory, int quality, std::string& error);
    // Interpolates between two frame slots at `phase` into the engine's output texture.
    bool interpolate(Gpu& gpu, int slotA, int slotB, float phase, bool shotChange, std::string& error);
    void destroy();
    bool ready() const { return ready_; }
    std::string version() const { return version_; }
    int quality() const { return quality_; }
    double lastMs() const { return lastMs_; }
private:
    struct Api;
    Api* api_ = nullptr;
    void* effect_ = nullptr;
    void* stream_ = nullptr;
    void* slotImages_[6] = {};          // D3D11 frame slots (5) and the output texture
    void* gpuA_ = nullptr; void* gpuB_ = nullptr; void* gpuOut_ = nullptr; void* temp_ = nullptr;
    std::vector<HMODULE> modules_;
    bool ready_ = false;
    int quality_ = 1;
    std::string version_;
    double lastMs_ = 0;
};

} // namespace fdl
