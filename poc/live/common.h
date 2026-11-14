// Shared helpers for the FrameDoubler live engine (capture preview).
#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <wrl/client.h>

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <deque>
#include <map>
#include <mutex>
#include <string>
#include <vector>

namespace fdl {
using Microsoft::WRL::ComPtr;

inline std::string utf8(const std::wstring& text) {
    if (text.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, text.data(), int(text.size()), nullptr, 0, nullptr, nullptr);
    std::string out(size_t(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), int(text.size()), out.data(), n, nullptr, nullptr);
    return out;
}
inline std::wstring wide(const std::string& text) {
    if (text.empty()) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, text.data(), int(text.size()), nullptr, 0);
    std::wstring out(size_t(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.data(), int(text.size()), out.data(), n);
    return out;
}
inline std::string jsonString(const std::string& text) {
    std::string out = "\"";
    for (unsigned char c : text) {
        switch (c) {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default:
            if (c < 0x20) { char b[8]; snprintf(b, sizeof b, "\\u%04x", c); out += b; }
            else out += char(c);
        }
    }
    return out + "\"";
}
inline std::string hresult(HRESULT hr) { char b[16]; snprintf(b, sizeof b, "0x%08lX", (unsigned long)hr); return b; }

// 100 ns ticks of the performance counter: the same clock Media Foundation sample
// times (MFGetSystemTime) and WASAPI QPC positions use.
inline int64_t now100ns() {
    static const int64_t frequency = [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return f.QuadPart; }();
    LARGE_INTEGER c; QueryPerformanceCounter(&c);
    return int64_t((__int128)c.QuadPart * 10000000 / frequency);
}

// "key=value" lines (UTF-8). Used for configuration and commands so the C API stays
// free of a JSON parser.
using Settings = std::map<std::string, std::string>;
inline Settings parseSettings(const char* text) {
    Settings s;
    if (!text) return s;
    std::string all(text);
    size_t start = 0;
    while (start <= all.size()) {
        size_t end = all.find('\n', start);
        std::string line = all.substr(start, end == std::string::npos ? std::string::npos : end - start);
        if (!line.empty() && line.back() == '\r') line.pop_back();
        size_t eq = line.find('=');
        if (eq != std::string::npos && eq > 0) s[line.substr(0, eq)] = line.substr(eq + 1);
        if (end == std::string::npos) break;
        start = end + 1;
    }
    return s;
}
inline std::string get(const Settings& s, const char* key, const std::string& fallback = {}) {
    auto it = s.find(key); return it == s.end() ? fallback : it->second;
}
inline int getInt(const Settings& s, const char* key, int fallback) {
    auto it = s.find(key); if (it == s.end() || it->second.empty()) return fallback;
    try { return std::stoi(it->second); } catch (...) { return fallback; }
}
inline double getDouble(const Settings& s, const char* key, double fallback) {
    auto it = s.find(key); if (it == s.end() || it->second.empty()) return fallback;
    try { return std::stod(it->second); } catch (...) { return fallback; }
}

// Messages for the host (log lines and events). Bounded so a host that stops polling
// cannot grow memory.
class EventLog {
public:
    void add(const std::string& kind, const std::string& message) {
        std::lock_guard<std::mutex> lock(mutex_);
        events_.push_back("{\"kind\":" + jsonString(kind) + ",\"message\":" + jsonString(message) + "}");
        if (events_.size() > 200) events_.pop_front();
        if (echo) fprintf(stderr, "[%s] %s\n", kind.c_str(), message.c_str());
    }
    std::vector<std::string> drain() {
        std::lock_guard<std::mutex> lock(mutex_);
        std::vector<std::string> out(events_.begin(), events_.end());
        events_.clear();
        return out;
    }
    bool echo = false;
private:
    std::mutex mutex_;
    std::deque<std::string> events_;
};

} // namespace fdl
