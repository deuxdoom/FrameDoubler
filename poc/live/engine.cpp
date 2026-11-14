#include "engine.h"
#include "audio.h"
#include "capture.h"
#include "gpu.h"
#include "recorder.h"
#include "timeline.h"
#include "vfx.h"
#include "dlss.h"
#include "nvof.h"
#include "health.h"

#include <avrt.h>
#include <objbase.h>
#include <shlobj.h>
#include <wincodec.h>

#include <algorithm>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <future>
#include <memory>
#include <thread>

namespace fdl {
namespace {

char* duplicate(const std::string& text) {
    auto* out = static_cast<char*>(CoTaskMemAlloc(text.size() + 1));
    if (out) memcpy(out, text.c_str(), text.size() + 1);
    return out;
}

PixelFormat parseFormat(const std::string& s) {
    if (s == "NV12") return PixelFormat::NV12;
    if (s == "YUY2") return PixelFormat::YUY2;
    if (s == "UYVY") return PixelFormat::UYVY;
    if (s == "RGB32") return PixelFormat::RGB32;
    return PixelFormat::Unknown;
}

std::string fixed(double v, int digits = 2) {
    if (!std::isfinite(v)) return "null";
    char b[32]; snprintf(b, sizeof b, "%.*f", digits, v); return b;
}

// Content timing of kept pictures. A game picture appears somewhere between the previous
// sample (still the old picture) and the sample that first shows it. A least-squares fit
// of the kept sample times gives the content period; while the prediction falls inside
// that window it is used, so regular content (30 fps in 60 Hz, 40 fps in 60 Hz) gets
// evenly spaced times even though samples arrive on the 60 Hz grid. Two misses in a row
// mean the rate changed and the fit restarts. Design learned from Veyra's ContentDedup.
class ContentClock {
public:
    int64_t observe(int64_t sample, int64_t previousSample, int64_t transport) {
        if (!have_ || sample - lastSample_ > 6 * transport || sample <= lastSample_) {
            have_ = true; kept_.clear(); kept_.push_back(sample); period_ = 0; misses_ = 0;
            lastContent_ = sample; lastSample_ = sample; return sample;
        }
        kept_.push_back(sample);
        if (kept_.size() > 32) kept_.pop_front();
        if (kept_.size() >= 8) {
            const double n = double(kept_.size()), meanX = (n - 1) / 2;
            double meanT = 0; for (auto t : kept_) meanT += double(t - kept_.front());
            meanT /= n;
            double sxx = 0, sxt = 0; int i = 0;
            for (auto t : kept_) { double x = i++ - meanX; sxx += x * x; sxt += x * (double(t - kept_.front()) - meanT); }
            period_ = std::max<int64_t>(int64_t(std::llround(sxt / sxx)), transport - transport / 8);
        }
        int64_t content = sample;
        if (period_ > 0) {
            const int64_t predicted = lastContent_ + period_, lo = previousSample, hi = sample, tol = transport / 2;
            if (predicted >= lo - tol && predicted <= hi + tol) { content = std::clamp(predicted, lo + 1, hi); misses_ = 0; ++regular; }
            else if (++misses_ >= 2) { kept_.clear(); kept_.push_back(sample); period_ = 0; misses_ = 0; ++irregular; }
            else ++irregular;
        }
        content = std::max(content, lastContent_ + 10000);
        lastContent_ = content; lastSample_ = sample;
        return content;
    }
    void reset() { have_ = false; }
    int64_t period() const { return period_; }
    bool stable() const { return period_ > 0 && kept_.size() >= 24; }
    uint64_t regular = 0, irregular = 0;
private:
    bool have_ = false;
    std::deque<int64_t> kept_;
    int64_t period_ = 0, lastContent_ = 0, lastSample_ = 0;
    int misses_ = 0;
};

bool savePng(const std::wstring& path, const std::vector<uint8_t>& rgba, int width, int height) {
    ComPtr<IWICImagingFactory> factory;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)))) return false;
    ComPtr<IWICStream> stream; ComPtr<IWICBitmapEncoder> encoder; ComPtr<IWICBitmapFrameEncode> frame;
    if (FAILED(factory->CreateStream(&stream)) || FAILED(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE))) return false;
    if (FAILED(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder)) || FAILED(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache))) return false;
    if (FAILED(encoder->CreateNewFrame(&frame, nullptr)) || FAILED(frame->Initialize(nullptr))) return false;
    frame->SetSize(UINT(width), UINT(height));
    WICPixelFormatGUID format = GUID_WICPixelFormat24bppBGR;
    frame->SetPixelFormat(&format);
    std::vector<uint8_t> bgr(size_t(width) * height * 3);
    for (size_t i = 0, n = size_t(width) * height; i < n; ++i) { bgr[i * 3] = rgba[i * 4 + 2]; bgr[i * 3 + 1] = rgba[i * 4 + 1]; bgr[i * 3 + 2] = rgba[i * 4]; }
    if (FAILED(frame->WritePixels(UINT(height), UINT(width * 3), UINT(bgr.size()), bgr.data()))) return false;
    return SUCCEEDED(frame->Commit()) && SUCCEEDED(encoder->Commit());
}

std::wstring timestampFile(const std::wstring& dir, const wchar_t* ext) {
    SYSTEMTIME t; GetLocalTime(&t);
    wchar_t b[96];
    swprintf(b, 96, L"FrameDoubler_%04d%02d%02d_%02d%02d%02d_%03d", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    return dir + L"\\" + b + ext;
}

struct Packet { bool fresh = false; int buffer = -1; int64_t sample = 0, arrival = 0, content = 0; uint64_t sequence = 0; SceneSignature scene; };

struct Options {
    int fg = 0;            // 0 off, 1 own interpolation, 2 NVIDIA VFX
    int multiplier = 2;    // outputs per unique content frame (input frame when dedup is off)
    int aa = 0;            // 0 off, 1 low, 2 high
    bool dedup = false;
    int range = 0;         // 0 auto (limited), 1 limited, 2 full
    int matrix = 0;        // 0 auto, 1 BT.709, 2 BT.601
    int vfxQuality = 1;
    float volume = 1.f;
    bool mute = false;
    int audioOffsetMs = 0;
    int sync = 0;          // 0 newest frame without tearing, 1 VSync, 2 tearing allowed
    int aspect = 0;        // 0 fit, 1 fill, 2 stretch
    int compare = 0;       // 0 off, 1 split original/processed, 2 original only
    int outputCap = 0;     // 0 off, -1 the display's refresh rate, otherwise frames per second
};

struct Window { double seconds = 0; uint64_t input = 0, unique = 0, outputs = 0, interpolated = 0, dropped = 0, skipped = 0, presented = 0; double latency = 0, dedupMs = 0, vfxMs = 0; int latencyCount = 0, vfxCount = 0; };

} // namespace

class Session {
public:
    ~Session() { close(); }
    bool open(const Settings& s, HWND hwnd, bool replay, std::string& error);
    void command(const Settings& s);
    std::string status();
    std::string summary();
    void close();
    void feed(const FrameView& v);
    void feedAudio(const float* samples, int frames, int channels, int rate, int64_t time) {
        if (replay_ && replayAudio_) if (auto recorder = getRecorder(); recorder && recorder->active()) recorder->pushAudio(samples, frames, channels, rate, time);
    }
    void feedPacked(const uint8_t* data, int pitch, int64_t time) {
        FrameView v; v.data = data; v.pitch = pitch; v.width = width_; v.height = height_; v.format = format_;
        v.chroma = format_ == PixelFormat::NV12 ? data + size_t(pitch) * height_ : nullptr;
        v.sampleTime = time; v.arrival = time; v.sequence = ++replaySequence_;
        feed(v);
    }
private:
    uint64_t replaySequence_ = 0;
    void onFrame(const FrameView& v);
    void renderLoop();
    void processFresh(const Packet& p);
    void ensurePyramid(int slot);
    void ensureOwnMotion(int slotA,int slotB);
    void renderOutputs(const Packet& tick, bool moreWaiting);
    void applyOptions(const Settings& s);
    void startVfx();
    std::shared_ptr<Recorder> getRecorder() { std::lock_guard<std::mutex> lock(recorderMutex_); return recorder_; }
    void finishRecording();
    void publish();
    ColorSetup color() const;
    int64_t outputInterval(const Options& o) const {
        const int64_t period = o.dedup ? contentPeriod_.load() : interval_;
        return std::max<int64_t>(1, (period > 0 ? period : interval_) / o.multiplier);
    }

    Settings config_;
    Gpu gpu_;
    std::unique_ptr<CaptureSource> capture_;
    AudioPassthrough audio_;
    std::shared_ptr<Recorder> recorder_;
    std::mutex recorderMutex_;
    std::thread recorderFinisher_;
    std::atomic<bool> recordFinalizing_{false};
    std::atomic<bool> recordStarting_{false};
    std::atomic<int> recordRequest_{0}; // latest command: 1 start, -1 stop
    std::vector<std::future<void>> screenshotJobs_;
    double recordingFps_ = 0;
    std::unique_ptr<VfxGenerator> vfx_;
    std::thread vfxLoader_, renderThread_;
    std::atomic<bool> vfxReady_{false}, vfxLoading_{false};
    std::mutex vfxMutex_;
    std::condition_variable vfxWake_;
    uint64_t vfxRequest_ = 0;
    std::wstring vfxDirectory_;
    int vfxRequestedQuality_ = 1, vfxActiveQuality_ = -1;
    std::string vfxRuntime_;
    // DLSS frame generation: created once on its own thread; a failure keeps the own interpolation.
    std::unique_ptr<DlssGenerator> dlss_;
    std::thread dlssLoader_;
    std::atomic<bool> dlssReady_{false}, dlssLoading_{false}, dlssTried_{false}, dlssLimitLogged_{false};
    std::string dlssRuntime_;
    uint64_t slotId_[Gpu::kSlots] = {};
    uint64_t pyramidId_[Gpu::kSlots] = {}, motionA_[Gpu::kSlots] = {}, motionB_[Gpu::kSlots] = {};
    SceneSignature slotScene_[Gpu::kSlots];
    uint64_t slotSceneEpoch_[Gpu::kSlots] = {}, sceneEpoch_ = 0, vfxSceneEpoch_ = 0;
    void startDlss();
    EventLog log_;
    HWND hwnd_ = nullptr;
    bool replay_ = false;
    bool replayAudio_ = false;
    int width_ = 0, height_ = 0;
    uint32_t fpsNum_ = 60, fpsDen_ = 1;
    PixelFormat format_ = PixelFormat::NV12;
    int64_t interval_ = 166667;

    // Capture side.
    DuplicateDetector detector_;
    ContentClock clock_;
    std::atomic<int64_t> contentPeriod_{0};
    std::atomic<bool> contentClockStable_{false};
    int64_t previousSample_ = 0;
    static constexpr int kBuffers = 4;
    std::vector<uint8_t> buffers_[kBuffers];
    bool bufferBusy_[kBuffers] = {};
    std::deque<Packet> queue_;
    std::mutex queueMutex_;
    std::condition_variable queueWake_, idleWake_;
    bool rendering_ = false;

    // Render side.
    std::atomic<bool> stop_{false};
    OutputTimeline timeline_;
    uint64_t uniqueId_ = 0;
    int lastSlot_ = -1;
    bool sceneCut_[Gpu::kSlots] = {};
    std::mutex optionsMutex_;
    Options options_;
    std::atomic<bool> screenshotRequest_{false};
    HANDLE timer_ = nullptr, inputEvent_ = nullptr;
    int64_t lastPresent_ = 0;
    int64_t nextT_ = 0, lastOutputT_ = INT64_MIN;
    bool haveNextOutput_ = false;
    std::atomic<int> displayHz_{0};

    // Statistics.
    std::mutex statsMutex_;
    Window window_, published_;
    double stages_[StageCount] = {};
    int64_t windowStart_ = 0;
    std::atomic<uint64_t> totalInput_{0}, totalUnique_{0}, totalDuplicates_{0}, totalDropped_{0};
    uint64_t totalOutputs_ = 0, totalInterpolated_ = 0, totalSkipped_ = 0;
    double totalStage_[StageCount] = {}; int totalStageCount_[StageCount] = {};
    std::string lastRecording_;
    std::atomic<int> audioDelayMs_{60};
};

ColorSetup Session::color() const {
    ColorSetup c;
    c.limited = options_.range != 2;
    c.bt709 = options_.matrix == 1 || (options_.matrix == 0 && height_ >= 720);
    return c;
}

bool Session::open(const Settings& s, HWND hwnd, bool replay, std::string& error) {
    config_ = s; replay_ = replay; hwnd_ = hwnd;
    replayAudio_ = replay && getInt(s, "replayAudio", 0) != 0;
    log_.echo = get(s, "echo") == "1";
    width_ = getInt(s, "width", 0); height_ = getInt(s, "height", 0);
    fpsNum_ = uint32_t(getInt(s, "fpsNum", 60)); fpsDen_ = uint32_t(std::max(1, getInt(s, "fpsDen", 1)));
    format_ = parseFormat(get(s, "format", "NV12"));
    if (width_ <= 0 || height_ <= 0 || width_ % 2 || height_ % 2 || !fpsNum_) { error = "invalid picture size or rate"; return false; }
    if (!formatSupported(format_)) { error = "pixel format not supported (NV12, YUY2, UYVY or RGB32)"; return false; }
    interval_ = int64_t(10000000.0 * fpsDen_ / fpsNum_);
    applyOptions(s);
    if (!replay) {
        VideoMode mode; mode.width = width_; mode.height = height_; mode.fpsNum = fpsNum_; mode.fpsDen = fpsDen_; mode.format = format_;
        std::string note;
        capture_ = openCapture(get(s, "device"), mode, error, note);
        if (!capture_) { close(); return false; }
        if (!note.empty()) log_.add("info", note);
        format_ = capture_->outputFormat();
        if (!formatSupported(format_)) { error = "capture decoder did not deliver a supported raw format"; close(); return false; }
    }
    if (!gpu_.create(error)) return false;
    if (!gpu_.configure(width_, height_, error)) return false;
    if (hwnd && !gpu_.attach(hwnd, error)) return false;
    const size_t bytes = format_ == PixelFormat::NV12 ? size_t(width_) * height_ * 3 / 2 : size_t(width_) * height_ * bytesPerPixel(format_);
    for (auto& b : buffers_) b.resize(bytes);
    timeline_.configure(interval_);
    timer_ = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    inputEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    windowStart_ = now100ns();
    log_.add("info", "GPU " + gpu_.adapterName() + ", " + std::to_string(width_) + "x" + std::to_string(height_) + " " + formatName(format_));
    stop_ = false;
    renderThread_ = std::thread([this] { renderLoop(); });
    if (!replay) {
        capture_->start([this](const FrameView& v) { onFrame(v); }, &log_);
        const std::string audioIn = get(s, "audio");
        if (!audioIn.empty() && audioIn != "none") {
            std::string audioError;
            audio_.setVolume(options_.volume); audio_.setMute(options_.mute);
            audio_.setTap([this](const float* d, int frames, int ch, int rate, int64_t t) {
                if (auto recorder = getRecorder(); recorder && recorder->active()) recorder->pushAudio(d, frames, ch, rate, t);
            });
            if (!audio_.start(audioIn, get(s, "audioOutput"), audioDelayMs_, audioError, &log_)) log_.add("warning", "sound: " + audioError);
        }
    }
    if (options_.fg == 2) startVfx();
    if (options_.fg == 3) startDlss();
    return true;
}

void Session::applyOptions(const Settings& s) {
    std::lock_guard<std::mutex> lock(optionsMutex_);
    if (s.count("fg")) { const auto v = get(s, "fg"); options_.fg = v == "off" ? 0 : v == "vfx" ? 2 : v == "dlss" ? 3 : 1; }
    if (s.count("multiplier")) options_.multiplier = std::clamp(getInt(s, "multiplier", 2), 2, 4);
    if (s.count("aa")) options_.aa = std::clamp(getInt(s, "aa", 0), 0, 3);
    if (s.count("sync")) { const auto v = get(s, "sync"); options_.sync = v == "vsync" ? 1 : v == "tearing" ? 2 : 0; }
    if (s.count("aspect")) { const auto v = get(s, "aspect"); options_.aspect = v == "fill" ? 1 : v == "stretch" ? 2 : 0; }
    if (s.count("compare")) { const auto v = get(s, "compare"); options_.compare = v == "split" ? 1 : v == "original" ? 2 : 0; }
    if (s.count("outputCap")) { const auto v = get(s, "outputCap"); options_.outputCap = v == "display" ? -1 : v == "off" || v.empty() ? 0 : std::clamp(getInt(s, "outputCap", 0), 24, 480); }
    if (s.count("dedup")) options_.dedup = get(s, "dedup") != "0";
    if (s.count("range")) { const auto v = get(s, "range"); options_.range = v == "limited" ? 1 : v == "full" ? 2 : 0; }
    if (s.count("matrix")) { const auto v = get(s, "matrix"); options_.matrix = v == "709" ? 1 : v == "601" ? 2 : 0; }
    if (s.count("vfxQuality")) options_.vfxQuality = std::clamp(getInt(s, "vfxQuality", 1), 0, 2);
    if (s.count("volume")) options_.volume = float(std::clamp(getDouble(s, "volume", 100) / 100.0, 0.0, 1.0));
    if (s.count("mute")) options_.mute = get(s, "mute") == "1";
    if (s.count("audioOffsetMs")) options_.audioOffsetMs = std::clamp(getInt(s, "audioOffsetMs", 0), -200, 500);
    for (const char* key : {"screenshotDir", "recordDir", "recordCodec", "recordBitrate", "vfxDir", "dlssDir", "dlssDataDir", "dlssMotion", "dlssLite", "audioOutput"}) if (s.count(key)) config_[key] = get(s, key);
}

void Session::command(const Settings& s) {
    Options before, after;
    std::string directoryBefore;
    { std::lock_guard<std::mutex> lock(optionsMutex_); before = options_; directoryBefore = get(config_, "vfxDir"); }
    applyOptions(s);
    bool directoryChanged;
    { std::lock_guard<std::mutex> lock(optionsMutex_); after = options_; directoryChanged = directoryBefore != get(config_, "vfxDir"); }
    audio_.setVolume(after.volume); audio_.setMute(after.mute);
    if (s.count("audioOutput")) audio_.selectOutput(get(s, "audioOutput"));
    if (after.fg == 2 && (before.fg != 2 || before.vfxQuality != after.vfxQuality || directoryChanged)) startVfx();
    if (after.fg == 3 && before.fg != 3) startDlss();
    const std::string action = get(s, "action");
    if (action == "screenshot") screenshotRequest_ = true;
    else if (action == "record-start") { recordStarting_ = true; recordRequest_ = 1; }
    else if (action == "record-stop") { recordStarting_ = false; recordRequest_ = -1; }
    queueWake_.notify_one();
}

void Session::startDlss() {
    if (dlssTried_.exchange(true)) return;   // one attempt per session; the result does not change
    std::wstring dir, data;
    DlssOptions options;
    {
        std::lock_guard<std::mutex> lock(optionsMutex_);
        dir = wide(get(config_, "dlssDir")); data = wide(get(config_, "dlssDataDir"));
        // DLSS-G 310 estimates the motion itself; vectors only cost time unless asked for.
        options.motion = parseDlssMotion(get(config_, "dlssMotion", "none"));
        options.lite = get(config_, "dlssLite") == "1";
    }
    dlssLoading_ = true;
    log_.add("info", "DLSS frame generation: loading");
    dlssLoader_ = std::thread([this, dir, data, options] {
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        std::string error; bool ok;
        auto generator = std::make_unique<DlssGenerator>();
        {
            std::lock_guard<std::mutex> gpuLock(gpu_.lock());
            ok = generator->create(gpu_, dir, data, error, options);
            if (ok) { std::lock_guard<std::mutex> lock(vfxMutex_); dlssRuntime_ = generator->version(); dlss_ = std::move(generator); }
        }
        dlssReady_ = ok; dlssLoading_ = false;
        if (ok) log_.add("info", "DLSS frame generation ready (" + dlssRuntime_ + ")");
        else log_.add("warning", "DLSS frame generation unavailable, using FrameDoubler interpolation: " + error);
        CoUninitialize();
    });
}

void Session::startVfx() {
    std::wstring dir; int quality;
    { std::lock_guard<std::mutex> lock(optionsMutex_); dir = wide(get(config_, "vfxDir")); quality = options_.vfxQuality; }
    std::lock_guard<std::mutex> requestLock(vfxMutex_);
    vfxDirectory_ = dir; vfxRequestedQuality_ = quality; ++vfxRequest_;
    vfxReady_ = false; vfxLoading_ = true; vfxActiveQuality_ = -1; vfxRuntime_.clear();
    log_.add("info", "NVIDIA VFX frame generation: loading");
    if (!vfxLoader_.joinable()) vfxLoader_ = std::thread([this] {
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        uint64_t handled = 0;
        for (;;) {
            std::unique_lock<std::mutex> requestLock(vfxMutex_);
            vfxWake_.wait(requestLock, [this, &handled] { return stop_ || vfxRequest_ != handled; });
            if (stop_) break;
            const auto request = vfxRequest_;
            const auto dir = vfxDirectory_;
            const int quality = vfxRequestedQuality_;
            requestLock.unlock();
            std::string error, runtime;
            bool ok;
            {
                std::lock_guard<std::mutex> gpuLock(gpu_.lock());
                // A D3D11 texture can have only one CUDA registration. Release the
                // previous model's wrappers before the replacement registers it.
                vfx_.reset();
                auto generator = std::make_unique<VfxGenerator>();
                ok = generator->create(gpu_, dir, quality, error);
                if (ok) { runtime = generator->version(); vfx_ = std::move(generator); }
            }
            requestLock.lock();
            handled = request;
            // A selection made while loading supersedes this result. Keep loading
            // until the most recent request is applied, without blocking commands.
            if (stop_) break;
            if (request != vfxRequest_) continue;
            vfxRuntime_ = runtime; vfxActiveQuality_ = ok ? quality : -1;
            vfxReady_ = ok; vfxLoading_ = false;
            if (ok) log_.add("info", "NVIDIA VFX frame generation ready (runtime " + runtime + ")");
            else log_.add("warning", "NVIDIA VFX frame generation unavailable, using FrameDoubler interpolation: " + error);
        }
        CoUninitialize();
    });
    vfxWake_.notify_one();
}

void Session::feed(const FrameView& v) {
    onFrame(v);
    std::unique_lock<std::mutex> lock(queueMutex_);
    idleWake_.wait(lock, [this] { return (queue_.empty() && !rendering_) || stop_; });
}

void Session::onFrame(const FrameView& v) {
    ++totalInput_;
    const int64_t start = now100ns();
    bool analyseScene;
    { std::lock_guard<std::mutex> lock(optionsMutex_); detector_.enabled = options_.dedup; analyseScene = options_.fg != 0; }
    const uint8_t* luma = v.data + (v.format == PixelFormat::UYVY || v.format == PixelFormat::RGB32 ? 1 : 0);
    const int step = bytesPerPixel(v.format);
    auto signature = makeSignature(luma, v.pitch, step, v.width, v.height);
    // The detector takes ownership, so extract the tiny scene descriptor first.
    const auto scene = analyseScene ? sceneSignature(signature) : SceneSignature{};
    bool repeat = detector_.observe(std::move(signature));
    const double dedupMs = double(now100ns() - start) / 1e4;
    Packet p;
    p.scene = scene;
    // Driver times that are missing, running backwards or far from arrival fall back to
    // the arrival time (Veyra 2.1.5 lists the same failure on some cards).
    const bool timeValid = v.sampleTime > 0 && std::llabs(v.arrival - v.sampleTime) < 20000000 && v.sampleTime > previousSample_;
    p.sample = timeValid || replay_ ? v.sampleTime : v.arrival;
    p.arrival = v.arrival; p.sequence = v.sequence;
    if (repeat) ++totalDuplicates_;
    else {
        p.content = clock_.observe(p.sample, previousSample_ ? previousSample_ : p.sample - interval_, interval_);
        int index = -1;
        {
            std::lock_guard<std::mutex> lock(queueMutex_);
            for (int i = 0; i < kBuffers; ++i) if (!bufferBusy_[i]) { index = i; break; }
            if (index < 0) {
                // Newest wins: the oldest waiting picture becomes a plain tick.
                for (auto& q : queue_) if (q.fresh) { index = q.buffer; q.fresh = false; q.buffer = -1; ++totalDropped_; break; }
            }
            if (index >= 0) bufferBusy_[index] = true;
        }
        if (index >= 0) {
            uint8_t* dst = buffers_[index].data();
            if (v.format == PixelFormat::NV12) {
                // Chroma follows the luma rows; some drivers pad the luma plane to 16 rows.
                const int lumaRows = v.chroma ? 0 : v.height;
                for (int y = 0; y < v.height; ++y) memcpy(dst + size_t(y) * v.width, v.data + ptrdiff_t(y) * v.pitch, size_t(v.width));
                const uint8_t* chroma = v.chroma ? v.chroma : v.data + ptrdiff_t(lumaRows) * v.pitch;
                for (int y = 0; y < v.height / 2; ++y) memcpy(dst + size_t(v.width) * v.height + size_t(y) * v.width, chroma + ptrdiff_t(y) * v.pitch, size_t(v.width));
            } else {
                const size_t rowBytes = size_t(v.width) * bytesPerPixel(v.format);
                for (int y = 0; y < v.height; ++y) memcpy(dst + size_t(y) * rowBytes, v.data + ptrdiff_t(y) * v.pitch, rowBytes);
            }
            p.fresh = true; p.buffer = index;
            ++totalUnique_;
        } else ++totalDropped_;
    }
    previousSample_ = p.sample;
    contentPeriod_ = clock_.period();
    contentClockStable_ = clock_.stable();
    {
        std::lock_guard<std::mutex> lock(queueMutex_);
        queue_.push_back(p);
        if (inputEvent_) SetEvent(inputEvent_);
        // A stalled renderer must not build latency: keep only a short backlog of ticks.
        while (queue_.size() > 8 && !queue_.front().fresh) queue_.pop_front();
        std::lock_guard<std::mutex> stats(statsMutex_);
        window_.dedupMs += dedupMs;
    }
    queueWake_.notify_one();
}

void Session::ensurePyramid(int slot) {
    if(pyramidId_[slot]==slotId_[slot])return;
    gpu_.pyramid(slot);pyramidId_[slot]=slotId_[slot];
}
void Session::ensureOwnMotion(int slotA,int slotB) {
    if(motionA_[slotB]==slotId_[slotA] && motionB_[slotB]==slotId_[slotB])return;
    ensurePyramid(slotA);ensurePyramid(slotB);gpu_.motion(slotA,slotB);
    motionA_[slotB]=slotId_[slotA];motionB_[slotB]=slotId_[slotB];
}
void Session::processFresh(const Packet& p) {
    Options o;
    { std::lock_guard<std::mutex> lock(optionsMutex_); o = options_; }
    const int slot = int(uniqueId_ % Gpu::kSlots);
    const bool cut = lastSlot_ >= 0 && hardSceneCut(slotScene_[lastSlot_], p.scene);
    const uint8_t* data = buffers_[p.buffer].data();
    std::lock_guard<std::mutex> lock(gpu_.lock());
    std::string keepError;
    gpu_.keepOriginals(o.compare != 0, keepError);
    if (!keepError.empty()) log_.add("warning", "comparison view: " + keepError);
    gpu_.timer.mark(gpu_.context(), StageUpload, true);
    if (format_ == PixelFormat::NV12) gpu_.upload(slot, data, width_, data + size_t(width_) * height_, width_, format_, color(), o.aa);
    else gpu_.upload(slot, data, width_ * bytesPerPixel(format_), nullptr, 0, format_, color(), o.aa);
    gpu_.timer.mark(gpu_.context(), StageUpload, false);
    slotId_[slot]=uniqueId_+1;
    {
        std::lock_guard<std::mutex> q(queueMutex_);
        bufferBusy_[p.buffer] = false;
    }
    // DLSS without engine vectors needs no pyramid or block field; a failed or over-limit
    // pair computes them lazily (ensureOwnMotion) before the own interpolation.
    const bool selfContainedDlss=o.fg==3 && dlssReady_ && dlss_ && !dlss_->needsEngineMotion() && o.multiplier-1<=dlss_->maxGenerated();
    if (o.fg != 0 && !selfContainedDlss) {
        gpu_.timer.mark(gpu_.context(), StageMotion, true);
        ensurePyramid(slot);
        if (!cut && lastSlot_ >= 0 && (o.fg == 1 || o.fg == 3)) ensureOwnMotion(lastSlot_, slot);
        gpu_.timer.mark(gpu_.context(), StageMotion, false);
    }
    sceneCut_[slot] = cut;
    if (sceneCut_[slot]) ++sceneEpoch_;
    slotScene_[slot] = p.scene; slotSceneEpoch_[slot] = sceneEpoch_;
    timeline_.addUnique({p.content, slot, ++uniqueId_});
    slotId_[slot] = uniqueId_;
    lastSlot_ = slot;
}

void Session::renderLoop() {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    DWORD taskIndex = 0;
    HANDLE task = AvSetMmThreadCharacteristicsW(L"Games", &taskIndex);
    bool pendingRecording = false;
    int64_t recordingRequested = 0;
    while (!stop_) {
        std::deque<Packet> packets;
        {
            std::unique_lock<std::mutex> lock(queueMutex_);
            queueWake_.wait_for(lock, std::chrono::milliseconds(50), [this] { return stop_ || !queue_.empty() || screenshotRequest_ || recordRequest_ != 0; });
            packets.swap(queue_);
            if (inputEvent_) ResetEvent(inputEvent_);
            rendering_ = !packets.empty();
        }
        if (stop_) break;
        const bool captureFailed = capture_ && capture_->failed();
        const int recordRequest = recordRequest_.exchange(0);
        if (recordRequest == -1 || captureFailed) {
            pendingRecording = false; recordStarting_ = false;
            finishRecording();
        }
        if (recordRequest == 1) {
            const auto current = getRecorder();
            if (captureFailed) { recordStarting_ = false; log_.add("warning", "recording: restart the capture input first"); }
            else if (recordFinalizing_) { recordStarting_ = false; log_.add("warning", "recording: previous recording is still being saved"); }
            else if (!(current && current->active())) { pendingRecording = true; recordingRequested = now100ns(); }
            else recordStarting_ = false;
        }
        Options recordingOptions; { std::lock_guard<std::mutex> lock(optionsMutex_); recordingOptions = options_; }
        // A request immediately after capture starts waits briefly for the game cadence,
        // otherwise a 30-in-60 signal would incorrectly declare a 120 fps recording at 2x.
        if (pendingRecording && (recordingOptions.fg == 0 || !recordingOptions.dedup || contentClockStable_ || now100ns()-recordingRequested >= 10000000)) {
            pendingRecording = false;
            Options o; { std::lock_guard<std::mutex> lock(optionsMutex_); o = options_; }
            RecorderConfig rc;
            rc.directory = wide(get(config_, "recordDir"));
            if (rc.directory.empty()) { wchar_t* videos = nullptr; if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Videos, 0, nullptr, &videos))) { rc.directory = std::wstring(videos) + L"\\FrameDoubler"; CoTaskMemFree(videos); } }
            rc.width = width_; rc.height = height_;
            if (o.fg != 0) { rc.fpsNum = 10000000; rc.fpsDen = uint32_t(outputInterval(o)); }
            else { rc.fpsNum = fpsNum_; rc.fpsDen = fpsDen_; }
            rc.hevc = get(config_, "recordCodec") == "hevc";
            rc.bitrateMbps = getInt(config_, "recordBitrate", 0);
            rc.audio = audio_.running() || replayAudio_;
            auto r = std::make_shared<Recorder>();
            std::string path, error;
            bool ok;
            { std::lock_guard<std::mutex> lock(gpu_.lock()); ok = r->start(rc, gpu_, path, error); }
            recordStarting_ = false;
            if (ok) {
                { std::lock_guard<std::mutex> lock(recorderMutex_); recorder_ = std::move(r); recordingFps_ = double(rc.fpsNum)/rc.fpsDen; }
                log_.add("recording", "started " + path);
            }
            else log_.add("error", "recording: " + error);
        }
        if (!packets.empty()) {
            // One timing frame covers the upload/motion work and the outputs of this tick.
            { std::lock_guard<std::mutex> lock(gpu_.lock()); gpu_.timer.begin(gpu_.context()); }
            for (auto& p : packets) if (p.fresh) processFresh(p);
            const bool more = [&] { std::lock_guard<std::mutex> lock(queueMutex_); return !queue_.empty(); }();
            totalSkipped_ += packets.size() - 1;
            { std::lock_guard<std::mutex> lock(statsMutex_); window_.input += packets.size(); window_.skipped += packets.size() - 1;
              for (auto& p : packets) if (p.fresh) ++window_.unique; }
            renderOutputs(packets.back(), more);
            std::lock_guard<std::mutex> lock(gpu_.lock());
            gpu_.timer.end(gpu_.context());
            gpu_.timer.collect(gpu_.context());
        }
        {
            std::lock_guard<std::mutex> lock(queueMutex_);
            rendering_ = false;
        }
        idleWake_.notify_all();
        publish();
    }
    finishRecording();
    if (task) AvRevertMmThreadCharacteristics(task);
    CoUninitialize();
}

void Session::finishRecording() {
    std::shared_ptr<Recorder> recorder;
    { std::lock_guard<std::mutex> lock(recorderMutex_); recorder.swap(recorder_); }
    if (!recorder) return;
    if (recorderFinisher_.joinable()) recorderFinisher_.join();
    const auto path = recorder->path();
    { std::lock_guard<std::mutex> lock(statsMutex_); lastRecording_ = path; }
    recordFinalizing_ = true;
    recorderFinisher_ = std::thread([this, recorder, path] {
        const auto com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        recorder->stop();
        const auto error = recorder->lastError();
        if (error.empty()) log_.add("recording", "saved " + path);
        else log_.add("error", "recording: " + error);
        recordFinalizing_ = false;
        if (SUCCEEDED(com)) CoUninitialize();
    });
}

void Session::renderOutputs(const Packet& tick, bool moreWaiting) {
    Options o; { std::lock_guard<std::mutex> lock(optionsMutex_); o = options_; }
    if (lastSlot_ < 0) return;
    const bool useVfx = o.fg == 2 && vfxReady_;
    const bool timeline = o.fg != 0;
    const int64_t outInterval = outputInterval(o);
    if (timeline) timeline_.advance(o.dedup);
    const int64_t limit = tick.sample - timeline_.delay(), horizon = limit + interval_;
    if (timeline && (!haveNextOutput_ || nextT_ < limit - 4 * outInterval)) {
        nextT_ = std::max(limit, lastOutputT_ == INT64_MIN ? limit : lastOutputT_ + outInterval);
        haveNextOutput_ = true;
    }
    if (!timeline) haveNextOutput_ = false;
    for (int sub = 0; !timeline ? sub == 0 : nextT_ < horizon; ++sub) {
        if (timeline && !replay_) {
            if (sub > 0 && moreWaiting) break;
            const int64_t due = tick.arrival + (nextT_ - limit);
            const int64_t wait = due - now100ns();
            if (wait > 0 && timer_) {
                LARGE_INTEGER when; when.QuadPart = -wait;
                SetWaitableTimer(timer_, &when, 0, nullptr, nullptr, FALSE);
                HANDLE waits[] = {timer_, inputEvent_};
                const DWORD ready = WaitForMultipleObjects(inputEvent_ ? 2 : 1, waits, FALSE, DWORD(wait / 10000 + 5));
                if (ready == WAIT_OBJECT_0 + 1) break;
            }
            std::lock_guard<std::mutex> lock(queueMutex_);
            if (sub > 0 && !queue_.empty()) break; // always render one output even under load
        }
        OutputPlan plan;
        if (timeline) plan = timeline_.planAt(nextT_);
        else { plan.kind = OutputPlan::Original; plan.slotA = lastSlot_; plan.contentTime = tick.sample; }
        if (plan.kind == OutputPlan::Hold) { plan.kind = OutputPlan::Original; plan.slotA = lastSlot_; plan.contentTime = tick.sample; }
        std::lock_guard<std::mutex> lock(gpu_.lock());
        ID3D11DeviceContext* c = gpu_.context();
        ID3D11ShaderResourceView* source = gpu_.frameView(plan.slotA);
        ID3D11Texture2D* sourceTexture = gpu_.frame(plan.slotA);
        bool interpolated = false;
        if (plan.kind == OutputPlan::Interpolate && slotSceneEpoch_[plan.slotA] != slotSceneEpoch_[plan.slotB]) {
            // No blended/generated picture across a detected cut. The new scene becomes
            // a fresh DLSS sequence even when a later pair is rendered from history.
            const int original = plan.phase < .5 ? plan.slotA : plan.slotB;
            source = gpu_.frameView(original); sourceTexture = gpu_.frame(original);
            if (dlss_) dlss_->resetHistory();
        } else if (plan.kind == OutputPlan::Interpolate) {
            if (useVfx && vfxReady_ && vfx_) {
                std::string error;
                if (vfx_->interpolate(gpu_, plan.slotA, plan.slotB, float(plan.phase), vfxSceneEpoch_ != slotSceneEpoch_[plan.slotA], error)) {
                    vfxSceneEpoch_ = slotSceneEpoch_[plan.slotA];
                    interpolated = true;
                    std::lock_guard<std::mutex> s(statsMutex_);
                    window_.vfxMs += vfx_->lastMs(); ++window_.vfxCount;
                } else { log_.add("warning", "NVIDIA VFX failed, switching to FrameDoubler interpolation: " + error); vfxReady_ = false; }
            }
            if (!interpolated && o.fg == 3 && dlssReady_ && dlss_) {
                // DLSS-G makes evenly spaced frames only, and no more than the driver allows.
                const int generated = std::max(1, o.multiplier - 1);
                if (generated > dlss_->maxGenerated()) {
                    if (!dlssLimitLogged_.exchange(true)) log_.add("warning", "DLSS frame generation on this GPU supports up to " + std::to_string(dlss_->maxGenerated() + 1) + "x; using FrameDoubler interpolation for this multiplier");
                } else {
                    dlssLimitLogged_ = false;
                    std::string error;
                    if(dlss_->needsEngineMotion())ensureOwnMotion(plan.slotA,plan.slotB);
                    if (dlss_->interpolate(gpu_, plan.slotA, plan.slotB, slotId_[plan.slotA], slotId_[plan.slotB], float(plan.phase), generated, error)) {
                        interpolated = true;
                        std::lock_guard<std::mutex> s(statsMutex_);
                        window_.vfxMs += dlss_->lastMs(); ++window_.vfxCount;
                    } else { log_.add("warning", "DLSS frame generation failed, switching to FrameDoubler interpolation: " + error); dlssReady_ = false; }
                }
            }
            if (!interpolated) {
                gpu_.timer.mark(c, StageInterpolate, true);
                // Includes a failed hardware-DLSS pair whose own pyramids were skipped.
                ensureOwnMotion(plan.slotA, plan.slotB);
                gpu_.interpolate(plan.slotA, plan.slotB, float(plan.phase));
                gpu_.timer.mark(c, StageInterpolate, false);
                interpolated = true;
            }
            source = gpu_.outputView(); sourceTexture = gpu_.output();
        }
        // Display only: comparison views and the output cap never change what is recorded.
        const int capFps = o.outputCap > 0 ? o.outputCap : o.outputCap < 0 ? displayHz_.load() : 0;
        const int64_t now = now100ns();
        const bool due = capFps <= 0 || now - lastPresent_ >= int64_t(1e7 / capFps * 0.92);
        if (hwnd_ && due) {
            ID3D11ShaderResourceView* original = o.compare ? gpu_.originalView(plan.slotA) : nullptr;
            gpu_.timer.mark(c, StagePresent, true);
            if (!gpu_.present(o.compare == 2 ? original : source, o.compare == 1 ? original : nullptr, o.sync, o.aspect)) { log_.add("warning", "preview window unavailable"); hwnd_ = nullptr; }
            lastPresent_ = now;
            std::lock_guard<std::mutex> s(statsMutex_);
            ++window_.presented;
        } else if (replay_) c->Flush();
        if (auto recorder = getRecorder(); recorder && recorder->active()) {
            gpu_.timer.mark(c, StageEncode, true);
            recorder->pushVideo(gpu_, source, plan.contentTime);
            gpu_.timer.mark(c, StageEncode, false);
        }
        if (screenshotRequest_.exchange(false)) {
            std::vector<uint8_t> rgba;
            if (gpu_.readback(sourceTexture, rgba)) {
                std::wstring dir = wide(get(config_, "screenshotDir"));
                if (dir.empty()) { wchar_t* pictures = nullptr; if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Pictures, 0, nullptr, &pictures))) { dir = std::wstring(pictures) + L"\\FrameDoubler"; CoTaskMemFree(pictures); } }
                const int w = width_, h = height_;
                screenshotJobs_.erase(std::remove_if(screenshotJobs_.begin(), screenshotJobs_.end(), [](auto& job) { return job.wait_for(std::chrono::seconds(0)) == std::future_status::ready; }), screenshotJobs_.end());
                screenshotJobs_.push_back(std::async(std::launch::async, [this, dir, w, h, rgba = std::move(rgba)] {
                    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
                    SHCreateDirectoryExW(nullptr, dir.c_str(), nullptr);
                    const std::wstring path = timestampFile(dir, L".png");
                    if (savePng(path, rgba, w, h)) log_.add("screenshot", "saved " + utf8(path));
                    else log_.add("error", "screenshot could not be saved to " + utf8(dir));
                    CoUninitialize();
                }));
            } else log_.add("error", "screenshot readback failed");
        }
        const double latency = double(now100ns() - plan.contentTime) / 1e4;
        std::lock_guard<std::mutex> s(statsMutex_);
        ++window_.outputs; ++totalOutputs_;
        if (interpolated) { ++window_.interpolated; ++totalInterpolated_; }
        if (!replay_ && latency > 0 && latency < 2000) { window_.latency += latency; ++window_.latencyCount; }
        lastOutputT_ = plan.contentTime;
        if (timeline) nextT_ += outInterval;
    }
}

void Session::publish() {
    const int64_t now = now100ns();
    if (now - windowStart_ < 10000000) return;
    std::lock_guard<std::mutex> lock(statsMutex_);
    window_.seconds = double(now - windowStart_) / 1e7;
    published_ = window_;
    if (hwnd_) {
        // Refresh rate of the monitor that shows the preview (follows the window between screens).
        MONITORINFOEXW info{}; info.cbSize = sizeof info;
        DEVMODEW mode{}; mode.dmSize = sizeof mode;
        if (GetMonitorInfoW(MonitorFromWindow(hwnd_, MONITOR_DEFAULTTONEAREST), &info) && EnumDisplaySettingsW(info.szDevice, ENUM_CURRENT_SETTINGS, &mode))
            displayHz_ = int(mode.dmDisplayFrequency);
    }
    gpu_.timer.take(stages_);
    for (int i = 0; i < StageCount; ++i) if (stages_[i] >= 0) { totalStage_[i] += stages_[i]; ++totalStageCount_[i]; }
    // Keep the sound as late as the picture: measured capture-to-present latency.
    if (window_.latencyCount) {
        const int target = std::clamp(int(window_.latency / window_.latencyCount) - 15 + options_.audioOffsetMs, 20, 400);
        audioDelayMs_ = target; audio_.setDelay(target);
    }
    window_ = {};
    windowStart_ = now;
}

std::string Session::status() {
    Window w; double stages[StageCount];
    { std::lock_guard<std::mutex> lock(statsMutex_); w = published_; std::copy(std::begin(stages_), std::end(stages_), stages); }
    Options o; { std::lock_guard<std::mutex> lock(optionsMutex_); o = options_; }
    const auto audioStatus = audio_.status();
    const LUID gpuLuid = gpu_.adapterLuid();
    char gpuLuidText[32]; snprintf(gpuLuidText, sizeof gpuLuidText, "0x%08x_0x%08x", unsigned(gpuLuid.HighPart), unsigned(gpuLuid.LowPart));
    const double sec = w.seconds > 0 ? w.seconds : 1;
    auto stage = [&](int i) { return stages[i] < 0 ? std::string("null") : fixed(stages[i]); };
    std::string backend, runtime, dlssMotion; int vfxQuality, dlssMaximum;
    {
        std::lock_guard<std::mutex> lock(vfxMutex_);
        backend = o.fg == 0 ? "off" : (o.fg == 2 && vfxReady_) ? "vfx" : (o.fg == 2 && vfxLoading_) ? "loading" :
                  (o.fg == 3 && dlssReady_ && o.multiplier - 1 <= (dlss_ ? dlss_->maxGenerated() : 0)) ? "dlss" : (o.fg == 3 && dlssLoading_) ? "loading" : "own";
        runtime = o.fg == 3 ? dlssRuntime_ : vfxRuntime_; vfxQuality = vfxReady_ ? vfxActiveQuality_ : -1;
        dlssMaximum = dlssReady_ && dlss_ ? dlss_->maxGenerated() + 1 : 0;
        dlssMotion=dlssReady_ && dlss_ ? dlss_->motionSource() : "";
    }
    std::string out = "{\"running\":" + std::string(stop_ ? "false" : "true") +
        ",\"captureFailed\":" + (capture_ && capture_->failed() ? "true" : "false") +
        ",\"captureState\":" + jsonString(capture_ && capture_->failed() ? "failed" : totalInput_ ? "active" : "waiting") +
        ",\"width\":" + std::to_string(width_) + ",\"height\":" + std::to_string(height_) +
        ",\"format\":" + jsonString(formatName(format_)) + ",\"modeFps\":" + fixed(double(fpsNum_) / fpsDen_, 3) +
        ",\"targetFps\":" + fixed(o.fg == 0 ? double(fpsNum_) / fpsDen_ : 1e7 / outputInterval(o), 3) +
        ",\"gpu\":" + jsonString(gpu_.adapterName()) +
        ",\"gpuLuid\":" + jsonString(gpuLuidText) +
        ",\"inputFps\":" + fixed(w.input / sec) + ",\"contentFps\":" + fixed(w.unique / sec) +
        ",\"outputFps\":" + fixed(w.outputs / sec) + ",\"interpolatedFps\":" + fixed(w.interpolated / sec) +
        ",\"presentedFps\":" + fixed(w.presented / sec) + ",\"displayHz\":" + std::to_string(displayHz_.load()) +
        ",\"tearing\":" + (gpu_.tearingSupported() ? "true" : "false") +
        ",\"skipped\":" + std::to_string(w.skipped) + ",\"dedupMs\":" + fixed(w.input ? w.dedupMs / double(w.input) : 0, 3) +
        ",\"latencyMs\":" + (w.latencyCount ? fixed(w.latency / w.latencyCount, 1) : std::string("null")) +
        ",\"stages\":{\"upload\":" + stage(StageUpload) + ",\"motion\":" + stage(StageMotion) + ",\"interpolate\":" +
        ((backend == "vfx" || backend == "dlss") && w.vfxCount ? fixed(w.vfxMs / w.vfxCount) : stage(StageInterpolate)) +
        ",\"present\":" + stage(StagePresent) + ",\"encode\":" + stage(StageEncode) + "}" +
        ",\"fg\":" + jsonString(backend) + ",\"vfxRuntime\":" + jsonString(runtime) + ",\"vfxQuality\":" + std::to_string(vfxQuality) +
        ",\"dlssMaxMultiplier\":" + std::to_string(dlssMaximum) +
        ",\"dlssMotion\":" + jsonString(dlssMotion) +
        ",\"audioDelayMs\":" + std::to_string(audioDelayMs_.load()) + ",\"audio\":" + (audio_.running() ? "true" : "false") +
        ",\"audioInputPeak\":" + fixed(audio_.inputPeak(), 6) + ",\"audioOutputPeak\":" + fixed(audio_.outputPeak(), 6) +
        ",\"audioInputFrames\":" + std::to_string(audio_.inputFrames()) + ",\"audioOutputFrames\":" + std::to_string(audio_.outputFrames()) +
        ",\"audioInputState\":" + jsonString(audioStatus.input) + ",\"audioOutputState\":" + jsonString(audioStatus.output) +
        ",\"audioOutputId\":" + jsonString(audioStatus.outputId) + ",\"audioOutputName\":" + jsonString(audioStatus.outputName) +
        ",\"audioQueueMs\":" + std::to_string(audioStatus.queueMs) + ",\"audioBufferMs\":" + std::to_string(audioStatus.bufferMs) +
        ",\"audioOutputAttempts\":" + std::to_string(audioStatus.retries) + ",\"audioUnderruns\":" + std::to_string(audioStatus.underruns) +
        ",\"totals\":{\"input\":" + std::to_string(totalInput_.load()) + ",\"unique\":" + std::to_string(totalUnique_.load()) +
        ",\"duplicates\":" + std::to_string(totalDuplicates_.load()) + ",\"dropped\":" + std::to_string(totalDropped_.load()) + "}";
    const auto recorder = getRecorder();
    const bool recording = recorder && recorder->active();
    double recordFps; { std::lock_guard<std::mutex> lock(recorderMutex_); recordFps = recordingFps_; }
    std::string recordingPath; { std::lock_guard<std::mutex> lock(statsMutex_); recordingPath = lastRecording_; }
    out += ",\"recording\":{\"active\":" + std::string(recording ? "true" : "false") +
           ",\"starting\":" + std::string(recordStarting_ ? "true" : "false") + ",\"finalizing\":" + std::string(recordFinalizing_ ? "true" : "false") + ",\"fps\":" + fixed(recordFps, 6) +
           ",\"path\":" + jsonString(recording ? recorder->path() : recordingPath) +
           ",\"seconds\":" + fixed(recording ? recorder->seconds() : 0, 1) +
           ",\"dropped\":" + std::to_string(recording ? recorder->droppedFrames() : 0) + "}";
    out += ",\"events\":[";
    auto events = log_.drain();
    for (size_t i = 0; i < events.size(); ++i) { if (i) out += ","; out += events[i]; }
    return out + "]}";
}

std::string Session::summary() {
    std::string recordingPath; { std::lock_guard<std::mutex> lock(statsMutex_); recordingPath = lastRecording_; }
    auto avg = [&](int i) { return totalStageCount_[i] ? fixed(totalStage_[i] / totalStageCount_[i], 3) : std::string("null"); };
    // Include the last partial window.
    {
        std::lock_guard<std::mutex> lock(statsMutex_);
        double part[StageCount]; gpu_.timer.take(part);
        for (int i = 0; i < StageCount; ++i) if (part[i] >= 0) { totalStage_[i] += part[i]; ++totalStageCount_[i]; }
    }
    return "{\"input\":" + std::to_string(totalInput_.load()) + ",\"unique\":" + std::to_string(totalUnique_.load()) +
           ",\"duplicates\":" + std::to_string(totalDuplicates_.load()) + ",\"dropped\":" + std::to_string(totalDropped_.load()) +
           ",\"outputs\":" + std::to_string(totalOutputs_) + ",\"interpolated\":" + std::to_string(totalInterpolated_) +
           ",\"skippedTicks\":" + std::to_string(totalSkipped_) + ",\"contentPeriodMs\":" + fixed(double(clock_.period()) / 1e4, 3) +
           ",\"cadenceRegular\":" + std::to_string(clock_.regular) + ",\"cadenceIrregular\":" + std::to_string(clock_.irregular) +
           ",\"dedupThreshold\":" + fixed(detector_.currentThreshold(), 3) +
           ",\"gpu\":" + jsonString(gpu_.adapterName()) +
           ",\"stagesMs\":{\"upload\":" + avg(StageUpload) + ",\"motion\":" + avg(StageMotion) + ",\"interpolate\":" + avg(StageInterpolate) +
           ",\"present\":" + avg(StagePresent) + ",\"encode\":" + avg(StageEncode) + "},\"recording\":" + jsonString(recordingPath) + "}";
}

void Session::close() {
    if (stop_ && !renderThread_.joinable()) return;
    if (capture_) capture_->stop();
    audio_.stop();
    {
        std::scoped_lock lock(queueMutex_, vfxMutex_);
        stop_ = true;
    }
    queueWake_.notify_all(); idleWake_.notify_all(); vfxWake_.notify_all();
    if (renderThread_.joinable()) renderThread_.join();
    for (auto& job : screenshotJobs_) job.wait();
    screenshotJobs_.clear();
    if (recorderFinisher_.joinable()) recorderFinisher_.join();
    if (vfxLoader_.joinable()) vfxLoader_.join();
    vfx_.reset();
    if (dlssLoader_.joinable()) dlssLoader_.join();
    dlss_.reset();
    gpu_.detach();
    if (timer_) { CloseHandle(timer_); timer_ = nullptr; }
    if (inputEvent_) { CloseHandle(inputEvent_); inputEvent_ = nullptr; }
}

// ---------------------------------------------------------------- self-test
namespace {
double psnr(const std::vector<uint8_t>& a, const std::vector<uint8_t>& b, int w, int h, int border) {
    double se = 0; size_t n = 0;
    for (int y = border; y < h - border; ++y) for (int x = border; x < w - border; ++x) for (int c = 0; c < 3; ++c) {
        double d = double(a[(size_t(y) * w + x) * 4 + c]) - double(b[(size_t(y) * w + x) * 4 + c]); se += d * d; ++n;
    }
    double mse = se / double(std::max<size_t>(1, n));
    return mse <= 1e-12 ? 99.0 : 10 * std::log10(255.0 * 255.0 / mse);
}
uint8_t toByte(double v) { return uint8_t(std::lround(std::clamp(v, 0.0, 1.0) * 255)); }
// Fractal value noise: non-periodic detail at several scales, like natural or game
// textures, and defined at any sub-pixel offset (so the true middle picture is exact).
double lattice(int x, int y, int seed) {
    uint32_t h = uint32_t(x) * 374761393u + uint32_t(y) * 668265263u + uint32_t(seed) * 2246822519u;
    h = (h ^ (h >> 13)) * 1274126177u; h ^= h >> 16;
    return double(h & 0xffffff) / double(0xffffff);
}
double valueNoise(double x, double y, double cell, int seed) {
    const double fx = x / cell, fy = y / cell;
    const int ix = int(std::floor(fx)), iy = int(std::floor(fy));
    double tx = fx - ix, ty = fy - iy;
    tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
    const double a = lattice(ix, iy, seed), b = lattice(ix + 1, iy, seed), c = lattice(ix, iy + 1, seed), d = lattice(ix + 1, iy + 1, seed);
    return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
}
double fractal(double x, double y, int seed) {
    return 0.42 * valueNoise(x, y, 48, seed) + 0.28 * valueNoise(x, y, 19, seed + 7) + 0.18 * valueNoise(x, y, 8, seed + 13) + 0.12 * valueNoise(x, y, 3.5, seed + 29);
}
void texture(std::vector<uint8_t>& img, int w, int h, double dx, double dy, int variant) {
    img.assign(size_t(w) * h * 4, 255);
    for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x) {
        const double u = x - dx, v = y - dy;
        const double base = fractal(u, v, variant * 101);
        img[(size_t(y) * w + x) * 4 + 0] = toByte(base);
        img[(size_t(y) * w + x) * 4 + 1] = toByte(0.15 + 0.7 * fractal(u, v, variant * 101 + 3));
        img[(size_t(y) * w + x) * 4 + 2] = toByte(1.0 - 0.8 * base);
    }
}
void composite(std::vector<uint8_t>& img, int w, int h, double ox, double oy, int size) {
    // A textured square moving over the background (occlusion case).
    for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x) {
        const double u = x - ox, v = y - oy;
        if (u < 0 || v < 0 || u >= size || v >= size) continue;
        const double c = std::clamp(0.5 + 1.6 * (fractal(u, v, 977) - 0.5), 0.0, 1.0);
        img[(size_t(y) * w + x) * 4 + 0] = toByte(c);
        img[(size_t(y) * w + x) * 4 + 1] = toByte(1 - c);
        img[(size_t(y) * w + x) * 4 + 2] = toByte(0.3 + 0.4 * c);
    }
}
} // namespace

std::string selfTest(const Settings& s) {
    std::string out = "{\"checks\":[";
    int passed = 0, failed = 0;
    auto check = [&](const std::string& name, bool ok, const std::string& detail) {
        if (passed + failed) out += ",";
        out += "{\"name\":" + jsonString(name) + ",\"ok\":" + (ok ? "true" : "false") + ",\"detail\":" + jsonString(detail) + "}";
        if (getInt(s, "echo", 0)) { fprintf(stderr, "[selftest] %s: %s (%s)\n", name.c_str(), ok ? "PASS" : "FAIL", detail.c_str()); fflush(stderr); }
        ok ? ++passed : ++failed;
    };
    check("capture-callback-lifetime", captureCallbackSelfTest(), "sample reference, type-change flag, latched failure and late callback after cancellation");
    {
        const int w = 128, h = 72;
        std::vector<uint8_t> black(size_t(w) * h, 16), white(black.size(), 235), fade(black.size(), 36), moved(black.size()), shifted(black.size());
        for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x) moved[size_t(y) * w + x] = uint8_t(16 + (x + y) % 128);
        for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x) shifted[size_t(y) * w + x] = uint8_t(16 + (x + y + 8) % 128);
        const auto a = sceneSignature(makeSignature(black.data(), w, 1, w, h));
        const auto b = sceneSignature(makeSignature(white.data(), w, 1, w, h));
        const auto f = sceneSignature(makeSignature(fade.data(), w, 1, w, h));
        const auto pan = sceneSignature(makeSignature(moved.data(), w, 1, w, h));
        const auto nextPan = sceneSignature(makeSignature(shifted.data(), w, 1, w, h));
        check("scene-cuts", hardSceneCut(a, b) && !hardSceneCut(a, a) && !hardSceneCut(a, f) && !hardSceneCut(pan, nextPan) && !hardSceneCut(SceneSignature{}, b),
              "hard cut resets; identical pictures, small fades and missing history do not");
    }
    check("audio-output-routing", audioRoutingSelfTest(), "default output follows only render/console; pinned output ignores default and unrelated endpoint changes");
    bool heartbeats = !packetTimedOut(100, 0, 49900100) && packetTimedOut(100, 0, 50000100) && packetTimedOut(100, 10000100, 40000100);
    for (int i = 1; i <= 6000; ++i) heartbeats = heartbeats && !packetTimedOut(100, int64_t(i)*1000000, int64_t(i)*1000000+999999, 2000, 2000);
    check("packet-deadlines", heartbeats, "bounded first/gap waits; ten minutes of silent/static but arriving packets never time out");
    check("recording-h264-levels", recordingH264Level(3840,2160,60,1)==0 && recordingH264Level(3840,2160,90,1)==60 && recordingH264Level(3840,2160,120,1)==60 && recordingH264Level(3840,2160,240,1)==61 && recordingH264Level(1920,1080,240000,1001)==0,
          "default through level 5.2; minimum 6.0/6.1 for higher macroblock rates, including rational NTSC rates");
    // Duplicate detector.
    {
        const int w = 640, h = 360;
        std::vector<uint8_t> a(size_t(w) * h), noisy(a.size()), moved(a.size());
        for (size_t i = 0; i < a.size(); ++i) a[i] = uint8_t((i * 37 + (i / w) * 11) & 0xff);
        for (size_t i = 0; i < a.size(); ++i) noisy[i] = uint8_t(std::clamp(int(a[i]) + int((i * 7919) % 3) - 1, 0, 255));
        moved = a; for (int y = 100; y < 124; ++y) for (int x = 200; x < 230; ++x) moved[size_t(y) * w + x] = uint8_t(255 - moved[size_t(y) * w + x]);
        DuplicateDetector d;
        bool first = d.observe(makeSignature(a.data(), w, 1, w, h));
        bool same = d.observe(makeSignature(a.data(), w, 1, w, h));
        bool noise = d.observe(makeSignature(noisy.data(), w, 1, w, h));
        bool change = d.observe(makeSignature(moved.data(), w, 1, w, h));
        check("dedup", !first && same && noise && !change, "first unique, exact repeat, +-1 noise repeat, 30x24 change unique");
        std::vector<uint8_t> bottomUp(a.size());
        for (int y=0;y<h;++y) memcpy(bottomUp.data()+size_t(h-1-y)*w,a.data()+size_t(y)*w,w);
        check("negative-pitch",makeSignature(a.data(),w,1,w,h).samples==makeSignature(bottomUp.data()+size_t(h-1)*w,-w,1,w,h).samples,
              "bottom-up rows have the same luma signature as top-down rows");
    }
    // Output timeline: 30 fps content in a 60 Hz signal alternates originals and midpoints.
    {
        OutputTimeline t; t.configure(166667);
        std::string pattern; bool ok = true;
        for (int i = 0; i < 24; ++i) {
            const int64_t tau = int64_t(i) * 166667;
            if (i % 2 == 0) t.addUnique({tau, i % 5, uint64_t(i + 1)});
            t.advance(true);
            OutputPlan p = t.plan(tau, 0, 1, true, true);
            if (i >= 8) {
                const bool expectMid = i % 2 == 1;
                pattern += p.kind == OutputPlan::Interpolate ? "I" : "O";
                if (expectMid != (p.kind == OutputPlan::Interpolate) || (expectMid && std::abs(p.phase - 0.5) > 1e-6)) ok = false;
            }
        }
        check("timeline-30in60", ok, pattern);
        // 2x output of 60 fps content: originals and midpoints.
        OutputTimeline u; u.configure(166667); std::string p2; bool ok2 = true;
        for (int i = 0; i < 12; ++i) {
            u.addUnique({int64_t(i) * 166667, i % 5, uint64_t(i + 1)});
            u.advance(false);
            if (i < 4) continue;
            OutputPlan a = u.plan(int64_t(i) * 166667, 0, 2, false, true), b = u.plan(int64_t(i) * 166667, 1, 2, false, true);
            p2 += a.kind == OutputPlan::Interpolate ? "I" : "O"; p2 += b.kind == OutputPlan::Interpolate ? "I" : "O";
            // Half an interval is rounded to whole 100 ns ticks, hence the tolerance.
            if (a.kind != OutputPlan::Original || b.kind != OutputPlan::Interpolate || std::abs(b.phase - 0.5) > 1e-4) ok2 = false;
        }
        check("timeline-2x", ok2, p2);
    }
    // Content clock: 40 fps content sampled at 60 Hz gets 25 ms spacing.
    {
        ContentClock c; int64_t last = 0, prevSample = -166667; double worst = 0; int counted = 0;
        for (int i = 0; i < 120; ++i) {
            const int64_t sample = int64_t(i) * 166667;
            const int picture = int((sample + 1) / 250000);                // picture shown at this sample
            const int before = i == 0 ? -1 : int((sample - 166667 + 1) / 250000);
            if (picture != before) {
                const int64_t content = c.observe(sample, prevSample, 166667);
                if (i > 40) { worst = std::max(worst, std::abs(double(content - last) - 250000.0) / 1e4); ++counted; }
                last = content;
            }
            prevSample = sample;
        }
        check("content-clock-40in60", counted > 20 && worst < 0.6, "max spacing error " + fixed(worst, 3) + " ms over " + std::to_string(counted));
    }
    // Content-based rates, including a non-integral content/signal cadence. Count only
    // after clock acquisition; every planned timestamp must increase and stay bracketed.
    for (int contentFps : {30,40,60}) for (int multiplier : {2,3,4}) {
        ContentClock clock; OutputTimeline timeline; timeline.configure(166667);
        int64_t previous=-166667,next=0,last=INT64_MIN; bool initialized=false,ok=true; int outputs=0,mids=0;
        for (int i=0;i<240;++i) {
            const int64_t sample=int64_t(i)*166667;
            const int picture=int(sample*contentFps/10000000),before=i?int(previous*contentFps/10000000):-1;
            if (picture!=before) timeline.addUnique({clock.observe(sample,previous,166667),picture%5,uint64_t(picture+1)});
            previous=sample; timeline.advance(true);
            const int64_t period=clock.period()>0?clock.period():166667,step=period/multiplier;
            const int64_t limit=sample-timeline.delay(),horizon=limit+166667;
            if(!initialized||next<limit-4*step){next=std::max(limit,last==INT64_MIN?limit:last+step);initialized=true;}
            while(next<horizon){
                auto p=timeline.planAt(next);
                if(i>=120){++outputs;if(p.kind==OutputPlan::Interpolate)++mids;if(next<=last||p.kind==OutputPlan::Hold)ok=false;}
                last=next;next+=step;
            }
        }
        const int expected=contentFps*multiplier*2;
        check("content-rate-"+std::to_string(contentFps)+"x"+std::to_string(multiplier),ok&&std::abs(outputs-expected)<=2&&mids>0,
              std::to_string(outputs)+" outputs / 2 s, expected "+std::to_string(expected)+", interpolated "+std::to_string(mids));
    }
    // GPU checks.
    Gpu gpu; std::string error;
    const int w = 640, h = 368;
    if (!gpu.create(error) || !gpu.configure(w, h, error)) { check("gpu", false, error); }
    else {
        check("gpu", true, gpu.adapterName());
        // Independently packed variants must decode identically. RGB32 must retain
        // channel order and bypass YUV range/matrix conversion.
        {
            std::vector<uint8_t> yuy2(size_t(w)*h*2),uyvy(yuy2.size()),ya,ua,bgra(size_t(w)*h*4),rgb;
            for(size_t i=0;i<yuy2.size();i+=4){yuy2[i]=uint8_t(16+(i/4)%220);yuy2[i+1]=90;yuy2[i+2]=uint8_t(16+(i/4+70)%220);yuy2[i+3]=180;
                uyvy[i]=yuy2[i+1];uyvy[i+1]=yuy2[i];uyvy[i+2]=yuy2[i+3];uyvy[i+3]=yuy2[i+2];}
            ColorSetup color;
            gpu.upload(0,yuy2.data(),w*2,nullptr,0,PixelFormat::YUY2,color,0);gpu.readback(gpu.frame(0),ya);
            gpu.upload(1,uyvy.data(),w*2,nullptr,0,PixelFormat::UYVY,color,0);gpu.readback(gpu.frame(1),ua);
            check("uyvy",!ya.empty()&&ya==ua,"YUY2 and UYVY packing decode to identical RGBA pixels");
            for(size_t i=0;i<bgra.size();i+=4){bgra[i]=17;bgra[i+1]=99;bgra[i+2]=231;bgra[i+3]=0;}
            gpu.upload(2,bgra.data(),w*4,nullptr,0,PixelFormat::RGB32,color,0);gpu.readback(gpu.frame(2),rgb);
            bool exact=rgb.size()==bgra.size();for(size_t i=0;i<rgb.size();i+=4)if(rgb[i]!=231||rgb[i+1]!=99||rgb[i+2]!=17||rgb[i+3]!=255)exact=false;
            check("rgb32",exact,"BGRA capture pixels retain RGB values with opaque alpha");
        }
        std::vector<uint8_t> a, b, truth, result, blend;
        auto fgCase = [&](const std::string& name, bool occlusion) {
            if (occlusion) {
                texture(a, w, h, 0, 0, 0); composite(a, w, h, 180, 120, 120);
                texture(b, w, h, 0, 0, 0); composite(b, w, h, 220, 136, 120);
                texture(truth, w, h, 0, 0, 0); composite(truth, w, h, 200, 128, 120);
            } else { texture(a, w, h, 0, 0, 1); texture(b, w, h, 14, 6, 1); texture(truth, w, h, 7, 3, 1); }
            blend.resize(a.size());
            for (size_t i = 0; i < a.size(); ++i) blend[i] = uint8_t((int(a[i]) + int(b[i]) + 1) / 2);
            gpu.uploadRgba(0, a.data(), w * 4, 0); gpu.uploadRgba(1, b.data(), w * 4, 0);
            gpu.pyramid(0); gpu.pyramid(1); gpu.motion(0, 1); gpu.interpolate(0, 1, 0.5f);
            gpu.readback(gpu.output(), result);
            // Vector diagnostics: share of blocks at the true background motion.
            std::vector<float> field; int gw = 0, gh = 0; int exact = 0;
            const float tx = occlusion ? 0.f : 14.f, ty = occlusion ? 0.f : 6.f;
            if (gpu.readField(1, field, gw, gh))
                for (int i = 0; i < gw * gh; ++i) if (std::abs(field[size_t(i) * 2] - tx) <= 0.5f && std::abs(field[size_t(i) * 2 + 1] - ty) <= 0.5f) ++exact;
            const char* debugDir = getenv("FDL_DEBUG_DIR");
            if (debugDir) {
                const std::wstring dir = wide(debugDir);
                savePng(dir + L"\\" + wide(name) + L"-result.png", result, w, h);
                savePng(dir + L"\\" + wide(name) + L"-truth.png", truth, w, h);
            }
            const double fg = psnr(result, truth, w, h, 24), plain = psnr(blend, truth, w, h, 24);
            // Pure motion must be near exact. Occlusion (uncovered background) leaves a blended
            // band at object edges with block motion; it must still beat a plain blend.
            const bool pass = occlusion ? fg > plain + 1.0 : (fg > 34.0 && fg > plain + 6);
            check(name, pass, "interpolated " + fixed(fg) + " dB, plain blend " + fixed(plain) + " dB, blocks at true motion " +
                  std::to_string(exact) + "/" + std::to_string(gw * gh));
            return fg;
        };
        fgCase("fg-pan", false);
        fgCase("fg-occlusion", true);
        if(getInt(s,"nvof",0)) {
            NvofFlow flow; std::string e;
            if(!flow.create(gpu,getInt(s,"nvofQuality",1),e))check("nvof",false,e);
            else {
                texture(a,w,h,0,0,1);texture(b,w,h,14,6,1);texture(truth,w,h,7,3,1);
                gpu.uploadRgba(0,a.data(),w*4,0);gpu.uploadRgba(1,b.data(),w*4,0);
                bool ok=flow.estimate(gpu,0,1,true,e);
                std::vector<float> xy;int fw=0,fh=0;
                ok=ok&&flow.readBackward(gpu,xy,fw,fh);
                int correct=0,total=0;
                if(ok)for(int y=8;y<fh-8;++y)for(int x=8;x<fw-8;++x) {
                    size_t i=(size_t(y)*fw+x)*2;++total;
                    if(std::abs(xy[i]+14)<1.5f&&std::abs(xy[i+1]+6)<1.5f)++correct;
                }
                if(ok){flow.interpolate(gpu,0,1,.5f);ok=gpu.readback(gpu.output(),result);}
                double p=ok?psnr(result,truth,w,h,24):0;
                check("nvof",ok&&total>0&&correct>total*.9&&p>30,
                    ok?"B-to-A vectors "+std::to_string(correct)+"/"+std::to_string(total)+", grid "+std::to_string(flow.grid())+", midpoint "+fixed(p)+" dB":e);
                // DLSS uses the one-direction session and a full-size float vector map.
                flow.destroy();
                bool expanded=flow.create(gpu,1,e,false)&&flow.estimate(gpu,0,1,true,e);
                ComPtr<ID3D11Texture2D> target,staging;
                ComPtr<ID3D11UnorderedAccessView> uav;
                D3D11_TEXTURE2D_DESC d{UINT(w),UINT(h),1,1,DXGI_FORMAT_R32G32_FLOAT,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_UNORDERED_ACCESS,0,0};
                if(expanded)expanded=SUCCEEDED(gpu.device()->CreateTexture2D(&d,nullptr,&target))&&SUCCEEDED(gpu.device()->CreateUnorderedAccessView(target.Get(),nullptr,&uav));
                int expandedCorrect=0,expandedTotal=0;
                if(expanded) {
                    flow.motionVectors(gpu,uav.Get());d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
                    expanded=SUCCEEDED(gpu.device()->CreateTexture2D(&d,nullptr,&staging));
                    if(expanded) {
                        gpu.context()->CopyResource(staging.Get(),target.Get());D3D11_MAPPED_SUBRESOURCE m{};
                        expanded=SUCCEEDED(gpu.context()->Map(staging.Get(),0,D3D11_MAP_READ,0,&m));
                        if(expanded) {
                            for(int y=32;y<h-32;++y) {
                                auto row=reinterpret_cast<const float*>(static_cast<const uint8_t*>(m.pData)+size_t(y)*m.RowPitch);
                                for(int x=32;x<w-32;++x){++expandedTotal;if(std::abs(row[x*2]+14)<1.5f&&std::abs(row[x*2+1]+6)<1.5f)++expandedCorrect;}
                            }
                            gpu.context()->Unmap(staging.Get(),0);
                        }
                    }
                }
                check("nvof-dlss-vectors",expanded&&expandedTotal>0&&expandedCorrect>expandedTotal*.9,
                    expanded?"full-size current-to-previous vectors "+std::to_string(expandedCorrect)+"/"+std::to_string(expandedTotal):e);
            }
        }
        // Anti-aliasing: an aliased slanted edge gains intermediate values; flat areas stay.
        {
            std::vector<uint8_t> edge(size_t(w) * h * 4, 255), aa;
            for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x) {
                const bool dark = y > 0.3 * x + 60;
                for (int c = 0; c < 3; ++c) edge[(size_t(y) * w + x) * 4 + c] = dark ? 20 : 235;
            }
            gpu.uploadRgba(2, edge.data(), w * 4, 2);
            gpu.readback(gpu.frame(2), aa);
            int partial = 0, flatChanged = 0;
            for (int y = 4; y < h - 4; ++y) for (int x = 4; x < w - 4; ++x) {
                const double dist = std::abs(y - (0.3 * x + 60)) / std::sqrt(1.09);
                const uint8_t v = aa[(size_t(y) * w + x) * 4];
                if (dist < 1.5 && v > 30 && v < 225) ++partial;
                if (dist > 4 && v != edge[(size_t(y) * w + x) * 4]) ++flatChanged;
            }
            check("anti-aliasing", partial > w / 2 && flatChanged == 0, std::to_string(partial) + " blended edge pixels, " + std::to_string(flatChanged) + " flat pixels changed");
        }
        // RGB -> NV12 (recorder) -> RGB (capture path) round trip.
        {
            std::vector<uint8_t> src, back;
            texture(src, w, h, 0, 0, 2);
            gpu.uploadRgba(3, src.data(), w * 4, 0);
            D3D11_TEXTURE2D_DESC d{}; d.Width = UINT(w); d.Height = UINT(h); d.MipLevels = 1; d.ArraySize = 1; d.Format = DXGI_FORMAT_NV12;
            d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_RENDER_TARGET;
            ComPtr<ID3D11Texture2D> nv12, staging;
            bool ok = SUCCEEDED(gpu.device()->CreateTexture2D(&d, nullptr, &nv12)) && gpu.toNv12(gpu.frameView(3), nv12.Get());
            d.Usage = D3D11_USAGE_STAGING; d.BindFlags = 0; d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            ok = ok && SUCCEEDED(gpu.device()->CreateTexture2D(&d, nullptr, &staging));
            std::vector<uint8_t> planes(size_t(w) * h * 3 / 2);
            if (ok) {
                gpu.context()->CopyResource(staging.Get(), nv12.Get());
                D3D11_MAPPED_SUBRESOURCE m{};
                ok = SUCCEEDED(gpu.context()->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &m));
                if (ok) {
                    for (int y = 0; y < h * 3 / 2; ++y) memcpy(planes.data() + size_t(y) * w, static_cast<uint8_t*>(m.pData) + size_t(y) * m.RowPitch, size_t(w));
                    gpu.context()->Unmap(staging.Get(), 0);
                }
            }
            if (ok) {
                ColorSetup cs; cs.limited = true; cs.bt709 = true;
                gpu.upload(4, planes.data(), w, planes.data() + size_t(w) * h, w, PixelFormat::NV12, cs, 0);
                gpu.readback(gpu.frame(4), back);
            }
            const double p = ok ? psnr(src, back, w, h, 2) : 0;
            check("nv12-roundtrip", ok && p > 38, "PSNR " + fixed(p) + " dB");
        }
        // NVIDIA VFX, when its runtime folder is given.
        const std::string vfxDir = get(s, "vfxDir");
        if (!vfxDir.empty()) {
            VfxGenerator vfx; std::string vfxError;
            if (!vfx.create(gpu, wide(vfxDir), getInt(s, "vfxQuality", 1), vfxError)) check("vfx", false, vfxError);
            else {
                texture(a, w, h, 0, 0, 1); texture(b, w, h, 14, 6, 1); texture(truth, w, h, 7, 3, 1);
                gpu.uploadRgba(0, a.data(), w * 4, 0); gpu.uploadRgba(1, b.data(), w * 4, 0);
                std::string e; bool ok = vfx.interpolate(gpu, 0, 1, 0.5f, false, e);
                ok = ok && vfx.interpolate(gpu, 0, 1, 0.5f, false, e);   // second run: warm timing
                gpu.readback(gpu.output(), result);
                const double p = ok ? psnr(result, truth, w, h, 24) : 0;
                check("vfx", ok && p > 30, ok ? "runtime " + vfx.version() + ", PSNR " + fixed(p) + " dB, " + fixed(vfx.lastMs()) + " ms" : e);
            }
        }
    }
    // D3D11 <-> D3D12 hand-over used by DLSS, without NGX: shared textures, both fences,
    // allocator reuse and GPU timestamps on a plain copy.
    {
        Gpu gpu; std::string error;
        if (!gpu.create(error) || !gpu.configure(320, 180, error)) check("dlss-bridge", false, error);
        else { std::string detail; const bool ok = DlssGenerator::bridgeSelfTest(gpu, detail); check("dlss-bridge", ok, detail); }
    }
    // DLSS frame generation, when its runtime folder is given.
    const std::string dlssDir = get(s, "dlssDir");
    if (!dlssDir.empty()) {
        const int w = 1920, h = 1088;
        Gpu gpu; std::string error;
        if (!gpu.create(error) || !gpu.configure(w, h, error)) check("dlss", false, error);
        else {
            DlssGenerator dlss;
            DlssOptions options; options.diagnostics = getInt(s, "echo", 0) != 0;
            options.motion = parseDlssMotion(get(s, "dlssMotion", "none")); options.lite = get(s, "dlssLite") == "1";
            if (!dlss.create(gpu, wide(dlssDir), wide(get(s, "dlssDataDir")), error, options)) check("dlss", false, error);
            else {
                std::vector<uint8_t> a, b, truth, result, repeated;
                texture(a, w, h, 0, 0, 1);
                gpu.uploadRgba(0, a.data(), w * 4, 0); gpu.pyramid(0);
                bool ok = true, quality = true, repeatStable = true;
                double cold = 0, warmMin = 99, warmMs = 0;
                std::string detail = dlss.version();
                // Warm the temporal model with NEW real pictures. Repeating B for timing
                // changes its history and can silently turn the supposed midpoint into B.
                for (int pair = 0; pair < 6 && ok; ++pair) {
                    const int slotA = pair % 2, slotB = 1 - slotA;
                    texture(b, w, h, 14 * (pair + 1), 6 * (pair + 1), 1);
                    texture(truth, w, h, 14 * pair + 7, 6 * pair + 3, 1);
                    gpu.uploadRgba(slotB, b.data(), w * 4, 0);
                    if(dlss.needsEngineMotion()){gpu.pyramid(slotB); gpu.motion(slotA, slotB);}
                    ok = dlss.interpolate(gpu, slotA, slotB, uint64_t(pair + 1), uint64_t(pair + 2), 0.5f, 1, error);
                    if (!ok) break;
                    ok = gpu.readback(gpu.output(), result);
                    if (!ok) { error = "DLSS output readback"; break; }
                    const double p = psnr(result, truth, w, h, 24);
                    const double pa = psnr(result, a, w, h, 24), pb = psnr(result, b, w, h, 24);
                    if (pair == 0) cold = p;
                    else { warmMin = std::min(warmMin, p); quality = quality && p > 30; }
                    if (getInt(s, "echo", 0)) {
                        fprintf(stderr, "[dlss-test] pair=%d PSNR=%s dB, to first=%s, to second=%s, generate=%s ms\n", pair + 1, fixed(p).c_str(), fixed(pa).c_str(), fixed(pb).c_str(), fixed(dlss.lastMs()).c_str());
                        fflush(stderr);
                    }
                    // Repeated display requests must preserve that exact midpoint, while
                    // subsequent new pairs must invalidate the old result.
                    ok = dlss.interpolate(gpu, slotA, slotB, uint64_t(pair + 1), uint64_t(pair + 2), 0.5f, 1, error);
                    ok = ok && gpu.readback(gpu.output(), repeated);
                    repeatStable = repeatStable && ok && result == repeated;
                    a.swap(b);
                }
                warmMs = dlss.averageGpuMs();
                detail += ", motion " + dlss.motionSource() + ", cold PSNR " + fixed(cold) + " dB, warm minimum " + fixed(warmMin) + " dB, repeated output " + (repeatStable ? "identical" : "CHANGED") +
                          ", DLSS-G GPU average " + (warmMs >= 0 ? fixed(warmMs) + " ms over " + std::to_string(dlss.gpuSamples()) : std::string("unmeasured"));
                check("dlss", ok && quality && repeatStable, ok ? detail : error);
                if(ok){
                    dlss.resetHistory();
                    bool resetOk = dlss.interpolate(gpu, 1, 0, 6, 7, .5f, 1, error) && gpu.readback(gpu.output(), result);
                    const double resetPsnr = resetOk ? psnr(result, truth, w, h, 24) : 0;
                    check("dlss-history-reset", resetOk && resetPsnr > 30, resetOk ? "new history PSNR " + fixed(resetPsnr) + " dB" : error);
                }
            }
        }
    }
    out += "],\"passed\":" + std::to_string(passed) + ",\"failed\":" + std::to_string(failed) + "}";
    return out;
}

} // namespace fdl

using namespace fdl;

FDL_API int fdl_api_version() { return 1; }

FDL_API char* fdl_devices() {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    std::string json = devicesJson(listVideoDevices(true), listAudioEndpoints(true));
    CoUninitialize();
    return duplicate(json);
}

static void* openSession(const char* settings, HWND window, bool replay, char** error) {
    auto* session = new Session();
    std::string message;
    if (!session->open(parseSettings(settings), window, replay, message)) {
        delete session;
        if (error) *error = duplicate(message);
        return nullptr;
    }
    return session;
}

FDL_API void* fdl_open(const char* settings, HWND window, char** error) { return openSession(settings, window, false, error); }
FDL_API void* fdl_open_replay(const char* settings, char** error) { return openSession(settings, nullptr, true, error); }

FDL_API int fdl_command(void* session, const char* command) {
    if (!session) return 0;
    static_cast<Session*>(session)->command(parseSettings(command));
    return 1;
}

FDL_API char* fdl_status(void* session) { return session ? duplicate(static_cast<Session*>(session)->status()) : nullptr; }
FDL_API char* fdl_summary(void* session) { return session ? duplicate(static_cast<Session*>(session)->summary()) : nullptr; }

FDL_API int fdl_feed(void* session, const unsigned char* data, int pitch, long long time100ns) {
    if (!session) return 0;
    // Replay frames arrive as packed NV12/YUY2 at the configured size; the call returns
    // after the frame has been processed, so replay never drops pictures.
    static_cast<Session*>(session)->feedPacked(data, pitch, time100ns);
    return 1;
}

FDL_API void fdl_close(void* session) { delete static_cast<Session*>(session); }
FDL_API int fdl_feed_audio(void* session, const float* samples, int frames, int channels, int rate, long long time100ns) {
    if (!session || !samples || frames <= 0 || channels <= 0 || rate <= 0) return 0;
    static_cast<Session*>(session)->feedAudio(samples, frames, channels, rate, time100ns);
    return 1;
}

FDL_API char* fdl_selftest(const char* settings) {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    std::string result = selfTest(parseSettings(settings));
    CoUninitialize();
    return duplicate(result);
}

FDL_API void fdl_free(void* text) { CoTaskMemFree(text); }
