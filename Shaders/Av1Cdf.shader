// AV1 rules/tables and range arithmetic: AOM/tinyavif, BSD-2-Clause + PATENTS. GPU scheduling is project code.
Shader "VrcAvif/Adaptive CDF"
{
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always
        CGINCLUDE


#include "Av1Events.hlsl"
#include "UnityCG.cginc"
Texture2D<float4> _Events, _Sorted, _Initial, _Checkpoints, _Intervals;
int _Count, _Run;
float4 ReadRecord(Texture2D<float4> t, int id)
{
    return t.Load(int3(id % 1024, id / 1024, 0));
}
uint ReadEventWord(int id)
{
    uint4 b = (uint4)round(ReadRecord(_Events, id) * 255);
    return b.x | (b.y << 8) | (b.z << 16) | (b.w << 24);
}
float4 PackBytes(uint v)
{
    return float4(v & 255, (v >> 8) & 255, (v >> 16) & 255, v >> 24) / 255.;
}
int PixelIndex(v2f_img i)
{
    int2 p = (int2)i.pos.xy;
    return p.y * 1024 + p.x;
}
bool EventBefore(float4 a, float4 b)
{
    return a.x < b.x || (a.x == b.x && a.w < b.w);
}
float4 InitializeRecords(v2f_img i) : SV_Target
{
    int id = PixelIndex(i);
    if (id >= _Count) return 0;
    uint e = ReadEventWord(id);
    return float4(e & 4095, e >> 12, 0, id);
}
float4 MergeRecords(v2f_img v) : SV_Target
{
    int id = PixelIndex(v);
    if (id >= _Count) return 0;
    int base = (id / (_Run * 2)) * (_Run * 2), na = min(_Run, _Count - base),
        nb = min(_Run, max(0, _Count - base - _Run));
    int k = id - base, lo = max(0, k - nb), hi = min(k, na), a = 0, b = 0;
    [loop] while (lo <= hi)
    {
        a = (lo + hi) / 2;
        b = k - a;
        if (a > 0 && b < nb &&
            EventBefore(ReadRecord(_Sorted, base + _Run + b), ReadRecord(_Sorted, base + a - 1)))
            hi = a - 1;
        else if (b > 0 && a < na &&
                 EventBefore(ReadRecord(_Sorted, base + a), ReadRecord(_Sorted, base + _Run + b - 1)))
            lo = a + 1;
        else
            break;
    }
    if (a >= na) return ReadRecord(_Sorted, base + _Run + b);
    if (b >= nb) return ReadRecord(_Sorted, base + a);
    float4 x = ReadRecord(_Sorted, base + a), y = ReadRecord(_Sorted, base + _Run + b);
    return EventBefore(x, y) ? x : y;
}
int FindContextStart(int c)
{
    int lo = 0, hi = _Count;
    [loop] while (lo < hi)
    {
        int m = (lo + hi) / 2;
        if (ReadRecord(_Sorted, m).x < c)
            lo = m + 1;
        else
            hi = m;
    }
    return lo;
}
int ReadThreshold(int WriteCheckpoints, int j, int context, bool initial)
{
    return initial ? (int)ReadRecord(_Initial, context * 4 + j / 4)[j % 4]
                   : (int)ReadRecord(_Checkpoints, WriteCheckpoints * 4 + j / 4)[j % 4];
}
float4 WriteCheckpoints(v2f_img v) : SV_Target
{
    int at = PixelIndex(v), id = (at / 4) * 256, group = at % 4;
    if (id >= _Count) return 0;
    int2 e = (int2)ReadRecord(_Sorted, id).xy;
    if (e.x == UniformContext) return 0;
    int length = (e.y >> 4) & 15, start = FindContextStart(e.x), rank = id - start;
    int4 p = (int4)ReadRecord(_Initial, e.x * 4 + group);
    int count = 0;
    if (group * 4 >= length && group != 3) return 0;
    if (group == 3 && length <= 12)
    {
        [loop] for (int k = 0; k < rank && count < 32;
                    k++) if (((int)ReadRecord(_Sorted, start + k).y >> 8) == 0) count++;
        return float4(0, 0, 0, count);
    }
    [loop] for (int k = 0; k < rank; k++)
    {
        int code = (int)ReadRecord(_Sorted, start + k).y;
        if ((code >> 8) != 0) continue;
        int rate = 4 + (min(count, 32) >> 4) + (length > 2 ? 1 : 0), s = code & 15;
        [unroll] for (int j = 0; j < 4; j++) if (group * 4 + j < length) p[j] =
            s > group * 4 + j ? p[j] - (p[j] >> rate) : p[j] + ((32768 - p[j]) >> rate);
        count = min(count + 1, 32);
    }
    if (group == 3) p.w = count;
    return (float4)p;
}
float4 WriteIntervals(v2f_img v) : SV_Target
{
    int id = PixelIndex(v);
    if (id >= _Count) return 0;
    int2 e = (int2)ReadRecord(_Sorted, id).xy;
    int s = e.y & 15, length = (e.y >> 4) & 15, kind = e.y >> 8;
    if (kind == UniformEvent) return float4(s == 0 ? 0 : 16384, s == 0 ? 16384 : 32768, 0, 0);
    int start = FindContextStart(e.x), rank = id - start, cp = id / 256,
        prefix = max(start, cp * 256) - start;
    bool initial = prefix == 0;
    int count = initial ? 0 : (int)ReadRecord(_Checkpoints, cp * 4 + 3).w;
    if (kind == AdaptiveEvent)
    {
        int lo = s == 0 ? 0 : ReadThreshold(cp, s - 1, e.x, initial),
            hi = s == length ? 32768 : ReadThreshold(cp, s, e.x, initial);
        [loop] for (int k = prefix; k < rank; k++)
        {
            int code = (int)ReadRecord(_Sorted, start + k).y;
            if ((code >> 8) != 0) continue;
            int rate = 4 + (min(count, 32) >> 4) + (length > 2 ? 1 : 0), prev = code & 15;
            if (s > 0) lo = prev > s - 1 ? lo - (lo >> rate) : lo + ((32768 - lo) >> rate);
            if (s < length) hi = prev > s ? hi - (hi >> rate) : hi + ((32768 - hi) >> rate);
            count = min(count + 1, 32);
        }
        return float4(lo, hi, 0, 0);
    }
    int p[10];
    [unroll] for (int j = 0; j < 9; j++) p[j] = ReadThreshold(cp, j, e.x, initial);
    p[9] = 32768;
    [loop] for (int k = prefix; k < rank; k++)
    {
        int code = (int)ReadRecord(_Sorted, start + k).y;
        if ((code >> 8) != 0) continue;
        int rate = 5 + (min(count, 32) >> 4), prev = code & 15;
        [unroll] for (int j = 0; j < 9; j++) p[j] =
            prev > j ? p[j] - (p[j] >> rate) : p[j] + ((32768 - p[j]) >> rate);
        count = min(count + 1, 32);
    }
    int prob = 0;
    [unroll] for (int j = 1; j < 10; j++)
    {
        bool use = kind == BottomEdgeEvent ? (j == 2 || j == 3 || j == 4 || j == 6 || j == 7 || j == 9)
                                           : (j == 1 || j == 3 || j == 4 || j == 5 || j == 6 || j == 8);
        if (use) prob += p[j] - p[j - 1];
    }
    return float4(32768 - prob, 32768, 0, 0);
}
float4 RestoreTokenOrder(v2f_img i) : SV_Target
{
    int id = PixelIndex(i);
    if (id >= _Count) return 0;
    uint e = ReadEventWord(id);
    int c = e & 4095, s = (e >> 12) & 15, n = ((e >> 16) & 15) + 1, kind = e >> 20;
    if (kind == UniformEvent)
        return PackBytes((uint)((s == 0 ? 1023 : 256) | ((s == 0 ? 256 : 0) << 10) | ((2 - s) << 20)));
    float4 key = float4(c, 0, 0, id);
    int lo = 0, hi = _Count;
    [loop] while (lo < hi)
    {
        int m = (lo + hi) / 2;
        if (EventBefore(ReadRecord(_Sorted, m), key))
            lo = m + 1;
        else
            hi = m;
    }
    int2 p = (int2)ReadRecord(_Intervals, lo).xy;
    if (kind != 0)
    {
        s = 1;
        n = 2;
    }
    return PackBytes(
        (uint)((s == 0 ? 1023 : (32768 - p.x) >> 6) | (((32768 - p.y) >> 6) << 10) | ((n - s) << 20)));
}
        ENDCG
        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert_img
            #pragma fragment InitializeRecords
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert_img
            #pragma fragment MergeRecords
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert_img
            #pragma fragment WriteCheckpoints
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert_img
            #pragma fragment WriteIntervals
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert_img
            #pragma fragment RestoreTokenOrder
            ENDCG
        }
    }
}
