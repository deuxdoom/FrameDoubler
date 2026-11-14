#include "audio.h"
#include "health.h"

#include <audioclient.h>
#include <avrt.h>
#include <ksmedia.h>
#include <mmdeviceapi.h>
#include <propvarutil.h>

#include <algorithm>
#include <cmath>
#include <cstring>

namespace fdl {
namespace {
// KSDATAFORMAT_SUBTYPE_IEEE_FLOAT and KSDATAFORMAT_SUBTYPE_PCM, defined locally so no
// import library has to provide them.
const GUID kSubtypeFloat = {0x00000003, 0x0000, 0x0010, {0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71}};
const GUID kSubtypePcm = {0x00000001, 0x0000, 0x0010, {0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71}};
enum class SampleKind { Float32, Int16, Int24In32, Int32, Unknown };

SampleKind kindOf(const WAVEFORMATEX* f) {
    WORD tag = f->wFormatTag;
    WORD valid = f->wBitsPerSample;
    if (tag == WAVE_FORMAT_EXTENSIBLE) {
        auto* x = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(f);
        if (x->SubFormat == kSubtypeFloat) tag = WAVE_FORMAT_IEEE_FLOAT;
        else if (x->SubFormat == kSubtypePcm) tag = WAVE_FORMAT_PCM;
        valid = x->Samples.wValidBitsPerSample ? x->Samples.wValidBitsPerSample : f->wBitsPerSample;
    }
    if (tag == WAVE_FORMAT_IEEE_FLOAT && f->wBitsPerSample == 32) return SampleKind::Float32;
    if (tag == WAVE_FORMAT_PCM && f->wBitsPerSample == 16) return SampleKind::Int16;
    if (tag == WAVE_FORMAT_PCM && f->wBitsPerSample == 32) return valid == 24 ? SampleKind::Int24In32 : SampleKind::Int32;
    return SampleKind::Unknown;
}

void toFloat(const BYTE* in, SampleKind kind, size_t count, float* out) {
    switch (kind) {
    case SampleKind::Float32: memcpy(out, in, count * 4); break;
    case SampleKind::Int16: for (size_t i = 0; i < count; ++i) out[i] = reinterpret_cast<const int16_t*>(in)[i] / 32768.f; break;
    case SampleKind::Int24In32:
    case SampleKind::Int32: for (size_t i = 0; i < count; ++i) out[i] = float(reinterpret_cast<const int32_t*>(in)[i] / 2147483648.0); break;
    default: memset(out, 0, count * 4);
    }
}

HRESULT activate(IMMDeviceEnumerator* e, const std::string& id, EDataFlow flow, IAudioClient** client) {
    ComPtr<IMMDevice> device;
    HRESULT hr = id.empty() ? e->GetDefaultAudioEndpoint(flow, eConsole, &device) : e->GetDevice(wide(id).c_str(), &device);
    if (FAILED(hr)) return hr;
    return device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(client));
}
} // namespace

struct AudioRoute {
    std::mutex mutex;
    std::string requested, active;
    uint64_t revision = 1;
};

class OutputNotifications final : public IMMNotificationClient {
public:
    explicit OutputNotifications(std::shared_ptr<AudioRoute> route) : route_(std::move(route)) {}
    STDMETHODIMP QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (id != __uuidof(IUnknown) && id != __uuidof(IMMNotificationClient)) return E_NOINTERFACE;
        *out = static_cast<IMMNotificationClient*>(this); AddRef(); return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs_; }
    STDMETHODIMP_(ULONG) Release() override { ULONG n = --refs_; if (!n) delete this; return n; }
    STDMETHODIMP OnDefaultDeviceChanged(EDataFlow flow, ERole role, LPCWSTR) override {
        if (flow == eRender && role == eConsole) { std::lock_guard<std::mutex> lock(route_->mutex); if (route_->requested.empty()) ++route_->revision; }
        return S_OK;
    }
    STDMETHODIMP OnDeviceStateChanged(LPCWSTR id, DWORD) override { changed(id, false); return S_OK; }
    STDMETHODIMP OnDeviceAdded(LPCWSTR id) override { changed(id, true); return S_OK; }
    STDMETHODIMP OnDeviceRemoved(LPCWSTR id) override { changed(id, false); return S_OK; }
    STDMETHODIMP OnPropertyValueChanged(LPCWSTR, const PROPERTYKEY) override { return S_OK; }
private:
    void changed(LPCWSTR id, bool added) {
        const auto key = id ? utf8(id) : std::string();
        std::lock_guard<std::mutex> lock(route_->mutex);
        if (key == route_->active || key == route_->requested || (added && route_->active.empty() && route_->requested.empty())) ++route_->revision;
    }
    std::atomic<ULONG> refs_{1};
    std::shared_ptr<AudioRoute> route_;
};

bool AudioPassthrough::start(const std::string& captureId, const std::string& renderId, int delayMs, std::string& error, EventLog* log) {
    stop();
    log_ = log; delayMs_ = delayMs; stop_ = false; startError_.clear();
    inputPeak_ = 0; outputPeak_ = 0; inputFrames_ = 0; outputFrames_ = 0;
    route_ = std::make_shared<AudioRoute>(); route_->requested = renderId;
    { std::lock_guard<std::mutex> lock(stateMutex_); state_ = {}; state_.input = "waiting"; state_.output = "waiting"; }
    ready_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!ready_) { error = "audio startup event failed"; return false; }
    thread_ = std::thread([this, captureId, renderId] { run(captureId, renderId); });
    const auto wait = WaitForSingleObject(ready_, 5000);
    if (wait != WAIT_OBJECT_0) { error = "audio startup timed out"; stop(); return false; }
    if (!running_) { error = startError_.empty() ? "audio did not start" : startError_; stop(); return false; }
    return true;
}

void AudioPassthrough::selectOutput(const std::string& renderId) {
    if (!route_) return;
    std::lock_guard<std::mutex> lock(route_->mutex);
    if (route_->requested != renderId) { route_->requested = renderId; ++route_->revision; }
}

void AudioPassthrough::stop() {
    stop_ = true;
    if (thread_.joinable()) thread_.join();
    if (ready_) { CloseHandle(ready_); ready_ = nullptr; }
    running_ = false;
}

void AudioPassthrough::run(std::string captureId, std::string) {
    const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    DWORD taskIndex = 0;
    HANDLE task = AvSetMmThreadCharacteristicsW(L"Pro Audio", &taskIndex);
    bool signalled = false;
    auto process = [&]() -> std::string {
        if (FAILED(com)) return "audio COM initialization " + hresult(com);
        ComPtr<IMMDeviceEnumerator> enumerator;
        ComPtr<IAudioClient> capture, render;
        ComPtr<IAudioCaptureClient> captureClient;
        ComPtr<IAudioRenderClient> renderClient;
        HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator));
        if (SUCCEEDED(hr)) hr = activate(enumerator.Get(), captureId, eCapture, &capture);
        if (FAILED(hr)) return "audio input could not be opened (" + hresult(hr) + ")";
        WAVEFORMATEX* rawMix = nullptr;
        hr = capture->GetMixFormat(&rawMix);
        const auto freeMix = [](WAVEFORMATEX* p) { if (p) CoTaskMemFree(p); };
        std::unique_ptr<WAVEFORMATEX, decltype(freeMix)> mix(rawMix, freeMix);
        if (FAILED(hr) || !mix) return "audio input format " + hresult(hr);
        const SampleKind kind = kindOf(mix.get());
        if (kind == SampleKind::Unknown) return "unsupported audio input format";
        HANDLE event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!event) return "audio capture event failed";
        struct EventCloser { HANDLE h; ~EventCloser() { CloseHandle(h); } } closeEvent{event};
        hr = capture->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 1000000, 0, mix.get(), nullptr);
        if (SUCCEEDED(hr)) hr = capture->SetEventHandle(event);
        if (SUCCEEDED(hr)) hr = capture->GetService(IID_PPV_ARGS(&captureClient));
        if (FAILED(hr)) return "audio input " + hresult(hr);
        const int channels = mix->nChannels, rate = int(mix->nSamplesPerSec);
        channels_ = channels; rate_ = rate;
        WAVEFORMATEXTENSIBLE format{};
        format.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE; format.Format.nChannels = WORD(channels); format.Format.nSamplesPerSec = DWORD(rate);
        format.Format.wBitsPerSample = 32; format.Format.nBlockAlign = WORD(4 * channels); format.Format.nAvgBytesPerSec = format.Format.nBlockAlign * rate;
        format.Format.cbSize = 22; format.Samples.wValidBitsPerSample = 32; format.SubFormat = kSubtypeFloat;
        format.dwChannelMask = mix->wFormatTag == WAVE_FORMAT_EXTENSIBLE ? reinterpret_cast<WAVEFORMATEXTENSIBLE*>(mix.get())->dwChannelMask
                                                                      : (channels == 2 ? SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT : 0);
        ComPtr<OutputNotifications> notifications;
        notifications.Attach(new OutputNotifications(route_));
        hr = enumerator->RegisterEndpointNotificationCallback(notifications.Get());
        const bool registered = SUCCEEDED(hr);
        if (!registered && log_) log_->add("warning", "audio output notifications unavailable: " + hresult(hr));
        struct Unregister { IMMDeviceEnumerator* e; IMMNotificationClient* n; bool active; ~Unregister() { if (active) e->UnregisterEndpointNotificationCallback(n); } } unregister{enumerator.Get(), notifications.Get(), registered};
        UINT32 renderFrames = 0;
        uint64_t revision = 0; int attempts = 0;
        int64_t nextAttempt = 0, outputBegan = 0;
        auto closeOutput = [&] {
            if (render) render->Stop();
            renderClient.Reset(); render.Reset(); renderFrames = 0; outputPeak_ = 0;
            { std::lock_guard<std::mutex> lock(route_->mutex); route_->active.clear(); }
            std::lock_guard<std::mutex> lock(stateMutex_); state_.queueMs = 0; state_.outputId.clear(); state_.outputName.clear(); state_.bufferMs = 0;
        };
        auto openOutput = [&](const std::string& id) -> HRESULT {
            closeOutput();
            ComPtr<IMMDevice> device;
            HRESULT result = id.empty() ? enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device) : enumerator->GetDevice(wide(id).c_str(), &device);
            if (FAILED(result)) return result;
            DWORD state = 0;
            if (FAILED(result = device->GetState(&state))) return result;
            if (!(state & DEVICE_STATE_ACTIVE)) return AUDCLNT_E_DEVICE_INVALIDATED;
            result = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(render.GetAddressOf()));
            if (SUCCEEDED(result)) result = render->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY, 2000000, 0, &format.Format, nullptr);
            if (SUCCEEDED(result)) result = render->GetService(IID_PPV_ARGS(&renderClient));
            if (SUCCEEDED(result)) result = render->GetBufferSize(&renderFrames);
            BYTE* silence = nullptr;
            const UINT32 prefill = std::min<UINT32>(renderFrames, UINT32(int64_t(delayMs_) * rate / 1000));
            if (SUCCEEDED(result) && prefill) {
                result = renderClient->GetBuffer(prefill, &silence);
                if (SUCCEEDED(result)) result = renderClient->ReleaseBuffer(prefill, AUDCLNT_BUFFERFLAGS_SILENT);
            }
            if (SUCCEEDED(result)) result = render->Start();
            if (FAILED(result)) { closeOutput(); return result; }
            LPWSTR actual = nullptr; std::string actualId;
            if (SUCCEEDED(device->GetId(&actual))) { actualId = utf8(actual); CoTaskMemFree(actual); }
            std::string name; ComPtr<IPropertyStore> store;
            const PROPERTYKEY friendly = {{0xa45c254e,0xdf1c,0x4efd,{0x80,0x20,0x67,0xd1,0x46,0xa8,0x50,0xe0}},14};
            if (SUCCEEDED(device->OpenPropertyStore(STGM_READ, &store))) {
                PROPVARIANT value; PropVariantInit(&value);
                if (SUCCEEDED(store->GetValue(friendly, &value)) && value.vt == VT_LPWSTR) name = utf8(value.pwszVal);
                PropVariantClear(&value);
            }
            { std::lock_guard<std::mutex> lock(route_->mutex); route_->active = actualId; }
            { std::lock_guard<std::mutex> lock(stateMutex_); state_.outputId = actualId; state_.outputName = name; state_.output = "active"; state_.bufferMs = int(int64_t(renderFrames) * 1000 / rate); }
            outputBegan = now100ns();
            if (log_) log_->add("audio-output", "ready");
            return S_OK;
        };
        auto outputFailed = [&](HRESULT error) {
            closeOutput(); nextAttempt = now100ns() + 5000000 * std::max(1, attempts);
            { std::lock_guard<std::mutex> lock(stateMutex_); state_.output = attempts < 3 ? "recovering" : "failed"; }
            if (log_) log_->add("audio-output", std::string(attempts < 3 ? "recovering " : "failed ") + hresult(error));
        };
        hr = capture->Start();
        if (FAILED(hr)) return "audio input stream start " + hresult(hr);
        running_ = true; signalled = true; SetEvent(ready_);
        const int64_t began = now100ns(); int64_t lastPacket = 0;
        std::vector<float> buffer;
        while (!stop_) {
            uint64_t selected; std::string requested;
            { std::lock_guard<std::mutex> lock(route_->mutex); selected = route_->revision; requested = route_->requested; }
            if (selected != revision) { revision = selected; attempts = 0; nextAttempt = 0; closeOutput(); }
            if (!renderClient && attempts < 3 && now100ns() >= nextAttempt) {
                ++attempts;
                { std::lock_guard<std::mutex> lock(stateMutex_); ++state_.retries; state_.output = "recovering"; }
                hr = openOutput(requested);
                if (FAILED(hr)) outputFailed(hr);
            }
            if (WaitForSingleObject(event, 100) == WAIT_FAILED) { hr = E_FAIL; break; }
            for (;;) {
                BYTE* data = nullptr; UINT32 frames = 0; DWORD flags = 0; UINT64 position = 0, qpc = 0;
                hr = captureClient->GetBuffer(&data, &frames, &flags, &position, &qpc);
                if (hr == AUDCLNT_S_BUFFER_EMPTY) { hr = S_OK; break; }
                if (FAILED(hr)) break;
                if (!frames) { hr = captureClient->ReleaseBuffer(0); break; }
                buffer.resize(size_t(frames) * channels);
                if (flags & AUDCLNT_BUFFERFLAGS_SILENT) std::fill(buffer.begin(), buffer.end(), 0.f);
                else if (data) toFloat(data, kind, buffer.size(), buffer.data());
                else { captureClient->ReleaseBuffer(frames); hr = E_POINTER; break; }
                hr = captureClient->ReleaseBuffer(frames);
                if (FAILED(hr)) break;
                lastPacket = now100ns();
                float peak = 0; for (float sample : buffer) peak = std::max(peak, std::abs(sample));
                inputPeak_ = peak; inputFrames_ += frames;
                { std::lock_guard<std::mutex> lock(stateMutex_); state_.input = peak > 0.00001f ? "active" : "silent"; }
                const int64_t time = (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) || !qpc ? lastPacket : int64_t(qpc);
                { std::lock_guard<std::mutex> lock(tapMutex_); if (tap_) tap_(buffer.data(), int(frames), channels, rate, time); }
                if (!renderClient) continue; // Preserve capture/recording during output recovery.
                const float gain = mute_ ? 0.f : volume_.load();
                if (gain != 1.f) for (auto& sample : buffer) sample *= gain;
                UINT32 padding = 0;
                HRESULT outResult = render->GetCurrentPadding(&padding);
                if (SUCCEEDED(outResult) && padding > renderFrames) outResult = E_UNEXPECTED;
                const UINT32 target = UINT32(int64_t(delayMs_) * rate / 1000);
                UINT32 skip = 0;
                if (SUCCEEDED(outResult)) {
                    if (padding > target + UINT32(rate / 25)) skip = std::min(frames, padding - target);
                    if (padding < UINT32(rate / 200) && target > padding) {
                        if (padding == 0) { std::lock_guard<std::mutex> lock(stateMutex_); ++state_.underruns; }
                        const auto silenceCount = std::min(renderFrames - padding, target - padding);
                        BYTE* out = nullptr;
                        if (silenceCount) {
                            outResult = renderClient->GetBuffer(silenceCount, &out);
                            if (SUCCEEDED(outResult)) outResult = renderClient->ReleaseBuffer(silenceCount, AUDCLNT_BUFFERFLAGS_SILENT);
                        }
                    }
                    if (SUCCEEDED(outResult)) outResult = render->GetCurrentPadding(&padding);
                }
                if (SUCCEEDED(outResult) && padding > renderFrames) outResult = E_UNEXPECTED;
                if (SUCCEEDED(outResult)) {
                    const UINT32 count = std::min(frames - skip, renderFrames - padding);
                    BYTE* out = nullptr;
                    if (count) {
                        outResult = renderClient->GetBuffer(count, &out);
                        if (SUCCEEDED(outResult)) {
                            memcpy(out, buffer.data() + size_t(skip) * channels, size_t(count) * channels * 4);
                            outResult = renderClient->ReleaseBuffer(count, 0);
                            if (SUCCEEDED(outResult)) {
                                float outputPeak = 0;
                                for (size_t i = size_t(skip)*channels; i < size_t(skip+count)*channels; ++i) outputPeak = std::max(outputPeak, std::abs(buffer[i]));
                                outputPeak_ = outputPeak; outputFrames_ += count;
                                { std::lock_guard<std::mutex> lock(stateMutex_); state_.output = outputPeak > 0.00001f ? "active" : "silent"; state_.queueMs = int(int64_t(padding + count) * 1000 / rate); }
                                if (now100ns() - outputBegan > 50000000) attempts = 0;
                            }
                        }
                    }
                }
                if (FAILED(outResult)) outputFailed(outResult);
            }
            if (FAILED(hr)) {
                { std::lock_guard<std::mutex> lock(stateMutex_); state_.input = "failed"; }
                if (log_) log_->add("warning", "audio input stopped: " + hresult(hr));
                break;
            }
            if (packetTimedOut(began, lastPacket, now100ns(), 2000, 2000)) {
                { std::lock_guard<std::mutex> lock(stateMutex_); state_.input = "stalled"; }
                inputPeak_ = 0;
                if (log_) log_->add("warning", "audio packets stopped; video continues, restart the preview to retry audio");
                break;
            }
        }
        capture->Stop(); closeOutput();
        return {};
    };
    const auto error = process();
    if (!signalled) {
        startError_ = error;
        { std::lock_guard<std::mutex> lock(stateMutex_); state_.input = "failed"; state_.output = "off"; }
        SetEvent(ready_);
    }
    running_ = false;
    if (task) AvRevertMmThreadCharacteristics(task);
    if (SUCCEEDED(com)) CoUninitialize();
}

bool audioRoutingSelfTest() {
    auto route = std::make_shared<AudioRoute>();
    route->active = "speaker";
    auto* callback = new OutputNotifications(route);
    callback->OnDefaultDeviceChanged(eCapture, eConsole, L"microphone");
    callback->OnDefaultDeviceChanged(eRender, eCommunications, L"communications");
    bool ok = route->revision == 1;
    callback->OnDefaultDeviceChanged(eRender, eConsole, L"headset");
    ok = route->revision == 2 && ok;
    route->requested = "pinned";
    callback->OnDefaultDeviceChanged(eRender, eConsole, L"other");
    callback->OnDeviceRemoved(L"unrelated");
    ok = route->revision == 2 && ok;
    callback->OnDeviceRemoved(L"pinned");
    ok = route->revision == 3 && ok;
    callback->Release();
    return ok;
}
} // namespace fdl
