#include "capture.h"
#include "health.h"

#include <avrt.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mmdeviceapi.h>
#include <propvarutil.h>

#include <algorithm>
#include <cctype>
#include <condition_variable>
#include <chrono>
#include <set>

namespace fdl {
// Own only the outstanding sample/result, never a pointer to the capture session.
// Late MF callbacks can therefore finish safely after the consumer has stopped.
class ReaderCallback final : public IMFSourceReaderCallback {
public:
    STDMETHODIMP QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (id != __uuidof(IUnknown) && id != __uuidof(IMFSourceReaderCallback)) return E_NOINTERFACE;
        *out = static_cast<IMFSourceReaderCallback*>(this); AddRef(); return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs_; }
    STDMETHODIMP_(ULONG) Release() override { ULONG n = --refs_; if (!n) delete this; return n; }
    STDMETHODIMP OnReadSample(HRESULT hr, DWORD, DWORD flags, LONGLONG time, IMFSample* sample) override {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!cancelled_ && !(ready_ && FAILED(hr_))) { hr_ = hr; flags_ = flags; time_ = time; sample_ = sample; ready_ = true; wake_.notify_all(); }
        return S_OK;
    }
    STDMETHODIMP OnEvent(DWORD stream, IMFMediaEvent* event) override {
        HRESULT status = S_OK;
        if (event && SUCCEEDED(event->GetStatus(&status)) && FAILED(status)) OnReadSample(status, stream, MF_SOURCE_READERF_ERROR, 0, nullptr);
        return S_OK;
    }
    STDMETHODIMP OnFlush(DWORD) override { return S_OK; }
    void cancel() { std::lock_guard<std::mutex> lock(mutex_); cancelled_ = true; sample_.Reset(); wake_.notify_all(); }
    bool take(HRESULT& hr, DWORD& flags, LONGLONG& time, ComPtr<IMFSample>& sample) {
        std::unique_lock<std::mutex> lock(mutex_);
        wake_.wait_for(lock, std::chrono::milliseconds(100), [this] { return ready_ || cancelled_; });
        if (!ready_ || cancelled_) return false;
        hr = hr_; flags = flags_; time = time_; sample = std::move(sample_); ready_ = false; return true;
    }
private:
    std::atomic<ULONG> refs_{1};
    std::mutex mutex_; std::condition_variable wake_;
    bool ready_ = false, cancelled_ = false;
    HRESULT hr_ = S_OK; DWORD flags_ = 0; LONGLONG time_ = 0;
    ComPtr<IMFSample> sample_;
};
namespace {
// PKEY_Device_FriendlyName, declared here to avoid instantiating every GUID of the
// header in this translation unit.
const PROPERTYKEY kFriendlyName = {{0xa45c254e, 0xdf1c, 0x4efd, {0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0}}, 14};

PixelFormat fromSubtype(const GUID& g) {
    if (g == MFVideoFormat_NV12) return PixelFormat::NV12;
    if (g == MFVideoFormat_YUY2) return PixelFormat::YUY2;
    if (g == MFVideoFormat_UYVY) return PixelFormat::UYVY;
    if (g == MFVideoFormat_P010) return PixelFormat::P010;
    if (g == MFVideoFormat_RGB32 || g == MFVideoFormat_ARGB32) return PixelFormat::RGB32;
    return PixelFormat::Unknown;
}

std::string attributeString(IMFAttributes* a, const GUID& key) {
    WCHAR* value = nullptr; UINT32 length = 0;
    if (FAILED(a->GetAllocatedString(key, &value, &length)) || !value) return {};
    std::string out = utf8(std::wstring(value, length));
    CoTaskMemFree(value);
    return out;
}

bool readMode(IMFMediaType* type, int index, VideoMode& mode) {
    GUID major{}, subtype{};
    if (FAILED(type->GetGUID(MF_MT_MAJOR_TYPE, &major)) || major != MFMediaType_Video) return false;
    if (FAILED(type->GetGUID(MF_MT_SUBTYPE, &subtype))) return false;
    if (subtype == MFVideoFormat_MJPG) return false; // compressed modes are not offered
    UINT64 size = 0, rate = 0;
    if (FAILED(type->GetUINT64(MF_MT_FRAME_SIZE, &size))) return false;
    type->GetUINT64(MF_MT_FRAME_RATE, &rate);
    mode.width = int(size >> 32); mode.height = int(size & 0xffffffff);
    mode.fpsNum = uint32_t(rate >> 32); mode.fpsDen = std::max<uint32_t>(1, uint32_t(rate & 0xffffffff));
    mode.format = fromSubtype(subtype);
    mode.nativeIndex = index;
    return mode.width > 0 && mode.height > 0;
}

bool sameMode(const VideoMode& a, const VideoMode& b) {
    // Rates compare as fractions; drivers report 60000/1001 and 60/1 as different modes.
    return a.width == b.width && a.height == b.height && a.format == b.format &&
           uint64_t(a.fpsNum) * b.fpsDen == uint64_t(b.fpsNum) * a.fpsDen;
}

bool compatibleOutput(IMFMediaType* type, const VideoMode& native, PixelFormat output) {
    VideoMode actual;
    return readMode(type, -1, actual) && actual.width == native.width && actual.height == native.height && actual.format == output &&
        (!actual.fpsNum || uint64_t(actual.fpsNum) * native.fpsDen == uint64_t(native.fpsNum) * actual.fpsDen);
}

HRESULT openSource(const std::string& id, IMFMediaSource** source) {
    ComPtr<IMFAttributes> attributes;
    HRESULT hr = MFCreateAttributes(&attributes, 2);
    if (SUCCEEDED(hr)) hr = attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
    if (SUCCEEDED(hr)) hr = attributes->SetString(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, wide(id).c_str());
    if (SUCCEEDED(hr)) hr = MFCreateDeviceSource(attributes.Get(), source);
    return hr;
}

HRESULT createReader(IMFMediaSource* source, IMFSourceReaderCallback* callback, IMFSourceReader** reader) {
    ComPtr<IMFAttributes> attributes;
    HRESULT hr = MFCreateAttributes(&attributes, 2);
    // Native samples only: no converter may slip a different format or rate in.
    if (SUCCEEDED(hr)) hr = attributes->SetUINT32(MF_READWRITE_DISABLE_CONVERTERS, TRUE);
    if (SUCCEEDED(hr)) attributes->SetUINT32(MF_LOW_LATENCY, TRUE);
    if (SUCCEEDED(hr)) hr = attributes->SetUnknown(MF_SOURCE_READER_ASYNC_CALLBACK, callback);
    if (SUCCEEDED(hr)) hr = MFCreateSourceReaderFromMediaSource(source, attributes.Get(), reader);
    return hr;
}

HRESULT typeHandler(IMFMediaSource* source, IMFMediaTypeHandler** handler) {
    ComPtr<IMFPresentationDescriptor> pd; ComPtr<IMFStreamDescriptor> sd; BOOL selected = FALSE;
    HRESULT hr = source->CreatePresentationDescriptor(&pd);
    if (SUCCEEDED(hr)) hr = pd->GetStreamDescriptorByIndex(0, &selected, &sd);
    if (SUCCEEDED(hr)) hr = sd->GetMediaTypeHandler(handler);
    return hr;
}

struct MfSession {
    bool ok = false;
    MfSession() { ok = SUCCEEDED(MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET)); }
    ~MfSession() { if (ok) MFShutdown(); }
};

std::string lower(std::string s) { for (auto& c : s) c = char(std::tolower(static_cast<unsigned char>(c))); return s; }
} // namespace

const char* formatName(PixelFormat f) {
    switch (f) {
    case PixelFormat::NV12: return "NV12";
    case PixelFormat::YUY2: return "YUY2";
    case PixelFormat::UYVY: return "UYVY";
    case PixelFormat::P010: return "P010";
    case PixelFormat::RGB32: return "RGB32";
    default: return "other";
    }
}

std::vector<VideoDevice> listVideoDevices(bool withModes, bool allDirectShow) {
    std::vector<VideoDevice> devices;
    {
        MfSession mf;
        ComPtr<IMFAttributes> attributes;
        IMFActivate** activates = nullptr; UINT32 count = 0;
        if (mf.ok && SUCCEEDED(MFCreateAttributes(&attributes, 1)) &&
            SUCCEEDED(attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID)) &&
            SUCCEEDED(MFEnumDeviceSources(attributes.Get(), &activates, &count))) {
            for (UINT32 i = 0; i < count; ++i) {
                VideoDevice device;
                device.api = "mf";
                device.name = attributeString(activates[i], MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
                device.id = attributeString(activates[i], MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
                if (withModes) {
                    ComPtr<IMFMediaSource> source; ComPtr<IMFMediaTypeHandler> handler;
                    HRESULT hr = openSource(device.id, &source);
                    if (SUCCEEDED(hr)) hr = typeHandler(source.Get(), &handler);
                    if (SUCCEEDED(hr)) {
                        std::set<std::tuple<int, int, uint32_t, uint32_t, int>> seen;
                        DWORD types = 0; handler->GetMediaTypeCount(&types);
                        for (DWORD index = 0; index < types; ++index) {
                            ComPtr<IMFMediaType> type;
                            if (FAILED(handler->GetMediaTypeByIndex(index, &type))) continue;
                            VideoMode mode;
                            if (!readMode(type.Get(), int(index), mode)) continue;
                            auto key = std::make_tuple(mode.width, mode.height, mode.fpsNum, mode.fpsDen, int(mode.format));
                            if (seen.insert(key).second) device.modes.push_back(mode);
                        }
                    } else device.error = "open " + hresult(hr);
                    if (source) source->Shutdown();
                }
                devices.push_back(std::move(device));
                activates[i]->Release();
            }
            CoTaskMemFree(activates);
        }
    }
    // Capture cards whose drivers only register DirectShow filters are invisible to Media
    // Foundation; list them too, under their own API.
    for (auto& d : listDirectShowDevices(withModes)) {
        bool known = false;
        for (auto& m : devices) if (m.api == "mf" && lower(m.name) == lower(d.name)) known = true;
        if (!known || allDirectShow) devices.push_back(std::move(d));
    }
    return devices;
}

std::vector<AudioEndpoint> listAudioEndpoints(bool capture) {
    std::vector<AudioEndpoint> endpoints;
    ComPtr<IMMDeviceEnumerator> enumerator;
    if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)))) return endpoints;
    const EDataFlow flow = capture ? eCapture : eRender;
    std::wstring defaultId;
    ComPtr<IMMDevice> defaultDevice;
    if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(flow, eConsole, &defaultDevice))) {
        LPWSTR id = nullptr;
        if (SUCCEEDED(defaultDevice->GetId(&id))) { defaultId = id; CoTaskMemFree(id); }
    }
    ComPtr<IMMDeviceCollection> collection;
    if (FAILED(enumerator->EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, &collection))) return endpoints;
    UINT count = 0; collection->GetCount(&count);
    for (UINT i = 0; i < count; ++i) {
        ComPtr<IMMDevice> device;
        if (FAILED(collection->Item(i, &device))) continue;
        LPWSTR id = nullptr;
        if (FAILED(device->GetId(&id))) continue;
        AudioEndpoint e; e.id = utf8(id); e.isDefault = defaultId == id; CoTaskMemFree(id);
        ComPtr<IPropertyStore> store;
        if (SUCCEEDED(device->OpenPropertyStore(STGM_READ, &store))) {
            PROPVARIANT value; PropVariantInit(&value);
            if (SUCCEEDED(store->GetValue(kFriendlyName, &value)) && value.vt == VT_LPWSTR) e.name = utf8(value.pwszVal);
            PropVariantClear(&value);
        }
        endpoints.push_back(std::move(e));
    }
    return endpoints;
}

std::string devicesJson(const std::vector<VideoDevice>& video, const std::vector<AudioEndpoint>& audio) {
    std::string out = "{\"video\":[";
    for (size_t i = 0; i < video.size(); ++i) {
        const auto& d = video[i];
        if (i) out += ",";
        out += "{\"name\":" + jsonString(d.name) + ",\"id\":" + jsonString(d.id) + ",\"api\":" + jsonString(d.api) + ",\"error\":" + jsonString(d.error) + ",\"modes\":[";
        for (size_t m = 0; m < d.modes.size(); ++m) {
            const auto& v = d.modes[m];
            if (m) out += ",";
            out += "{\"width\":" + std::to_string(v.width) + ",\"height\":" + std::to_string(v.height) +
                   ",\"fpsNum\":" + std::to_string(v.fpsNum) + ",\"fpsDen\":" + std::to_string(v.fpsDen) +
                   ",\"format\":" + jsonString(formatName(v.format)) + ",\"supported\":" + (formatSupported(v.format) ? "true" : "false") + "}";
        }
        out += "]}";
    }
    out += "],\"audio\":[";
    for (size_t i = 0; i < audio.size(); ++i) {
        if (i) out += ",";
        out += "{\"id\":" + jsonString(audio[i].id) + ",\"name\":" + jsonString(audio[i].name) + ",\"default\":" + (audio[i].isDefault ? "true" : "false") + "}";
    }
    out += "],\"audioOutputs\":[";
    const auto outputs = listAudioEndpoints(false);
    for (size_t i = 0; i < outputs.size(); ++i) {
        if (i) out += ",";
        out += "{\"id\":" + jsonString(outputs[i].id) + ",\"name\":" + jsonString(outputs[i].name) + ",\"default\":" + (outputs[i].isDefault ? "true" : "false") + "}";
    }
    return out + "]}";
}

VideoCapture::~VideoCapture() { stop(); }

bool VideoCapture::open(const std::string& id, const VideoMode& wanted, std::string& error) {
    if (FAILED(MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET))) { error = "Media Foundation unavailable"; return false; }
    started_ = true;
    HRESULT hr = openSource(id, &source_);
    if (FAILED(hr)) { error = "capture device could not be opened (" + hresult(hr) + "); close other programs that use it"; return false; }
    // Select the exact native mode on the device itself, then read it unchanged.
    ComPtr<IMFMediaTypeHandler> handler;
    hr = typeHandler(source_, &handler);
    if (FAILED(hr)) { error = "device formats " + hresult(hr); return false; }
    ComPtr<IMFMediaType> native;
    DWORD types = 0; handler->GetMediaTypeCount(&types);
    for (DWORD index = 0; index < types && !native; ++index) {
        ComPtr<IMFMediaType> type; VideoMode mode;
        if (SUCCEEDED(handler->GetMediaTypeByIndex(index, &type)) && readMode(type.Get(), int(index), mode) && sameMode(mode, wanted)) { native = type; mode_ = mode; }
    }
    if (!native) { error = "the requested mode is not offered by this device"; return false; }
    hr = handler->SetCurrentMediaType(native.Get());
    if (FAILED(hr)) { error = "mode rejected by the driver (" + hresult(hr) + ")"; return false; }
    readerCallback_ = new ReaderCallback();
    hr = createReader(source_, readerCallback_, &reader_);
    if (FAILED(hr)) { error = "source reader " + hresult(hr); return false; }
    hr = reader_->SetCurrentMediaType(DWORD(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, native.Get());
    if (FAILED(hr)) { error = "mode rejected by the reader (" + hresult(hr) + ")"; return false; }
    output_ = mode_.format;
    return true;
}

void VideoCapture::start(Callback callback, EventLog* log) {
    callback_ = std::move(callback); log_ = log; stop_ = false; failed_ = false;
    thread_ = std::thread([this] { run(); });
}

void VideoCapture::stop() {
    stop_ = true;
    if (readerCallback_) readerCallback_->cancel();
    if (reader_) reader_->Flush(DWORD(MF_SOURCE_READER_ALL_STREAMS));
    if (thread_.joinable()) thread_.join();
    if (reader_) { reader_->Release(); reader_ = nullptr; }
    if (source_) { source_->Shutdown(); source_->Release(); source_ = nullptr; }
    if (readerCallback_) { readerCallback_->Release(); readerCallback_ = nullptr; }
    if (started_) { MFShutdown(); started_ = false; }
}

void VideoCapture::run() {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    DWORD taskIndex = 0;
    HANDLE task = AvSetMmThreadCharacteristicsW(L"Capture", &taskIndex);
    uint64_t sequence = 0;
    const int packedBytes = bytesPerPixel(output_);
    LONG fallbackPitch = LONG(mode_.width * packedBytes);
    ComPtr<IMFMediaType> outputType;
    UINT32 declaredStride = 0;
    if (SUCCEEDED(reader_->GetCurrentMediaType(DWORD(MF_SOURCE_READER_FIRST_VIDEO_STREAM), &outputType)) &&
        SUCCEEDED(outputType->GetUINT32(MF_MT_DEFAULT_STRIDE, &declaredStride)) && declaredStride)
        fallbackPitch = LONG(declaredStride);
    const int64_t began = now100ns(); int64_t lastPicture = 0;
    auto fail = [&](const std::string& message) { failed_ = true; if (log_) log_->add("error", message); };
    while (!stop_) {
        if (packetTimedOut(began, lastPicture, now100ns())) {
            fail(lastPicture ? "capture packets stopped; check the signal and restart" : "capture first frame timed out; check the signal and restart"); break;
        }
        DWORD flags = 0; LONGLONG time = 0;
        ComPtr<IMFSample> sample;
        HRESULT hr = reader_->ReadSample(DWORD(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0, nullptr, nullptr, nullptr, nullptr);
        if (FAILED(hr)) { if (!stop_) fail("capture request failed: " + hresult(hr)); break; }
        bool received = false;
        while (!stop_ && !(received = readerCallback_->take(hr, flags, time, sample))) {
            if (packetTimedOut(began, lastPicture, now100ns())) {
                fail(lastPicture ? "capture packets stopped; check the signal and restart" : "capture first frame timed out; check the signal and restart");
                break;
            }
        }
        const int64_t arrival = now100ns();
        if (stop_ || !received) break;
        if (FAILED(hr) || (flags & MF_SOURCE_READERF_ERROR)) {
            if (log_) log_->add("error", "capture stopped: " + hresult(hr));
            failed_ = true; break;
        }
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) { if (log_) log_->add("error", "capture ended"); failed_ = true; break; }
        if (flags & (MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED | MF_SOURCE_READERF_NATIVEMEDIATYPECHANGED)) {
            ComPtr<IMFMediaType> type;
            if (FAILED(reader_->GetCurrentMediaType(DWORD(MF_SOURCE_READER_FIRST_VIDEO_STREAM), &type)) || !compatibleOutput(type.Get(), mode_, output_)) {
                fail("capture format changed; select the new mode and restart"); break;
            }
            UINT32 stride = 0;
            if (SUCCEEDED(type->GetUINT32(MF_MT_DEFAULT_STRIDE, &stride)) && stride) fallbackPitch = LONG(stride);
        }
        if (!sample) {
            if (packetTimedOut(began, lastPicture, arrival)) {
                fail(lastPicture ? "capture packets stopped; check the signal and restart" : "capture first frame timed out; check the signal and restart"); break;
            }
            continue;
        }
        ComPtr<IMFMediaBuffer> buffer;
        if (FAILED(sample->ConvertToContiguousBuffer(&buffer))) continue;
        ComPtr<IMF2DBuffer> buffer2d;
        BYTE* data = nullptr; LONG pitch = 0; DWORD length = 0;
        bool locked2d = SUCCEEDED(buffer.As(&buffer2d)) && SUCCEEDED(buffer2d->Lock2D(&data, &pitch));
        if (!locked2d) {
            if (FAILED(buffer->Lock(&data, nullptr, &length))) continue;
            pitch = fallbackPitch;
            if (pitch < 0) data += ptrdiff_t(mode_.height - 1) * -ptrdiff_t(pitch);
        }
        DWORD total = 0; buffer->GetCurrentLength(&total);
        const size_t minimum = output_ == PixelFormat::NV12 ? size_t(std::abs(pitch)) * mode_.height * 3 / 2 : size_t(std::abs(pitch)) * (mode_.height - 1) + size_t(mode_.width) * packedBytes;
        const bool valid = std::abs(pitch) >= mode_.width * packedBytes && total >= minimum && (output_ != PixelFormat::NV12 || pitch > 0);
        if (valid) {
            FrameView view;
            view.data = data; view.pitch = int(pitch); view.width = mode_.width; view.height = mode_.height;
            view.format = output_;
            if (output_ == PixelFormat::NV12 && pitch > 0) {
                // Some drivers pad the luma plane (1080 -> 1088 rows); the chroma plane then
                // starts after the padded rows. The buffer length tells which.
                DWORD total = 0; buffer->GetCurrentLength(&total);
                const DWORD rows = total / DWORD(pitch);
                const int lumaRows = (rows % 3 == 0 && int(rows / 3 * 2) >= mode_.height) ? int(rows / 3 * 2) : mode_.height;
                view.chroma = data + size_t(lumaRows) * size_t(pitch);
            }
            view.sampleTime = time; view.arrival = arrival; view.sequence = ++sequence;
            lastPicture = arrival;
            callback_(view);
        }
        if (locked2d) buffer2d->Unlock2D(); else buffer->Unlock();
        if (!valid) { fail("capture returned a truncated frame or invalid stride"); break; }
    }
    readerCallback_->cancel();
    reader_->Flush(DWORD(MF_SOURCE_READER_ALL_STREAMS));
    if (task) AvRevertMmThreadCharacteristics(task);
    CoUninitialize();
}

bool captureCallbackSelfTest() {
    MfSession mf;
    ComPtr<IMFSample> source;
    if (!mf.ok || FAILED(MFCreateSample(&source))) return false;
    auto* callback = new ReaderCallback();
    HRESULT hr = E_FAIL; DWORD flags = 0; LONGLONG time = 0; ComPtr<IMFSample> received;
    callback->OnReadSample(S_OK, 0, MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED, 123, source.Get());
    bool ok = callback->take(hr, flags, time, received) && hr == S_OK && time == 123 && received.Get() == source.Get() && flags == MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED;
    callback->OnReadSample(E_FAIL, 0, MF_SOURCE_READERF_ERROR, 0, nullptr);
    callback->OnReadSample(S_OK, 0, 0, 124, source.Get());
    ok = callback->take(hr, flags, time, received) && hr == E_FAIL && !received && ok;
    callback->cancel();
    callback->OnReadSample(S_OK, 0, 0, 125, source.Get());
    ok = !callback->take(hr, flags, time, received) && ok;
    callback->Release();
    ComPtr<IMFMediaType> type;
    if (FAILED(MFCreateMediaType(&type))) return false;
    type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video); type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    type->SetUINT64(MF_MT_FRAME_SIZE, (UINT64(1920) << 32) | 1080); type->SetUINT64(MF_MT_FRAME_RATE, UINT64(60) << 32 | 1);
    VideoMode native; native.width = 1920; native.height = 1080; native.fpsNum = 60; native.fpsDen = 1;
    ok = compatibleOutput(type.Get(), native, PixelFormat::NV12) && ok;
    type->SetUINT64(MF_MT_FRAME_SIZE, (UINT64(3840) << 32) | 2160);
    ok = !compatibleOutput(type.Get(), native, PixelFormat::NV12) && ok;
    type->SetUINT64(MF_MT_FRAME_SIZE, (UINT64(1920) << 32) | 1080); type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_P010);
    ok = !compatibleOutput(type.Get(), native, PixelFormat::NV12) && ok;
    type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12); type->SetUINT64(MF_MT_FRAME_RATE, UINT64(30) << 32 | 1);
    ok = !compatibleOutput(type.Get(), native, PixelFormat::NV12) && ok;
    type->SetUINT64(MF_MT_FRAME_RATE, 0);
    ok = compatibleOutput(type.Get(), native, PixelFormat::NV12) && ok; // Missing metadata is not evidence of a rate change.
    return ok;
}
} // namespace fdl
