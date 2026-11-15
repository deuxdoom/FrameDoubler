// Recording of the processed picture and the pass-through sound (Media Foundation).
#pragma once
#include "common.h"
#include <condition_variable>
#include <memory>
#include <thread>

struct IMFSinkWriter;
struct ID3D11Texture2D;
struct ID3D11ShaderResourceView;
struct IMFDXGIDeviceManager;

namespace fdl {
class Gpu;
struct RecorderSlots;
inline constexpr int kRecorderSlots = 8;
unsigned recordingH264Level(int width, int height, uint32_t fpsNum, uint32_t fpsDen);

struct RecorderConfig {
    std::wstring directory;
    int width = 0, height = 0;
    uint32_t fpsNum = 60, fpsDen = 1;   // output picture rate
    bool hevc = false;
    int bitrateMbps = 0;                // 0: from picture size and rate
    bool audio = true;
};

class Recorder {
public:
    ~Recorder() { stop(); }
    bool start(const RecorderConfig& config, Gpu& gpu, std::string& path, std::string& error);
    // Render thread: converts the picture to NV12 on the GPU and queues it. Returns false
    // when every encoder slot is busy (the frame is skipped, never waited for).
    bool pushVideo(Gpu& gpu, ID3D11ShaderResourceView* picture, int64_t time);
    // Audio thread: float samples at the capture rate; converted to 48 kHz stereo.
    void pushAudio(const float* samples, int frames, int channels, int rate, int64_t time);
    void stop();
    bool active() const { return active_; }
    std::string path() const { return path_; }
    double seconds() const;
    uint64_t droppedFrames() const { return dropped_; }
    uint64_t writtenFrames() const { return written_; }
    uint64_t repeatedFrames() const { return repeated_; }   // empty grid slots filled with the previous picture
    std::string lastError();
private:
    struct VideoItem { int slot; int64_t time; };
    struct AudioItem { std::vector<int16_t> samples; int64_t time; };
    void run();
    void release(int slot);
    int takeSlot();
    RecorderConfig config_;
    IMFSinkWriter* writer_ = nullptr;
    IMFDXGIDeviceManager* manager_ = nullptr;
    DWORD videoStream_ = 0, audioStream_ = 0;
    static constexpr int kSlots = kRecorderSlots;
    ComPtr<ID3D11Texture2D> textures_[kSlots];
    std::shared_ptr<RecorderSlots> slots_;
    std::thread thread_;
    std::mutex mutex_;
    std::condition_variable wake_;
    std::deque<VideoItem> video_;
    std::deque<AudioItem> audio_;
    std::atomic<bool> active_{false}, stop_{false};
    std::atomic<uint64_t> dropped_{0}, written_{0}, repeated_{0};
    ComPtr<ID3D11Texture2D> held_;      // NV12 copy of the last picture, for empty slots
    double slotLength_ = 166666.67;     // 100 ns per recorded frame
    int64_t lastIndex_ = -1;            // grid slot of the last picture (render thread)
    int64_t origin_ = -1;               // time of the first picture
    std::atomic<int64_t> lastVideo_{-1};
    int64_t audioSamples_ = 0;          // 48 kHz samples written
    double resamplePos_ = 0;
    std::vector<float> resampleTail_;
    std::string path_, error_;
};

} // namespace fdl
