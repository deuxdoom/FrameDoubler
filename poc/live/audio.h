// WASAPI pass-through of the capture card's sound, with a tap for the recorder.
#pragma once
#include "common.h"
#include <functional>
#include <memory>
#include <thread>

namespace fdl {
struct AudioRoute;
bool audioRoutingSelfTest();
struct AudioStatus {
    std::string input = "off", output = "off", outputId, outputName;
    int queueMs = 0, bufferMs = 0;
    uint64_t retries = 0, underruns = 0;
};

class AudioPassthrough {
public:
    // Interleaved float samples, the channel count and rate of the capture endpoint, and
    // the capture time of the first sample (100 ns performance-counter ticks).
    using Tap = std::function<void(const float*, int frames, int channels, int rate, int64_t time)>;
    ~AudioPassthrough() { stop(); }
    bool start(const std::string& captureId, const std::string& renderId, int delayMs, std::string& error, EventLog* log);
    void stop();
    void setVolume(float volume) { volume_ = volume; }
    void setMute(bool mute) { mute_ = mute; }
    void setDelay(int ms) { delayMs_ = ms; }
    void setTap(Tap tap) { std::lock_guard<std::mutex> lock(tapMutex_); tap_ = std::move(tap); }
    // Diagnostics (set before start): one CSV row per played packet with its capture time
    // and the time IAudioClock places its first sample at the output (not a DAC measurement).
    void setTrace(const std::string& path) { tracePath_ = path; }
    void selectOutput(const std::string& renderId);
    AudioStatus status() const { std::lock_guard<std::mutex> lock(stateMutex_); return state_; }
    bool running() const { return running_; }
    int channels() const { return channels_; }
    int rate() const { return rate_; }
    float inputPeak() const { return inputPeak_; }
    float outputPeak() const { return outputPeak_; }
    uint64_t inputFrames() const { return inputFrames_; }
    uint64_t outputFrames() const { return outputFrames_; }
    // Capture time -> IAudioClock play time of the latest packet, and the smoothed part of it
    // that is not the output queue (0 = not measured yet).
    double latencyMs() const { return latencyMs_; }
    double ownLatencyMs() const { return ownLatencyMs_; }
private:
    void run(std::string captureId, std::string renderId);
    std::thread thread_;
    std::atomic<bool> stop_{false}, running_{false};
    std::atomic<float> volume_{1.0f};
    std::atomic<bool> mute_{false};
    std::atomic<int> delayMs_{60};
    std::atomic<int> channels_{0}, rate_{0};
    std::atomic<float> inputPeak_{0}, outputPeak_{0};
    std::atomic<uint64_t> inputFrames_{0}, outputFrames_{0};
    std::atomic<double> latencyMs_{0}, ownLatencyMs_{0};
    std::mutex tapMutex_;
    Tap tap_;
    EventLog* log_ = nullptr;
    std::string tracePath_;
    HANDLE ready_ = nullptr;
    std::string startError_;
    std::shared_ptr<AudioRoute> route_;
    mutable std::mutex stateMutex_;
    AudioStatus state_;
};

} // namespace fdl
