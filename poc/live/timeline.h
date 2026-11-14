// Duplicate detection and output timeline for live capture.
// Pure C++ (no Windows or GPU types) so the self-test can exercise it directly.
#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstddef>
#include <cstdlib>
#include <deque>
#include <vector>
#include <array>

namespace fdl {

// A capture signal repeats game frames (30 fps content in a 60 Hz signal, drops as
// triple frames). Each incoming frame is compared with the last *unique* frame on a
// reduced luma grid. Tiles of 8x8 samples are scored by their mean absolute
// difference so that encoder or scaler noise does not count as motion, while any
// real change (a moving cursor covers several samples) does.
struct LumaSignature {
    int width = 0, height = 0;          // sample grid
    std::vector<uint8_t> samples;
};

// A small descriptor for conservative hard-cut detection. It is metadata only:
// full-resolution pictures are still fed to every frame generator unchanged.
struct SceneSignature {
    std::array<uint8_t, 64 * 36> pixels{};
    std::array<float, 4 * 4 * 16> histogram{};
    bool valid = false;
};
inline SceneSignature sceneSignature(const LumaSignature& luma) {
    SceneSignature result;
    if (luma.width <= 0 || luma.height <= 0 || luma.samples.empty()) return result;
    for (int y = 0; y < 36; ++y) for (int x = 0; x < 64; ++x) {
        const int sx = std::min(luma.width - 1, (2 * x + 1) * luma.width / 128);
        const int sy = std::min(luma.height - 1, (2 * y + 1) * luma.height / 72);
        const uint8_t value = luma.samples[size_t(sy) * luma.width + sx];
        result.pixels[size_t(y) * 64 + x] = value;
        const int cell = (y / 9) * 4 + x / 16;
        result.histogram[size_t(cell) * 16 + value / 16] += 1.f / 144.f;
    }
    result.valid = true;
    return result;
}
inline bool hardSceneCut(const SceneSignature& a, const SceneSignature& b) {
    if (!a.valid || !b.valid) return false;
    double histogram = 0, difference = 0;
    for (size_t i = 0; i < a.histogram.size(); ++i) histogram += std::abs(a.histogram[i] - b.histogram[i]);
    for (size_t i = 0; i < a.pixels.size(); ++i) difference += std::abs(int(a.pixels[i]) - int(b.pixels[i]));
    // Require both a strong regional histogram change and a large picture difference;
    // histogram bin crossings from noise or a slow fade must not reset the model.
    return histogram / 32.0 > .60 && difference / a.pixels.size() > 32.0;
}

inline LumaSignature makeSignature(const uint8_t* luma, int lumaPitch, int pixelStep,
                                   int width, int height) {
    // pixelStep: byte distance between horizontally adjacent luma samples (1 for NV12,
    // 2 for YUY2). The grid keeps at most about 960x540 samples.
    LumaSignature s;
    int sx = std::max(1, (width + 959) / 960), sy = std::max(1, (height + 539) / 540);
    s.width = width / sx; s.height = height / sy;
    s.samples.resize(size_t(s.width) * s.height);
    for (int y = 0; y < s.height; ++y) {
        const uint8_t* row = luma + ptrdiff_t(y * sy) * lumaPitch;
        uint8_t* out = s.samples.data() + size_t(y) * s.width;
        for (int x = 0; x < s.width; ++x) out[x] = row[size_t(x * sx) * pixelStep];
    }
    return s;
}

struct SignatureDifference {
    double maxTileMean = 0;  // largest mean absolute difference of an 8x8 tile
    int changedTiles = 0;    // tiles above the current noise threshold
    int tiles = 0;
};

inline SignatureDifference compareSignatures(const LumaSignature& a, const LumaSignature& b,
                                             double threshold) {
    SignatureDifference d;
    if (a.width != b.width || a.height != b.height || a.samples.empty()) {
        d.maxTileMean = 255; d.changedTiles = 1; d.tiles = 1; return d;
    }
    const int tw = (a.width + 7) / 8, th = (a.height + 7) / 8;
    d.tiles = tw * th;
    for (int ty = 0; ty < th; ++ty) for (int tx = 0; tx < tw; ++tx) {
        int sum = 0, count = 0;
        const int y1 = std::min(a.height, ty * 8 + 8), x1 = std::min(a.width, tx * 8 + 8);
        for (int y = ty * 8; y < y1; ++y) {
            const uint8_t* pa = a.samples.data() + size_t(y) * a.width;
            const uint8_t* pb = b.samples.data() + size_t(y) * b.width;
            for (int x = tx * 8; x < x1; ++x) { sum += std::abs(int(pa[x]) - int(pb[x])); ++count; }
        }
        const double mean = count ? double(sum) / count : 0;
        d.maxTileMean = std::max(d.maxTileMean, mean);
        if (mean > threshold) ++d.changedTiles;
    }
    return d;
}

class DuplicateDetector {
public:
    bool enabled = true;
    // Returns true when the frame repeats the last unique frame.
    bool observe(LumaSignature signature, SignatureDifference* detail = nullptr) {
        if (!hasReference_ || !enabled) { accept(std::move(signature)); if (detail) *detail = {}; return false; }
        const double threshold = currentThreshold();
        SignatureDifference d = compareSignatures(reference_, signature, threshold);
        if (detail) *detail = d;
        if (d.changedTiles == 0) {
            noise_.push_back(d.maxTileMean);
            if (noise_.size() > 32) noise_.pop_front();
            ++duplicates_;
            return true;
        }
        accept(std::move(signature));
        return false;
    }
    void reset() { hasReference_ = false; noise_.clear(); }
    double currentThreshold() const {
        // Lossless capture repeats are exact (noise 0); encoded or scaled sources carry a
        // small noise floor. Follow 2.5x the median noise of confirmed repeats, bounded.
        if (noise_.size() < 4) return kBase;
        std::vector<double> sorted(noise_.begin(), noise_.end());
        std::nth_element(sorted.begin(), sorted.begin() + sorted.size() / 2, sorted.end());
        return std::clamp(2.5 * sorted[sorted.size() / 2], kBase, kMax);
    }
    uint64_t duplicates() const { return duplicates_; }
    static constexpr double kBase = 1.5, kMax = 6.0;
private:
    void accept(LumaSignature s) { reference_ = std::move(s); hasReference_ = true; }
    LumaSignature reference_;
    bool hasReference_ = false;
    std::deque<double> noise_;
    uint64_t duplicates_ = 0;
};

// Output timeline. Unique frames keep their capture time t_i. At every input tick tau the
// output shows content time T = tau + s*interval/M - D (s = 0..M-1), where D is a delay at
// least as long as a normal gap between unique frames. The pair with t_a <= T < t_b is
// interpolated at phase (T - t_a)/(t_b - t_a), the same rule the offline converter uses.
struct UniqueFrame { int64_t time = 0; int slot = -1; uint64_t id = 0; };

struct OutputPlan {
    enum Kind { Hold, Original, Interpolate } kind = Hold;
    int slotA = -1, slotB = -1;
    uint64_t idA = 0, idB = 0;
    double phase = 0;
    int64_t contentTime = 0;
};

class OutputTimeline {
public:
    // interval: nominal input frame interval in 100 ns units.
    void configure(int64_t interval) { interval_ = std::max<int64_t>(1, interval); }
    void reset() { frames_.clear(); gaps_.clear(); delay_ = -1; }
    void addUnique(const UniqueFrame& f) {
        if (!frames_.empty()) {
            int64_t gap = f.time - frames_.back().time;
            if (gap > 0 && gap <= 4 * interval_ + interval_ / 2) { gaps_.push_back(gap); if (gaps_.size() > 16) gaps_.pop_front(); }
        }
        frames_.push_back(f);
        while (frames_.size() > kKeep) frames_.pop_front();
    }
    // Target delay: the second largest recent gap, so isolated drops do not raise latency
    // for every following frame, bounded to one..three input intervals.
    int64_t targetDelay(bool dedup) const {
        if (!dedup || gaps_.size() < 2) return interval_;
        std::vector<int64_t> g(gaps_.begin(), gaps_.end());
        std::sort(g.begin(), g.end());
        int64_t d = g[g.size() - 2];
        return std::clamp<int64_t>(d, interval_, 3 * interval_);
    }
    // Called once per input tick. A longer delay is taken at once (otherwise the output
    // would run past the newest picture and stall). A shorter one is approached by at most
    // 1/30 of an interval per tick, so playback runs a fraction faster for a moment instead
    // of skipping content time, which would be a visible jump.
    void advance(bool dedup) {
        const int64_t target = targetDelay(dedup);
        if (delay_ < 0 || target >= delay_) { delay_ = target; return; }
        delay_ -= std::min<int64_t>(std::max<int64_t>(1, interval_ / 30), delay_ - target);
    }
    int64_t delay() const { return delay_ < 0 ? interval_ : delay_; }
    OutputPlan plan(int64_t tau, int sub, int multiplier, bool dedup, bool interpolate) const {
        (void)dedup;
        return planAt(tau + sub * interval_ / std::max(1, multiplier) - delay(), interpolate);
    }
    OutputPlan planAt(int64_t T, bool interpolate = true) const {
        OutputPlan p;
        if (frames_.empty()) return p;
        p.contentTime = T;
        const UniqueFrame& last = frames_.back();
        if (T >= last.time) { p.kind = OutputPlan::Original; p.slotA = last.slot; p.idA = last.id; return p; }
        if (T < frames_.front().time) { p.kind = OutputPlan::Original; p.slotA = frames_.front().slot; p.idA = frames_.front().id; return p; }
        for (size_t i = 0; i + 1 < frames_.size(); ++i) {
            const UniqueFrame& a = frames_[i]; const UniqueFrame& b = frames_[i + 1];
            if (T < a.time || T >= b.time) continue;
            const double phase = double(T - a.time) / double(b.time - a.time);
            p.slotA = a.slot; p.idA = a.id; p.slotB = b.slot; p.idB = b.id; p.phase = phase;
            // Long gaps are static scenes; do not stretch motion across them. Outputs within
            // 4% of an original frame show that frame (offline rule uses 5% of an interval).
            const bool staticGap = (b.time - a.time) > 4 * interval_ + interval_ / 2;
            if (!interpolate || staticGap || phase < 0.04) { p.kind = OutputPlan::Original; p.phase = 0; }
            else if (phase > 0.96) { p.kind = OutputPlan::Original; p.slotA = b.slot; p.idA = b.id; p.phase = 0; }
            else p.kind = OutputPlan::Interpolate;
            return p;
        }
        return p;
    }
    size_t size() const { return frames_.size(); }
    static constexpr size_t kKeep = 4;
private:
    int64_t interval_ = 166667;
    int64_t delay_ = -1;
    std::deque<UniqueFrame> frames_;
    std::deque<int64_t> gaps_;
};

} // namespace fdl
