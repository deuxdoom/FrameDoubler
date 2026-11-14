// Global (camera) motion between two pictures: a homography fitted robustly to block
// vectors. Camera turns, roll and zoom move the whole picture by one projective map; moving
// objects and the HUD are outliers and get down-weighted.
#pragma once
#include <algorithm>
#include <array>
#include <cmath>
#include <vector>

namespace fdl {

struct Homography {
    double h[9] = {1, 0, 0, 0, 1, 0, 0, 0, 1};   // row-major, to ~ H * from (homogeneous)
    bool valid = false;
    double inlierShare = 0;   // share of pairs within 3 sigma
    double rms = 0;           // pixel error of the inliers
};

// pairs: {fromX, fromY, toX, toY} in pixels. Normalised DLT with iteratively reweighted
// least squares (Cauchy weights). Block fields hold many wrong vectors in flat areas, so the
// fit starts from the median shift and narrows its tolerance from 32 px down to sigma; a plain
// least-squares start is pulled away by them. Valid when at least half of the pairs agree.
inline Homography fitHomography(const std::vector<std::array<double, 4>>& pairs, double sigma = 2.0, int iterations = 10) {
    Homography result;
    const size_t n = pairs.size();
    if (n < 16) return result;
    // Conditioning: move both point sets to their centroid and scale to an average radius of sqrt(2).
    double fx = 0, fy = 0, tx = 0, ty = 0;
    for (auto& p : pairs) { fx += p[0]; fy += p[1]; tx += p[2]; ty += p[3]; }
    fx /= double(n); fy /= double(n); tx /= double(n); ty /= double(n);
    double fs = 0, ts = 0;
    for (auto& p : pairs) { fs += std::hypot(p[0] - fx, p[1] - fy); ts += std::hypot(p[2] - tx, p[3] - ty); }
    fs = fs > 0 ? std::sqrt(2.0) * double(n) / fs : 1; ts = ts > 0 ? std::sqrt(2.0) * double(n) / ts : 1;
    std::vector<double> weight(n, 1.0);
    double hn[9] = {1, 0, 0, 0, 1, 0, 0, 0, 1};
    auto tolerance = [&](int step) { return std::max(sigma, 32.0 / double(1 << std::min(step, 4))); };
    {
        std::vector<double> dx(n), dy(n);
        for (size_t i = 0; i < n; ++i) { dx[i] = pairs[i][2] - pairs[i][0]; dy[i] = pairs[i][3] - pairs[i][1]; }
        std::nth_element(dx.begin(), dx.begin() + n / 2, dx.end()); std::nth_element(dy.begin(), dy.begin() + n / 2, dy.end());
        const double mx = dx[n / 2], my = dy[n / 2], t = tolerance(0);
        for (size_t i = 0; i < n; ++i) {
            const double r = std::hypot(pairs[i][2] - pairs[i][0] - mx, pairs[i][3] - pairs[i][1] - my) / t;
            weight[i] = 1.0 / (1.0 + r * r);
        }
    }
    auto apply = [](const double* H, double x, double y, double& u, double& v) {
        const double w = H[6] * x + H[7] * y + H[8];
        u = (H[0] * x + H[1] * y + H[2]) / w; v = (H[3] * x + H[4] * y + H[5]) / w;
    };
    for (int iteration = 0; iteration < iterations; ++iteration) {
        double A[8][9] = {};   // normal equations with the right-hand side in column 8
        for (size_t i = 0; i < n; ++i) {
            const double x = (pairs[i][0] - fx) * fs, y = (pairs[i][1] - fy) * fs;
            const double u = (pairs[i][2] - tx) * ts, v = (pairs[i][3] - ty) * ts;
            const double rows[2][9] = {{x, y, 1, 0, 0, 0, -u * x, -u * y, u}, {0, 0, 0, x, y, 1, -v * x, -v * y, v}};
            for (auto& r : rows)
                for (int a = 0; a < 8; ++a)
                    for (int b = 0; b < 9; ++b) A[a][b] += weight[i] * r[a] * r[b];
        }
        // Gaussian elimination with partial pivoting.
        bool singular = false;
        for (int c = 0; c < 8 && !singular; ++c) {
            int pivot = c;
            for (int r = c + 1; r < 8; ++r) if (std::fabs(A[r][c]) > std::fabs(A[pivot][c])) pivot = r;
            if (std::fabs(A[pivot][c]) < 1e-12) { singular = true; break; }
            if (pivot != c) for (int b = 0; b < 9; ++b) std::swap(A[c][b], A[pivot][b]);
            for (int r = 0; r < 8; ++r) {
                if (r == c) continue;
                const double f = A[r][c] / A[c][c];
                for (int b = c; b < 9; ++b) A[r][b] -= f * A[c][b];
            }
        }
        if (singular) return result;
        for (int k = 0; k < 8; ++k) hn[k] = A[k][8] / A[k][k];
        hn[8] = 1;
        // Reweight by the error in pixels (denormalised).
        for (size_t i = 0; i < n; ++i) {
            double u, v; apply(hn, (pairs[i][0] - fx) * fs, (pairs[i][1] - fy) * fs, u, v);
            const double ex = u / ts + tx - pairs[i][2], ey = v / ts + ty - pairs[i][3];
            const double r = std::hypot(ex, ey) / tolerance(iteration + 1);
            weight[i] = 1.0 / (1.0 + r * r);
        }
    }
    // H = T_to^-1 * Hn * T_from, with T(p) = s * (p - c).
    const double Tf[9] = {fs, 0, -fs * fx, 0, fs, -fs * fy, 0, 0, 1};
    const double Ti[9] = {1 / ts, 0, tx, 0, 1 / ts, ty, 0, 0, 1};
    double tmp[9] = {}, H[9] = {};
    for (int r = 0; r < 3; ++r) for (int c = 0; c < 3; ++c) for (int k = 0; k < 3; ++k) tmp[r * 3 + c] += hn[r * 3 + k] * Tf[k * 3 + c];
    for (int r = 0; r < 3; ++r) for (int c = 0; c < 3; ++c) for (int k = 0; k < 3; ++k) H[r * 3 + c] += Ti[r * 3 + k] * tmp[k * 3 + c];
    for (double& value : H) value /= H[8];
    size_t inliers = 0; double squared = 0;
    for (auto& p : pairs) {
        double u, v; apply(H, p[0], p[1], u, v);
        const double e = std::hypot(u - p[2], v - p[3]);
        if (e < 3 * sigma) { ++inliers; squared += e * e; }
    }
    for (int k = 0; k < 9; ++k) result.h[k] = H[k];
    result.inlierShare = double(inliers) / double(n);
    result.rms = inliers ? std::sqrt(squared / double(inliers)) : 0;
    result.valid = std::isfinite(H[0]) && result.inlierShare > 0.5;
    return result;
}

// Correspondences from the engine's block field of a pair (Gpu::readField): each grid cell
// holds the A->B displacement of the content at the cell centre at the pair's midpoint.
// Returns current (B) -> previous (A) pairs, skipping cells that touch the picture border.
// step > 1 keeps every step-th cell in both directions (a 4K field has 32,400 cells).
inline std::vector<std::array<double, 4>> fieldPairsBtoA(const std::vector<float>& xy, int gridWidth, int gridHeight, double cell, int step = 1) {
    std::vector<std::array<double, 4>> pairs;
    pairs.reserve(size_t(gridWidth) * gridHeight);
    for (int gy = 1; gy + 1 < gridHeight; gy += step)
        for (int gx = 1; gx + 1 < gridWidth; gx += step) {
            const size_t i = (size_t(gy) * gridWidth + gx) * 2;
            const double cx = (gx + 0.5) * cell, cy = (gy + 0.5) * cell, vx = xy[i], vy = xy[i + 1];
            pairs.push_back({cx + vx / 2, cy + vy / 2, cx - vx / 2, cy - vy / 2});
        }
    return pairs;
}

} // namespace fdl
