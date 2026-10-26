// AV1 rules/tables and range arithmetic: AOM/tinyavif, BSD-2-Clause + PATENTS. GPU scheduling is project code.
Shader "VrcAvif/Range coding"
{
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always
        CGINCLUDE


#include "UnityCG.cginc"
#include "CodecCommon.hlsl"
// Restricted AV1 range coding, AOM-derived arithmetic. BSD-2-Clause + PATENTS.
// Save state every 2048 symbols; each output word replays only its suffix.
Texture2D<float4> _Tokens, _Headers, _Digits, _Counts, _Checkpoints;
int _TokenCount, _QuantIndex, _QuantBit, _ByteTextureWidth, _CheckpointWidth;
float4 _HeaderSizes, _HeaderPatch;
static uint2 low;
static uint range;
static int rangeCount, outputAt, wanted;
static uint4 digits;
uint2 Add64(uint2 value, uint increment)
{
    uint old = value.x;
    value.x += increment;
    value.y += (value.x < old);
    return value;
}
uint2 ShiftLeft64(uint2 value, int shift)
{
    if (shift == 0) return value;
    return uint2(value.x << shift, (value.y << shift) | (value.x >> (32 - shift)));
}
uint2 ShiftRight64(uint2 value, int shift)
{
    if (shift == 0) return value;
    if (shift >= 32) return uint2(value.y >> (shift - 32), 0);
    return uint2((value.x >> shift) | (value.y << (32 - shift)), value.y >> shift);
}
uint2 Mask64(int bits)
{
    if (bits >= 32) return uint2(0xffffffffu, (1u << (bits - 32)) - 1u);
    return uint2((1u << bits) - 1u, 0);
}
void EmitDigit(uint digit)
{
    int at = outputAt - wanted;
    if (at >= 0 && at < 4)
    {
        if (at == 0)
            digits.x = digit;
        else if (at == 1)
            digits.y = digit;
        else if (at == 2)
            digits.z = digit;
        else
            digits.w = digit;
    };
    outputAt++;
}
uint ReadToken(int index)
{
    uint4 b = (uint4)round(_Tokens.Load(int3(index & 1023, index >> 10, 0)) * 255);
    return b.x | (b.y << 8) | (b.z << 16) | (b.w << 24);
}

void FlushRange(int shift)
{
    int bytes = (rangeCount >> 3) + 1, keep = rangeCount - shift + 24 - (bytes << 3);
    uint2 emitted = ShiftRight64(low, keep);
    low &= Mask64(keep);
    [loop] for (int i = bytes - 1; i >= 0; i--)
        EmitDigit(ShiftRight64(emitted, 8 * i).x & (i == bytes - 1 ? 511u : 255u));
    rangeCount = keep + shift - 24;
}

void InitializeRange(int target)
{
    low = 0;
    range = 32768;
    rangeCount = -9;
    outputAt = 0;
    wanted = target;
    digits = 0;
}
void EncodeSymbol(uint token)
{
    uint a = token & 1023u, b = (token >> 10) & 1023u, tail = (token >> 20) & 31u;
    if (a == 1023u)
        range -= ((range >> 8) * b >> 1) + 4u * (tail - 1u);
    else
    {
        uint hi = ((range >> 8) * a >> 1) + 4u * tail, loBound = ((range >> 8) * b >> 1) + 4u * (tail - 1u);
        low = Add64(low, range - hi);
        range = hi - loBound;
    }
    if (range < 32768)
    {
        int shift = 15 - firstbithigh(range);
        rangeCount += shift;
        if (rangeCount >= 40) FlushRange(shift);
        low = ShiftLeft64(low, shift);
        range <<= shift;
    }
}
void FinishRange()
{
    int remaining = rangeCount + 10;
    uint2 final = Add64(low, 16383);
    final.x = (final.x & ~16383u) | 16384u;
    uint2 mask = Mask64(rangeCount + 16);
    [loop] while (remaining > 0)
    {
        EmitDigit(ShiftRight64(final, rangeCount + 16).x & 511u);
        final &= mask;
        remaining -= 8;
        rangeCount -= 8;
        mask = ShiftRight64(mask, 8);
    }
}
float4 WriteCheckpoints(v2f_img pixel) : SV_Target
{
    uint2 id = (uint2)pixel.pos.xy;
    int record = (int)id.y * _CheckpointWidth + (int)id.x;
    int slot = record / 2;
    int count = _TokenCount;
    if (slot > (count + 2047) / 2048) return 0;
    int stop = min(slot * 2048, count);
    InitializeRange(-100);
    [loop] for (int i = 0; i < stop; i++) EncodeSymbol(ReadToken(i));
    if ((record & 1) == 0) return float4(low.x & 65535u, low.x >> 16, low.y & 65535u, low.y >> 16);
    return float4(range, rangeCount, outputAt, stop);
}
float4 ReadCheckpoint(int record)
{
    return _Checkpoints.Load(int3(record % _CheckpointWidth, record / _CheckpointWidth, 0));
}
int RestoreCheckpoint(int slot, int target)
{
    float4 lo = ReadCheckpoint(slot * 2);
    float4 state = ReadCheckpoint(slot * 2 + 1);
    low = uint2((uint)lo.x | ((uint)lo.y << 16), (uint)lo.z | ((uint)lo.w << 16));
    range = (uint)state.x;
    rangeCount = (int)state.y;
    outputAt = (int)state.z;
    wanted = target;
    digits = 0;
    return (int)state.w;
}
float4 CountBytes(v2f_img pixel) : SV_Target
{
    uint2 id = (uint2)pixel.pos.xy;
    int count = _TokenCount;
    RestoreCheckpoint((count + 2047) / 2048, -100);
    FinishRange();
    return float4(outputAt, 0, 0, 0);
}
float4 WriteDigits(v2f_img pixel) : SV_Target
{
    uint2 id = (uint2)pixel.pos.xy;
    int target = ((int)id.y * _ByteTextureWidth + (int)id.x) * 4;
    int length = (int)_Counts.Load(int3(0, 0, 0)).r;
    if (target >= length) return 0;
    int count = _TokenCount;
    int left = 0, right = (count + 2047) / 2048;
    [loop] while (left < right)
    {
        int mid = (left + right + 1) / 2;
        int position = (int)ReadCheckpoint(mid * 2 + 1).z;
        if (position <= target)
            left = mid;
        else
            right = mid - 1;
    }
    int from = RestoreCheckpoint(left, target);
    [loop] for (int i = from; i < count; i++)
    {
        EncodeSymbol(ReadToken(i));
        if (outputAt >= target + 4) return (float4)digits;
    }
    FinishRange();
    return (float4)digits;
}
uint ReadDigit(int index)
{
    int wordIndex = index / 4;
    float4 word = _Digits.Load(int3(wordIndex % _ByteTextureWidth, wordIndex / _ByteTextureWidth, 0));
    return (uint)round(word[index % 4]);
}
uint SelectCarry(int3 value, int at)
{
    return at == 0 ? value.x : at == 1 ? value.y : value.z;
}
uint ResolveCarry(int index, int length)
{
    if (index >= length) return 0;
    int3 effect = int3(0, 1, 2);
    [loop] for (int next = index + 1; next < length; next++)
    {
        int digit = (int)ReadDigit(next);
        int3 g = (digit + int3(0, 1, 2)) >> 8;
        effect = int3(SelectCarry(effect, g.x), SelectCarry(effect, g.y), SelectCarry(effect, g.z));
        if (effect.x == effect.y && effect.y == effect.z) break;
    }
    return (ReadDigit(index) + (uint)effect.x) & 255u;
}
uint AssembleByte(int at, int length, int page, int headerLength)
{
    if (at >= headerLength) return ResolveCarry(at - headerLength, length);
    int iloc = (int)_HeaderPatch.x, mdat = (int)_HeaderPatch.y, content = (int)_HeaderPatch.z,
        frame = (int)_HeaderPatch.w;
    if (at >= iloc && at < iloc + 4)
        return ((uint)(headerLength - content + length) >> (24 - 8 * (at - iloc))) & 255u;
    if (at >= mdat && at < mdat + 4)
        return ((uint)(headerLength - mdat + length) >> (24 - 8 * (at - mdat))) & 255u;
    int frameStart = headerLength - frame, leb = frameStart - page - 1;
    if (at >= leb && at < frameStart)
        return (((uint)(length + frame) >> (7 * (at - leb))) & 127u) | (at < frameStart - 1 ? 128u : 0u);
    uint4 bytes = (uint4)round(_Headers.Load(int3(at / 4, page, 0)) * 255);
    uint value = bytes[at % 4];
    if (at >= frameStart + _QuantBit / 8 && at <= frameStart + (_QuantBit + 7) / 8)
    {
        [unroll] for (int bit = 0; bit < 8; bit++)
        {
            int q = (at - frameStart) * 8 + bit - _QuantBit;
            if (q >= 0 && q < 8)
                value = (value & ~(1u << (7 - bit))) | (((uint)(_QuantIndex >> (7 - q)) & 1u) << (7 - bit));
        }
    }
    return value;
}
float4 WriteAvif(v2f_img pixel) : SV_Target
{
    uint2 id = (uint2)pixel.pos.xy;
    int word = (int)id.y * _ByteTextureWidth + (int)id.x, length = (int)_Counts.Load(int3(0, 0, 0)).r;
    int payload = length + (int)_HeaderPatch.w, page = payload >= 2097152 ? 3 : payload >= 16384 ? 2 : payload >= 128 ? 1 : 0;
    int headerLength = (int)_HeaderSizes[page], total = headerLength + length;
    if (word == 0) return float4(total & 255, (total >> 8) & 255, (total >> 16) & 255, (total >> 24) & 255) / 255.;
    int at = (word - 1) * 4;
    if (at >= total) return 0;
    return float4(AssembleByte(at, length, page, headerLength),
                  AssembleByte(at + 1, length, page, headerLength),
                  AssembleByte(at + 2, length, page, headerLength),
                  AssembleByte(at + 3, length, page, headerLength)) /
           255.;
}
        ENDCG
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
            #pragma fragment CountBytes
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert_img
            #pragma fragment WriteDigits
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert_img
            #pragma fragment WriteAvif
            ENDCG
        }
    }
}
