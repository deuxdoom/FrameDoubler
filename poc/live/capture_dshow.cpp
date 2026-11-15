// DirectShow capture for devices whose drivers only register DirectShow filters (older PCIe
// cards, some professional cards and virtual cameras). Graph: device -> Sample Grabber ->
// Null Renderer, without a reference clock so samples are delivered as they arrive.
#include "capture.h"
#include "health.h"

#include <dshow.h>
#include <dvdmedia.h>

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <numeric>

namespace fdl {
namespace {
// Sample Grabber (qedit.dll, part of Windows): declared locally from its public interface.
const GUID kClsidSampleGrabber = {0xC1F400A0, 0x3F08, 0x11d3, {0x9F, 0x0B, 0x00, 0x60, 0x08, 0x03, 0x9E, 0x37}};
const GUID kClsidNullRenderer = {0xC1F400A4, 0x3F08, 0x11d3, {0x9F, 0x0B, 0x00, 0x60, 0x08, 0x03, 0x9E, 0x37}};
const GUID kIidSampleGrabber = {0x6B652FFF, 0x11FE, 0x4fce, {0x92, 0xAD, 0x02, 0x66, 0xB5, 0xD7, 0xC7, 0x8F}};
const GUID kIidSampleGrabberCB = {0x0579154A, 0x2B53, 0x4994, {0xB0, 0xD0, 0xE7, 0x73, 0x14, 0x8E, 0xFF, 0x85}};
struct IGrabberCallback : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE SampleCB(double time, IMediaSample* sample) = 0;
    virtual HRESULT STDMETHODCALLTYPE BufferCB(double time, BYTE* buffer, long length) = 0;
};
struct IGrabber : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE SetOneShot(BOOL) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetMediaType(const AM_MEDIA_TYPE*) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetConnectedMediaType(AM_MEDIA_TYPE*) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetBufferSamples(BOOL) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetCurrentBuffer(long*, long*) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetCurrentSample(IMediaSample**) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetCallback(IGrabberCallback*, long) = 0;
};

GUID fourcc(const char* code) {
    GUID g = {0, 0x0000, 0x0010, {0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71}};
    g.Data1 = DWORD(uint8_t(code[0])) | DWORD(uint8_t(code[1])) << 8 | DWORD(uint8_t(code[2])) << 16 | DWORD(uint8_t(code[3])) << 24;
    return g;
}
const GUID kRgb32 = {0xe436eb7e, 0x524f, 0x11ce, {0x9f, 0x53, 0x00, 0x20, 0xaf, 0x0b, 0xa7, 0x70}};
const GUID kArgb32 = {0x773c9ac0, 0x3274, 0x11d0, {0xb7, 0x24, 0x00, 0xaa, 0x00, 0x6c, 0x1a, 0x01}};
const GUID kRgb24 = {0xe436eb7d, 0x524f, 0x11ce, {0x9f, 0x53, 0x00, 0x20, 0xaf, 0x0b, 0xa7, 0x70}};

PixelFormat fromSubtype(const GUID& g) {
    if (g == fourcc("NV12")) return PixelFormat::NV12;
    if (g == fourcc("YUY2")) return PixelFormat::YUY2;
    if (g == fourcc("UYVY") || g == fourcc("HDYC")) return PixelFormat::UYVY;
    if (g == fourcc("P010")) return PixelFormat::P010;
    if (g == kRgb32 || g == kArgb32) return PixelFormat::RGB32;
    if (g == fourcc("I420") || g == fourcc("IYUV")) return PixelFormat::I420;
    if (g == fourcc("YV12")) return PixelFormat::YV12;
    if (g == kRgb24) return PixelFormat::RGB24;
    return PixelFormat::Unknown;
}
GUID toSubtype(PixelFormat f) {
    switch (f) {
    case PixelFormat::NV12: return fourcc("NV12");
    case PixelFormat::UYVY: return fourcc("UYVY");
    case PixelFormat::RGB32: return kRgb32;
    case PixelFormat::I420: return fourcc("I420");
    case PixelFormat::YV12: return fourcc("YV12");
    case PixelFormat::RGB24: return kRgb24;
    default: return fourcc("YUY2");
    }
}

void freeType(AM_MEDIA_TYPE* t) {
    if (!t) return;
    if (t->cbFormat && t->pbFormat) CoTaskMemFree(t->pbFormat);
    if (t->pUnk) t->pUnk->Release();
    t->cbFormat = 0; t->pbFormat = nullptr; t->pUnk = nullptr;
}
void deleteType(AM_MEDIA_TYPE* t) { if (t) { freeType(t); CoTaskMemFree(t); } }

const BITMAPINFOHEADER* header(const AM_MEDIA_TYPE* t, REFERENCE_TIME* interval) {
    if (t->formattype == FORMAT_VideoInfo && t->cbFormat >= sizeof(VIDEOINFOHEADER)) {
        auto* v = reinterpret_cast<const VIDEOINFOHEADER*>(t->pbFormat); if (interval) *interval = v->AvgTimePerFrame; return &v->bmiHeader;
    }
    if (t->formattype == FORMAT_VideoInfo2 && t->cbFormat >= sizeof(VIDEOINFOHEADER2)) {
        auto* v = reinterpret_cast<const VIDEOINFOHEADER2*>(t->pbFormat); if (interval) *interval = v->AvgTimePerFrame; return &v->bmiHeader;
    }
    return nullptr;
}

// Frame intervals become exact rates where they match a broadcast rate.
void rate(REFERENCE_TIME interval, uint32_t& num, uint32_t& den) {
    num = 0; den = 1;
    if (interval <= 0) return;
    const double fps = 1e7 / double(interval);
    const struct { uint32_t n, d; } known[] = {{24000, 1001}, {24, 1}, {25, 1}, {30000, 1001}, {30, 1}, {50, 1}, {60000, 1001}, {60, 1}, {100, 1}, {120000, 1001}, {120, 1}, {144, 1}, {165, 1}, {240, 1}};
    for (auto k : known) if (std::abs(fps - double(k.n) / k.d) < 0.006 * fps / 60 + 0.004) { num = k.n; den = k.d; return; }
    num = 10000000; den = uint32_t(interval);
    const uint32_t g = std::gcd(num, den); num /= g; den /= g;
}

std::string bagString(IPropertyBag* bag, const wchar_t* name) {
    VARIANT v; VariantInit(&v);
    std::string out;
    if (SUCCEEDED(bag->Read(name, &v, nullptr)) && v.vt == VT_BSTR) out = utf8(v.bstrVal);
    VariantClear(&v);
    return out;
}

struct Entry { std::string name, id; ComPtr<IMoniker> moniker; };
std::vector<Entry> enumerate() {
    std::vector<Entry> out;
    ComPtr<ICreateDevEnum> devices; ComPtr<IEnumMoniker> list;
    if (FAILED(CoCreateInstance(CLSID_SystemDeviceEnum, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&devices)))) return out;
    if (devices->CreateClassEnumerator(CLSID_VideoInputDeviceCategory, &list, 0) != S_OK || !list) return out;
    ComPtr<IMoniker> moniker;
    int index = 0;
    while (list->Next(1, &moniker, nullptr) == S_OK) {
        ComPtr<IPropertyBag> bag;
        if (SUCCEEDED(moniker->BindToStorage(nullptr, nullptr, IID_PPV_ARGS(&bag)))) {
            Entry e; e.name = bagString(bag.Get(), L"FriendlyName");
            std::string path = bagString(bag.Get(), L"DevicePath");
            e.id = "dshow:" + (path.empty() ? "name:" + e.name + "#" + std::to_string(index) : path);
            e.moniker = moniker;
            out.push_back(std::move(e));
        }
        moniker.Reset(); ++index;
    }
    return out;
}

// Most cards expose a capture pin; some only a preview pin or an uncategorised output pin.
const GUID* const kPinCategories[] = {&PIN_CATEGORY_CAPTURE, &PIN_CATEGORY_PREVIEW, nullptr};

HRESULT findStreamConfig(ICaptureGraphBuilder2* builder, IBaseFilter* filter, IAMStreamConfig** config, const GUID** category) {
    HRESULT hr = E_FAIL;
    for (const GUID* c : kPinCategories) {
        hr = builder->FindInterface(c, &MEDIATYPE_Video, filter, IID_IAMStreamConfig, reinterpret_cast<void**>(config));
        if (SUCCEEDED(hr) && *config) { if (category) *category = c; return hr; }
    }
    return FAILED(hr) ? hr : E_NOINTERFACE;
}

HRESULT streamConfig(IBaseFilter* filter, IAMStreamConfig** config) {
    ComPtr<ICaptureGraphBuilder2> builder;
    HRESULT hr = CoCreateInstance(CLSID_CaptureGraphBuilder2, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&builder));
    if (SUCCEEDED(hr)) hr = findStreamConfig(builder.Get(), filter, config, nullptr);
    return hr;
}

void modesOf(IAMStreamConfig* config, std::vector<VideoMode>& modes) {
    int count = 0, size = 0;
    if (FAILED(config->GetNumberOfCapabilities(&count, &size)) || size != sizeof(VIDEO_STREAM_CONFIG_CAPS)) return;
    for (int i = 0; i < count; ++i) {
        AM_MEDIA_TYPE* type = nullptr; VIDEO_STREAM_CONFIG_CAPS caps{};
        if (FAILED(config->GetStreamCaps(i, &type, reinterpret_cast<BYTE*>(&caps))) || !type) continue;
        REFERENCE_TIME interval = 0;
        const BITMAPINFOHEADER* h = type->subtype == fourcc("MJPG") ? nullptr : header(type, &interval); // compressed modes are not offered
        if (h) {
            VideoMode m; m.width = h->biWidth; m.height = std::abs(h->biHeight); m.format = fromSubtype(type->subtype); m.nativeIndex = i;
            // The default interval and, when it differs, the fastest one the entry allows.
            for (REFERENCE_TIME t : {interval, caps.MinFrameInterval}) {
                if (t <= 0) continue;
                rate(t, m.fpsNum, m.fpsDen);
                bool seen = false;
                for (auto& x : modes) seen |= x.width == m.width && x.height == m.height && x.format == m.format && uint64_t(x.fpsNum) * m.fpsDen == uint64_t(m.fpsNum) * x.fpsDen;
                if (!seen && m.width > 0 && m.height > 0) modes.push_back(m);
            }
        }
        deleteType(type);
    }
}

class GrabberCallback : public IGrabberCallback {
public:
    std::function<void(IMediaSample*)> handler;
    STDMETHODIMP QueryInterface(REFIID id, void** out) override {
        if (id == __uuidof(IUnknown) || id == kIidSampleGrabberCB) { *out = static_cast<IGrabberCallback*>(this); AddRef(); return S_OK; }
        *out = nullptr; return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }
    STDMETHODIMP SampleCB(double, IMediaSample* sample) override { if (handler) handler(sample); return S_OK; }
    STDMETHODIMP BufferCB(double, BYTE*, long) override { return E_NOTIMPL; }
    virtual ~GrabberCallback() = default;
};

class DirectShowCapture : public CaptureSource {
public:
    ~DirectShowCapture() override { stop(); }
    bool open(const std::string& id, const VideoMode& wanted, std::string& error) override;
    void start(Callback callback, EventLog* log) override;
    void stop() override;
    bool failed() const override { return failed_; }
    PixelFormat outputFormat() const override { return output_; }
private:
    ComPtr<IGraphBuilder> graph_;
    ComPtr<IMediaControl> control_;
    ComPtr<IGrabber> grabber_;
    ComPtr<IMediaEvent> events_;
    GrabberCallback callback_;
    VideoMode mode_;
    PixelFormat output_ = PixelFormat::Unknown;
    int stride_ = 0;
    bool bottomUp_ = false;
    Callback user_;
    std::atomic<bool> failed_{false};
    std::atomic<bool> stop_{false};
    std::atomic<int64_t> lastPicture_{0};
    std::thread monitor_;
    std::atomic<uint64_t> sequence_{0};
};

bool DirectShowCapture::open(const std::string& id, const VideoMode& wanted, std::string& error) {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ComPtr<IMoniker> moniker;
    for (auto& e : enumerate()) if (e.id == id) moniker = e.moniker;
    if (!moniker) { error = "capture device not found"; return false; }
    ComPtr<IBaseFilter> device;
    HRESULT hr = moniker->BindToObject(nullptr, nullptr, IID_PPV_ARGS(&device));
    if (FAILED(hr)) { error = "capture device could not be opened (" + hresult(hr) + "); close other programs that use it"; return false; }
    ComPtr<ICaptureGraphBuilder2> builder;
    hr = CoCreateInstance(CLSID_FilterGraph, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&graph_));
    if (SUCCEEDED(hr)) hr = CoCreateInstance(CLSID_CaptureGraphBuilder2, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&builder));
    if (SUCCEEDED(hr)) hr = builder->SetFiltergraph(graph_.Get());
    if (SUCCEEDED(hr)) hr = graph_->AddFilter(device.Get(), L"Capture");
    ComPtr<IAMStreamConfig> config;
    const GUID* category = &PIN_CATEGORY_CAPTURE;
    if (SUCCEEDED(hr)) hr = findStreamConfig(builder.Get(), device.Get(), config.GetAddressOf(), &category);
    if (FAILED(hr)) { error = "DirectShow graph " + hresult(hr); return false; }
    // WDM cards with an input selector need their upstream crossbar in the graph before the
    // capture pin delivers anything. Looking it up makes the builder insert and connect it;
    // the input the driver already routes is left as it is.
    {
        ComPtr<IAMCrossbar> crossbar;
        builder->FindInterface(&LOOK_UPSTREAM_ONLY, nullptr, device.Get(), IID_IAMCrossbar, reinterpret_cast<void**>(crossbar.GetAddressOf()));
    }
    // Exact mode: size and format from a capability entry, the rate as its frame interval.
    const REFERENCE_TIME interval = REFERENCE_TIME(std::llround(1e7 * wanted.fpsDen / std::max<uint32_t>(1, wanted.fpsNum)));
    int count = 0, size = 0; config->GetNumberOfCapabilities(&count, &size);
    bool set = false;
    for (int i = 0; i < count && !set && size == sizeof(VIDEO_STREAM_CONFIG_CAPS); ++i) {
        AM_MEDIA_TYPE* type = nullptr; VIDEO_STREAM_CONFIG_CAPS caps{};
        if (FAILED(config->GetStreamCaps(i, &type, reinterpret_cast<BYTE*>(&caps))) || !type) continue;
        REFERENCE_TIME current = 0;
        const BITMAPINFOHEADER* h = header(type, &current);
        if (h && h->biWidth == wanted.width && std::abs(h->biHeight) == wanted.height && fromSubtype(type->subtype) == wanted.format &&
            (interval == current || (caps.MinFrameInterval > 0 && interval >= caps.MinFrameInterval - 2 && (caps.MaxFrameInterval <= 0 || interval <= caps.MaxFrameInterval + 2)))) {
            if (type->formattype == FORMAT_VideoInfo) reinterpret_cast<VIDEOINFOHEADER*>(type->pbFormat)->AvgTimePerFrame = interval;
            else reinterpret_cast<VIDEOINFOHEADER2*>(type->pbFormat)->AvgTimePerFrame = interval;
            hr = config->SetFormat(type);
            if (type->formattype == FORMAT_VideoInfo2) color_ = colorFromDxvaFlags(reinterpret_cast<VIDEOINFOHEADER2*>(type->pbFormat)->dwControlFlags);
            if (type->subtype == fourcc("HDYC") && !color_.matrix) color_.matrix = 1;   // HDYC: UYVY with BT.709 by definition
            set = SUCCEEDED(hr);
        }
        deleteType(type);
    }
    if (!set) { error = "the requested mode is not offered by this device"; return false; }
    mode_ = wanted;
    output_ = wanted.format;
    ComPtr<IBaseFilter> grabberFilter, renderer;
    hr = CoCreateInstance(kClsidSampleGrabber, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&grabberFilter));
    if (FAILED(hr)) { error = "Sample Grabber unavailable (" + hresult(hr) + ")"; return false; }
    grabberFilter->QueryInterface(kIidSampleGrabber, reinterpret_cast<void**>(grabber_.GetAddressOf()));
    hr = CoCreateInstance(kClsidNullRenderer, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&renderer));
    if (FAILED(hr) || !grabber_) { error = "Null Renderer unavailable"; return false; }
    AM_MEDIA_TYPE want{}; want.majortype = MEDIATYPE_Video; want.subtype = toSubtype(output_); want.formattype = FORMAT_VideoInfo;
    grabber_->SetMediaType(&want);
    grabber_->SetBufferSamples(FALSE); grabber_->SetOneShot(FALSE);
    graph_->AddFilter(grabberFilter.Get(), L"Grabber"); graph_->AddFilter(renderer.Get(), L"Null");
    hr = builder->RenderStream(category, &MEDIATYPE_Video, device.Get(), grabberFilter.Get(), renderer.Get());
    if (FAILED(hr)) { error = "DirectShow could not connect the capture pin (" + hresult(hr) + ")"; return false; }
    AM_MEDIA_TYPE connected{};
    if (SUCCEEDED(grabber_->GetConnectedMediaType(&connected))) {
        if (const BITMAPINFOHEADER* h = header(&connected, nullptr)) {
            bottomUp_ = (output_ == PixelFormat::RGB32 || output_ == PixelFormat::RGB24) && h->biHeight > 0;
            stride_ = planar420(output_) ? std::max(int(h->biWidth), wanted.width) : ((h->biWidth * h->biBitCount / 8 + 3) & ~3);
        }
        freeType(&connected);
    }
    if (!stride_) stride_ = wanted.width * bytesPerPixel(output_);
    // No reference clock: deliver each sample as soon as the driver hands it over.
    ComPtr<IMediaFilter> filter;
    if (SUCCEEDED(graph_.As(&filter))) filter->SetSyncSource(nullptr);
    graph_.As(&control_);
    graph_->QueryInterface(IID_IMediaEvent, reinterpret_cast<void**>(events_.GetAddressOf()));
    return control_ != nullptr;
}

void DirectShowCapture::start(Callback callback, EventLog* log) {
    user_ = std::move(callback);
    failed_ = false; stop_ = false; lastPicture_ = 0;
    callback_.handler = [this, log](IMediaSample* sample) {
        if (stop_ || failed_ || !sample) return;
        auto fail = [&](const std::string& message) { if (!failed_.exchange(true) && log) log->add("error", message); };
        AM_MEDIA_TYPE* type = nullptr;
        if (sample->GetMediaType(&type) == S_OK && type) {
            REFERENCE_TIME interval = 0;
            const auto* h = header(type, &interval);
            const int stride = h ? (planar420(output_) ? h->biWidth : ((h->biWidth * h->biBitCount / 8 + 3) & ~3)) : 0;
            const bool compatible = h && h->biWidth == mode_.width && std::abs(h->biHeight) == mode_.height &&
                fromSubtype(type->subtype) == output_ && stride == stride_ && ((output_ != PixelFormat::RGB32 && output_ != PixelFormat::RGB24) || (h->biHeight > 0) == bottomUp_) &&
                (!interval || std::llabs(interval - REFERENCE_TIME(10000000.0 * mode_.fpsDen / mode_.fpsNum)) <= 2);
            freeType(type); CoTaskMemFree(type);
            if (!compatible) { fail("capture format changed; select the new mode and restart"); return; }
        }
        BYTE* buffer = nullptr; const long length = sample->GetActualDataLength();
        if (FAILED(sample->GetPointer(&buffer)) || !buffer || length < 0) { fail("capture returned an invalid sample"); return; }
        FrameView v;
        v.width = mode_.width; v.height = mode_.height; v.format = output_;
        v.pitch = stride_; v.data = buffer;
        if (bottomUp_) { v.data = buffer + size_t(mode_.height - 1) * size_t(stride_); v.pitch = -stride_; }
        const size_t need = planar420(output_) ? size_t(stride_) * mode_.height * 3 / 2 : size_t(stride_) * mode_.height;
        if (size_t(length) < need) { fail("capture returned a truncated frame"); return; }
        v.arrival = now100ns(); v.sampleTime = 0; v.sequence = ++sequence_;
        lastPicture_ = v.arrival;
        user_(v);
    };
    grabber_->SetCallback(&callback_, 0);
    HRESULT hr = control_->Run();
    if (FAILED(hr)) { failed_ = true; if (log) log->add("error", "DirectShow capture did not start: " + hresult(hr)); }
    if (!failed_) monitor_ = std::thread([this, log] {
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        const auto began = now100ns();
        while (!stop_ && !failed_) {
            if (events_) {
                long code = 0; LONG_PTR first = 0, second = 0;
                while (events_->GetEvent(&code, &first, &second, 0) == S_OK) {
                    events_->FreeEventParams(code, first, second);
                    if (code == EC_ERRORABORT || code == EC_DEVICE_LOST || code == EC_COMPLETE) {
                        if (!failed_.exchange(true) && log) log->add("error", "DirectShow capture stopped (event " + std::to_string(code) + "); check the signal and restart");
                        break;
                    }
                }
            }
            const auto last = lastPicture_.load();
            if (packetTimedOut(began, last, now100ns())) {
                if (!failed_.exchange(true) && log) log->add("error", last ? "capture packets stopped; check the signal and restart" : "capture first frame timed out; check the signal and restart");
                break;
            }
            Sleep(50);
        }
        CoUninitialize();
    });
}

void DirectShowCapture::stop() {
    stop_ = true;
    if (monitor_.joinable()) monitor_.join();
    if (control_) control_->Stop();
    if (grabber_) grabber_->SetCallback(nullptr, 0);
    events_.Reset(); control_.Reset(); grabber_.Reset(); graph_.Reset();
}
} // namespace

std::vector<VideoDevice> listDirectShowDevices(bool withModes) {
    std::vector<VideoDevice> out;
    for (auto& e : enumerate()) {
        VideoDevice d; d.name = e.name; d.id = e.id; d.api = "dshow";
        if (withModes) {
            ComPtr<IBaseFilter> filter; ComPtr<IAMStreamConfig> config;
            HRESULT hr = e.moniker->BindToObject(nullptr, nullptr, IID_PPV_ARGS(&filter));
            if (SUCCEEDED(hr)) hr = streamConfig(filter.Get(), &config);
            if (SUCCEEDED(hr)) modesOf(config.Get(), d.modes);
            else d.error = "open " + hresult(hr);
        }
        out.push_back(std::move(d));
    }
    return out;
}

std::unique_ptr<CaptureSource> createCapture(const std::string& id) {
    if (id.rfind("dshow:", 0) == 0) return std::make_unique<DirectShowCapture>();
    return std::make_unique<VideoCapture>();
}

std::unique_ptr<CaptureSource> openCapture(const std::string& id, const VideoMode& mode, std::string& error, std::string& note) {
    auto capture = createCapture(id);
    if (capture->open(id, mode, error)) return capture;
    if (id.rfind("dshow:", 0) == 0) return nullptr;
    // Some drivers list a mode through Media Foundation but only deliver it through
    // DirectShow. Try the DirectShow entry of the same device when it offers the same mode.
    std::string name;
    for (auto& d : listVideoDevices(false)) if (d.id == id) name = d.name;
    if (name.empty()) return nullptr;
    capture.reset(); // release the device before the second attempt
    for (auto& d : listDirectShowDevices(true)) {
        if (d.name != name) continue;
        bool offered = false;
        for (auto& m : d.modes) offered |= m.width == mode.width && m.height == mode.height && m.format == mode.format &&
            uint64_t(m.fpsNum) * mode.fpsDen == uint64_t(mode.fpsNum) * m.fpsDen;
        if (!offered) continue;
        auto fallback = std::make_unique<DirectShowCapture>();
        std::string second;
        if (fallback->open(d.id, mode, second)) { note = "Media Foundation could not open this mode (" + error + "); using DirectShow"; error.clear(); return fallback; }
    }
    return nullptr;
}

} // namespace fdl
