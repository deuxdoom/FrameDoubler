#include "recorder.h"
#include "gpu.h"

#include <codecapi.h>
#include <evr.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <shlobj.h>

#include <algorithm>
#include <cmath>
#include <cstring>

extern "C" HRESULT WINAPI MFCreateTrackedSample(IMFTrackedSample** sample);

namespace fdl {
// Samples referring to each encoder texture; a repeated picture adds a reference to the same texture.
struct RecorderSlots { std::mutex mutex; int uses[kRecorderSlots] = {}; };

unsigned recordingH264Level(int width, int height, uint32_t fpsNum, uint32_t fpsDen) {
    const int64_t blocks = int64_t((width + 15) / 16) * ((height + 15) / 16);
    const double rate = double(blocks) * fpsNum / std::max<uint32_t>(1, fpsDen);
    // Annex A macroblock limits. Auto works through 5.2; older MFT auto-selection
    // does not discover the newer level 6 family, so specify the minimum needed.
    if (blocks <= 36864 && rate <= 2073600) return 0;
    if (blocks <= 139264 && rate <= 4177920) return 60;
    if (blocks <= 139264 && rate <= 8355840) return 61;
    return 62;
}

// Returns an encoder texture to the pool once Media Foundation releases the sample that
// wraps it (the hardware encoder may hold it for several frames).
struct TrackedRelease : IMFAsyncCallback {
    std::shared_ptr<RecorderSlots> pool; int slot; std::atomic<ULONG> refs{1};
    TrackedRelease(std::shared_ptr<RecorderSlots> p, int s) : pool(std::move(p)), slot(s) {}
    virtual ~TrackedRelease() = default;
    STDMETHODIMP QueryInterface(REFIID id, void** out) override {
        if (id == __uuidof(IUnknown) || id == __uuidof(IMFAsyncCallback)) { *out = static_cast<IMFAsyncCallback*>(this); AddRef(); return S_OK; }
        *out = nullptr; return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    STDMETHODIMP_(ULONG) Release() override { ULONG r = --refs; if (!r) delete this; return r; }
    STDMETHODIMP GetParameters(DWORD*, DWORD*) override { return E_NOTIMPL; }
    STDMETHODIMP Invoke(IMFAsyncResult*) override { std::lock_guard<std::mutex> lock(pool->mutex); if (pool->uses[slot] > 0) --pool->uses[slot]; return S_OK; }
};

namespace {
std::wstring timestampName() {
    SYSTEMTIME t; GetLocalTime(&t);
    wchar_t b[64];
    swprintf(b, 64, L"FrameDoubler_%04d%02d%02d_%02d%02d%02d", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
    return b;
}
HRESULT videoType(IMFMediaType** out, const GUID& subtype, const RecorderConfig& c, UINT32 bitrate) {
    ComPtr<IMFMediaType> t;
    HRESULT hr = MFCreateMediaType(&t);
    if (SUCCEEDED(hr)) hr = t->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (SUCCEEDED(hr)) hr = t->SetGUID(MF_MT_SUBTYPE, subtype);
    if (SUCCEEDED(hr)) hr = t->SetUINT64(MF_MT_FRAME_SIZE, (UINT64(c.width) << 32) | UINT32(c.height));
    if (SUCCEEDED(hr)) hr = t->SetUINT64(MF_MT_FRAME_RATE, (UINT64(c.fpsNum) << 32) | c.fpsDen);
    if (SUCCEEDED(hr)) hr = t->SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, (UINT64(1) << 32) | 1);
    if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (SUCCEEDED(hr) && bitrate) {
        hr = t->SetUINT32(MF_MT_AVG_BITRATE, bitrate);
        if (SUCCEEDED(hr)) t->SetUINT32(MF_MT_MPEG2_PROFILE, c.hevc ? UINT32(eAVEncH265VProfile_Main_420_8) : UINT32(eAVEncH264VProfile_High));
        if (!c.hevc) if (const auto level = recordingH264Level(c.width, c.height, c.fpsNum, c.fpsDen)) t->SetUINT32(MF_MT_MPEG2_LEVEL, level);
    }
    if (SUCCEEDED(hr)) { t->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235); t->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709);
                         t->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709); t->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709); }
    if (SUCCEEDED(hr)) *out = t.Detach();
    return hr;
}
HRESULT audioType(IMFMediaType** out, bool aac) {
    ComPtr<IMFMediaType> t;
    HRESULT hr = MFCreateMediaType(&t);
    if (SUCCEEDED(hr)) hr = t->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
    if (SUCCEEDED(hr)) hr = t->SetGUID(MF_MT_SUBTYPE, aac ? MFAudioFormat_AAC : MFAudioFormat_PCM);
    if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
    if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000);
    if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2);
    if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, aac ? 24000 : 192000);
    if (SUCCEEDED(hr) && !aac) hr = t->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 4);
    if (SUCCEEDED(hr)) *out = t.Detach();
    return hr;
}
} // namespace

bool Recorder::start(const RecorderConfig& config, Gpu& gpu, std::string& path, std::string& error) {
    stop();
    config_ = config;
    slots_ = std::make_shared<RecorderSlots>();
    if (config.width % 2 || config.height % 2) { error = "recording needs even picture dimensions"; return false; }
    if (SHCreateDirectoryExW(nullptr, config.directory.c_str(), nullptr) != ERROR_SUCCESS && GetFileAttributesW(config.directory.c_str()) == INVALID_FILE_ATTRIBUTES) {
        error = "recording folder could not be created"; return false;
    }
    std::wstring file = config.directory + L"\\" + timestampName() + L".mp4";
    for (int n = 2; GetFileAttributesW(file.c_str()) != INVALID_FILE_ATTRIBUTES; ++n)
        file = config.directory + L"\\" + timestampName() + L"-" + std::to_wstring(n) + L".mp4";
    MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
    for (int i = 0; i < kSlots; ++i) {
        D3D11_TEXTURE2D_DESC d{};
        d.Width = UINT(config.width); d.Height = UINT(config.height); d.MipLevels = 1; d.ArraySize = 1; d.Format = DXGI_FORMAT_NV12;
        d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_VIDEO_ENCODER;
        if (FAILED(gpu.device()->CreateTexture2D(&d, nullptr, &textures_[i]))) {
            d.BindFlags = D3D11_BIND_RENDER_TARGET;
            if (FAILED(gpu.device()->CreateTexture2D(&d, nullptr, &textures_[i]))) { error = "this GPU cannot render NV12 pictures for the encoder"; MFShutdown(); return false; }
        }
    }
    UINT token = 0;
    HRESULT hr = MFCreateDXGIDeviceManager(&token, &manager_);
    if (SUCCEEDED(hr)) hr = manager_->ResetDevice(gpu.device(), token);
    ComPtr<IMFAttributes> attributes;
    if (SUCCEEDED(hr)) hr = MFCreateAttributes(&attributes, 4);
    if (SUCCEEDED(hr)) {
        attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);
        attributes->SetUnknown(MF_SINK_WRITER_D3D_MANAGER, manager_);
        // The fragmented MF sink rejects HEVC at BeginWriting on supported Windows
        // drivers. Keep H.264 fragmented; finalize HEVC with the standard MP4 sink.
        attributes->SetGUID(MF_TRANSCODE_CONTAINERTYPE, config.hevc ? MFTranscodeContainerType_MPEG4 : MFTranscodeContainerType_FMPEG4);
        hr = MFCreateSinkWriterFromURL(file.c_str(), nullptr, attributes.Get(), &writer_);
    }
    const double pixelsPerSecond = double(config.width) * config.height * config.fpsNum / std::max<uint32_t>(1, config.fpsDen);
    const UINT32 bitrate = config.bitrateMbps > 0 ? UINT32(config.bitrateMbps) * 1000000u
                                                  : UINT32(std::clamp(pixelsPerSecond * (config.hevc ? 0.07 : 0.11), 4e6, 150e6));
    ComPtr<IMFMediaType> out, in;
    if (SUCCEEDED(hr)) hr = videoType(&out, config.hevc ? MFVideoFormat_HEVC : MFVideoFormat_H264, config, bitrate);
    if (SUCCEEDED(hr)) hr = writer_->AddStream(out.Get(), &videoStream_);
    if (SUCCEEDED(hr)) hr = videoType(&in, MFVideoFormat_NV12, config, 0);
    if (SUCCEEDED(hr)) hr = writer_->SetInputMediaType(videoStream_, in.Get(), nullptr);
    if (FAILED(hr)) {
        error = std::string("video encoder unavailable for ") + (config.hevc ? "HEVC" : "H.264") + " " + std::to_string(config.width) + "x" +
                std::to_string(config.height) + " (" + hresult(hr) + ")";
        if (writer_) { writer_->Release(); writer_ = nullptr; DeleteFileW(file.c_str()); }
        if (manager_) { manager_->Release(); manager_ = nullptr; }
        for (auto& t : textures_) t.Reset();
        MFShutdown();
        return false;
    }
    if (config.audio) {
        ComPtr<IMFMediaType> aout, ain;
        hr = audioType(&aout, true);
        if (SUCCEEDED(hr)) hr = writer_->AddStream(aout.Get(), &audioStream_);
        if (SUCCEEDED(hr)) hr = audioType(&ain, false);
        if (SUCCEEDED(hr)) hr = writer_->SetInputMediaType(audioStream_, ain.Get(), nullptr);
        if (FAILED(hr)) config_.audio = false;
    }
    hr = writer_->BeginWriting();
    if (FAILED(hr)) { error = "recording could not start (" + hresult(hr) + ")"; writer_->Release(); writer_ = nullptr; manager_->Release(); manager_ = nullptr; for (auto& t : textures_) t.Reset(); DeleteFileW(file.c_str()); MFShutdown(); return false; }
    path_ = utf8(file); path = path_;
    origin_ = -1; lastVideo_ = -1; lastIndex_ = -1; heldSlot_ = -1; audioSamples_ = 0; resamplePos_ = 0; resampleTail_.clear();
    slotLength_ = 1e7 * double(config_.fpsDen) / double(std::max<uint32_t>(1, config_.fpsNum));
    dropped_ = 0; written_ = 0; error_.clear();
    stop_ = false; active_ = true;
    thread_ = std::thread([this] { run(); });
    return true;
}

void Recorder::release(int slot) {
    std::lock_guard<std::mutex> lock(slots_->mutex);
    if (slots_->uses[slot] > 0) --slots_->uses[slot];
}

void Recorder::addUse(int slot) {
    std::lock_guard<std::mutex> lock(slots_->mutex);
    ++slots_->uses[slot];
}

int Recorder::takeSlot() {
    std::lock_guard<std::mutex> lock(slots_->mutex);
    for (int i = 0; i < kSlots; ++i) if (slots_->uses[i] == 0) { slots_->uses[i] = 1; return i; }
    return -1;
}

// The encoder gets a strict constant-rate sequence: picture k at k * slot length. An
// empty slot repeats the previous picture, and a second picture in a slot is skipped.
// Handing the hardware encoder pictures 2 and 3 slots apart (48 fps content on a 120 fps
// grid) produced a file whose timestamps ran 4.4% short of the content time, 1.1 s shorter
// than its sound after 24 s; a regular sequence kept them exact.
bool Recorder::pushVideo(Gpu& gpu, ID3D11ShaderResourceView* picture, int64_t time) {
    if (!active_) return false;
    int64_t origin;
    { std::lock_guard<std::mutex> lock(mutex_); if (origin_ < 0) origin_ = time; origin = origin_; }
    const int64_t index = std::max<int64_t>(0, std::llround(double(time - origin) / slotLength_));
    if (lastIndex_ >= 0 && index <= lastIndex_) { ++dropped_; return false; }
    const int slot = takeSlot();
    if (slot < 0) { ++dropped_; return false; }
    if (!gpu.toNv12(picture, textures_[slot].Get())) { release(slot); ++dropped_; return false; }
    gpu.context()->Flush();
    // Empty grid slots repeat the previous picture as extra references to its texture, which
    // the recorder holds until the next picture. A repeat therefore never needs a free slot,
    // and a slow encoder start leaves no hole in the grid. (Repeats taking free slots dropped
    // 1470 new pictures at a 90 fps start; every hole shortened the file against its sound.)
    // Holes over ten seconds (a stopped capture) keep their gap.
    const int64_t missing = heldSlot_ >= 0 && index - lastIndex_ - 1 <= int64_t(1e8 / slotLength_) ? index - lastIndex_ - 1 : 0;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        for (int64_t k = 1; k <= missing; ++k) {
            addUse(heldSlot_);
            video_.push_back({heldSlot_, std::llround(double(lastIndex_ + k) * slotLength_)});
        }
        video_.push_back({slot, std::llround(double(index) * slotLength_)});
    }
    repeated_ += uint64_t(missing);
    if (heldSlot_ >= 0) release(heldSlot_);
    addUse(slot); heldSlot_ = slot;
    lastIndex_ = index;
    wake_.notify_one();
    return true;
}

void Recorder::pushAudio(const float* samples, int frames, int channels, int rate, int64_t time) {
    if (!active_ || !config_.audio || channels <= 0 || rate <= 0) return;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        if (origin_ < 0) return;          // sound starts with the first picture
    }
    // Stereo float at the source rate, then linear resampling to 48 kHz.
    std::vector<float> stereo(size_t(frames) * 2);
    for (int i = 0; i < frames; ++i) {
        stereo[size_t(i) * 2] = samples[size_t(i) * channels];
        stereo[size_t(i) * 2 + 1] = samples[size_t(i) * channels + (channels > 1 ? 1 : 0)];
    }
    AudioItem item; item.time = time;
    if (rate == 48000) {
        item.samples.resize(stereo.size());
        for (size_t i = 0; i < stereo.size(); ++i) item.samples[i] = int16_t(std::lround(std::clamp(stereo[i], -1.f, 1.f) * 32767.f));
    } else {
        std::vector<float> all(resampleTail_);
        all.insert(all.end(), stereo.begin(), stereo.end());
        const double step = double(rate) / 48000.0;
        const size_t available = all.size() / 2;
        while (resamplePos_ + 1 < double(available)) {
            size_t i = size_t(resamplePos_); double f = resamplePos_ - double(i);
            for (int ch = 0; ch < 2; ++ch) {
                float v = float(all[i * 2 + ch] * (1 - f) + all[(i + 1) * 2 + ch] * f);
                item.samples.push_back(int16_t(std::lround(std::clamp(v, -1.f, 1.f) * 32767.f)));
            }
            resamplePos_ += step;
        }
        size_t keep = size_t(resamplePos_);
        resampleTail_.assign(all.begin() + ptrdiff_t(std::min(keep, available) * 2), all.end());
        resamplePos_ -= double(keep);
    }
    {
        std::lock_guard<std::mutex> lock(mutex_);
        audio_.push_back(std::move(item));
        if (audio_.size() > 400) audio_.pop_front();
    }
    wake_.notify_one();
}

void Recorder::run() {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const int64_t frameDuration = std::llround(slotLength_);
    while (true) {
        std::deque<VideoItem> video; std::deque<AudioItem> audio; int64_t origin;
        {
            std::unique_lock<std::mutex> lock(mutex_);
            wake_.wait_for(lock, std::chrono::milliseconds(50), [&] { return stop_ || !video_.empty() || !audio_.empty(); });
            video.swap(video_); audio.swap(audio_); origin = origin_;
            if (stop_ && video.empty() && audio.empty()) break;
        }
        for (auto& v : video) {
            // Already on the constant-rate grid (pushVideo): a clean track that stays with the sound.
            const int64_t t = v.time;
            if (lastVideo_ >= 0 && t <= lastVideo_) { release(v.slot); ++dropped_; continue; }
            ComPtr<IMFMediaBuffer> buffer; ComPtr<IMF2DBuffer> buffer2d; ComPtr<IMFTrackedSample> tracked; ComPtr<IMFSample> sample;
            HRESULT hr = MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D), textures_[v.slot].Get(), 0, FALSE, &buffer);
            DWORD length = 0;
            if (SUCCEEDED(hr) && SUCCEEDED(buffer.As(&buffer2d)) && SUCCEEDED(buffer2d->GetContiguousLength(&length))) buffer->SetCurrentLength(length);
            if (SUCCEEDED(hr)) hr = MFCreateTrackedSample(&tracked);
            if (SUCCEEDED(hr)) hr = tracked.As(&sample);
            if (SUCCEEDED(hr)) hr = sample->AddBuffer(buffer.Get());
            if (SUCCEEDED(hr)) { sample->SetSampleTime(t); sample->SetSampleDuration(frameDuration); }
            bool handed = false;
            if (SUCCEEDED(hr)) {
                TrackedRelease* callback = new TrackedRelease(slots_, v.slot);
                hr = tracked->SetAllocator(callback, nullptr);
                callback->Release();
                handed = SUCCEEDED(hr);
            }
            if (SUCCEEDED(hr)) hr = writer_->WriteSample(videoStream_, sample.Get());
            if (!handed) release(v.slot);
            if (FAILED(hr)) { std::lock_guard<std::mutex> lock(mutex_); error_ = "video write " + hresult(hr); ++dropped_; continue; }
            lastVideo_ = t; ++written_;
        }
        if (config_.audio) for (auto& a : audio) {
            if (a.samples.empty()) continue;
            int64_t expected = a.time - origin;
            if (expected < 0) continue;
            int64_t written = audioSamples_ * 10000000 / 48000;
            std::vector<int16_t> pcm = std::move(a.samples);
            // Hold the track to the capture clock: fill gaps with silence, trim overlaps.
            if (expected - written > 200000) pcm.insert(pcm.begin(), size_t((expected - written) * 48000 / 10000000) * 2, int16_t(0));
            else if (written - expected > 200000) {
                size_t cut = std::min(pcm.size(), size_t((written - expected) * 48000 / 10000000) * 2);
                pcm.erase(pcm.begin(), pcm.begin() + ptrdiff_t(cut));
            }
            if (pcm.empty()) continue;
            ComPtr<IMFMediaBuffer> buffer; ComPtr<IMFSample> sample;
            const DWORD bytes = DWORD(pcm.size() * 2);
            if (FAILED(MFCreateMemoryBuffer(bytes, &buffer))) continue;
            BYTE* data = nullptr;
            if (SUCCEEDED(buffer->Lock(&data, nullptr, nullptr))) { memcpy(data, pcm.data(), bytes); buffer->Unlock(); buffer->SetCurrentLength(bytes); }
            if (FAILED(MFCreateSample(&sample))) continue;
            sample->AddBuffer(buffer.Get());
            const int64_t frames = int64_t(pcm.size() / 2);
            sample->SetSampleTime(audioSamples_ * 10000000 / 48000);
            sample->SetSampleDuration(frames * 10000000 / 48000);
            if (SUCCEEDED(writer_->WriteSample(audioStream_, sample.Get()))) audioSamples_ += frames;
        }
    }
    CoUninitialize();
}

void Recorder::stop() {
    if (!writer_) { active_ = false; return; }
    active_ = false;
    { std::lock_guard<std::mutex> lock(mutex_); stop_ = true; }
    wake_.notify_one();
    if (thread_.joinable()) thread_.join();
    HRESULT hr = writer_->Finalize();
    if (FAILED(hr)) { std::lock_guard<std::mutex> lock(mutex_); error_ = "finalize " + hresult(hr); }
    writer_->Release(); writer_ = nullptr;
    if (manager_) { manager_->Release(); manager_ = nullptr; }
    for (auto& t : textures_) t.Reset();
    heldSlot_ = -1;
    MFShutdown();
}

double Recorder::seconds() const { const auto last = lastVideo_.load(); return last > 0 ? double(last) / 1e7 : 0; }

std::string Recorder::lastError() { std::lock_guard<std::mutex> lock(mutex_); return error_; }

} // namespace fdl
