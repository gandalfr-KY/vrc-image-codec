// AV1 rules/tables and range arithmetic: AOM/tinyavif, BSD-2-Clause + PATENTS. GPU scheduling is project code.
Shader "VrcAvif/Syntax"
{
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always
        CGINCLUDE


#include "Av1Events.hlsl"
#include "UnityCG.cginc"
Texture2D<float4> _Packet, _Prefix;
int _Cols, _Rows, _Blocks, _Qctx, _Stride, _Count;
static const int scan8[64] = {0,  1,  8,  16, 9,  2,  3,  10, 17, 24, 32, 25, 18, 11, 4,  5,
                              12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6,  7,  14, 21, 28,
                              35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
                              58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63};
static const int scan4[16] = {0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15};
static const int modeContext[13] = {0, 1, 2, 3, 4, 4, 4, 4, 3, 0, 1, 2, 0};
static int wanted, cursor;
static uint descriptor;
uint ReadPacketWord(int id)
{
    uint4 b = (uint4)round(_Packet.Load(int3(id % 512, id / 512, 0)) * 255);
    return b.x | (b.y << 8) | (b.z << 16) | (b.w << 24);
}
float4 PackBytes(uint v)
{
    return float4(v & 255, (v >> 8) & 255, (v >> 16) & 255, v >> 24) / 255.;
}
int ReadPrefix(int id)
{
    return id < 0 ? 0 : (int)_Prefix.Load(int3(id % 1024, id / 1024, 0)).r;
}
int PixelIndex(v2f_img i)
{
    int2 p = (int2)i.pos.xy;
    return p.y * 1024 + p.x;
}
void EmitEvent(int context, int symbol, int length, int kind)
{
    if (cursor == wanted) descriptor = PackEvent(context, symbol, length, kind);
    cursor++;
}
void EmitLiteral(int value, int bits)
{
    [loop] for (int k = bits - 1; k >= 0; k--) EmitEvent(UniformContext, (value >> k) & 1, 1, UniformEvent);
}
void EmitGolomb(int value)
{
    value++;
    int bits = firstbithigh((uint)value);
    EmitLiteral(0, bits);
    EmitLiteral(value, bits + 1);
}
int PlaneHeader(int block, int plane)
{
    return block * 99 + (plane == 0 ? 0 : plane == 1 ? 65 : 82);
}
int ReadCoefficient(int h, int plane, int k)
{
    int loc = plane == 0 ? scan8[k] : scan4[k];
    return (int)(ReadPacketWord(h + 1 + loc) & 65535) - 32768;
}
bool IsSkippedBlock(int block)
{
    return (ReadPacketWord(block * 99) & 255) == 0 && (ReadPacketWord(block * 99 + 65) & 255) == 0 &&
           (ReadPacketWord(block * 99 + 82) & 255) == 0;
}
int ReadDcSign(int block, int plane)
{
    int h = PlaneHeader(block, plane);
    if ((ReadPacketWord(h) & 255) == 0) return 0;
    return sign(ReadCoefficient(h, plane, 0));
}
void EmitPartition(int bx, int by, int size)
{
    bool subRows = by * 2 + size / 8 < _Rows * 2, subCols = bx * 2 + size / 8 < _Cols * 2;
    int context = (size == 16 ? 3 : size == 32 ? 39 : 75) + ((bx > 0 ? 2 : 0) + (by > 0 ? 1 : 0)) * 9;
    if (subRows && subCols)
        EmitEvent(context, 3, 9, 0);
    else if (subCols)
        EmitEvent(context, 1, 9, 1);
    else if (subRows)
        EmitEvent(context, 1, 9, 2);
}
void GeneratePlaneSyntax(int bx, int by, int plane)
{
    int block = by * _Cols + bx, h = PlaneHeader(block, plane);
    uint hdr = ReadPacketWord(h);
    int eob = hdr & 255;
    int tx = plane == 0 ? 1 : 0, pt = plane == 0 ? 0 : 1, ReadTotal = plane == 0 ? 64 : 16;
    int zero = 0;
    if (plane > 0)
        zero = 7 + (by > 0 && ((ReadPacketWord(PlaneHeader(block - _Cols, plane)) >> 8) & 255) != 0 ? 1 : 0) +
               (bx > 0 && ((ReadPacketWord(PlaneHeader(block - 1, plane)) >> 8) & 255) != 0 ? 1 : 0);
    EmitEvent(164 + (_Qctx * 2 + tx) * 13 + zero, eob == 0 ? 1 : 0, 1, 0);
    if (eob == 0 || cursor > wanted) return;
    if (plane == 0)
    {
        int type = (ReadPacketWord(block * 99) >> 24) & 7;
        EmitEvent(112 + ((ReadPacketWord(block * 99) >> 16) & 255) * 4,
                  type == 4   ? 0
                  : type == 0 ? 1
                  : type == 1 ? 3
                  : type == 2 ? 4
                              : 2,
                  4, 0);
    }
    int eobClass = eob <= 1 ? 0 : firstbithigh((uint)(eob - 1)) + 1;
    EmitEvent((plane == 0 ? 268 : 316) + (_Qctx * 2 + pt) * (plane == 0 ? 6 : 4), eobClass,
              plane == 0 ? 6 : 4, 0);
    if (eobClass > 1)
    {
        int bottom = (1 << (eobClass - 1)) + 1, shift = eobClass - 2, bit = ((eob - bottom) >> shift) & 1;
        EmitEvent((plane == 0 ? 348 : 388) + (_Qctx * 2 + pt) * (plane == 0 ? 5 : 3) + shift, bit, 1, 0);
        EmitLiteral(eob - bottom - (bit << shift), shift);
    }
    int family = (_Qctx * 2 + tx) * 2 + pt;
    [loop] for (int k = eob - 1; k >= 0; k--)
    {
        if (cursor > wanted) return;
        int loc = plane == 0 ? scan8[k] : scan4[k];
        uint packed = ReadPacketWord(h + 1 + loc);
        int magnitude = abs((int)(packed & 65535) - 32768);
        if (k == eob - 1)
        {
            int ctx = k == 0 ? 0 : k <= ReadTotal / 8 ? 1 : k <= ReadTotal / 4 ? 2 : 3;
            EmitEvent(412 + family * 4 + ctx, min(magnitude - 1, 2), 2, 0);
        }
        else
            EmitEvent(540 + family * 26 + ((packed >> 16) & 255), min(magnitude, 3), 3, 0);
        if (magnitude > 2)
        {
            int level = 3;
            [unroll] for (int j = 0; j < 4; j++)
            {
                int value = min(magnitude - level, 3);
                EmitEvent(1788 + family * 21 + ((packed >> 24) & 255), value, 3, 0);
                level += value;
                if (value < 3) break;
            }
        }
    }
    int dc = ReadCoefficient(h, plane, 0);
    if (dc != 0)
    {
        int net =
            (by > 0 ? ReadDcSign(block - _Cols, plane) : 0) + (bx > 0 ? ReadDcSign(block - 1, plane) : 0);
        EmitEvent(2796 + (_Qctx * 2 + pt) * 3 + (net == 0 ? 0 : net < 0 ? 1 : 2), dc < 0 ? 1 : 0, 1, 0);
    }
    if (abs(dc) >= 15) EmitGolomb(abs(dc) - 15);
    [loop] for (int k = 1; k < eob; k++)
    {
        if (cursor > wanted) return;
        int value = ReadCoefficient(h, plane, k), magnitude = abs(value);
        if (value != 0) EmitEvent(UniformContext, value < 0 ? 1 : 0, 1, UniformEvent);
        if (magnitude >= 15) EmitGolomb(magnitude - 15);
    }
}
void GenerateBlockSyntax(int id, int target)
{
    wanted = target;
    cursor = 0;
    descriptor = 0;
    int leaf = id % 64, sb = id / 64, sbCols = (_Cols + 7) / 8;
    int bx = (sb % sbCols) * 8 + (leaf & 1) + ((leaf >> 1) & 2) + ((leaf >> 2) & 4);
    int by = (sb / sbCols) * 8 + ((leaf >> 1) & 1) + ((leaf >> 2) & 2) + ((leaf >> 3) & 4);
    if (bx >= _Cols || by >= _Rows) return;
    if (leaf == 0) EmitPartition(bx, by, 64);
    if (leaf % 16 == 0) EmitPartition(bx, by, 32);
    if (leaf % 4 == 0) EmitPartition(bx, by, 16);
    EmitEvent(0, 0, 3, 0);
    int block = by * _Cols + bx;
    bool skip = IsSkippedBlock(block);
    int ctx =
        (by > 0 && IsSkippedBlock(block - _Cols) ? 1 : 0) + (bx > 0 && IsSkippedBlock(block - 1) ? 1 : 0);
    EmitEvent(3900 + ctx, skip ? 1 : 0, 1, 0);
    uint hdr = ReadPacketWord(block * 99);
    int mode = (hdr >> 16) & 255;
    int above = by > 0 ? (ReadPacketWord((block - _Cols) * 99) >> 16) & 255 : 0,
        left = bx > 0 ? (ReadPacketWord((block - 1) * 99) >> 16) & 255 : 0;
    EmitEvent(3300 + (modeContext[above] * 5 + modeContext[left]) * 12, mode, 12, 0);
    if (mode >= 1 && mode <= 8) EmitEvent(3210 + (mode - 1) * 6, (hdr >> 27), 6, 0);
    uint u = ReadPacketWord(block * 99 + 65), v = ReadPacketWord(block * 99 + 82);
    int au = ((u >> 16) & 255) - 16, av = ((v >> 16) & 255) - 16, uv = u >> 24;
    EmitEvent(2928 + mode * 13, uv, 13, 0);
    if (uv >= 1 && uv <= 8) EmitEvent(3210 + (uv - 1) * 6, 3, 6, 0);
    if (uv == 13)
    {
        int su = au == 0 ? 0 : au < 0 ? 1 : 2, sv = av == 0 ? 0 : av < 0 ? 1 : 2;
        EmitEvent(3109, su * 3 + sv - 1, 7, 0);
        if (su != 0) EmitEvent(3116 + ((su - 1) * 3 + sv) * 15, abs(au) - 1, 15, 0);
        if (sv != 0) EmitEvent(3116 + ((sv - 1) * 3 + su) * 15, abs(av) - 1, 15, 0);
    }
    if (skip || cursor > wanted) return;
    [unroll] for (int plane = 0; plane < 3; plane++) GeneratePlaneSyntax(bx, by, plane);
}
float4 CountEvents(v2f_img i) : SV_Target
{
    int id = PixelIndex(i);
    if (id >= _Blocks) return 0;
    GenerateBlockSyntax(id, 2147483647);
    return float4(cursor, 0, 0, 0);
}
float4 ScanCounts(v2f_img i) : SV_Target
{
    int id = PixelIndex(i);
    if (id >= _Blocks) return 0;
    return float4(ReadPrefix(id) + ReadPrefix(id - _Stride), 0, 0, 0);
}
float4 ReadTotal(v2f_img i) : SV_Target
{
    return PackBytes((uint)ReadPrefix(_Blocks - 1));
}
float4 WriteEvents(v2f_img i) : SV_Target
{
    int id = PixelIndex(i);
    if (id >= _Count) return 0;
    int lo = 0, hi = _Blocks - 1;
    [loop] while (lo < hi)
    {
        int m = (lo + hi) / 2;
        if (ReadPrefix(m) <= id)
            lo = m + 1;
        else
            hi = m;
    }
    GenerateBlockSyntax(lo, id - ReadPrefix(lo - 1));
    return PackBytes(descriptor);
}
        ENDCG
        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert_img
            #pragma fragment CountEvents
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert_img
            #pragma fragment ScanCounts
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert_img
            #pragma fragment ReadTotal
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert_img
            #pragma fragment WriteEvents
            ENDCG
        }
    }
}
