// Copyright (c) 2001-2016, Alliance for Open Media. All rights reserved.
// Restricted still-image decoder; AOM range arithmetic, BSD-2-Clause + PATENTS.
// State atlas: 4096 CDF contexts x 4 float4 cells (last.w = adaptation count),
// two range/cursor cells, then two neighbor-context cells per 8x8 block.
// Each 16-block chunk reads immutable state and scatters updates into its copy.
// Keep state/CDF on GPU; only the final validity flag is read back.
// D3D11 optimizations are disabled here because FXC rejects valid dynamic-cache GS positions (X4580).
// Restricted AV1 entropy decoder. AOM range arithmetic, BSD-2-Clause + PATENTS.
Shader "VrcImageCodec/AV1 Decode"{
SubShader{Cull Off ZWrite Off ZTest Always
CGINCLUDE

#include "CodecCommon.hlsl"
#include "UnityCG.cginc"
Texture2D<float4> _Bytes, _Initial, _State;
int _ChunkStart, _OutputHeight;
int _Start, _End, _Cols, _Rows, _Qctx, _Subsampled, _DisableCdf;
int PacketWords();
int PlaneHeader(int plane);
static uint difference, range;
static int at, rangeCount, cols, rows, qctx;
static bool valid, subsampleChroma, disableCdfUpdate;
static int keys[256], counts[256];
static int4 models[1024];

static int values[64];
static int wantedWord;
static int metaCount, metaKeys[16];
static float4 metaA[16], metaB[16];
static const int locations[25] = {0,  1,  6, 6,  21, 1,  6,  6,  21, 21, 6,  6, 21,
                                  21, 21, 6, 21, 21, 21, 21, 21, 21, 21, 21, 21};
uint ByteAt(int index)
{
    int pixel = index / 4;
    uint4 b = (uint4)round(_Bytes.Load(int3(pixel % 1024, pixel / 1024, 0)) * 255);
    return b[index % 4];
}
void Refill()
{
    int shift = 8 - rangeCount;
    [loop] while (shift >= 0 && at < _End)
    {
        difference ^= ByteAt(at++) << shift;
        rangeCount += 8;
        shift -= 8;
    }
    if (at == _End) rangeCount = 16384;
}
void NormalizeRange()
{
    if (range == 0)
    {
        valid = false;
        return;
    }
    if (range >= 32768) return;
    int shift = 15 - firstbithigh(range);
    rangeCount -= shift;
    difference = ((difference + 1) << shift) - 1;
    range <<= shift;
    if (rangeCount < 0) Refill();
}
float4 StateCell(int index)
{
    return _State.Load(int3(index % 1024, index / 1024, 0));
}
int ModelSlot(int context)
{
    int slot = (int)((uint)context * 2654435761u) & 255;
    [loop] for (int probe = 0; probe < 256; probe++)
    {
        if (keys[slot] == context + 1) return slot;
        if (keys[slot] == 0)
        {
            keys[slot] = context + 1;
            [unroll] for (int j = 0; j < 4; j++) models[slot * 4 + j] = (int4)StateCell(context * 4 + j);
            counts[slot] = models[slot * 4 + 3].w;
            return slot;
        }
        slot = (slot + 1) & 255;
    }
    valid = false;
    return 0;
}
int MetaSlot(int block)
{
    int found = -1;
    [unroll] for (int i = 0; i < 16; i++) if (i < metaCount && metaKeys[i] == block) found = i;
    return found;
}
float4 Meta(int block, int second)
{
    float4 value = StateCell(16386 + block * 2 + second);
    [unroll] for (int i = 0; i < 16; i++) if (i < metaCount && metaKeys[i] == block)
    {
        if (second != 0)
            value = metaB[i];
        else
            value = metaA[i];
    }
    return value;
}
void SetMeta(int block, int field, int value)
{
    int slot = MetaSlot(block);
    if (slot < 0)
    {
        if (metaCount == 16)
        {
            valid = false;
            return;
        }
        slot = metaCount++;
        metaKeys[slot] = block;
        metaA[slot] = StateCell(16386 + block * 2);
        metaB[slot] = StateCell(16387 + block * 2);
    }
    int k = field % 4;
    float4 v = metaA[slot];
    if (field >= 4) v = metaB[slot];
    v = float4(k == 0 ? value : v.x, k == 1 ? value : v.y, k == 2 ? value : v.z, k == 3 ? value : v.w);
    if (field < 4)
        metaA[slot] = v;
    else
        metaB[slot] = v;
}
int Prediction(int block)
{
    return (int)Meta(block, 0).x;
}
bool Skipped(int block)
{
    return Meta(block, 0).y != 0;
}
int Level(int block, int plane)
{
    float4 v = Meta(block, 0);
    if (plane == 2) return (int)Meta(block, 1).x;
    return (int)v[plane + 2];
}
int SignValue(int block, int plane)
{
    return (int)Meta(block, 1)[plane + 1];
}
int Cdf(int slot, int j)
{
    int4 value = models[slot * 4 + j / 4];
    return value[j % 4];
}
void SetCdf(int slot, int j, int value)
{
    int index = slot * 4 + j / 4;
    int4 old = models[index];
    models[index] = int4(j % 4 == 0 ? value : old.x, j % 4 == 1 ? value : old.y, j % 4 == 2 ? value : old.z,
                         j % 4 == 3 ? value : old.w);
}
int Probability(int slot, int symbol)
{
    return (symbol == 9 ? 32768 : Cdf(slot, symbol)) - (symbol == 0 ? 0 : Cdf(slot, symbol - 1));
}
int ReadAdaptive(int context, int length)
{
    int slot = ModelSlot(context);
    uint previous = range, next = range, code = difference >> 16;
    int symbol = 0;
    [loop] for (; symbol <= length; symbol++)
    {
        previous = next;
        next = symbol == length ? 0
                                : ((range >> 8) * (uint)((32768 - Cdf(slot, symbol)) >> 6) >> 1) +
                                      (uint)(4 * (length - symbol));
        if (code >= next) break;
    }
    difference -= next << 16;
    range = previous - next;
    NormalizeRange();
    if (!disableCdfUpdate)
    {
        int rate = 4 + (counts[slot] >> 4) + (length > 2 ? 1 : 0);
        [loop] for (int j = 0; j < length; j++)
        {
            int p = Cdf(slot, j);
            SetCdf(slot, j, j < symbol ? p - (p >> rate) : p + ((32768 - p) >> rate));
        }
        counts[slot] = min(counts[slot] + 1, 32);
    }
    return symbol;
}
int Bit(int p)
{
    uint split = ((range >> 8) * (uint)((32768 - p) >> 6) >> 1) + 4, threshold = split << 16;
    int value = difference >= threshold ? 0 : 1;
    if (value == 0)
    {
        difference -= threshold;
        range -= split;
    }
    else
        range = split;
    NormalizeRange();
    return value;
}
static uint packet[195];
void WritePacketByte(int address, int value)
{
    int block = address / (PacketWords() * 4);
    if (block != wantedWord / PacketWords()) return;
    int word = (address / 4) % PacketWords(), shift = (address % 4) * 8;
    packet[word] = (packet[word] & ~(255u << shift)) | (((uint)value & 255) << shift);
}
void OrPacketByte(int address, int value)
{
    int block = address / (PacketWords() * 4);
    if (block == wantedWord / PacketWords())
        packet[(address / 4) % PacketWords()] |= ((uint)value & 255) << ((address % 4) * 8);
}
static const int scan4[16] = {0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15};
static const int scan8[64] = {0,  1,  8,  16, 9,  2,  3,  10, 17, 24, 32, 25, 18, 11, 4,  5,
                              12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6,  7,  14, 21, 28,
                              35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
                              58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63};
static const int modeContexts[13] = {0, 1, 2, 3, 4, 4, 4, 4, 3, 0, 1, 2, 0};
int Scan(int k, int count)
{
    return count == 64 ? scan8[k] : scan4[k];
}
int Literal(int bits)
{
    int value = 0;
    for (int i = 0; i < bits; i++)
        value = (value << 1) | Bit(16384);
    return value;
}
int Golomb()
{
    int bits = 0;
    while (valid && Bit(16384) == 0)
    {
        if (++bits > 15)
        {
            valid = false;
            return 0;
        }
    }
    return (1 << bits) + Literal(bits) - 1;
}
void Partition(int bx, int by, int size)
{
    int context = (size == 16 ? 3 : size == 32 ? 39 : 75) + ((bx > 0 ? 2 : 0) + (by > 0 ? 1 : 0)) * 9;
    bool subRows = by * 2 + size / 8 < rows * 2, subCols = bx * 2 + size / 8 < cols * 2;
    if (subRows && subCols)
    {
        if (ReadAdaptive(context, 9) != 3) valid = false;
        return;
    }
    int slot = ModelSlot(context), p = 0;
    if (subCols)
    {
        p = Probability(slot, 2) + Probability(slot, 3) + Probability(slot, 4) + Probability(slot, 6) +
            Probability(slot, 7) + Probability(slot, 9);
        if (Bit(32768 - p) != 1) valid = false;
        return;
    }
    if (subRows)
    {
        p = Probability(slot, 1) + Probability(slot, 3) + Probability(slot, 4) + Probability(slot, 5) +
            Probability(slot, 6) + Probability(slot, 8);
        if (Bit(32768 - p) != 1) valid = false;
    }
}
void Partition8()
{
    if (ReadAdaptive(0, 3) != 0) valid = false;
}
void PredictionModes(int bx, int by)
{
    int block = by * cols + bx, offset = block * PacketWords() * 4;
    int above = by > 0 ? Prediction(block - cols) : 0, left = bx > 0 ? Prediction(block - 1) : 0;
    int prediction = ReadAdaptive(3300 + (modeContexts[above] * 5 + modeContexts[left]) * 12, 12);
    int delta = 3;
    if (prediction >= 1 && prediction <= 8) delta = ReadAdaptive(3210 + (prediction - 1) * 6, 6);
    if (prediction == 7)
    {
        valid = false;
        return;
    }
    SetMeta(block, 0, prediction);
    WritePacketByte(offset + 2, prediction);
    WritePacketByte(offset + 3, (delta << 3));
    int uv = ReadAdaptive(2928 + prediction * 13, 13), au = 0, av = 0;
    if (uv >= 1 && uv <= 8)
        if (ReadAdaptive(3210 + (uv - 1) * 6, 6) != 3) valid = false;
    if (uv == 13)
    {
        int joint = ReadAdaptive(3109 + 0, 7) + 1, su = joint / 3, sv = joint % 3;
        if (su != 0) au = (ReadAdaptive(3116 + ((su - 1) * 3 + sv) * 15, 15) + 1) * (su == 1 ? -1 : 1);
        if (sv != 0) av = (ReadAdaptive(3116 + ((sv - 1) * 3 + su) * 15, 15) + 1) * (sv == 1 ? -1 : 1);
    }
    if (uv >= 3 && uv <= 8)
    {
        valid = false;
        return;
    }
    if (!subsampleChroma && uv != 0)
    {
        valid = false;
        return;
    }
    WritePacketByte(offset + 65 * 4 + 2, (au + 16));
    WritePacketByte(offset + PlaneHeader(2) * 4 + 2, (av + 16));
    WritePacketByte(offset + 65 * 4 + 3, uv);
    WritePacketByte(offset + PlaneHeader(2) * 4 + 3, uv);
}
int PacketWords()
{
    return subsampleChroma ? 99 : 195;
}
int PlaneHeader(int plane)
{
    return plane == 0 ? 0 : plane == 1 ? 65 : subsampleChroma ? 82 : 130;
}
void Plane(int bx, int by, int plane)
{
    int block = by * cols + bx, header = (block * PacketWords() + PlaneHeader(plane)) * 4,
        offset = header + 4;
    int tx = plane == 0 || !subsampleChroma ? 1 : 0, pt = plane == 0 ? 0 : 1,
        total = plane == 0 || !subsampleChroma ? 64 : 16, n = plane == 0 || !subsampleChroma ? 8 : 4,
        mi = block * 3 + plane;

    [loop] for (int i = 0; i < 64; i++) values[i] = 0;
    int zeroContext = plane == 0 ? 0
                                 : 7 + (by > 0 && Level(block - cols, plane) != 0 ? 1 : 0) +
                                       (bx > 0 && Level(block - 1, plane) != 0 ? 1 : 0);
    if (ReadAdaptive(164 + (qctx * 2 + tx) * 13 + zeroContext, 1) != 0) return;
    if (plane == 0)
    {
        int type = ReadAdaptive(112 + Prediction(block) * 4, 4);
        OrPacketByte(header + 3, (type == 0 ? 4 : type == 1 ? 0 : type == 2 ? 3 : type == 3 ? 1 : 2));
    }
    int eobClass;
    if (plane == 0 || !subsampleChroma)
        eobClass = ReadAdaptive(268 + (qctx * 2 + pt) * 6, 6);
    else
        eobClass = ReadAdaptive(316 + (qctx * 2 + pt) * 4, 4);
    int eob = eobClass == 0 ? 1 : eobClass == 1 ? 2 : (1 << (eobClass - 1)) + 1;
    if (eobClass > 1)
    {
        int shift = eobClass - 2;
        int high = ReadAdaptive((plane == 0 || !subsampleChroma ? 348 : 388) +
                                    (qctx * 2 + pt) * (plane == 0 || !subsampleChroma ? 5 : 3) + shift,
                                1);
        int low = Literal(shift);
        eob += (high << shift) + low;
    }
    WritePacketByte(header, eob);
    int family = (qctx * 2 + tx) * 2 + pt, sum = 0;
    for (int k = eob - 1; k >= 0 && valid; k--)
    {
        int loc = Scan(k, total), row = loc / n, col = loc % n, mag = 0, brMag = 0;
        if (col + 1 < n)
        {
            int c = values[loc + 1];
            mag += min(c, 3);
            brMag += min(c, 15);
        }
        if (row + 1 < n)
        {
            int c = values[loc + n];
            mag += min(c, 3);
            brMag += min(c, 15);
        }
        if (row + 1 < n && col + 1 < n)
        {
            int c = values[loc + n + 1];
            mag += min(c, 3);
            brMag += min(c, 15);
        }
        if (col + 2 < n) mag += min(values[loc + 2], 3);
        if (row + 2 < n) mag += min(values[loc + n * 2], 3);
        int value;
        if (k == eob - 1)
        {
            int ctx = k == 0 ? 0 : k <= total / 8 ? 1 : k <= total / 4 ? 2 : 3;
            value = ReadAdaptive(412 + family * 4 + ctx, 2) + 1;
        }
        else
        {
            int ctx = loc == 0 ? 0 : min((mag + 1) / 2, 4) + locations[min(row, 4) * 5 + min(col, 4)];
            value = ReadAdaptive(540 + family * 26 + ctx, 3);
        }
        if (value > 2)
        {
            int ctx = min((brMag + 1) / 2, 6) + (loc == 0 ? 0 : row < 2 && col < 2 ? 7 : 14);
            for (int j = 0; j < 4; j++)
            {
                int extra = ReadAdaptive(1788 + family * 21 + ctx, 3);
                value += extra;
                if (extra < 3) break;
            }
        }
        values[loc] = value;
        sum += value;
    }
    for (int k = 0; k < eob && valid; k++)
    {
        int loc = Scan(k, total), value = values[loc], negative = 0;
        if (k == 0 && value != 0)
        {
            int net =
                (by > 0 ? SignValue(block - cols, plane) : 0) + (bx > 0 ? SignValue(block - 1, plane) : 0);
            negative = ReadAdaptive(2796 + (qctx * 2 + pt) * 3 + (net == 0 ? 0 : net < 0 ? 1 : 2), 1);
        }
        if (k > 0 && value != 0) negative = Bit(16384);
        if (value >= 15)
        {
            int extra = Golomb();
            value += extra;
            sum += extra;
            if (value > 32767)
            {
                valid = false;
                break;
            }
        }
        int signedValue = negative != 0 ? -value : value, p = offset + loc * 4;
        WritePacketByte(p, (signedValue & 255));
        WritePacketByte(p + 1, ((signedValue >> 8) & 255));
        if (k == 0) SetMeta(block, 5 + plane, value == 0 ? 0 : negative != 0 ? -1 : 1);
    }
    SetMeta(block, 2 + plane, min(sum, 63));
    WritePacketByte(header + 1, Level(block, plane));
}

int SpatialBlock(int index)
{
    int sb = index / 64, leaf = index % 64, sbCols = (cols + 7) / 8;
    int bx = (sb % sbCols) * 8 + (leaf & 1) + ((leaf >> 1) & 2) + ((leaf >> 2) & 4);
    int by = (sb / sbCols) * 8 + ((leaf >> 1) & 1) + ((leaf >> 2) & 2) + ((leaf >> 3) & 4);
    return bx < cols && by < rows ? by * cols + bx : -1;
}
void DecodeChunk(int last)
{
    cols = _Cols;
    rows = _Rows;
    qctx = _Qctx;
    subsampleChroma = _Subsampled != 0;
    disableCdfUpdate = _DisableCdf != 0;
    metaCount = 0;
    [unroll] for (int m = 0; m < 16; m++)
    {
        metaKeys[m] = 0;
        metaA[m] = 0;
        metaB[m] = 0;
    }
    [unroll] for (int c = 0; c < 256; c++)
    {
        keys[c] = 0;
        counts[c] = 0;
    }
    float4 core = StateCell(16384), tail = StateCell(16385);
    difference = (uint)core.x | ((uint)core.y << 16);
    range = (uint)core.z;
    rangeCount = (int)core.w;
    at = (int)tail.x;
    valid = tail.y != 0;
    [loop] for (int index = _ChunkStart; index <= last && valid; index++)
    {
        int block = SpatialBlock(index);
        if (block < 0) continue;
        int bx = block % cols, by = block / cols, leaf = index % 64;
        if (leaf == 0) Partition(bx, by, 64);
        if (leaf % 16 == 0) Partition(bx, by, 32);
        if (leaf % 4 == 0) Partition(bx, by, 16);
        Partition8();
        int skipContext = (by > 0 && Skipped(block - cols) ? 1 : 0) + (bx > 0 && Skipped(block - 1) ? 1 : 0);
        bool omitted = ReadAdaptive(3900 + skipContext, 1) != 0;
        SetMeta(block, 1, omitted ? 1 : 0);
        PredictionModes(bx, by);
        if (!omitted)
        {
            [loop] for (int plane = 0; plane < 3 && valid; plane++) Plane(bx, by, plane);
        }
    }
}
struct StatePoint
{
    float4 pos : SV_Position;
    float4 value : TEXCOORD0;
};
struct PacketPoint
{
    float4 pos : SV_Position;
    nointerpolation uint value : TEXCOORD0;
};
float4 Position(int index)
{
    return float4(((index % 1024) + .5) * 2 / 1024. - 1, 1 - ((index / 1024) + .5) * 2 / _OutputHeight, 0, 1);
}
float4 InitializeState(v2f_img i) : SV_Target
{
    int index = (int)i.pos.y * 1024 + (int)i.pos.x;
    if (index < 16384) return _Initial.Load(int3(index % 1024, index / 1024, 0));
    if (index == 16384 || index == 16385)
    {
        at = _Start;
        range = 32768;
        difference = 0x7fffffff;
        rangeCount = -15;
        Refill();
        if (index == 16384) return float4(difference & 65535, difference >> 16, range, rangeCount);
        return float4(at, 1, 0, 0);
    }
    return 0;
}
[maxvertexcount(128)][instance(16)] void WriteState(triangle v2f_img points[3],
                                                    uint primitive : SV_PrimitiveID,
                                                    uint instance : SV_GSInstanceID,
                                                    inout PointStream<StatePoint> outputStream)
{
    if (primitive != 0 || instance > 8) return;
    // State replay emits no coefficient packet: a negative block index cannot match a decoded block.
    wantedWord = -PacketWords();
    DecodeChunk(_ChunkStart + 15);
    int count = 128;
    if (instance == 8) count = 2 + metaCount * 2;
    [loop] for (int j = 0; j < count; j++)
    {
        int target = -1;
        float4 value = 0;
        if (instance < 8)
        {
            int index = (int)instance * 128 + j, slot = index / 4, part = index % 4;
            if (keys[slot] != 0)
            {
                int4 model = models[index];
                if (part == 3) model.w = counts[slot];
                target = (keys[slot] - 1) * 4 + part;
                value = (float4)model;
            }
        }
        else if (j == 0)
        {
            target = 16384;
            value = float4(difference & 65535, difference >> 16, range, rangeCount);
        }
        else if (j == 1)
        {
            target = 16385;
            value = float4(at, valid ? 1 : 0, 0, 0);
        }
        else
        {
            int m = (j - 2) / 2, part = (j - 2) % 2;
            target = 16386 + metaKeys[m] * 2 + part;
            if (part == 0)
                value = metaA[m];
            else
                value = metaB[m];
        }
        if (target >= 0)
        {
            StatePoint item = (StatePoint)0;
            item.pos = Position(target);
            item.value = value;
            outputStream.Append(item);
        }
    }
}[maxvertexcount(195)][instance(16)] void WritePackets(triangle v2f_img points[3],
                                                       uint primitive : SV_PrimitiveID,
                                                       uint instance : SV_GSInstanceID,
                                                       inout PointStream<PacketPoint> outputStream)
{
    if (primitive != 0) return;
    cols = _Cols;
    rows = _Rows;
    subsampleChroma = _Subsampled != 0;
    int total = ((cols + 7) / 8) * ((rows + 7) / 8) * 64, index = _ChunkStart + (int)instance;
    if (index >= total) return;
    int block = SpatialBlock(index);
    if (block < 0) return;
    wantedWord = block * PacketWords();
    [loop] for (int k = 0; k < 195; k++) packet[k] = 0;
    DecodeChunk(index);
    [loop] for (int w = 0; w < PacketWords(); w++)
    {
        PacketPoint item = (PacketPoint)0;
        item.pos = Position(wantedWord + w);
        item.value = valid ? packet[w] : 0xffff00ffu;
        outputStream.Append(item);
    }
}
float4 StoreState(StatePoint item) : SV_Target
{
    return item.value;
}
float4 StorePacket(PacketPoint item) : SV_Target
{
    uint v = item.value;
    return float4(v & 255, (v >> 8) & 255, (v >> 16) & 255, v >> 24) / 255.;
}
float4 ReadStatus(v2f_img i) : SV_Target
{
    return CodecBytes(StateCell(16385).y != 0 ? 0 : 1);
}
float4 CopyState(v2f_img i) : SV_Target
{
    return StateCell((int)i.pos.y * 1024 + (int)i.pos.x);
}
ENDCG
Pass{CGPROGRAM
#pragma target 5.0
#pragma skip_optimizations d3d11
#pragma vertex vert_img
#pragma fragment InitializeState
ENDCG}
Pass{CGPROGRAM
#pragma target 5.0
#pragma skip_optimizations d3d11
#pragma vertex vert_img
#pragma geometry WriteState
#pragma fragment StoreState
ENDCG}
Pass{CGPROGRAM
#pragma target 5.0
#pragma skip_optimizations d3d11
#pragma vertex vert_img
#pragma geometry WritePackets
#pragma fragment StorePacket
ENDCG}

Pass{CGPROGRAM
#pragma target 5.0
#pragma skip_optimizations d3d11
#pragma vertex vert_img
#pragma fragment CopyState
ENDCG}
Pass{CGPROGRAM
#pragma target 5.0
#pragma skip_optimizations d3d11
#pragma vertex vert_img
#pragma fragment ReadStatus
ENDCG}
}}
