// Video capture (Media Foundation, with a DirectShow fallback for devices Media Foundation
// does not list) and WASAPI endpoint enumeration.
#pragma once
#include "common.h"
#include <functional>
#include <memory>
#include <thread>

struct IMFSourceReader;
struct IMFMediaSource;

namespace fdl {
class ReaderCallback;

// UYVY: packed U Y0 V Y1 (professional cards). RGB32: B G R X. Compressed (MJPG) modes are not
// offered and P010 is listed but not selectable, so the engine processes NV12, YUY2, UYVY or RGB32.
enum class PixelFormat { Unknown, NV12, YUY2, UYVY, P010, RGB32, I420, YV12, RGB24 };
// I420/YV12 (three planes, common on webcams and USB sticks) are repacked to NV12 and RGB24 is
// expanded to RGB32 while the picture is copied, so the GPU path stays the same.
inline bool planar420(PixelFormat f) { return f == PixelFormat::NV12 || f == PixelFormat::I420 || f == PixelFormat::YV12; }
inline PixelFormat processingFormat(PixelFormat f) { return f == PixelFormat::I420 || f == PixelFormat::YV12 ? PixelFormat::NV12 : f == PixelFormat::RGB24 ? PixelFormat::RGB32 : f; }
const char* formatName(PixelFormat f);
inline bool formatSupported(PixelFormat f) {
    return f == PixelFormat::NV12 || f == PixelFormat::YUY2 || f == PixelFormat::UYVY || f == PixelFormat::RGB32 ||
           f == PixelFormat::I420 || f == PixelFormat::YV12 || f == PixelFormat::RGB24;
}
inline int bytesPerPixel(PixelFormat f) { return f == PixelFormat::RGB32 ? 4 : f == PixelFormat::RGB24 ? 3 : (f == PixelFormat::YUY2 || f == PixelFormat::UYVY) ? 2 : 1; }
struct FrameView;
// Colour description the device gives for its YUV pictures (0: not stated).
struct ColorInfo { int range = 0; /* 1 limited 16-235, 2 full 0-255 */ int matrix = 0; /* 1 BT.709, 2 BT.601 */ };
// DirectShow VIDEOINFOHEADER2::dwControlFlags with AMCONTROL_COLORINFO_PRESENT (0x80): bits 8-31
// hold DXVA_ExtendedFormat after its 8-bit sample format (chroma 4, nominal range 3, matrix 3).
inline ColorInfo colorFromDxvaFlags(uint32_t flags) {
    ColorInfo c;
    if (!(flags & 0x80)) return c;
    const uint32_t range = (flags >> 12) & 7, matrix = (flags >> 15) & 7;
    c.range = range == 2 ? 1 : range == 1 ? 2 : 0;          // DXVA_NominalRange_16_235 / _0_255
    c.matrix = matrix == 1 ? 1 : matrix == 2 ? 2 : 0;        // DXVA_VideoTransferMatrix_BT709 / _BT601
    return c;
}
// Media Foundation MF_MT_VIDEO_NOMINAL_RANGE / MF_MT_YUV_MATRIX values.
inline ColorInfo colorFromMf(uint32_t nominalRange, uint32_t matrix) {
    ColorInfo c;
    c.range = nominalRange == 2 ? 1 : nominalRange == 1 ? 2 : 0;   // MFNominalRange_16_235 / _0_255
    c.matrix = matrix == 1 ? 1 : matrix == 2 ? 2 : 0;              // MFVideoTransferMatrix_BT709 / _BT601
    return c;
}
// Copies a delivered picture into the engine's tightly packed buffer in its processing format
// (NV12: luma then interleaved chroma; packed formats: rows of width * bytes).
void copyToProcessing(const FrameView& v, uint8_t* dst);

struct VideoMode {
    int width = 0, height = 0;
    uint32_t fpsNum = 0, fpsDen = 1;
    PixelFormat format = PixelFormat::Unknown;
    int nativeIndex = -1;
    double fps() const { return fpsDen ? double(fpsNum) / fpsDen : 0; }
};

struct VideoDevice {
    std::string name, id;          // friendly name; MF symbolic link or "dshow:<path>"
    std::string api;               // "mf" or "dshow"
    std::vector<VideoMode> modes;
    std::string error;             // when the device could not be opened to list modes
};

struct AudioEndpoint { std::string id, name; bool isDefault = false; };

// Media Foundation devices first, then DirectShow-only devices (cards whose drivers are not
// visible to Media Foundation). allDirectShow also lists DirectShow entries of MF devices.
std::vector<VideoDevice> listVideoDevices(bool withModes, bool allDirectShow = false);
std::vector<VideoDevice> listDirectShowDevices(bool withModes);
std::vector<AudioEndpoint> listAudioEndpoints(bool capture);
std::string devicesJson(const std::vector<VideoDevice>& video, const std::vector<AudioEndpoint>& audio);

// One captured picture, valid only during the callback.
struct FrameView {
    const uint8_t* data = nullptr;
    const uint8_t* chroma = nullptr; // NV12 chroma plane (null: directly after `height` luma rows)
    int pitch = 0;               // bytes per luma (or packed) row; negative for bottom-up rows
    int width = 0, height = 0;
    PixelFormat format = PixelFormat::Unknown;
    int64_t sampleTime = 0;      // 100 ns, device clock (0: unknown, the arrival time is used)
    int64_t arrival = 0;         // 100 ns, performance counter
    uint64_t sequence = 0;
};

class CaptureSource {
public:
    using Callback = std::function<void(const FrameView&)>;
    virtual ~CaptureSource() = default;
    // Opens the device in exactly the requested mode (size, rate, format); substitutions are
    // rejected so the preview never runs a different mode than the one shown.
    virtual bool open(const std::string& id, const VideoMode& wanted, std::string& error) = 0;
    virtual void start(Callback callback, EventLog* log) = 0;
    virtual void stop() = 0;
    virtual bool failed() const = 0;
    // Format of the delivered pictures (always the selected native format).
    virtual PixelFormat outputFormat() const = 0;
    virtual ColorInfo colorInfo() const { return color_; }
protected:
    ColorInfo color_;
};
std::unique_ptr<CaptureSource> createCapture(const std::string& id);
// Opens the exact mode; when Media Foundation refuses it, the same device's DirectShow entry
// is tried if it offers that mode. `note` reports a fallback.
std::unique_ptr<CaptureSource> openCapture(const std::string& id, const VideoMode& mode, std::string& error, std::string& note);
bool captureCallbackSelfTest();

class VideoCapture : public CaptureSource {
public:
    ~VideoCapture() override;
    bool open(const std::string& id, const VideoMode& wanted, std::string& error) override;
    void start(Callback callback, EventLog* log) override;
    void stop() override;
    bool failed() const override { return failed_; }
    PixelFormat outputFormat() const override { return output_; }
private:
    void run();
    IMFMediaSource* source_ = nullptr;
    IMFSourceReader* reader_ = nullptr;
    ReaderCallback* readerCallback_ = nullptr;
    VideoMode mode_;
    PixelFormat output_ = PixelFormat::Unknown;
    Callback callback_;
    EventLog* log_ = nullptr;
    std::thread thread_;
    std::atomic<bool> stop_{false}, failed_{false};
    bool started_ = false;
};

} // namespace fdl
