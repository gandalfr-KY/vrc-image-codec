// Restricted still-image AV1 decoding; AOM entdec.c, BSD-2-Clause + PATENTS.
// Copyright (c) 2001-2016, Alliance for Open Media. All rights reserved.
using System;
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Rendering;
using VRC.SDKBase;

namespace TernaryStars
{
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class TernaryAvifDecoder : UdonSharpBehaviour
{
    public byte[] inputAvif;
    public Material material;
    public float frameBudgetMs = 2;
    public int diagonalsPerFrame = 8;
    [HideInInspector]
    public RenderTexture resultTexture;
    [HideInInspector]
    public bool busy, complete;
    [HideInInspector]
    public string error = "";
    [HideInInspector]
    public int width, height, decodedBlocks;
    [HideInInspector]
    public float progress, elapsedSeconds, cpuSeconds, maxStepMs;
    private byte[] data, coefficients;
    private int[] levels, signs, predictionModes, adaptationCounts, coefficientValues;
    private bool[] skippedBlocks;
    private int cols, rows, sbCols, sbRows, cursor, stage, at, end, bitAt, headerEnd, quantizerIndex, qctx;
    private uint difference, range;
    private int rangeCount, partition8Cdf, partition8Count;
    private float startedAt;
    private Texture2D upload;
    private RenderTexture quant, recon, filtered, mode, uvMode, scratch, output;
    private int diagonal;
    private readonly int[] locations = { 0,  1,  6, 6,  21, 1,  6,  6,  21, 21, 6,  6, 21,
                                         21, 21, 6, 21, 21, 21, 21, 21, 21, 21, 21, 21 };

    public void BeginDecoding()
    {
        CancelDecoding();
        data = inputAvif;
        if (data == null || data.Length < 300 || data.Length > 100000 || !material)
        {
            Fail("AVIF decoding requires a configured material and a 300..100000 byte image.");
            return;
        }
        busy = true;
        startedAt = Time.realtimeSinceStartup;
        if (!ReadProfile())
            return;
        cols = width / 8;
        rows = height / 8;
        sbCols = (cols + 7) / 8;
        sbRows = (rows + 7) / 8;
        qctx = quantizerIndex <= 20 ? 0 : quantizerIndex <= 60 ? 1 : quantizerIndex <= 120 ? 2 : 3;
        ResetProbabilities();
        coefficientValues = new int[64];
        levels = new int[cols * rows * 3];
        signs = new int[levels.Length];
        predictionModes = new int[cols * rows];
        skippedBlocks = new bool[cols * rows];
        coefficients = new byte[512 * ((cols * rows * 99 + 511) / 512) * 4];
        range = 32768;
        difference = 0x7fffffff;
        rangeCount = -15;
        Refill();
        cursor = stage = decodedBlocks = 0;
    }

    public void CancelDecoding()
    {
        busy = complete = false;
        error = "";
        progress = elapsedSeconds = cpuSeconds = maxStepMs = 0;
        resultTexture = null;
        data = coefficients = null;
        levels = signs = predictionModes = null;
        skippedBlocks = null;
    }

    private void Update()
    {
        if (busy)
            StepDecoding();
    }

    public void StepDecoding()
    {
        if (!busy)
            return;
        float before = Time.realtimeSinceStartup;
        if (stage == 0)
        {
            float until = before + Mathf.Max(.25f, frameBudgetMs) * .001f;
            do
            {
                if (cursor >= sbCols * sbRows * 64)
                {
                    BeginReconstruction();
                    break;
                }
                int index = cursor++, sb = index / 64, leaf = index % 64;
                int bx = (sb % sbCols) * 8 + (leaf & 1) + ((leaf >> 1) & 2) + ((leaf >> 2) & 4);
                int by = (sb / sbCols) * 8 + ((leaf >> 1) & 1) + ((leaf >> 2) & 2) + ((leaf >> 3) & 4);
                if (bx >= cols || by >= rows)
                    continue;
                if (leaf == 0)
                    Partition(bx, by, 64);
                if (leaf % 16 == 0)
                    Partition(bx, by, 32);
                if (leaf % 4 == 0)
                    Partition(bx, by, 16);
                Partition8();
                if (!busy)
                    break;
                int block = by * cols + bx;
                int skipContext =
                    (by > 0 && skippedBlocks[block - cols] ? 1 : 0) + (bx > 0 && skippedBlocks[block - 1] ? 1 : 0);
                bool omitted = Small(3900 + skipContext, 1) != 0;
                skippedBlocks[block] = omitted;
                PredictionModes(bx, by);
                if (!busy)
                    break;
                if (!omitted)
                    for (int plane = 0; plane < 3 && busy; plane++)
                        Plane(bx, by, plane);
                if (!busy)
                    break;
                decodedBlocks++;
                progress = .75f * decodedBlocks / (cols * rows);
            } while (busy && Time.realtimeSinceStartup < until);
        }
        else
            Reconstruct();
        float work = Time.realtimeSinceStartup - before;
        cpuSeconds += work;
        maxStepMs = Mathf.Max(maxStepMs, work * 1000);
        elapsedSeconds = Time.realtimeSinceStartup - startedAt;
    }

    private void Fail(string message)
    {
        busy = false;
        complete = false;
        error = message;
    }
    private int UInt32(int pos)
    {
        if (pos < 0 || pos > data.Length - 4)
        {
            Fail("Truncated AVIF box.");
            return 0;
        }
        uint size = ((uint)data[pos] << 24) | ((uint)data[pos + 1] << 16) | ((uint)data[pos + 2] << 8) | data[pos + 3];
        if (size > int.MaxValue)
        {
            Fail("Oversized AVIF box.");
            return 0;
        }
        return (int)size;
    }
    private bool Tag(int pos, string tag)
    {
        if (pos < 0 || pos > data.Length - 4)
            return false;
        for (int i = 0; i < 4; i++)
            if (data[pos + i] != tag[i])
                return false;
        return true;
    }
    private int Byte()
    {
        if (at >= end)
        {
            Fail("Truncated AVIF payload.");
            return 0;
        }
        return data[at++];
    }
    private int Leb()
    {
        int value = 0;
        for (int i = 0; i < 4; i++)
        {
            int b = Byte();
            value |= (b & 127) << (7 * i);
            if (b < 128)
                return value;
        }
        Fail("Invalid OBU length.");
        return 0;
    }
    private int Bits(int count)
    {
        if (!busy || bitAt + count > headerEnd * 8)
        {
            Fail("Truncated AV1 header.");
            return 0;
        }
        int value = 0;
        for (int i = 0; i < count; i++)
            value = (value << 1) | ((data[bitAt >> 3] >> (7 - (bitAt++ & 7))) & 1);
        return value;
    }
    private void Expect(int value, int count)
    {
        if (busy && Bits(count) != value)
            Fail("Unsupported AVIF profile; use this library's single-image encoder.");
    }
    private bool ReadProfile()
    {
        if (!Tag(4, "ftyp") || !Tag(8, "avif"))
        {
            Fail("Not an AVIF image.");
            return false;
        }
        int payload = -1;
        for (int pos = 0, boxes = 0; pos < data.Length && busy; boxes++)
        {
            if (boxes > 16)
            {
                Fail("Too many AVIF boxes.");
                break;
            }
            int size = UInt32(pos);
            if (!busy || size < 8 || size > data.Length - pos)
            {
                Fail("Invalid AVIF box bounds.");
                break;
            }
            if (Tag(pos + 4, "mdat"))
            {
                if (payload != -1)
                {
                    Fail("Multiple AVIF payloads.");
                    break;
                }
                payload = pos + 8;
                end = pos + size;
            }
            pos += size;
        }
        if (!busy)
            return false;
        if (payload < 0)
        {
            Fail("Missing AVIF image payload.");
            return false;
        }
        at = payload;
        if (Byte() != 0x12 || Byte() != 0 || Byte() != 0x0a)
        {
            Fail("Unsupported AV1 sequence.");
            return false;
        }
        int sequenceLength = Leb();
        if (sequenceLength < 1 || sequenceLength > end - at)
        {
            Fail("Invalid sequence bounds.");
            return false;
        }
        bitAt = at * 8;
        headerEnd = at + sequenceLength;
        Expect(0, 3);
        Expect(1, 1);
        Expect(1, 1);
        int level = Bits(5);
        Expect(15, 4);
        Expect(15, 4);
        width = Bits(16) + 1;
        height = Bits(16) + 1;
        if (width < 16 || height < 16 || width > 1920 || height > 1920 || width % 8 != 0 || height % 8 != 0 ||
            width * height > 2073600 || level != (width * height > 665856 ? 8 : 4))
        {
            Fail("Unsupported AVIF dimensions or AV1 level.");
            return false;
        }
        Expect(2, 6);
        Expect(0, 1);
        Expect(0, 1);
        Expect(1, 1);
        Expect(1, 8);
        Expect(13, 8);
        Expect(6, 8);
        Expect(1, 1);
        Expect(0, 2);
        Expect(0, 1);
        Expect(0, 1);
        Expect(1, 1);
        while (busy && bitAt < headerEnd * 8)
            Expect(0, 1);
        at = headerEnd;
        if (!busy || Byte() != 0x32)
        {
            if (busy)
                Fail("Missing AV1 frame.");
            return false;
        }
        int frameLength = Leb();
        if (frameLength != end - at)
        {
            Fail("Invalid AV1 frame length.");
            return false;
        }
        bitAt = at * 8;
        headerEnd = end;
        // The encoder subset fixes all frame features except the base quantizer.
        Expect(0, 1);
        Expect(0, 1);
        Expect(0, 1);
        Expect(1, 1);
        if (width > 64)
            Expect(0, 1);
        if (height > 64)
            Expect(0, 1);
        quantizerIndex = Bits(8);
        Expect(0, 1);
        for (int i = 0; i < 2; i++)
        {
            Expect(1, 1);
            Expect(112, 7);
        }
        Expect(0, 1);
        Expect(0, 2);
        Expect(0, 6);
        Expect(0, 6);
        Expect(0, 3);
        Expect(0, 1);
        Expect(1, 2);
        Expect(0, 2);
        Expect(26, 6);
        Expect(0, 6);
        Expect(0, 1);
        Expect(1, 1);
        while (busy && (bitAt & 7) != 0)
            Expect(0, 1);
        at = bitAt / 8;
        if (at >= end || quantizerIndex == 0)
            Fail("Missing AV1 tile or unsupported lossless mode.");
        return busy;
    }

    private void Refill()
    {
        int shift = 8 - rangeCount;
        while (shift >= 0 && at < end)
        {
            difference ^= (uint)data[at++] << shift;
            rangeCount += 8;
            shift -= 8;
        }
        if (at == end)
            rangeCount = 16384;
    }
    private void Normalize()
    {
        if (range == 0)
        {
            Fail("Invalid AV1 entropy interval.");
            return;
        }
        if (range >= 32768)
            return;
        int shift = range >= 256 ? 7 - logByte[(int)(range >> 8)] : 15 - logByte[(int)range];
        rangeCount -= shift;
        difference = ((difference + 1) << shift) - 1;
        range <<= shift;
        if (rangeCount < 0)
            Refill();
    }
    private int Symbol(int[] cdf, int offset, int length)
    {
        uint previous = range, next = range, code = difference >> 16;
        int symbol = 0;
        for (; symbol <= length; symbol++)
        {
            previous = next;
            next = symbol == length ? 0
                                    : ((range >> 8) * (uint)((32768 - cdf[offset + symbol]) >> 6) >> 1) +
                                          (uint)(4 * (length - symbol));
            if (code >= next)
                break;
        }
        difference -= next << 16;
        range = previous - next;
        if (range < 32768)
            Normalize();
        return symbol;
    }
    private int Adaptive(int[] cdf, int countBase, int offset, int length)
    {
        int symbol = Symbol(cdf, offset, length), index = countBase + offset, count = adaptationCounts[index];
        int rate = 4 + (count >> 4) + (length > 2 ? 1 : 0);
        for (int i = 0; i < length; i++)
        {
            int p = cdf[offset + i];
            cdf[offset + i] = i < symbol ? p - (p >> rate) : p + ((32768 - p) >> rate);
        }
        if (count < 32)
            adaptationCounts[index] = count + 1;
        return symbol;
    }
    private int Small(int index, int length)
    {
        Vector3Int cdf = smallCdf[index];
        uint previous = range, next = range, code = difference >> 16;
        int symbol = 0;
        if (length == 1)
        {
            uint split = ((range >> 8) * (uint)((32768 - cdf.x) >> 6) >> 1) + 4;
            if (code >= split)
            {
                previous = range;
                next = split;
            }
            else
            {
                symbol = 1;
                previous = split;
                next = 0;
            }
        }
        else
            for (; symbol <= length; symbol++)
            {
                previous = next;
                next = symbol == length
                           ? 0
                           : ((range >> 8) * (uint)((32768 - cdf[symbol]) >> 6) >> 1) + (uint)(4 * (length - symbol));
                if (code >= next)
                    break;
            }
        difference -= next << 16;
        range = previous - next;
        if (range < 32768)
            Normalize();
        int count = adaptationCounts[index], rate = (count >> 4) + (length > 2 ? 1 : 0);
        smallCdf[index] = Vector3Int.FloorToInt((Vector3)cdf * cdfFactors[rate] + cdfBias[rate * 4 + symbol]);
        if (count < 32)
            adaptationCounts[index] = count + 1;
        return symbol;
    }
    private int Bit(int p)
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
        if (range < 32768)
            Normalize();
        return value;
    }
    private int Literal(int bits)
    {
        int value = 0;
        for (int i = 0; i < bits; i++)
            value = (value << 1) | Bit(16384);
        return value;
    }
    private int Golomb()
    {
        int bits = 0;
        while (busy && Bit(16384) == 0)
        {
            if (++bits > 15)
            {
                Fail("Excessive AV1 coefficient.");
                return 0;
            }
        }
        return (1 << bits) + Literal(bits) - 1;
    }
    private void Known(int symbol, int[] cdf, int countBase, int offset, int length)
    {
        if (Adaptive(cdf, countBase, offset, length) != symbol)
            Fail("Unsupported AV1 block partition.");
    }
    private int Probability(int[] cdf, int offset, int symbol)
    {
        return (symbol == 9 ? 32768 : cdf[offset + symbol]) - (symbol == 0 ? 0 : cdf[offset + symbol - 1]);
    }
    private void Partition(int bx, int by, int size)
    {
        int[] cdf = size == 16 ? partition16 : size == 32 ? partition32 : partition64;
        int offset = ((bx > 0 ? 2 : 0) + (by > 0 ? 1 : 0)) * 9;
        bool subRows = by * 2 + size / 8 < rows * 2, subCols = bx * 2 + size / 8 < cols * 2;
        if (subRows && subCols)
        {
            Known(3, cdf, size == 16 ? 3 : size == 32 ? 39 : 75, offset, 9);
            return;
        }
        if (subCols)
        {
            int p = Probability(cdf, offset, 2) + Probability(cdf, offset, 3) + Probability(cdf, offset, 4) +
                    Probability(cdf, offset, 6) + Probability(cdf, offset, 7) + Probability(cdf, offset, 9);
            if (Bit(32768 - p) != 1)
                Fail("Unsupported partition edge.");
            return;
        }
        if (subRows)
        {
            int p = Probability(cdf, offset, 1) + Probability(cdf, offset, 3) + Probability(cdf, offset, 4) +
                    Probability(cdf, offset, 5) + Probability(cdf, offset, 6) + Probability(cdf, offset, 8);
            if (Bit(32768 - p) != 1)
                Fail("Unsupported partition edge.");
        }
    }
    private void Partition8()
    {
        uint split = ((range >> 8) * (uint)((32768 - partition8Cdf) >> 6) >> 1) + 12;
        if (difference >> 16 < split)
        {
            Fail("Unsupported AV1 leaf size.");
            return;
        }
        difference -= split << 16;
        range -= split;
        Normalize();
        partition8Cdf += (32768 - partition8Cdf) >> (5 + (partition8Count >> 4));
        if (partition8Count < 32)
            partition8Count++;
    }
    private void PredictionModes(int bx, int by)
    {
        int block = by * cols + bx, offset = block * 396;
        int above = by > 0 ? predictionModes[block - cols] : 0, left = bx > 0 ? predictionModes[block - 1] : 0;
        int prediction = Adaptive(intraY, 3300, (modeContexts[above] * 5 + modeContexts[left]) * 12, 12);
        int delta = prediction >= 1 && prediction <= 8 ? Adaptive(angleDelta, 3210, (prediction - 1) * 6, 6) : 3;
        if (prediction == 7)
        {
            Fail("Unsupported directional prediction.");
            return;
        }
        predictionModes[block] = prediction;
        coefficients[offset + 2] = (byte)prediction;
        coefficients[offset + 3] = (byte)(delta << 3);
        int uv = Adaptive(intraUv, 2928, prediction * 13, 13), au = 0, av = 0;
        if (uv >= 1 && uv <= 8)
            Known(3, angleDelta, 3210, (uv - 1) * 6, 6);
        if (uv == 13)
        {
            int joint = Adaptive(cflSign, 3109, 0, 7) + 1, su = joint / 3, sv = joint % 3;
            if (su != 0)
                au = (Adaptive(cflAlpha, 3116, ((su - 1) * 3 + sv) * 15, 15) + 1) * (su == 1 ? -1 : 1);
            if (sv != 0)
                av = (Adaptive(cflAlpha, 3116, ((sv - 1) * 3 + su) * 15, 15) + 1) * (sv == 1 ? -1 : 1);
        }
        if (uv >= 3 && uv <= 8)
        {
            Fail("Unsupported chroma prediction.");
            return;
        }
        coefficients[offset + 65 * 4 + 2] = (byte)(au + 16);
        coefficients[offset + 82 * 4 + 2] = (byte)(av + 16);
        coefficients[offset + 65 * 4 + 3] = coefficients[offset + 82 * 4 + 3] = (byte)uv;
    }
    private void Plane(int bx, int by, int plane)
    {
        int block = by * cols + bx,
            header = (block * 99 + (plane == 0   ? 0
                                    : plane == 1 ? 65
                                                 : 82)) *
                     4,
            offset = header + 4;
        int tx = plane == 0 ? 1 : 0, pt = plane == 0 ? 0 : 1, total = plane == 0 ? 64 : 16, n = plane == 0 ? 8 : 4,
            mi = block * 3 + plane;
        int[] scan = plane == 0 ? scan8 : scan4;
        int[] values = coefficientValues;
        Array.Clear(values, 0, total);
        int zeroContext =
            plane == 0 ? 0
                       : 7 + (by > 0 && levels[mi - cols * 3] != 0 ? 1 : 0) + (bx > 0 && levels[mi - 3] != 0 ? 1 : 0);
        if (Small(164 + (qctx * 2 + tx) * 13 + zeroContext, 1) != 0)
            return;
        if (plane == 0)
        {
            int type = Adaptive(txType, 112, predictionModes[block] * 4, 4);
            coefficients[header + 3] |= (byte)(type == 0 ? 4 : type == 1 ? 0 : type == 2 ? 3 : type == 3 ? 1 : 2);
        }
        int eobClass =
            plane == 0 ? Adaptive(eob64, 268, (qctx * 2 + pt) * 6, 6) : Adaptive(eob16, 316, (qctx * 2 + pt) * 4, 4);
        int eob = eobClass == 0 ? 1 : eobClass == 1 ? 2 : (1 << (eobClass - 1)) + 1;
        if (eobClass > 1)
        {
            int shift = eobClass - 2;
            eob += (Small((plane == 0 ? 348 : 388) + (qctx * 2 + pt) * (plane == 0 ? 5 : 3) + shift, 1) << shift) +
                   Literal(shift);
        }
        coefficients[header] = (byte)eob;
        int family = (qctx * 2 + tx) * 2 + pt, sum = 0;
        for (int k = eob - 1; k >= 0 && busy; k--)
        {
            int loc = scan[k], row = loc / n, col = loc % n, mag = 0, brMag = 0;
            if (col + 1 < n)
            {
                int c = values[loc + 1];
                mag += Mathf.Min(c, 3);
                brMag += Mathf.Min(c, 15);
            }
            if (row + 1 < n)
            {
                int c = values[loc + n];
                mag += Mathf.Min(c, 3);
                brMag += Mathf.Min(c, 15);
            }
            if (row + 1 < n && col + 1 < n)
            {
                int c = values[loc + n + 1];
                mag += Mathf.Min(c, 3);
                brMag += Mathf.Min(c, 15);
            }
            if (col + 2 < n)
                mag += Mathf.Min(values[loc + 2], 3);
            if (row + 2 < n)
                mag += Mathf.Min(values[loc + n * 2], 3);
            int value;
            if (k == eob - 1)
            {
                int ctx = k == 0 ? 0 : k <= total / 8 ? 1 : k <= total / 4 ? 2 : 3;
                value = Small(412 + family * 4 + ctx, 2) + 1;
            }
            else
            {
                int ctx =
                    loc == 0 ? 0 : Mathf.Min((mag + 1) / 2, 4) + locations[Mathf.Min(row, 4) * 5 + Mathf.Min(col, 4)];
                value = Small(540 + family * 26 + ctx, 3);
            }
            if (value > 2)
            {
                int ctx = Mathf.Min((brMag + 1) / 2, 6) + (loc == 0 ? 0 : row < 2 && col < 2 ? 7 : 14);
                for (int j = 0; j < 4; j++)
                {
                    int extra = Small(1788 + family * 21 + ctx, 3);
                    value += extra;
                    if (extra < 3)
                        break;
                }
            }
            values[loc] = value;
            sum += value;
        }
        for (int k = 0; k < eob && busy; k++)
        {
            int loc = scan[k], value = values[loc], negative = 0;
            if (k == 0 && value != 0)
            {
                int net = (by > 0 ? signs[mi - cols * 3] : 0) + (bx > 0 ? signs[mi - 3] : 0);
                negative = Small(2796 + (qctx * 2 + pt) * 3 + (net == 0 ? 0 : net < 0 ? 1 : 2), 1);
            }
            if (k > 0 && value != 0)
                negative = Bit(16384);
            if (value >= 15)
            {
                int extra = Golomb();
                value += extra;
                sum += extra;
                if (value > 32767)
                {
                    Fail("AV1 coefficient exceeds the supported range.");
                    break;
                }
            }
            int signed = negative != 0 ? -value : value, p = offset + loc * 4;
            coefficients[p] = (byte)(signed & 255);
            coefficients[p + 1] = (byte)((signed >> 8) & 255);
            if (k == 0)
                signs[mi] = value == 0 ? 0 : negative != 0 ? -1 : 1;
        }
        levels[mi] = Mathf.Min(sum, 63);
        coefficients[header + 1] = (byte)levels[mi];
    }
    // Entropy state stays on the CPU; reconstruction only uploads coefficients.
    private RenderTexture Surface(int w, int h, RenderTextureFormat format, bool srgb)
    {
        var texture =
            new RenderTexture(w, h, 0, format, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.useMipMap = false;
        texture.antiAliasing = 1;
        texture.Create();
        return texture;
    }
    private void ReleaseSurfaces()
    {
        if (upload)
            Destroy(upload);
        if (quant)
        {
            quant.Release();
            Destroy(quant);
        }
        if (recon)
        {
            recon.Release();
            Destroy(recon);
        }
        if (filtered)
        {
            filtered.Release();
            Destroy(filtered);
        }
        if (mode)
        {
            mode.Release();
            Destroy(mode);
        }
        if (uvMode)
        {
            uvMode.Release();
            Destroy(uvMode);
        }
        if (scratch)
        {
            scratch.Release();
            Destroy(scratch);
        }
        if (output)
        {
            output.Release();
            Destroy(output);
        }
    }
    private void OnDestroy()
    {
        ReleaseSurfaces();
    }
    private void BeginReconstruction()
    {
        if (!output || output.width != width || output.height != height)
        {
            ReleaseSurfaces();
            upload = new Texture2D(512, coefficients.Length / 2048, TextureFormat.RGBA32, false, true);
            upload.filterMode = FilterMode.Point;
            upload.wrapMode = TextureWrapMode.Clamp;
            quant = Surface(width, rows * 12, RenderTextureFormat.ARGB32, false);
            recon = Surface(width, rows * 12, RenderTextureFormat.ARGB32, false);
            filtered = Surface(width, rows * 12, RenderTextureFormat.ARGB32, false);
            mode = Surface(cols, rows, RenderTextureFormat.ARGB32, false);
            uvMode = Surface(cols, rows, RenderTextureFormat.ARGB32, false);
            scratch = Surface(cols * 16, rows, RenderTextureFormat.ARGBFloat, false);
            output = Surface(width, height, RenderTextureFormat.ARGB32, true);
        }
        upload.LoadRawTextureData(coefficients);
        upload.Apply(false, false);
        material.SetVector("_PhotoSize", new Vector4(width, height, cols, rows));
        material.SetVector("_AtlasSize", new Vector4(width, rows * 12, 0, 0));
        material.SetVector("_PackedSize", new Vector4(upload.width, upload.height, 0, 0));
        material.SetTexture("_DecodedTex", upload);
        material.SetInt("_QuantDc", dcQuant[quantizerIndex]);
        material.SetInt("_QuantAc", acQuant[quantizerIndex]);
        material.SetInt("_QuantUvDc", dcQuant[Mathf.Clamp(quantizerIndex - 16, 0, 255)]);
        material.SetInt("_QuantUvAc", acQuant[Mathf.Clamp(quantizerIndex - 16, 0, 255)]);
        material.SetTexture("_QuantTex", quant);
        material.SetTexture("_ReconTex", recon);
        material.SetTexture("_ModeTex", mode);
        material.SetTexture("_UvModeTex", uvMode);
        material.SetTexture("_CflTex", uvMode);
        material.SetTexture("_OptimizedTex", scratch);
        material.SetInt("_DecodeChroma", 0);
        VRCGraphics.Blit(Texture2D.blackTexture, mode, material, 17);
        material.SetInt("_DecodeChroma", 1);
        VRCGraphics.Blit(Texture2D.blackTexture, uvMode, material, 17);
        VRCGraphics.Blit(Texture2D.blackTexture, recon);
        data = coefficients = null;
        levels = signs = predictionModes = null;
        skippedBlocks = null;
        stage = 1;
        diagonal = 0;
    }
    private void Reconstruct()
    {
        // Per-instance material properties are set before each submission so duplicated decoders cannot mix inputs.
        material.SetVector("_PhotoSize", new Vector4(width, height, cols, rows));
        material.SetVector("_AtlasSize", new Vector4(width, rows * 12, 0, 0));
        material.SetVector("_PackedSize", new Vector4(upload.width, upload.height, 0, 0));
        material.SetTexture("_DecodedTex", upload);
        material.SetTexture("_QuantTex", quant);
        material.SetTexture("_ReconTex", recon);
        material.SetTexture("_ModeTex", mode);
        material.SetTexture("_UvModeTex", uvMode);
        material.SetTexture("_CflTex", uvMode);
        material.SetTexture("_OptimizedTex", scratch);
        material.SetInt("_QuantDc", dcQuant[quantizerIndex]);
        material.SetInt("_QuantAc", acQuant[quantizerIndex]);
        material.SetInt("_QuantUvDc", dcQuant[Mathf.Clamp(quantizerIndex - 16, 0, 255)]);
        material.SetInt("_QuantUvAc", acQuant[Mathf.Clamp(quantizerIndex - 16, 0, 255)]);
        int diagonals = cols + 2 * rows - 2;
        for (int i = 0; i < Mathf.Max(1, diagonalsPerFrame) && diagonal < diagonals; i++, diagonal++)
        {
            material.SetInt("_Diagonal", diagonal);
            for (int plane = 0; plane < 2; plane++)
            {
                material.SetInt("_ChromaStage", plane);
                VRCGraphics.Blit(Texture2D.blackTexture, quant, material, 18);
                VRCGraphics.Blit(Texture2D.blackTexture, scratch, material, 15);
                VRCGraphics.Blit(Texture2D.blackTexture, recon, material, 2);
            }
        }
        progress = .75f + .25f * diagonal / diagonals;
        if (diagonal < diagonals)
            return;
        VRCGraphics.Blit(Texture2D.blackTexture, mode, material, 9);
        VRCGraphics.Blit(recon, filtered, material, 10);
        material.SetTexture("_ReconTex", filtered);
        VRCGraphics.Blit(Texture2D.blackTexture, output, material, 19);
        resultTexture = output;
        busy = false;
        complete = true;
        progress = 1;
    }

    private int[] intraYDefault = AvifTables.CreateIntraYDefault();
    private int[] intraY;
    private int[] intraUvDefault = AvifTables.CreateIntraUvDefault();
    private int[] intraUv;
    private int[] angleDeltaDefault = AvifTables.CreateAngleDeltaDefault();
    private int[] angleDelta;
    private int[] cflSignDefault = AvifTables.CreateCflSignDefault();
    private int[] cflSign;
    private int[] cflAlphaDefault = AvifTables.CreateCflAlphaDefault();
    private int[] cflAlpha;
    private int[] modeContexts = AvifTables.CreateModeContexts();
    private int[] logByte = AvifTables.CreateLogByte();
    private float[] cdfFactors = AvifTables.CreateCdfFactors();
    private Vector3[] cdfBias = AvifTables.CreateCdfBias();
    private int[] partition16Default = AvifTables.CreatePartition16Default();
    private int[] partition16;
    private int[] partition32Default = AvifTables.CreatePartition32Default();
    private int[] partition32;
    private int[] partition64Default = AvifTables.CreatePartition64Default();
    private int[] partition64;
    private Vector3Int[] skipDefault = AvifTables.CreateSkipDefault();
    private int[] txTypeDefault = AvifTables.CreateTxTypeDefault();
    private int[] txType;
    private Vector3Int[] allZeroDefault = AvifTables.CreateAllZeroDefault();
    private int[] eob64Default = AvifTables.CreateEob64Default();
    private int[] eob64;
    private int[] eob16Default = AvifTables.CreateEob16Default();
    private int[] eob16;
    private Vector3Int[] eobExtra8Default = AvifTables.CreateEobExtra8Default();
    private Vector3Int[] eobExtra4Default = AvifTables.CreateEobExtra4Default();
    private Vector3Int[] coeffBaseEobDefault = AvifTables.CreateCoeffBaseEobDefault();
    private Vector3Int[] coeffBaseDefault = AvifTables.CreateCoeffBaseDefault();
    private Vector3Int[] coeffBrDefault = AvifTables.CreateCoeffBrDefault();
    private Vector3Int[] dcSignDefault = AvifTables.CreateDcSignDefault();
    private int[] dcQuant = AvifTables.CreateDcQuant();
    private int[] acQuant = AvifTables.CreateAcQuant();
    private int[] scan4 = AvifTables.CreateScan4();
    private int[] scan8 = AvifTables.CreateScan8();
    private Vector3Int[] smallCdf;
    private void ResetProbabilities()
    {
        adaptationCounts = new int[4000];
        smallCdf = new Vector3Int[4000];
        partition8Cdf = 19132;
        partition8Count = 0;

        partition16 = new int[partition16Default.Length];
        Array.Copy(partition16Default, partition16, partition16.Length);
        partition32 = new int[partition32Default.Length];
        Array.Copy(partition32Default, partition32, partition32.Length);
        partition64 = new int[partition64Default.Length];
        Array.Copy(partition64Default, partition64, partition64.Length);
        Array.Copy(skipDefault, 0, smallCdf, 3900, skipDefault.Length);
        txType = new int[txTypeDefault.Length];
        Array.Copy(txTypeDefault, txType, txType.Length);
        Array.Copy(allZeroDefault, 0, smallCdf, 164, allZeroDefault.Length);
        eob64 = new int[eob64Default.Length];
        Array.Copy(eob64Default, eob64, eob64.Length);
        eob16 = new int[eob16Default.Length];
        Array.Copy(eob16Default, eob16, eob16.Length);
        Array.Copy(eobExtra8Default, 0, smallCdf, 348, eobExtra8Default.Length);
        Array.Copy(eobExtra4Default, 0, smallCdf, 388, eobExtra4Default.Length);
        Array.Copy(coeffBaseEobDefault, 0, smallCdf, 412, coeffBaseEobDefault.Length);
        Array.Copy(coeffBaseDefault, 0, smallCdf, 540, coeffBaseDefault.Length);
        Array.Copy(coeffBrDefault, 0, smallCdf, 1788, coeffBrDefault.Length);
        Array.Copy(dcSignDefault, 0, smallCdf, 2796, dcSignDefault.Length);
        intraY = new int[intraYDefault.Length];
        Array.Copy(intraYDefault, intraY, intraY.Length);
        intraUv = new int[intraUvDefault.Length];
        Array.Copy(intraUvDefault, intraUv, intraUv.Length);
        angleDelta = new int[angleDeltaDefault.Length];
        Array.Copy(angleDeltaDefault, angleDelta, angleDelta.Length);
        cflSign = new int[cflSignDefault.Length];
        Array.Copy(cflSignDefault, cflSign, cflSign.Length);
        cflAlpha = new int[cflAlphaDefault.Length];
        Array.Copy(cflAlphaDefault, cflAlpha, cflAlpha.Length);
    }
}
}
