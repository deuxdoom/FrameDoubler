// fd-live.exe: command-line checks for the live engine.
//   --list                                  devices and modes as JSON
//   --selftest [--vfx DIR] [--dlss DIR --dlss-data DIR]  synthetic checks (exit code 1 on failure)
//   --compare --width W --height H --fps N/D [--stride N] [--vfx DIR]
//             [--dlss DIR --dlss-data DIR]   NV12 stdin, original resolution, reference PSNR / time
//   --replay --width W --height H --fps N/D [--format NV12] [options]
//                                           raw frames from stdin, summary JSON at the end
//   --probe --device NAME --width W --height H --fps N/D [--seconds S] [--window] [options]
//                                           live capture with optional preview window
// Options: --fg off|own|vfx|dlss --multiplier 2|3|4 --aa 0|1|2 --dedup 0|1 --record DIR
//          --vfx DIR --vfx-quality 0|1|2 --audio auto|none --screenshot DIR
//          --dlss DIR --dlss-data DIR (NGX logs/cache, separate from the runtime)
// Compare/selftest: --nvof [--nvof-quality 0|1|2] --dlss-motion own|nvof
#include "engine.h"
#include "common.h"
#include "capture.h"
#include "gpu.h"
#include "timeline.h"
#include "vfx.h"
#include "dlss.h"
#include "nvof.h"

#include <algorithm>
#include <cmath>

#include <fcntl.h>
#include <io.h>
#include <cstdio>
#include <string>
#include <vector>

using namespace fdl;

namespace {
std::string arg(int argc, wchar_t** argv, const wchar_t* name, const std::string& fallback = {}) {
    for (int i = 1; i + 1 < argc; ++i) if (std::wstring(argv[i]) == name) return utf8(argv[i + 1]);
    return fallback;
}
bool flag(int argc, wchar_t** argv, const wchar_t* name) {
    for (int i = 1; i < argc; ++i) if (std::wstring(argv[i]) == name) return true;
    return false;
}
void print(char* text) { if (text) { fputs(text, stdout); fputc('\n', stdout); fflush(stdout); fdl_free(text); } }

std::string commonSettings(int argc, wchar_t** argv) {
    std::string s;
    const std::string fps = arg(argc, argv, L"--fps", "60/1");
    const size_t slash = fps.find('/');
    s += "width=" + arg(argc, argv, L"--width", "1920") + "\n";
    s += "height=" + arg(argc, argv, L"--height", "1080") + "\n";
    s += "fpsNum=" + fps.substr(0, slash) + "\n";
    s += "fpsDen=" + (slash == std::string::npos ? std::string("1") : fps.substr(slash + 1)) + "\n";
    s += "format=" + arg(argc, argv, L"--format", "NV12") + "\n";
    s += "fg=" + arg(argc, argv, L"--fg", "off") + "\n";
    s += "multiplier=" + arg(argc, argv, L"--multiplier", "2") + "\n";
    s += "aa=" + arg(argc, argv, L"--aa", "0") + "\n";
    s += "dedup=" + arg(argc, argv, L"--dedup", "0") + "\n";
    s += "vfxDir=" + arg(argc, argv, L"--vfx") + "\n";
    s += "vfxQuality=" + arg(argc, argv, L"--vfx-quality", "1") + "\n";
    s += "dlssDir=" + arg(argc, argv, L"--dlss") + "\n";
    s += "dlssDataDir=" + arg(argc, argv, L"--dlss-data") + "\n";
    s += "recordDir=" + arg(argc, argv, L"--record") + "\n";
    s += "screenshotDir=" + arg(argc, argv, L"--screenshot") + "\n";
    s += "recordCodec=" + arg(argc, argv, L"--codec", "h264") + "\n";
    if (flag(argc, argv, L"--echo")) s += "echo=1\n";
    return s;
}

double psnrRgba(const std::vector<uint8_t>& a, const std::vector<uint8_t>& b) {
    double se = 0; size_t n = 0;
    for (size_t i = 0; i + 3 < a.size(); i += 4) for (int c = 0; c < 3; ++c) { double d = double(a[i + c]) - double(b[i + c]); se += d * d; ++n; }
    double mse = se / double(std::max<size_t>(1, n));
    return mse <= 1e-12 ? 99.0 : 10 * std::log10(255.0 * 255.0 / mse);
}

struct Series {
    std::vector<double> values; double ms = 0; int timed = 0;
    std::vector<double> walls;
    void wall(double elapsed) { if (values.size() > 1) walls.push_back(elapsed); }
    std::string json() const {
        if (values.empty()) return "null";
        std::vector<double> v = values; std::sort(v.begin(), v.end());
        double mean = 0; for (double x : v) mean += x; mean /= double(v.size());
        char b[200]; snprintf(b, sizeof b, "{\"meanDb\":%.2f,\"medianDb\":%.2f,\"p10Db\":%.2f,\"minDb\":%.2f,\"ms\":%.3f}", mean, v[v.size() / 2],
                               v[v.size() / 10], v.front(), timed ? ms / timed : -1.0);
        std::string result = b;
        result.pop_back();
        result += ",\"samples\":" + std::to_string(values.size()) + ",\"wallTimedSamples\":" + std::to_string(walls.size());
        if (walls.empty()) return result + ",\"wallWithReadbackMeanMs\":null,\"wallWithReadbackP95Ms\":null}";
        std::vector<double> sorted = walls; std::sort(sorted.begin(), sorted.end());
        double sum = 0; for (double value : sorted) sum += value;
        return result + ",\"wallWithReadbackMeanMs\":" + std::to_string(sum / sorted.size()) +
            ",\"wallWithReadbackP95Ms\":" + std::to_string(sorted[std::min(sorted.size() - 1, size_t(std::ceil(sorted.size() * .95)) - 1)]) + "}";
    }
};

// Real-content quality: for unique pictures U0, U1, U2 the middle one is generated from
// U0 and U2 at its own time and compared with the real U1 (twice the normal gap, so a
// harder case than live use). DLSS-G, own interpolation, NVIDIA VFX and a plain blend.
int compareMode(int argc, wchar_t** argv) {
    const int w = std::stoi(arg(argc, argv, L"--width", "3840")), h = std::stoi(arg(argc, argv, L"--height", "2160"));
    const int stride = std::max(1, std::stoi(arg(argc, argv, L"--stride", "4")));
    const std::string vfxDir = arg(argc, argv, L"--vfx");
    const std::string dlssDir = arg(argc, argv, L"--dlss");
    if (w <= 0 || h <= 0 || w > 16384 || h > 16384 || (w & 1) || (h & 1)) { fprintf(stderr, "compare requires positive, even NV12 dimensions up to 16384\n"); return 2; }
    const std::string fps = arg(argc, argv, L"--fps", "60/1");
    const size_t slash = fps.find('/');
    const int fpsNum = std::stoi(fps.substr(0, slash)), fpsDen = slash == std::string::npos ? 1 : std::stoi(fps.substr(slash + 1));
    if (fpsNum <= 0 || fpsDen <= 0) { fprintf(stderr, "invalid input frame rate\n"); return 2; }
    std::vector<std::string> errors;
    auto failure = [&](const std::string& message) { errors.push_back(message); fprintf(stderr, "%s\n", message.c_str()); fflush(stderr); };
    Gpu gpu; std::string error;
    if (!gpu.create(error) || !gpu.configure(w, h, error)) { fprintf(stderr, "%s\n", error.c_str()); return 2; }
    VfxGenerator vfx;
    if (!vfxDir.empty() && !vfx.create(gpu, wide(vfxDir), std::stoi(arg(argc, argv, L"--vfx-quality", "1")), error)) failure("vfx: " + error);
    DlssGenerator dlss;
    if (!dlssDir.empty() && !dlss.create(gpu, wide(dlssDir), wide(arg(argc, argv, L"--dlss-data")), error, flag(argc, argv, L"--echo"),arg(argc,argv,L"--dlss-motion","own")=="nvof"?2:1)) failure("dlss: " + error);
    NvofFlow flow;
    const bool useFlow=flag(argc,argv,L"--nvof");
    if(useFlow && !flow.create(gpu,std::stoi(arg(argc,argv,L"--nvof-quality","1")),error))failure(error);
    _setmode(_fileno(stdin), _O_BINARY);
    const size_t bytes = size_t(w) * h * 3 / 2;
    std::vector<uint8_t> frame(bytes);
    std::deque<std::pair<std::vector<uint8_t>, int64_t>> uniques;
    DuplicateDetector detector;
    Series own, nv, dl, blend, of;
    ColorSetup color;
    int64_t index = 0; int triples = 0, skippedPhase = 0;
    std::vector<uint8_t> truth, result, a, b, mix;
    size_t read = 0;
    while ((read = fread(frame.data(), 1, bytes, stdin)) == bytes) {
        const int64_t time = int64_t((__int128)index++ * 10000000 * fpsDen / fpsNum);
        if (detector.observe(makeSignature(frame.data(), w, 1, w, h))) continue;
        uniques.emplace_back(frame, time);
        if (uniques.size() > 3) uniques.pop_front();
        if (uniques.size() < 3 || (triples++ % stride) != 0) continue;
        auto& u0 = uniques[0]; auto& u1 = uniques[1]; auto& u2 = uniques[2];
        const float phase = float(double(u1.second - u0.second) / double(u2.second - u0.second));
        // The standard 2x DLSS path only produces the midpoint. Compare all methods on
        // that same reference time; do not score an irregular reference against .5.
        if ((!dlssDir.empty() || useFlow) && std::abs(phase - .5f) > 1e-4f) { ++skippedPhase; continue; }
        gpu.upload(0, u0.first.data(), w, u0.first.data() + size_t(w) * h, w, PixelFormat::NV12, color, 0);
        gpu.upload(1, u1.first.data(), w, u1.first.data() + size_t(w) * h, w, PixelFormat::NV12, color, 0);
        gpu.upload(2, u2.first.data(), w, u2.first.data() + size_t(w) * h, w, PixelFormat::NV12, color, 0);
        if (!gpu.readback(gpu.frame(1), truth) || !gpu.readback(gpu.frame(0), a) || !gpu.readback(gpu.frame(2), b)) { failure("reference readback failed"); break; }
        mix.resize(a.size());
        for (size_t i = 0; i < a.size(); ++i) mix[i] = uint8_t(std::lround(a[i] * (1 - phase) + b[i] * phase));
        blend.values.push_back(psnrRgba(mix, truth));
        const int64_t ownStart = now100ns();
        gpu.timer.begin(gpu.context());
        gpu.timer.mark(gpu.context(), StageMotion, true);
        gpu.pyramid(0); gpu.pyramid(2); gpu.motion(0, 2);
        gpu.timer.mark(gpu.context(), StageMotion, false);
        gpu.timer.mark(gpu.context(), StageInterpolate, true);
        gpu.interpolate(0, 2, phase);
        gpu.timer.mark(gpu.context(), StageInterpolate, false);
        gpu.timer.end(gpu.context());
        if (!gpu.readback(gpu.output(), result)) { failure("own readback failed"); break; }
        const double ownWall = double(now100ns() - ownStart) / 1e4;
        own.values.push_back(psnrRgba(result, truth)); own.wall(ownWall);
        gpu.timer.collect(gpu.context());
        if(flow.ready()) {
            const int64_t start=now100ns();
            if(!flow.estimate(gpu,0,2,true,error)){failure(error);break;}
            flow.interpolate(gpu,0,2,phase);
            if(!gpu.readback(gpu.output(),result)){failure("NVOF readback failed");break;}
            const double wall=double(now100ns()-start)/1e4;
            of.values.push_back(psnrRgba(result,truth));of.wall(wall);
        }
        if (vfx.ready()) {
            std::string e;
            const int64_t vfxStart = now100ns();
            if (vfx.interpolate(gpu, 0, 2, phase, false, e)) {
                if (!gpu.readback(gpu.output(), result)) { failure("vfx readback failed"); break; }
                const double vfxWall = double(now100ns() - vfxStart) / 1e4;
                nv.values.push_back(psnrRgba(result, truth)); nv.wall(vfxWall);
                if (nv.values.size() > 1) { nv.ms += vfx.lastMs(); ++nv.timed; }
            } else { failure("vfx: " + e); break; }
        }
        if (dlss.ready()) {
            const int64_t dlssStart = now100ns();
            // Include its required motion estimation in the common wall-time scope.
            if(!dlss.hardwareMotion()){gpu.pyramid(0); gpu.pyramid(2); gpu.motion(0, 2);}
            std::string e;
            if (dlss.interpolate(gpu, 0, 2, uint64_t(u0.second), uint64_t(u2.second), phase, 1, e)) {
                if (!gpu.readback(gpu.output(), result)) { failure("dlss readback failed"); break; }
                const double dlssWall = double(now100ns() - dlssStart) / 1e4;
                dl.values.push_back(psnrRgba(result, truth)); dl.wall(dlssWall);
                if (dl.values.size() > 1) { dl.ms += dlss.lastMs(); ++dl.timed; }
            } else { failure("dlss: " + e); break; }
        }
        if ((flag(argc, argv, L"--echo") || flag(argc, argv, L"--progress")) && own.values.size() % 10 == 0) { fprintf(stderr, "[compare] %zu triples scored\n", own.values.size()); fflush(stderr); }
    }
    if (read != 0 && read != bytes) failure("truncated NV12 input frame");
    if (ferror(stdin)) failure("input read error");
    if (own.values.empty()) failure("no comparable frame triples");
    double stages[StageCount]; gpu.timer.take(stages);
    own.ms = stages[StageMotion] + stages[StageInterpolate]; own.timed = own.ms >= 0 ? 1 : 0;
    printf("{\"frames\":%lld,\"triplesScored\":%zu,\"width\":%d,\"height\":%d,\"ownMotionMs\":%.3f,\"ownInterpolateMs\":%.3f,\"own\":%s,\"vfx\":%s,\"blend\":%s,\"vfxRuntime\":%s",
           (long long)index, blend.values.size(), w, h, stages[StageMotion], stages[StageInterpolate], own.json().c_str(), nv.json().c_str(), blend.json().c_str(),
           jsonString(vfx.version()).c_str());
    printf(",\"nvof\":%s,\"nvofGrid\":%d",of.json().c_str(),flow.grid());
    printf(",\"dlssMotion\":%s",jsonString(dlss.hardwareMotion()?"nvof":"own").c_str());
    printf(",\"dlss\":%s,\"dlssRuntime\":%s,\"dlssMaxMultiplier\":%d,\"nonMidpointSkipped\":%d,\"timingScope\":%s,\"errors\":[", dl.json().c_str(), jsonString(dlss.version()).c_str(), dlss.ready() ? dlss.maxGenerated() + 1 : 0, skippedPhase,
           jsonString("wallWithReadback includes motion where required, generation and matching RGBA readback; first scored triple excluded; backend ms has backend-specific scope; no display/HDMI latency measured").c_str());
    for (size_t i = 0; i < errors.size(); ++i) printf("%s%s", i ? "," : "", jsonString(errors[i]).c_str());
    printf("]}\n");
    return errors.empty() ? 0 : 1;
}

LRESULT CALLBACK windowProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    if (m == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(h, m, w, l);
}
} // namespace

int wmain(int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (flag(argc, argv, L"--list")) { print(fdl_devices()); return 0; }
    if (flag(argc, argv, L"--compare")) return compareMode(argc, argv);
    if (flag(argc, argv, L"--selftest")) {
        std::string s = "vfxDir=" + arg(argc, argv, L"--vfx") + "\nvfxQuality=" + arg(argc, argv, L"--vfx-quality", "1") + "\ndlssDir=" + arg(argc, argv, L"--dlss") + "\ndlssDataDir=" + arg(argc, argv, L"--dlss-data") + "\n";
        if(flag(argc,argv,L"--nvof"))s+="nvof=1\nnvofQuality="+arg(argc,argv,L"--nvof-quality","1")+"\n";
        s+="dlssMotion="+arg(argc,argv,L"--dlss-motion","own")+"\n";
        if (flag(argc, argv, L"--echo") || !arg(argc, argv, L"--dlss").empty()) s += "echo=1\n";
        char* result = fdl_selftest(s.c_str());
        const bool failed = result && std::string(result).find("\"failed\":0}") == std::string::npos;
        print(result);
        return failed ? 1 : 0;
    }
    if (flag(argc, argv, L"--replay")) {
        const std::string settings = commonSettings(argc, argv);
        char* error = nullptr;
        void* session = fdl_open_replay(settings.c_str(), &error);
        if (!session) { fprintf(stderr, "open failed: %s\n", error ? error : "?"); return 2; }
        const Settings s = parseSettings(settings.c_str());
        const int w = getInt(s, "width", 0), h = getInt(s, "height", 0);
        const bool nv12 = get(s, "format") == "NV12";
        const int packedBytes = get(s, "format") == "RGB32" ? 4 : 2;
        const size_t frameBytes = nv12 ? size_t(w) * h * 3 / 2 : size_t(w) * h * packedBytes;
        const int64_t interval = int64_t(10000000.0 * getInt(s, "fpsDen", 1) / getInt(s, "fpsNum", 60));
        const int limit = std::stoi(arg(argc, argv, L"--limit", "0"));
        _setmode(_fileno(stdin), _O_BINARY);
        std::vector<unsigned char> frame(frameBytes);
        if (!get(s, "recordDir").empty()) fdl_command(session, "action=record-start");
        if (get(s, "fg") == "vfx") Sleep(100);
        long long index = 0;
        const int64_t start = now100ns();
        while (fread(frame.data(), 1, frameBytes, stdin) == frameBytes) {
            if (index == 1 && get(s, "fg") == "vfx") {
                // Wait for the optional runtime to load before timing the replay.
                for (int i = 0; i < 600; ++i) { char* st = fdl_status(session); std::string t = st ? st : ""; fdl_free(st); if (t.find("\"fg\":\"loading\"") == std::string::npos) break; Sleep(100); }
            }
            fdl_feed(session, frame.data(), nv12 ? w : w * packedBytes, 10000000 + index * interval);
            ++index;
            if (limit > 0 && index >= limit) break;
        }
        const double seconds = double(now100ns() - start) / 1e7;
        char* summary = fdl_summary(session);
        char* status = fdl_status(session);
        fdl_close(session);           // finishes a recording
        fprintf(stderr, "replayed %lld frames in %.2f s (%.1f fps)\n", index, seconds, index / std::max(seconds, 1e-6));
        print(summary); print(status);
        return 0;
    }
    if (flag(argc, argv, L"--probe")) {
        const std::string name = arg(argc, argv, L"--device");
        const std::string api = arg(argc, argv, L"--api");
        auto devices = listVideoDevices(false, api == "dshow");
        const VideoDevice* chosen = nullptr;
        for (auto& d : devices) if ((api.empty() || d.api == api) && (name.empty() || d.name.find(name) != std::string::npos)) { chosen = &d; break; }
        if (!chosen) { fprintf(stderr, "no capture device matching '%s'\n", name.c_str()); return 2; }
        std::string settings = commonSettings(argc, argv) + "device=" + chosen->id + "\n";
        const std::string audioChoice = arg(argc, argv, L"--audio", "auto");
        if (audioChoice != "none") {
            std::string base = chosen->name;
            for (const char* suffix : {"-Video", " Video"}) { size_t p = base.rfind(suffix); if (p != std::string::npos) base = base.substr(0, p); }
            for (auto& a : listAudioEndpoints(true)) if (a.name.find(base) != std::string::npos) { settings += "audio=" + a.id + "\n"; break; }
        }
        HWND window = nullptr;
        if (flag(argc, argv, L"--window")) {
            WNDCLASSW wc{}; wc.lpfnWndProc = windowProc; wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = L"FrameDoublerLiveProbe";
            wc.hCursor = LoadCursorW(nullptr, IDC_ARROW); wc.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
            RegisterClassW(&wc);
            window = CreateWindowExW(0, wc.lpszClassName, L"FrameDoubler live probe", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                                     CW_USEDEFAULT, CW_USEDEFAULT, 1280, 760, nullptr, nullptr, wc.hInstance, nullptr);
        }
        char* error = nullptr;
        void* session = fdl_open(settings.c_str(), window, &error);
        if (!session) { fprintf(stderr, "open failed: %s\n", error ? error : "?"); return 2; }
        if (!get(parseSettings(settings.c_str()), "recordDir").empty()) fdl_command(session, "action=record-start");
        const int seconds = std::stoi(arg(argc, argv, L"--seconds", "5"));
        const ULONGLONG end = GetTickCount64() + ULONGLONG(seconds) * 1000;
        ULONGLONG nextStatus = GetTickCount64() + 1100;
        bool shot = !arg(argc, argv, L"--screenshot").empty();
        while (GetTickCount64() < end) {
            MSG msg;
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { if (msg.message == WM_QUIT) goto finished; TranslateMessage(&msg); DispatchMessageW(&msg); }
            if (GetTickCount64() >= nextStatus) {
                if (shot) { fdl_command(session, "action=screenshot"); shot = false; }
                print(fdl_status(session)); nextStatus += 1000;
            }
            Sleep(5);
        }
    finished:
        if (!get(parseSettings(settings.c_str()), "recordDir").empty()) { fdl_command(session, "action=record-stop"); Sleep(1500); }
        print(fdl_summary(session));
        char* finalStatus=fdl_status(session);
        const bool captureOk=finalStatus && std::string(finalStatus).find("\"captureFailed\":true")==std::string::npos &&
            std::string(finalStatus).find("\"totals\":{\"input\":0,")==std::string::npos;
        print(finalStatus);
        fdl_close(session);
        if (window) DestroyWindow(window);
        return captureOk?0:1;
    }
    fprintf(stderr, "usage: fd-live --list | --selftest | --replay ... | --probe ...\n");
    return 2;
}
