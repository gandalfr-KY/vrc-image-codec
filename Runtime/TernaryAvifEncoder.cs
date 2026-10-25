// Adaptive CDF rules: AV1 specification and libaom aom_dsp/prob.h.
// Copyright (c) 2016, Alliance for Open Media. BSD-2-Clause + AOM patent grant.
using System;
using UdonSharp;
using UnityEngine;

namespace TernaryStars
{
// Single-image AV1: GPU prediction/transform/RDO; Udon entropy coding. Derived from tinyavif,
// Copyright (c) 2024-2025, the tinyavif contributors. BSD-2-Clause and
// AOMedia Patent License 1.0: see ThirdParty/AV1. No native library/threads.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class TernaryAvifEncoder : UdonSharpBehaviour
{
    public const int DefaultQuality = 60;
    public int[] modeCounts = new int[13];
    private int[] predictionModes;
    private bool[] skippedBlocks;
    private int[] adaptationCounts;
    public int width = 640, height = 360;
    [Range(0, 100)]
    public int quality = DefaultQuality;
    public float frameBudgetMs = 2;
    // Optional speed/size tradeoff: fixed probability models preserve reconstructed pixels.
    public bool disableCdfUpdate;
    public TernaryAvifGpuTransform gpuTransform;
    private byte[] packedCoefficients;
    [HideInInspector]
    public byte[] resultAvif;
    [HideInInspector]
    public bool busy, complete;
    [HideInInspector]
    public string error = "";
    [HideInInspector]
    public float progress, lastWorkTime, elapsedSeconds, entropySeconds, cpuSeconds, maxStepMs;
    [HideInInspector]
    public int encodedBlocks, quantizerIndex;
    private byte[] buffer;
    private int[] levels, signs;
    // Reuse coefficients across magnitude and sign passes; avoid unpacking every value twice.
    private int[] coefficientValues = new int[64];
    private int cols, rows, sbCols, sbRows, cursor, stage, outputSize, qctx, qdc, qac;
    private float startedAt;
    private ulong low;
    private uint range;
    private int rangeCount;

    public void BeginEncoding()
    {
        if (gpuTransform && gpuTransform.readbackPending)
        {
            error = "Previous AV1 GPU readback is still pending.";
            return;
        }
        CancelEncoding();
        if (width < 16 || height < 16 || width % 8 != 0 || height % 8 != 0 || width > 1920 || height > 1920 ||
            width * height > 2073600 || quality < 0 || quality > 100 || !gpuTransform || !gpuTransform.material)
        {
            error = "AVIF requires valid GPU resources, width/height 16..1920 in multiples of 8, at most 2073600 " +
                    "pixels and quality 0..100.";
            return;
        }
        cols = width / 8;
        rows = height / 8;
        sbCols = (cols + 7) / 8;
        sbRows = (rows + 7) / 8;
        ResetProbabilities();
        skippedBlocks = new bool[cols * rows];
        predictionModes = new int[cols * rows];
        modeCounts = new int[13];
        levels = new int[cols * rows * 3];
        signs = new int[levels.Length];
        buffer = new byte[100000];
        // Preserve the selected orange Q70 profile (qindex 92). Endpoints remain lossy.
        quantizerIndex = Mathf.Clamp((100 - quality) * 255 / 100 + 16, 1, 255);
        qdc = dcQuant[quantizerIndex];
        qac = acQuant[quantizerIndex];
        qctx = quantizerIndex <= 20 ? 0 : quantizerIndex <= 60 ? 1 : quantizerIndex <= 120 ? 2 : 3;
        outputSize = 0;
        low = 0;
        range = 32768;
        rangeCount = -9;
        cursor = 0;
        stage = -1;
        elapsedSeconds = entropySeconds = cpuSeconds = maxStepMs = 0;
        encodedBlocks = 0;
        startedAt = lastWorkTime = Time.realtimeSinceStartup;
        gpuTransform.width = width;
        gpuTransform.height = height;
        gpuTransform.quantDc = qdc;
        gpuTransform.quantAc = qac;
        gpuTransform.material.SetInt("_QuantUvDc", dcQuant[Mathf.Clamp(quantizerIndex - 16, 0, 255)]);
        gpuTransform.material.SetInt("_QuantUvAc", acQuant[Mathf.Clamp(quantizerIndex - 16, 0, 255)]);
        gpuTransform.BeginTransform();
        if (!gpuTransform.busy)
        {
            error = gpuTransform.error;
            return;
        }
        busy = true;
    }

    public void CancelEncoding()
    {
        if (gpuTransform)
            gpuTransform.CancelTransform();
        buffer = file = header = packedCoefficients = null;
        levels = signs = null;
        busy = false;
        complete = false;
        error = "";
        resultAvif = null;
        progress = 0;
    }

    private void Update()
    {
        if (busy)
            StepEncoding();
    }
    public void StepEncoding()
    {
        if (!busy)
            return;
        lastWorkTime = Time.realtimeSinceStartup;
        if (stage == -1)
        {
            elapsedSeconds = lastWorkTime - startedAt;
            if (gpuTransform.busy)
                return;
            if (!gpuTransform.complete)
            {
                Fail(gpuTransform.error);
                return;
            }
            packedCoefficients = gpuTransform.result;
            stage = 1;
            cursor = 0;
            progress = .15f;
        }
        float until = lastWorkTime + Mathf.Max(.25f, frameBudgetMs) * .001f;
        do
        {
            if (cursor < sbCols * sbRows * 64)
            {
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
                int at = by * cols + bx, p = at * 99 * 4;
                bool omitted = packedCoefficients[p] == 0 && packedCoefficients[p + 65 * 4] == 0 &&
                               packedCoefficients[p + 82 * 4] == 0;
                int skipContext =
                    (by > 0 && skippedBlocks[at - cols] ? 1 : 0) + (bx > 0 && skippedBlocks[at - 1] ? 1 : 0);
                AdaptiveSmall(omitted ? 1 : 0, 3900 + (skipContext), 1);
                skippedBlocks[at] = omitted;
                EncodePredictionModes(bx, by);
                if (!omitted)
                    for (int plane = 0; plane < 3 && busy; plane++)
                        EncodePlane(bx, by, plane);
                encodedBlocks++;
                progress = .15f + .8f * encodedBlocks / (cols * rows);
            }
            else
            {
                FinalizeRange();
                if (busy)
                    WrapAvif();
            }
        } while (busy && Time.realtimeSinceStartup < until);
        float now = Time.realtimeSinceStartup, work = now - lastWorkTime;
        cpuSeconds += work;
        entropySeconds = cpuSeconds;
        maxStepMs = Mathf.Max(maxStepMs, work * 1000);
        elapsedSeconds = now - startedAt;
    }

    // Default mode probabilities: AOM entropymode.c, BSD-2-Clause + patent grant.
    private int[] intraYDefault = AvifTables.CreateIntraYDefault();
    private int[] intraY;
    private int[] intraUvDefault = AvifTables.CreateIntraUvDefault();
    private int[] intraUv;
    private int[] angleDeltaDefault = AvifTables.CreateAngleDeltaDefault();
    private int[] angleDelta;

    private void EncodePredictionModes(int bx, int by)
    {
        int at = by * cols + bx, offset = at * 99 * 4, mode = packedCoefficients[offset + 2];
        int above = by > 0 ? predictionModes[at - cols] : 0, left = bx > 0 ? predictionModes[at - 1] : 0;
        AdaptiveSymbol(mode, intraY, 3300, (modeContexts[above] * 5 + modeContexts[left]) * 12, 12);
        if (mode >= 1 && mode <= 8)
            AdaptiveSymbol(packedCoefficients[offset + 3] >> 3, angleDelta, 3210, (mode - 1) * 6, 6);
        predictionModes[at] = mode;
        int au = packedCoefficients[offset + 65 * 4 + 2] - 16, av = packedCoefficients[offset + 82 * 4 + 2] - 16;
        int uvMode = packedCoefficients[offset + 65 * 4 + 3];
        AdaptiveSymbol(uvMode, intraUv, 2928, mode * 13, 13);
        if (uvMode >= 1 && uvMode <= 8)
            AdaptiveSymbol(3, angleDelta, 3210, (uvMode - 1) * 6, 6);
        if (uvMode == 13)
        {
            int su = au == 0 ? 0 : au < 0 ? 1 : 2, sv = av == 0 ? 0 : av < 0 ? 1 : 2;
            AdaptiveSymbol(su * 3 + sv - 1, cflSign, 3109, 0, 7);
            if (su != 0)
                AdaptiveSymbol(Mathf.Abs(au) - 1, cflAlpha, 3116, ((su - 1) * 3 + sv) * 15, 15);
            if (sv != 0)
                AdaptiveSymbol(Mathf.Abs(av) - 1, cflAlpha, 3116, ((sv - 1) * 3 + su) * 15, 15);
        }
        modeCounts[mode]++;
    }

    private int[] cflSignDefault = AvifTables.CreateCflSignDefault();
    private int[] cflSign;
    private int[] cflAlphaDefault = AvifTables.CreateCflAlphaDefault();
    private int[] cflAlpha;

    private int[] modeContexts = AvifTables.CreateModeContexts();

    private int[] logByte = AvifTables.CreateLogByte();

    // Only called for syntax elements proven constant for this encoder subset.
    private void StableSymbol(int symbol, int[] cdf, int countBase, int offset, int length)
    {
        int loCdf = symbol == 0 ? 0 : cdf[offset + symbol - 1], hiCdf = symbol == length ? 32768 : cdf[offset + symbol],
            symbols = length + 1;
        uint hi = (uint)(32768 - hiCdf);
        if (symbol == 0)
            range -= ((range >> 8) * (hi >> 6) >> 1) + (uint)(4 * (symbols - 1));
        else
        {
            uint lo = (uint)(32768 - loCdf), a = ((range >> 8) * (lo >> 6) >> 1) + (uint)(4 * (symbols - symbol)),
                 b = ((range >> 8) * (hi >> 6) >> 1) + (uint)(4 * (symbols - symbol - 1));
            low += range - a;
            range = a - b;
        }
        // A normalized interval needs no shift, log lookup or byte emission.
        if (range < 32768)
        {
            int d = range >= 256 ? 7 - logByte[(int)(range >> 8)] : 15 - logByte[(int)range];
            rangeCount += d;
            if (rangeCount >= 40)
                FlushRange(d);
            low <<= d;
            range <<= d;
        }
        if (disableCdfUpdate)
            return;
        int at = countBase + offset, count = adaptationCounts[at];
        if (count < 0)
            return;
        int rate = 4 + (count >> 4) + (length > 2 ? 1 : 0);
        bool changed = false;
        for (int i = 0; i < length; i++)
        {
            int p = cdf[offset + i], delta = i < symbol ? -(p >> rate) : (32768 - p) >> rate;
            if (delta != 0)
                changed = true;
            cdf[offset + i] = p + delta;
        }
        adaptationCounts[at] = count < 32 ? count + 1 : changed ? 32 : -1;
    }

    private void ResetProbabilities()
    {
        adaptationCounts = new int[4000];
        smallCdf = new Vector3Int[4000];
        partition8Cdf = 19132;
        partition8Count = 0;
        partition8Stable = false;
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

    // The fixed 8x8 leaf always emits partition symbol zero. Only its first
    // threshold is ever read; when adaptation converges its Q6 tail is one.
    private void Partition8()
    {
        if (partition8Stable)
            range -= (range >> 9) + 12;
        else
        {
            range -= ((range >> 8) * (uint)((32768 - partition8Cdf) >> 6) >> 1) + 12;
            if (!disableCdfUpdate)
            {
                int delta = (32768 - partition8Cdf) >> (5 + (partition8Count >> 4));
                partition8Cdf += delta;
                if (partition8Count < 32)
                    partition8Count++;
                else if (delta == 0)
                    partition8Stable = true;
            }
        }
        if (range < 32768)
        {
            int d = range >= 256 ? 7 - logByte[(int)(range >> 8)] : 15 - logByte[(int)range];
            rangeCount += d;
            if (rangeCount >= 40)
                FlushRange(d);
            low <<= d;
            range <<= d;
        }
    }
    private void Partition(int bx, int by, int size)
    {
        int[] cdf = size == 16 ? partition16 : size == 32 ? partition32 : partition64;
        int offset = ((bx > 0 ? 2 : 0) + (by > 0 ? 1 : 0)) * 9;
        bool subRows = by * 2 + size / 8 < rows * 2, subCols = bx * 2 + size / 8 < cols * 2;
        if (subRows && subCols)
            StableSymbol(3, cdf, size == 16 ? 3 : size == 32 ? 39 : 75, offset, 9);
        else if (subCols)
        {
            int p = Probability(cdf, offset, 2) + Probability(cdf, offset, 3) + Probability(cdf, offset, 4) +
                    Probability(cdf, offset, 6) + Probability(cdf, offset, 7) + Probability(cdf, offset, 9);
            Bit(1, 32768 - p);
        }
        else if (subRows)
        {
            int p = Probability(cdf, offset, 1) + Probability(cdf, offset, 3) + Probability(cdf, offset, 4) +
                    Probability(cdf, offset, 5) + Probability(cdf, offset, 6) + Probability(cdf, offset, 8);
            Bit(1, 32768 - p);
        }
    }
    private int Probability(int[] cdf, int at, int symbol)
    {
        return (symbol == 9 ? 32768 : cdf[at + symbol]) - (symbol == 0 ? 0 : cdf[at + symbol - 1]);
    }

    private void EncodePlane(int bx, int by, int plane)
    {
        int block = by * cols + bx, planeOffset = plane == 0 ? 0 : plane == 1 ? 65 : 82;
        int offset = (block * 99 + planeOffset) * 4, eob = packedCoefficients[offset],
            level = packedCoefficients[offset + 1];
        offset += 4;
        int tx = plane == 0 ? 1 : 0, pt = plane == 0 ? 0 : 1, total = plane == 0 ? 64 : 16, mi = block * 3 + plane;
        int[] scan = plane == 0 ? scan8 : scan4;
        int zeroCtx = 0;
        if (plane > 0)
            zeroCtx = 7 + (by > 0 && levels[mi - cols * 3] != 0 ? 1 : 0) + (bx > 0 && levels[mi - 3] != 0 ? 1 : 0);
        levels[mi] = Mathf.Min(level, 63);
        AdaptiveSmall(eob == 0 ? 1 : 0, 164 + ((qctx * 2 + tx) * 13 + zeroCtx), 1);
        if (eob == 0)
            return;
        if (plane == 0)
        {
            int type = packedCoefficients[block * 396 + 3] & 7;
            AdaptiveSymbol(type == 4   ? 0
                           : type == 0 ? 1
                           : type == 1 ? 3
                           : type == 2 ? 4
                                       : 2,
                           txType, 112, predictionModes[block] * 4, 4);
        }
        int eobClass = eob <= 1 ? 0 : logByte[eob - 1] + 1;
        if (plane == 0)
            AdaptiveSymbol(eobClass, eob64, 268, (qctx * 2 + pt) * 6, 6);
        else
            AdaptiveSymbol(eobClass, eob16, 316, (qctx * 2 + pt) * 4, 4);
        if (eobClass > 1)
        {
            int bottom = (1 << (eobClass - 1)) + 1, shift = eobClass - 2, bit = ((eob - bottom) >> shift) & 1;
            if (plane == 0)
                AdaptiveSmall(bit, 348 + ((qctx * 2 + pt) * 5 + shift), 1);
            else
                AdaptiveSmall(bit, 388 + ((qctx * 2 + pt) * 3 + shift), 1);
            Literal(eob - bottom - (bit << shift), shift);
        }
        int contextFamily = (qctx * 2 + tx) * 2 + pt, baseEobOffset = contextFamily * 4,
            baseOffset = contextFamily * 26, brOffset = contextFamily * 21;
        for (int k = eob - 1; k >= 0; k--)
        {
            int loc = scan[k], at = offset + loc * 4, word = BitConverter.ToInt32(packedCoefficients, at),
                coefficient = (word & 65535) - 32768, abs = Mathf.Abs(coefficient);
            coefficientValues[k] = coefficient;
            if (k == eob - 1)
            {
                int ctx = k == 0 ? 0 : k <= total / 8 ? 1 : k <= total / 4 ? 2 : 3;
                AdaptiveSmall(Mathf.Min(abs - 1, 2), 412 + (baseEobOffset + ctx), 2);
            }
            else
            {
                int ctx = (word >> 16) & 255;
                AdaptiveSmall(Mathf.Min(abs, 3), 540 + (baseOffset + ctx), 3);
            }
            if (abs > 2)
            {
                int ctx = (word >> 24) & 255;
                int lev = 3;
                for (int j = 0; j < 4; j++)
                {
                    int value = Mathf.Min(abs - lev, 3);
                    AdaptiveSmall(value, 1788 + (brOffset + ctx), 3);
                    lev += value;
                    if (value < 3)
                        break;
                }
            }
        }
        int dc = coefficientValues[0];
        if (dc != 0)
        {
            int net = (by > 0 ? signs[mi - cols * 3] : 0) + (bx > 0 ? signs[mi - 3] : 0), ctx = net == 0  ? 0
                                                                                                : net < 0 ? 1
                                                                                                          : 2;
            AdaptiveSmall(dc < 0 ? 1 : 0, 2796 + ((qctx * 2 + pt) * 3 + ctx), 1);
        }
        if (Mathf.Abs(dc) >= 15)
            Golomb(Mathf.Abs(dc) - 15);
        signs[mi] = dc == 0 ? 0 : dc < 0 ? -1 : 1;
        for (int k = 1; k < eob; k++)
        {
            int value = coefficientValues[k], magnitude = Mathf.Abs(value);
            if (value != 0)
                UniformBit(value < 0 ? 1 : 0);
            if (magnitude >= 15)
                Golomb(magnitude - 15);
        }
    }
    private int FloorLog(int value)
    {
        int result = 0;
        if (value >= 65536)
        {
            value >>= 16;
            result += 16;
        }
        if (value >= 256)
        {
            value >>= 8;
            result += 8;
        }
        if (value >= 16)
        {
            value >>= 4;
            result += 4;
        }
        if (value >= 4)
        {
            value >>= 2;
            result += 2;
        }
        if (value >= 2)
            result++;
        return result;
    }
    // Native vector operations update all small-CDF thresholds together.
    // For rate 4..7, p*(1-2^-rate)+bias is an exact binary fraction.
    // Floor matches p-(p>>rate) below the symbol and p+((32768-p)>>rate) above.
    private Vector3Int[] smallCdf;
    private float[] cdfFactors = AvifTables.CreateCdfFactors();
    private Vector3[] cdfBias = AvifTables.CreateCdfBias();
    private void AdaptiveSmall(int symbol, int index, int length)
    {
        Vector3Int cdf = smallCdf[index];
        int loCdf = symbol == 0 ? 0 : cdf[symbol - 1], hiCdf = symbol == length ? 32768 : cdf[symbol],
            symbols = length + 1;
        uint hi = (uint)(32768 - hiCdf);
        if (symbol == 0)
            range -= ((range >> 8) * (hi >> 6) >> 1) + (uint)(4 * (symbols - 1));
        else
        {
            uint lo = (uint)(32768 - loCdf), a = ((range >> 8) * (lo >> 6) >> 1) + (uint)(4 * (symbols - symbol)),
                 b = ((range >> 8) * (hi >> 6) >> 1) + (uint)(4 * (symbols - symbol - 1));
            low += range - a;
            range = a - b;
        }
        // A normalized interval needs no shift, log lookup or byte emission.
        if (range < 32768)
        {
            int d = range >= 256 ? 7 - logByte[(int)(range >> 8)] : 15 - logByte[(int)range];
            rangeCount += d;
            if (rangeCount >= 40)
                FlushRange(d);
            low <<= d;
            range <<= d;
        }
        if (disableCdfUpdate)
            return;
        int count = adaptationCounts[index], rate = (count >> 4) + (length > 2 ? 1 : 0);
        smallCdf[index] = Vector3Int.FloorToInt((Vector3)cdf * cdfFactors[rate] + cdfBias[rate * 4 + symbol]);
        if (count < 32)
            adaptationCounts[index] = count + 1;
    }
    private void AdaptiveSymbol(int symbol, int[] cdf, int countBase, int offset, int length)
    {
        int loCdf = symbol == 0 ? 0 : cdf[offset + symbol - 1], hiCdf = symbol == length ? 32768 : cdf[offset + symbol],
            symbols = length + 1;
        uint hi = (uint)(32768 - hiCdf);
        if (symbol == 0)
            range -= ((range >> 8) * (hi >> 6) >> 1) + (uint)(4 * (symbols - 1));
        else
        {
            uint lo = (uint)(32768 - loCdf), a = ((range >> 8) * (lo >> 6) >> 1) + (uint)(4 * (symbols - symbol)),
                 b = ((range >> 8) * (hi >> 6) >> 1) + (uint)(4 * (symbols - symbol - 1));
            low += range - a;
            range = a - b;
        }
        // A normalized interval needs no shift, log lookup or byte emission.
        if (range < 32768)
        {
            int d = range >= 256 ? 7 - logByte[(int)(range >> 8)] : 15 - logByte[(int)range];
            rangeCount += d;
            if (rangeCount >= 40)
                FlushRange(d);
            low <<= d;
            range <<= d;
        }
        if (disableCdfUpdate)
            return;
        {
            int at = countBase + offset, count = adaptationCounts[at];
            int rate = 4 + (count >> 4) + (length > 2 ? 1 : 0);
            if (length <= 3)
            {
                int p = cdf[offset];
                cdf[offset] = symbol > 0 ? p - (p >> rate) : p + ((32768 - p) >> rate);
                if (length >= 2)
                {
                    p = cdf[offset + 1];
                    cdf[offset + 1] = symbol > 1 ? p - (p >> rate) : p + ((32768 - p) >> rate);
                }
                if (length == 3)
                {
                    p = cdf[offset + 2];
                    cdf[offset + 2] = symbol > 2 ? p - (p >> rate) : p + ((32768 - p) >> rate);
                }
            }
            else
                for (int i = 0; i < length; i++)
                {
                    int p = cdf[offset + i];
                    cdf[offset + i] = i < symbol ? p - (p >> rate) : p + ((32768 - p) >> rate);
                }
            if (count < 32)
                adaptationCounts[at] = count + 1;
        }
    }
    private void Bit(int value, int p)
    {
        RangeSymbol(value, value == 0 ? 0 : p, value == 0 ? p : 32768, 2);
    }
    private void Literal(int value, int bits)
    {
        for (int s = bits - 1; s >= 0; s--)
            UniformBit((value >> s) & 1);
    }
    private void Golomb(int value)
    {
        value++;
        int bits = FloorLog(value);
        Literal(0, bits);
        Literal(value, bits + 1);
    }
    private void RangeSymbol(int symbol, int loCdf, int hiCdf, int symbols)
    {
        uint hi = (uint)(32768 - hiCdf);
        if (symbol == 0)
            range -= ((range >> 8) * (hi >> 6) >> 1) + (uint)(4 * (symbols - 1));
        else
        {
            uint lo = (uint)(32768 - loCdf), a = ((range >> 8) * (lo >> 6) >> 1) + (uint)(4 * (symbols - symbol)),
                 b = ((range >> 8) * (hi >> 6) >> 1) + (uint)(4 * (symbols - symbol - 1));
            low += range - a;
            range = a - b;
        }
        // A normalized interval needs no shift, log lookup or byte emission.
        if (range < 32768)
        {
            int d = range >= 256 ? 7 - logByte[(int)(range >> 8)] : 15 - logByte[(int)range];
            rangeCount += d;
            if (rangeCount >= 40)
                FlushRange(d);
            low <<= d;
            range <<= d;
        }
    }
    // Exact 1/2-probability specialization for signs and literals.
    // Keep normalization identical to RangeSymbol; golden bitstreams cover both.
    private void UniformBit(int value)
    {
        uint half = ((range >> 8) << 7) + 4;
        if (value == 0)
            range -= half;
        else
        {
            low += range - half;
            range = half;
        }
        // A normalized interval needs no shift, log lookup or byte emission.
        if (range < 32768)
        {
            // Splitting a normalized interval in half leaves 16380..32891: at most two shifts.
            int d = range >= 16384 ? 1 : 2;
            rangeCount += d;
            if (rangeCount >= 40)
                FlushRange(d);
            low <<= d;
            range <<= d;
        }
    }
    // Rare path shared by adaptive, fixed-probability and uniform symbols.
    private void FlushRange(int shift)
    {
        int bytes = (rangeCount >> 3) + 1, c = rangeCount - shift + 24 - (bytes << 3);
        if (outputSize + bytes > buffer.Length)
        {
            Fail("AVIF exceeds the server's 100KB image limit.");
            return;
        }
        ulong output = low >> c;
        low &= (1ul << c) - 1;
        if ((output & (1ul << (bytes << 3))) != 0)
            Carry();
        output &= (1ul << (bytes << 3)) - 1;
        for (int i = bytes - 1; i >= 0; i--)
            buffer[outputSize++] = (byte)((output >> (8 * i)) & 255ul);
        rangeCount = c + shift - 24;
    }
    private void Carry()
    {
        for (int i = outputSize - 1; i >= 0; i--)
        {
            if (buffer[i] == 255)
                buffer[i] = 0;
            else
            {
                buffer[i]++;
                return;
            }
        }
        Fail("AV1 entropy carry overflow");
    }
    private void Put(int value)
    {
        if (outputSize >= buffer.Length)
        {
            Fail("AVIF exceeds the server's 100KB image limit.");
            return;
        }
        buffer[outputSize++] = (byte)(value & 255);
    }
    private void Fail(string value)
    {
        busy = false;
        complete = false;
        error = value;
        Debug.LogError("[AV1CPU] " + value);
    }
    private void FinalizeRange()
    {
        int s = rangeCount + 10;
        ulong m = 16383, e = ((low + m) & ~m) | (m + 1), mask = (1ul << (rangeCount + 16)) - 1;
        while (s > 0)
        {
            ulong value = e >> (rangeCount + 16);
            if ((value & 256) != 0)
                Carry();
            Put((int)(value & 255ul));
            e &= mask;
            s -= 8;
            rangeCount -= 8;
            mask >>= 8;
        }
    }
    private byte[] file, header;
    private int fileCursor, headerBits;
    private void HeaderBits(int value, int count)
    {
        for (int k = count - 1; k >= 0; k--)
        {
            if (((value >> k) & 1) != 0)
                header[headerBits >> 3] |= (byte)(1 << (7 - (headerBits & 7)));
            headerBits++;
        }
    }
    private byte[] SequenceHeader()
    {
        header = new byte[64];
        headerBits = 0;
        HeaderBits(0, 3);
        HeaderBits(1, 1);
        HeaderBits(1, 1);
        HeaderBits(width * height > 665856 ? 8 : 4, 5);
        HeaderBits(15, 4);
        HeaderBits(15, 4);
        HeaderBits(width - 1, 16);
        HeaderBits(height - 1, 16);
        HeaderBits(2, 6);
        HeaderBits(0, 1);
        HeaderBits(0, 1);
        HeaderBits(1, 1);
        HeaderBits(1, 8);
        HeaderBits(13, 8);
        HeaderBits(6, 8);
        HeaderBits(1, 1);
        HeaderBits(0, 2);
        HeaderBits(0, 1);
        HeaderBits(0, 1);
        HeaderBits(1, 1);
        var result = new byte[(headerBits + 7) / 8];
        Array.Copy(header, result, result.Length);
        return result;
    }
    private byte[] FrameHeader()
    {
        header = new byte[64];
        headerBits = 0;
        HeaderBits(disableCdfUpdate ? 1 : 0, 1);
        HeaderBits(0, 1);
        HeaderBits(0, 1);
        HeaderBits(1, 1);
        if (width > 64)
            HeaderBits(0, 1);
        if (height > 64)
            HeaderBits(0, 1);
        HeaderBits(quantizerIndex, 8);
        HeaderBits(0, 1);
        for (int component = 0; component < 2; component++)
        {
            HeaderBits(1, 1);
            HeaderBits((-16) & 127, 7);
        }
        HeaderBits(0, 1); // Quantization matrices disabled in the selected profile.
        HeaderBits(0, 2);
        HeaderBits(0, 6);
        HeaderBits(0, 6);
        HeaderBits(0, 3);
        HeaderBits(0, 1);
        HeaderBits(1, 2);
        HeaderBits(0, 2);
        HeaderBits(26, 6);
        HeaderBits(0, 6);
        HeaderBits(0, 1);
        HeaderBits(1, 1);
        var result = new byte[(headerBits + 7) / 8];
        Array.Copy(header, result, result.Length);
        return result;
    }
    private void FByte(int value)
    {
        file[fileCursor++] = (byte)(value & 255);
    }
    private void UInt(int value, int bytes)
    {
        for (int i = bytes - 1; i >= 0; i--)
            FByte(value >> (8 * i));
    }
    private void Text(string value)
    {
        for (int i = 0; i < value.Length; i++)
            FByte(value[i]);
    }
    private void Patch(int at, int value)
    {
        for (int i = 0; i < 4; i++)
            file[at + i] = (byte)((value >> (24 - i * 8)) & 255);
    }
    private int Box(string type)
    {
        int at = fileCursor;
        UInt(0, 4);
        Text(type);
        return at;
    }
    private int Full(string type, int version)
    {
        int at = Box(type);
        UInt(version << 24, 4);
        return at;
    }
    private void End(int at)
    {
        Patch(at, fileCursor - at);
    }
    private void Leb(int value)
    {
        do
        {
            int part = value & 127;
            value >>= 7;
            FByte(part | (value != 0 ? 128 : 0));
        } while (value != 0);
    }
    private void Append(byte[] bytes)
    {
        Array.Copy(bytes, 0, file, fileCursor, bytes.Length);
        fileCursor += bytes.Length;
    }
    private void WrapAvif()
    {
        byte[] sequence = SequenceHeader(), frame = FrameHeader();
        file = new byte[outputSize + 600];
        fileCursor = 0;
        int ftyp = Box("ftyp");
        Text("avif");
        UInt(0, 4);
        Text("avifmif1miafMA1B");
        End(ftyp);
        int meta = Full("meta", 0), handler = Full("hdlr", 0);
        UInt(0, 4);
        Text("pict");
        for (int i = 0; i < 13; i++)
            FByte(0);
        End(handler);
        int primary = Full("pitm", 0);
        UInt(1, 2);
        End(primary);
        int location = Full("iloc", 0);
        FByte(0x44);
        FByte(0);
        UInt(1, 2);
        UInt(1, 2);
        UInt(0, 2);
        UInt(1, 2);
        int offsetMarker = fileCursor;
        UInt(0, 4);
        int sizeMarker = fileCursor;
        UInt(0, 4);
        End(location);
        int info = Full("iinf", 0);
        UInt(1, 2);
        int entry = Full("infe", 2);
        UInt(1, 2);
        UInt(0, 2);
        Text("av01Color");
        FByte(0);
        End(entry);
        End(info);
        int properties = Box("iprp"), container = Box("ipco"), size = Full("ispe", 0);
        UInt(width, 4);
        UInt(height, 4);
        End(size);
        int pixels = Full("pixi", 0);
        FByte(3);
        FByte(8);
        FByte(8);
        FByte(8);
        End(pixels);
        int config = Box("av1C");
        FByte(0x81);
        FByte(width * height > 665856 ? 8 : 4);
        FByte(12);
        FByte(0);
        End(config);
        int color = Box("colr");
        Text("nclx");
        UInt(1, 2);
        UInt(13, 2);
        UInt(6, 2);
        FByte(128);
        End(color);
        End(container);
        int mapping = Full("ipma", 0);
        UInt(1, 4);
        UInt(1, 2);
        FByte(4);
        FByte(1);
        FByte(2);
        FByte(0x83);
        FByte(4);
        End(mapping);
        End(properties);
        End(meta);
        int data = Box("mdat"), contentStart = fileCursor;
        FByte(0x12);
        FByte(0);
        FByte(0x0a);
        Leb(sequence.Length);
        Append(sequence);
        FByte(0x32);
        Leb(frame.Length + outputSize);
        Append(frame);
        Array.Copy(buffer, 0, file, fileCursor, outputSize);
        fileCursor += outputSize;
        Patch(offsetMarker, contentStart);
        Patch(sizeMarker, fileCursor - contentStart);
        End(data);
        if (fileCursor > 100000)
        {
            Fail("AVIF exceeds the server's 100KB image limit.");
            return;
        }
        resultAvif = new byte[fileCursor];
        Array.Copy(file, resultAvif, fileCursor);
        busy = false;
        complete = true;
        progress = 1;
        elapsedSeconds = Time.realtimeSinceStartup - startedAt;
        levels = signs = null;
        buffer = file = header = null;
        packedCoefficients = null;
        if (gpuTransform)
            gpuTransform.result = null;
        Debug.Log("[AV1CPU] Complete; seconds=" + elapsedSeconds + " bytes=" + resultAvif.Length +
                  " entropy=" + entropySeconds);
    }

    private int partition8Cdf, partition8Count;
    private bool partition8Stable;
    private int[] partition16Default = AvifTables.CreatePartition16Default();
    private int[] partition16;
    private int[] partition32Default = AvifTables.CreatePartition32Default();
    private int[] partition32;
    private int[] partition64Default = AvifTables.CreatePartition64Default();
    private int[] partition64;
    private Vector3Int[] skipDefault = AvifTables.CreateSkipDefault();
    private int[] uvMode = AvifTables.CreateUvMode();
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
}
}
