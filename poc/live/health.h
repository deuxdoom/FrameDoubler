#pragma once
#include <cstdint>

namespace fdl {
// Heartbeats describe receipt, never picture changes or audio amplitude.
inline bool packetTimedOut(int64_t began, int64_t last, int64_t now, int firstMs = 5000, int gapMs = 3000) {
    return now - (last ? last : began) >= int64_t(last ? gapMs : firstMs) * 10000;
}
}
