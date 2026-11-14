// HLSL sources for the live engine, compiled at start-up with d3dcompiler_47 (part of
// Windows). Written for FrameDoubler; no third-party shader code.
#pragma once

namespace fdl::shaders {

// Capture YUV -> gamma-encoded RGB (the working space; the offline converter also
// interpolates gamma-encoded RGB).
inline const char* kConvert = R"(
cbuffer Params : register(b0) {
    uint2 size; uint format; uint pad0;   // 0 NV12, 1 YUY2, 2 UYVY, 3 BGRA/RGB32
    float yOffset; float yScale; float cScale; float cOffset;
    float rv; float gu; float gv; float bu;
};
Texture2D<float> lumaPlane : register(t0);
Texture2D<float2> chromaPlane : register(t1);
Texture2D<float4> packed : register(t2);
Texture2D<float4> rgbFrame : register(t3);
SamplerState linearClamp : register(s0);
RWTexture2D<unorm float4> outFrame : register(u0);
float3 toRgb(float y, float2 c) {
    y = (y - yOffset) * yScale; c = (c - cOffset) * cScale;
    return saturate(float3(y + rv * c.y, y + gu * c.x + gv * c.y, y + bu * c.x));
}
[numthreads(16, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= size)) return;
    if (format == 3) { outFrame[id.xy] = float4(rgbFrame.Load(int3(id.xy, 0)).rgb, 1); return; }
    float y; float2 c;
    if (format == 0) {
        y = lumaPlane.Load(int3(id.xy, 0));
        // MPEG-2 ("left") chroma siting, the 4:2:0 default: sample (i, j) sits on luma
        // column 2i and between rows 2j and 2j+1. Luma pixel (x, y) therefore maps to chroma
        // texel coordinates (x / 2 + 0.5, y / 2 + 0.25).
        float2 chromaSize = float2(size) * 0.5;
        float2 uv = float2(id.x * 0.5 + 0.5, id.y * 0.5 + 0.25) / chromaSize;
        c = chromaPlane.SampleLevel(linearClamp, uv, 0);
    } else {
        uint px = id.x >> 1;
        float4 p = packed.Load(int3(px, id.y, 0));          // Y0 U Y1 V
        y = format == 2 ? ((id.x & 1) ? p.a : p.g) : ((id.x & 1) ? p.b : p.r);
        if (id.x & 1) {
            float4 q = packed.Load(int3(min(px + 1, (size.x >> 1) - 1), id.y, 0));
            c = format == 2 ? 0.5 * (float2(p.r, p.b) + float2(q.r, q.b)) : 0.5 * (float2(p.g, p.a) + float2(q.g, q.a));
        } else c = format == 2 ? float2(p.r, p.b) : float2(p.g, p.a);
    }
    outFrame[id.xy] = float4(toRgb(y, c), 1);
}
)";

// Half-resolution luma from the RGB frame, then further halvings: the motion pyramid.
inline const char* kLumaFromRgb = R"(
cbuffer Params : register(b0) { uint2 outSize; uint2 inSize; };
Texture2D<float4> src : register(t0);
RWTexture2D<unorm float> dst : register(u0);
[numthreads(16, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= outSize)) return;
    int2 p = int2(id.xy * 2); int2 m = int2(inSize) - 1;
    float3 s = src.Load(int3(min(p, m), 0)).rgb + src.Load(int3(min(p + int2(1, 0), m), 0)).rgb +
               src.Load(int3(min(p + int2(0, 1), m), 0)).rgb + src.Load(int3(min(p + int2(1, 1), m), 0)).rgb;
    dst[id.xy] = dot(s * 0.25, float3(0.2126, 0.7152, 0.0722));
}
)";

inline const char* kLumaHalve = R"(
cbuffer Params : register(b0) { uint2 outSize; uint2 inSize; };
Texture2D<float> src : register(t0);
RWTexture2D<unorm float> dst : register(u0);
[numthreads(16, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= outSize)) return;
    int2 p = int2(id.xy * 2); int2 m = int2(inSize) - 1;
    dst[id.xy] = 0.25 * (src.Load(int3(min(p, m), 0)) + src.Load(int3(min(p + int2(1, 0), m), 0)) +
                         src.Load(int3(min(p + int2(0, 1), m), 0)) + src.Load(int3(min(p + int2(1, 1), m), 0)));
}
)";

// Symmetric (bilateral) block matching on one pyramid level. For a block centred at c in
// the *intermediate* picture, the vector v pairs A(c - v/2) with B(c + v/2), so every
// output position owns exactly one vector (no holes or overlaps when interpolating).
// Vectors are stored in full-resolution pixels at every level.
// mode 0: full search (coarsest level); 1: predictors from the parent level, then
// refinement; 2: propagation, the 3x3 neighbours on the same level, then refinement.
// Each 8x8 block is matched over an overlapping 12x12 window; samples that fall outside
// the picture on either side are left out, so border blocks are not pulled to zero.
inline const char* kMotion = R"(
cbuffer Params : register(b0) {
    uint2 levelSize; uint2 grid;
    uint2 parentGrid; uint mode; uint finest;
    float scale; float lambda; int radius; uint pad0;
};
Texture2D<float> lumaA : register(t0);
Texture2D<float> lumaB : register(t1);
Texture2D<float2> predictors : register(t2);
SamplerState linearClamp : register(s0);
RWTexture2D<float2> field : register(u0);
RWTexture2D<float> costOut : register(u1);
RWByteAddressBuffer totals : register(u2);
float blockCost(int2 origin, float2 vLevel) {
    float2 size = float2(levelSize), inv = 1.0 / size;
    float sum = 0, count = 0;
    [loop] for (int y = -2; y < 10; ++y) [unroll] for (int x = -2; x < 10; ++x) {
        float2 p = float2(origin + int2(x, y)) + 0.5;
        float2 pa = p - 0.5 * vLevel, pb = p + 0.5 * vLevel;
        if (any(pa < 0) || any(pa > size) || any(pb < 0) || any(pb > size)) continue;
        sum += abs(lumaA.SampleLevel(linearClamp, pa * inv, 0) - lumaB.SampleLevel(linearClamp, pb * inv, 0));
        count += 1;
    }
    return count >= 24 ? sum / count : 1.0;
}
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= grid)) return;
    int2 origin = int2(id.xy) * 8;
    float2 best = 0; float bestCost = 1e9;
    float zeroCost = blockCost(origin, 0);
    float2 pred = 0;
    if (mode == 0) {
        for (int dy = -radius; dy <= radius; ++dy) for (int dx = -radius; dx <= radius; ++dx) {
            float2 v = float2(dx, dy);
            float c = blockCost(origin, v) + lambda * (abs(v.x) + abs(v.y));
            if (c < bestCost) { bestCost = c; best = v; }
        }
    } else {
        bool propagate = mode == 2;
        int2 centre = propagate ? int2(id.xy) : int2(id.xy) / 2;
        int2 limit = (propagate ? int2(grid) : int2(parentGrid)) - 1;
        pred = predictors.Load(int3(clamp(centre, int2(0, 0), limit), 0)) / scale;
        bestCost = zeroCost + lambda * (abs(pred.x) + abs(pred.y)); best = 0;
        [loop] for (int i = 0; i < 9; ++i) {
            int2 q = clamp(centre + int2(i % 3 - 1, i / 3 - 1), int2(0, 0), limit);
            float2 v = predictors.Load(int3(q, 0)) / scale;
            float c = blockCost(origin, v) + lambda * (abs(v.x - pred.x) + abs(v.y - pred.y));
            if (c < bestCost) { bestCost = c; best = v; }
        }
        // Integer refinement(s), then half-pixel on the finest level.
        uint firstRound = propagate ? 1u : 0u, rounds = finest != 0 ? 3u : 2u;
        [loop] for (uint round = firstRound; round < rounds; ++round) {
            float stepSize = round < 2 ? 1.0 : 0.5;
            float2 c0 = best;
            [loop] for (int j = 0; j < 9; ++j) {
                if (j == 4) continue;
                float2 v = c0 + stepSize * float2(j % 3 - 1, j / 3 - 1);
                float c = blockCost(origin, v) + lambda * (abs(v.x - pred.x) + abs(v.y - pred.y));
                if (c < bestCost) { bestCost = c; best = v; }
            }
        }
    }
    field[id.xy] = best * scale;
    float matchCost = blockCost(origin, best);
    costOut[id.xy] = matchCost;
    if (finest != 0 && mode == 2) {
        totals.InterlockedAdd(0, uint(min(matchCost, 1.0) * 4096.0));
        totals.InterlockedAdd(4, uint(min(zeroCost, 1.0) * 4096.0));
        totals.InterlockedAdd(8, 1);
    }
}
)";

// 3x3 vector median: the candidate with the smallest summed L1 distance to the others.
inline const char* kMedian = R"(
cbuffer Params : register(b0) { uint2 grid; uint2 pad; };
Texture2D<float2> src : register(t0);
RWTexture2D<float2> dst : register(u0);
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= grid)) return;
    float2 v[9];
    [unroll] for (int i = 0; i < 9; ++i) {
        int2 q = clamp(int2(id.xy) + int2(i % 3 - 1, i / 3 - 1), int2(0, 0), int2(grid) - 1);
        v[i] = src.Load(int3(q, 0));
    }
    float bestSum = 1e9; float2 best = v[4];
    [unroll] for (int a = 0; a < 9; ++a) {
        float s = 0;
        [unroll] for (int b = 0; b < 9; ++b) s += abs(v[a].x - v[b].x) + abs(v[a].y - v[b].y);
        if (s < bestSum) { bestSum = s; best = v[a]; }
    }
    dst[id.xy] = best;
}
)";

// Motion-compensated interpolation. Each output pixel tries the vectors of the four
// nearest blocks and zero motion; a candidate's weight is its bilinear block weight
// times exp(-k * (mismatch - smallest mismatch)), so at motion boundaries the vector
// that actually matches dominates, and occlusions fall back to a blend.
inline const char* kInterpolate = R"(
cbuffer Params : register(b0) {
    uint2 size; uint2 grid;
    float phase; float blockFull; float sharpness; float cutCost;
    float4 cameraRow0; float4 cameraRow1; float4 cameraRow2;   // B -> A pixel homography, row2.w: in use
};
Texture2D<float4> frameA : register(t0);
Texture2D<float4> frameB : register(t1);
Texture2D<float2> field : register(t2);
ByteAddressBuffer totals : register(t3);
SamplerState linearClamp : register(s0);
RWTexture2D<unorm float4> outFrame : register(u0);
[numthreads(16, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= size)) return;
    float2 inv = 1.0 / float2(size);
    float2 p = float2(id.xy) + 0.5;
    uint count = max(totals.Load(8), 1u);
    float meanCost = float(totals.Load(0)) / 4096.0 / float(count);
    if (meanCost > cutCost) {   // scene change: show the nearer original
        outFrame[id.xy] = phase < 0.5 ? frameA.Load(int3(id.xy, 0)) : frameB.Load(int3(id.xy, 0));
        return;
    }
    float2 g = p / blockFull - 0.5;
    int2 g0 = int2(floor(g)); float2 f = g - float2(g0);
    float2 vec[6]; float wb[6];
    [unroll] for (int i = 0; i < 4; ++i) {
        int2 q = clamp(g0 + int2(i & 1, i >> 1), int2(0, 0), int2(grid) - 1);
        vec[i] = field.Load(int3(q, 0));
        wb[i] = ((i & 1) ? f.x : 1 - f.x) * ((i >> 1) ? f.y : 1 - f.y) + 1e-4;
    }
    vec[4] = 0; wb[4] = 0.08;
    // Global camera motion (turn, roll, zoom): the displacement of this point from A to B.
    // Blocks follow it only piecewise; near rotation centres and edges it is the better guess.
    vec[5] = 0; wb[5] = 0;
    if (cameraRow2.w > 0.5) {
        float3 h = float3(p, 1);
        float w = dot(cameraRow2.xyz, h);
        vec[5] = p - float2(dot(cameraRow0.xyz, h), dot(cameraRow1.xyz, h)) / w;
        wb[5] = 0.5;
    }
    float3 colour[6]; float mismatch[6]; float smallest = 1e9;
    [unroll] for (int j = 0; j < 6; ++j) {
        float3 a = frameA.SampleLevel(linearClamp, (p - phase * vec[j]) * inv, 0).rgb;
        float3 b = frameB.SampleLevel(linearClamp, (p + (1 - phase) * vec[j]) * inv, 0).rgb;
        colour[j] = lerp(a, b, phase);
        mismatch[j] = dot(abs(a - b), 1.0 / 3.0);
        smallest = min(smallest, mismatch[j]);
    }
    float3 sum = 0; float wsum = 0;
    [unroll] for (int k = 0; k < 6; ++k) {
        float w = wb[k] * exp(-sharpness * (mismatch[k] - smallest));
        sum += colour[k] * w; wsum += w;
    }
    outFrame[id.xy] = float4(sum / wsum, 1);
}
)";

// Post-process anti-aliasing written for FrameDoubler: find a luma edge, its
// orientation and how far it runs along the edge, then re-sample across the edge by the
// coverage that geometry implies (with a sub-pixel term for isolated pixels).
// Per-pixel motion for frame generators that take motion vectors: the block field of a pair
// (displacement from the earlier to the later picture, in pixels) is smoothed to every
// pixel and reversed, so each value points from the later picture back to the earlier one.
inline const char* kMotionVectors = R"(
cbuffer Params : register(b0) {
    uint2 size; uint2 grid;
    float blockFull; float3 pad;
};
Texture2D<float2> field : register(t0);
RWTexture2D<float2> outMotion : register(u0);
[numthreads(16, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= size)) return;
    float2 g = (float2(id.xy) + 0.5) / blockFull - 0.5;
    int2 g0 = int2(floor(g)); float2 f = g - float2(g0);
    float2 v = 0;
    [unroll] for (int i = 0; i < 4; ++i) {
        int2 q = clamp(g0 + int2(i & 1, i >> 1), int2(0, 0), int2(grid) - 1);
        v += field.Load(int3(q, 0)) * (((i & 1) ? f.x : 1 - f.x) * ((i >> 1) ? f.y : 1 - f.y));
    }
    outMotion[id.xy] = -v;
}
)";

inline const char* kAntiAlias = R"(
cbuffer Params : register(b0) {
    uint2 size; uint steps; uint pad0;
    float edgeMin; float edgeRel; float subpixel; float pad1;
};
Texture2D<float4> src : register(t0);
SamplerState linearClamp : register(s0);
RWTexture2D<unorm float4> dst : register(u0);
float lumaOf(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
float lumaAt(int2 q) { return lumaOf(src.Load(int3(clamp(q, int2(0, 0), int2(size) - 1), 0)).rgb); }
float lumaUv(float2 uv) { return lumaOf(src.SampleLevel(linearClamp, uv, 0).rgb); }
[numthreads(16, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= size)) return;
    int2 q = int2(id.xy);
    float4 centre = src.Load(int3(q, 0));
    float c = lumaOf(centre.rgb);
    float n = lumaAt(q + int2(0, -1)), s = lumaAt(q + int2(0, 1)), e = lumaAt(q + int2(1, 0)), w = lumaAt(q + int2(-1, 0));
    float hi = max(c, max(max(n, s), max(e, w))), lo = min(c, min(min(n, s), min(e, w)));
    float range = hi - lo;
    if (range < max(edgeMin, hi * edgeRel)) { dst[id.xy] = centre; return; }
    float ne = lumaAt(q + int2(1, -1)), nw = lumaAt(q + int2(-1, -1)), se = lumaAt(q + int2(1, 1)), sw = lumaAt(q + int2(-1, 1));
    // Change across rows (a horizontal edge) versus across columns.
    float across = 2 * abs(n + s - 2 * c) + abs(ne + se - 2 * e) + abs(nw + sw - 2 * w);
    float along  = 2 * abs(e + w - 2 * c) + abs(ne + nw - 2 * n) + abs(se + sw - 2 * s);
    bool horizontal = across >= along;
    float l1 = horizontal ? n : w, l2 = horizontal ? s : e;
    float g1 = abs(l1 - c), g2 = abs(l2 - c);
    bool towardFirst = g1 >= g2;
    float2 inv = 1.0 / float2(size);
    float2 normal = horizontal ? float2(0, towardFirst ? -1 : 1) : float2(towardFirst ? -1 : 1, 0);
    float2 tangent = horizontal ? float2(1, 0) : float2(0, 1);
    float edgeLuma = 0.5 * (c + (towardFirst ? l1 : l2));
    float gradient = 0.25 * max(g1, g2);
    // Walk both ways along the edge midline until the luma pair stops straddling it.
    float2 mid = (float2(q) + 0.5 + 0.5 * normal) * inv;
    float d1 = 0, d2 = 0; bool done1 = false, done2 = false; float end1 = 0, end2 = 0;
    for (uint i = 1; i <= steps; ++i) {
        float stepLen = i < 4 ? 1.0 : (i < 8 ? 2.0 : 4.0);
        if (!done1) { d1 += stepLen; end1 = lumaUv(mid - d1 * tangent * inv) - edgeLuma; done1 = abs(end1) >= gradient; }
        if (!done2) { d2 += stepLen; end2 = lumaUv(mid + d2 * tangent * inv) - edgeLuma; done2 = abs(end2) >= gradient; }
        if (done1 && done2) break;
    }
    float span = d1 + d2;
    bool nearFirst = d1 < d2;
    float endLuma = nearFirst ? end1 : end2;
    // Only blend when the nearer edge end turns the way the centre does (a real step).
    bool consistent = ((c - edgeLuma) < 0) != (endLuma < 0);
    float offset = consistent ? 0.5 - min(d1, d2) / span : 0.0;
    float average = (2 * (n + s + e + w) + (ne + nw + se + sw)) / 12.0;
    float sub = saturate(abs(average - c) / range);
    sub = smoothstep(0, 1, sub); sub = sub * sub * subpixel;
    offset = max(offset, sub);
    float4 result = src.SampleLevel(linearClamp, (float2(q) + 0.5 + offset * normal) * inv, 0);
    dst[id.xy] = float4(result.rgb, centre.a);
}
)";

// Present: a full-screen triangle into the flip-model back buffer (no UAVs there).
inline const char* kPresentVs = R"(
float4 main(uint id : SV_VertexID, out float2 uv : TEXCOORD0) : SV_Position {
    uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}
)";

// split: the left half shows the original (before anti-aliasing and frame generation), the
// right half the processed picture, with a thin accent divider.
inline const char* kPresentPs = R"(
cbuffer Params : register(b0) { float2 srcTexel; float downscale; float split; float viewWidth; float3 pad; };
Texture2D<float4> src : register(t0);
Texture2D<float4> original : register(t1);
SamplerState linearClamp : register(s0);
float3 tap(Texture2D<float4> t, float2 uv) {
    if (downscale > 1.4) {
        // Four bilinear taps cover the destination pixel's footprint when shrinking.
        float2 o = 0.25 * downscale * srcTexel;
        return 0.25 * (t.SampleLevel(linearClamp, uv + float2(-o.x, -o.y), 0).rgb + t.SampleLevel(linearClamp, uv + float2(o.x, -o.y), 0).rgb +
                       t.SampleLevel(linearClamp, uv + float2(-o.x, o.y), 0).rgb + t.SampleLevel(linearClamp, uv + float2(o.x, o.y), 0).rgb);
    }
    return t.SampleLevel(linearClamp, uv, 0).rgb;
}
float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target {
    if (split > 0.5) {
        if (abs(uv.x - 0.5) * viewWidth < 1.0) return float4(1.0, 0.549, 0.376, 1);
        if (uv.x < 0.5) return float4(tap(original, uv), 1);
    }
    return float4(tap(src, uv), 1);
}
)";

// RGB -> NV12 (BT.709 limited range) for the recorder: luma and chroma render targets.
inline const char* kToLumaPs = R"(
Texture2D<float4> src : register(t0);
float main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target {
    float3 c = src.Load(int3(pos.xy, 0)).rgb;
    return 16.0 / 255.0 + 219.0 / 255.0 * dot(c, float3(0.2126, 0.7152, 0.0722));
}
)";

inline const char* kToChromaPs = R"(
Texture2D<float4> src : register(t0);
SamplerState linearClamp : register(s0);
float2 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target {
    // MPEG-2 ("left") siting to match H.264/HEVC defaults: centred on luma column 2i with a
    // [1 2 1] horizontal filter, halfway between rows 2j and 2j+1. Two bilinear taps on
    // pixel boundaries give exactly those weights.
    float w, h; src.GetDimensions(w, h);
    float2 t = float2(2.0 * (pos.x - 0.5), 2.0 * pos.y) / float2(w, h);
    float3 c = 0.5 * (src.SampleLevel(linearClamp, t, 0).rgb + src.SampleLevel(linearClamp, t + float2(1.0 / w, 0), 0).rgb);
    float y = dot(c, float3(0.2126, 0.7152, 0.0722));
    float cb = (c.b - y) / 1.8556, cr = (c.r - y) / 1.5748;
    return 128.0 / 255.0 + 224.0 / 255.0 * float2(cb, cr);
}
)";

} // namespace fdl::shaders
