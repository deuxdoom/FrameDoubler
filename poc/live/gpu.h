// Direct3D 11 processing for the live engine.
#pragma once
#include "common.h"
#include "capture.h"
#include <d3d11.h>
#include <dxgi1_6.h>

namespace fdl {

struct ColorSetup {
    bool limited = true;      // studio swing 16..235
    bool bt709 = true;        // otherwise BT.601
};

enum GpuStage { StageUpload, StageMotion, StageInterpolate, StagePresent, StageEncode, StageCount };

// Timestamp queries per stage; results are read a few frames later without stalling.
class GpuTimer {
public:
    void create(ID3D11Device* device);
    void begin(ID3D11DeviceContext* c);
    void mark(ID3D11DeviceContext* c, GpuStage stage, bool start);
    void end(ID3D11DeviceContext* c);
    void collect(ID3D11DeviceContext* c);
    // Average milliseconds per frame that ran the stage since the last call.
    void take(double out[StageCount]);
private:
    struct Set { ComPtr<ID3D11Query> disjoint; ComPtr<ID3D11Query> marks[StageCount][2]; bool used[StageCount] = {}; bool pending = false; };
    Set sets_[6];
    int write_ = 0;
    bool active_ = false;
    double sum_[StageCount] = {}; int count_[StageCount] = {};
    std::mutex mutex_;
};

class Gpu {
public:
    static constexpr int kSlots = 5;
    bool create(std::string& error);
    bool configure(int width, int height, std::string& error);
    ID3D11Device* device() const { return device_.Get(); }
    ID3D11DeviceContext* context() const { return context_.Get(); }
    std::string adapterName() const { return adapterName_; }
    LUID adapterLuid() const { return luid_; }
    int width() const { return width_; }
    int height() const { return height_; }

    // Converts a captured picture into frame slot `slot`, anti-aliased when aa > 0.
    void upload(int slot, const uint8_t* luma, int lumaPitch, const uint8_t* chroma, int chromaPitch,
                PixelFormat format, const ColorSetup& color, int aa);
    // Luma pyramid of a slot, then symmetric motion between two slots. The vector
    // field is kept with slot B (the later frame of the pair).
    void pyramid(int slot);
    void motion(int slotA, int slotB);
    // Own interpolation into the output texture.
    // cameraBtoA: optional row-major 3x3 pixel homography from B to A (global camera motion).
    void interpolate(int slotA, int slotB, float phase, const double* cameraBtoA = nullptr);
    // Motion vectors of the pair kept with slot B for an external frame generator: one
    // two-channel value per pixel, pointing from picture B back to picture A, in pixels.
    void motionVectors(int slotB, ID3D11UnorderedAccessView* target);
    ID3D11Texture2D* frame(int slot) const { return frames_[slot].texture.Get(); }
    ID3D11ShaderResourceView* frameView(int slot) const { return frames_[slot].srv.Get(); }
    ID3D11Texture2D* output() const { return output_.texture.Get(); }
    ID3D11ShaderResourceView* outputView() const { return output_.srv.Get(); }
    ID3D11UnorderedAccessView* outputUav() const { return output_.uav.Get(); }

    // Presentation onto a window (flip model). Returns false when the window is gone.
    // sync: 0 newest frame without tearing (DWM), 1 VSync, 2 tearing allowed (when supported).
    // aspect: 0 fit (letterbox), 1 fill (crop), 2 stretch. original: shown on the left in split view.
    bool attach(HWND hwnd, std::string& error);
    void detach();
    bool present(ID3D11ShaderResourceView* source, ID3D11ShaderResourceView* original = nullptr, int sync = 0, int aspect = 0);
    bool tearingSupported() const { return tearing_; }
    // Keep each slot's picture from before anti-aliasing (for the original/processed comparison).
    void keepOriginals(bool keep, std::string& error);
    ID3D11ShaderResourceView* originalView(int slot) const { return originalValid_[slot] && originals_[slot].srv ? originals_[slot].srv.Get() : frames_[slot].srv.Get(); }

    // RGB -> NV12 (BT.709 limited) for the recorder.
    bool toNv12(ID3D11ShaderResourceView* source, ID3D11Texture2D* nv12);
    // Copies a picture to the CPU as RGBA8 rows (for screenshots and tests).
    bool readback(ID3D11Texture2D* source, std::vector<uint8_t>& rgba);
    // Uploads RGBA8 rows into a slot (self-test and replay of RGB sources).
    void uploadRgba(int slot, const uint8_t* rgba, int pitch, int aa);
    void antiAliasInto(ID3D11ShaderResourceView* source, ID3D11UnorderedAccessView* target, int aa);
    // Diagnostics: the finest vector field of a pair (full-resolution pixels).
    bool readField(int slotB, std::vector<float>& xy, int& gridWidth, int& gridHeight);
    // The same in two steps so the copy overlaps other work: queue the copy right after
    // motion(), take it when the pair is first interpolated (waits only for unfinished GPU work).
    void queueFieldRead(int slotB);
    bool takeField(int slotB, std::vector<float>& xy, int& gridWidth, int& gridHeight);

    GpuTimer timer;
    std::mutex& lock() { return mutex_; }   // guards the immediate context across threads
private:
    struct Image { ComPtr<ID3D11Texture2D> texture; ComPtr<ID3D11ShaderResourceView> srv; ComPtr<ID3D11UnorderedAccessView> uav; };
    struct Field { Image vectors, cost; };
    bool image(Image& out, int w, int h, DXGI_FORMAT format, bool uav, std::string& error);
    bool compile(const char* source, const char* profile, ComPtr<ID3DBlob>& blob, std::string& error);
    template <class T> void constants(const T& value);
    void dispatch(ID3D11ComputeShader* shader, int x, int y, int groupX, int groupY);

    ComPtr<ID3D11Device> device_;
    ComPtr<ID3D11DeviceContext> context_;
    ComPtr<IDXGIFactory2> factory_;
    std::string adapterName_;
    LUID luid_{};
    ComPtr<ID3D11ComputeShader> convert_, lumaFromRgb_, lumaHalve_, motion_, median_, interpolate_, antiAlias_, motionVectors_;
    ComPtr<ID3D11VertexShader> fullscreen_;
    ComPtr<ID3D11PixelShader> present_, toLuma_, toChroma_;
    ComPtr<ID3D11SamplerState> linear_;
    ComPtr<ID3D11Buffer> constants_;
    int width_ = 0, height_ = 0, levels_ = 0;
    Image frames_[kSlots], staging_, output_;
    Image originals_[kSlots];
    bool originalValid_[kSlots] = {};
    bool keepOriginals_ = false, tearing_ = false;
    ComPtr<ID3D11Texture2D> lumaUpload_, chromaUpload_, packedUpload_, rgbUpload_;
    ComPtr<ID3D11ShaderResourceView> lumaUploadSrv_, chromaUploadSrv_, packedUploadSrv_, rgbUploadSrv_;
    std::vector<Image> pyramids_[kSlots];          // level 1..levels (index 0 = level 1)
    std::vector<Field> scratch_;                   // per level, coarse to fine work
    Field fields_[kSlots];                         // finest field per pair, keyed by slot B
    ComPtr<ID3D11Buffer> totals_[kSlots];
    ComPtr<ID3D11UnorderedAccessView> totalsUav_[kSlots];
    ComPtr<ID3D11ShaderResourceView> totalsSrv_[kSlots];
    ComPtr<ID3D11Texture2D> readback_;
    ComPtr<ID3D11Texture2D> fieldStaging_[kSlots];
    bool fieldQueued_[kSlots] = {};
    struct Nv12Targets { ComPtr<ID3D11Texture2D> texture; ComPtr<ID3D11RenderTargetView> luma, chroma; };
    std::vector<Nv12Targets> nv12Targets_;
    // Window.
    HWND hwnd_ = nullptr;
    ComPtr<IDXGISwapChain1> swapchain_;
    ComPtr<ID3D11RenderTargetView> backbuffer_;
    UINT backWidth_ = 0, backHeight_ = 0;
    std::mutex mutex_;
};

} // namespace fdl
