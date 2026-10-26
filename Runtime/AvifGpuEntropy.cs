// AV1 arithmetic: Alliance for Open Media, BSD-2-Clause + PATENTS.
using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Rendering;

namespace VrcImageCodec
{
// Owns syntax/CDF/range resources. Only a record count and completed AVIF cross to the CPU.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class AvifGpuEntropy : UdonSharpBehaviour
{
    public Material syntaxMaterial, cdfMaterial, rangeMaterial;
    public Texture2D initialCdf, containerHeaders;
    public bool subsampleChroma = true;
    public int width = 640, height = 360;
    [HideInInspector]
    public Vector4 headerSizes, headerPatch;
    [HideInInspector]
    public int quantizerBit;
    [HideInInspector]
    public bool busy, complete, readbackPending;
    [HideInInspector]
    public string error = "";
    [HideInInspector]
    public byte[] result;
    private RenderTexture prefixA, prefixB, countTexture, events;
    private RenderTexture sortedA, sortedB, cdfCheckpoints, intervals, resolvedTokens;
    private RenderTexture rangeCheckpoints, rangeCount, rangeDigits, rangeOutput;
    private bool waitingForCount;

    public void BeginEncoding(RenderTexture packet, int quantizerIndex)
    {
        busy = true;
        complete = false;
        error = "";
        result = null;
        int cols = width / 8, rows = height / 8, blocks = ((cols + 7) / 8) * ((rows + 7) / 8) * 64;
        prefixA = CodecBuffers.Surface(prefixA, 1024, (blocks + 1023) / 1024, RenderTextureFormat.RFloat);
        prefixB = CodecBuffers.Surface(prefixB, 1024, prefixA.height, RenderTextureFormat.RFloat);
        countTexture = CodecBuffers.Surface(countTexture, 1, 1, RenderTextureFormat.ARGB32);
        syntaxMaterial.SetInt("_Chroma444", subsampleChroma ? 0 : 1);
        syntaxMaterial.SetInt("_Cols", cols);
        syntaxMaterial.SetInt("_Rows", rows);
        syntaxMaterial.SetInt("_Blocks", blocks);
        syntaxMaterial.SetInt("_Qctx", quantizerIndex <= 20    ? 0
                                       : quantizerIndex <= 60  ? 1
                                       : quantizerIndex <= 120 ? 2
                                                               : 3);
        syntaxMaterial.SetTexture("_Packet", packet);
        rangeMaterial.SetTexture("_Headers", containerHeaders);
        rangeMaterial.SetVector("_HeaderSizes", headerSizes);
        rangeMaterial.SetVector("_HeaderPatch", headerPatch);
        rangeMaterial.SetInt("_QuantBit", quantizerBit);
        rangeMaterial.SetInt("_QuantIndex", quantizerIndex);
        VRCGraphics.Blit(Texture2D.blackTexture, prefixA, syntaxMaterial, 0);
        for (int step = 1; step < blocks; step *= 2)
        {
            syntaxMaterial.SetTexture("_Prefix", prefixA);
            syntaxMaterial.SetInt("_Stride", step);
            VRCGraphics.Blit(Texture2D.blackTexture, prefixB, syntaxMaterial, 1);
            RenderTexture swap = prefixA;
            prefixA = prefixB;
            prefixB = swap;
        }
        syntaxMaterial.SetTexture("_Prefix", prefixA);
        VRCGraphics.Blit(Texture2D.blackTexture, countTexture, syntaxMaterial, 2);
        waitingForCount = readbackPending = true;
        VRCAsyncGPUReadback.Request(countTexture, 0, TextureFormat.RGBA32, this);
    }

    private void EncodeRecords(int count)
    {
        int rows = (count + 1023) / 1024;
        events = CodecBuffers.Surface(events, 1024, rows, RenderTextureFormat.ARGB32);
        sortedA = CodecBuffers.Surface(sortedA, 1024, rows, RenderTextureFormat.ARGBFloat);
        sortedB = CodecBuffers.Surface(sortedB, 1024, rows, RenderTextureFormat.ARGBFloat);
        cdfCheckpoints = CodecBuffers.Surface(cdfCheckpoints, 1024, (((count + 255) / 256) * 4 + 1023) / 1024,
                                              RenderTextureFormat.ARGBFloat);
        intervals = CodecBuffers.Surface(intervals, 1024, rows, RenderTextureFormat.ARGBFloat);
        resolvedTokens = CodecBuffers.Surface(resolvedTokens, 1024, rows, RenderTextureFormat.ARGB32);
        syntaxMaterial.SetInt("_Count", count);
        VRCGraphics.Blit(Texture2D.blackTexture, events, syntaxMaterial, 3);
        cdfMaterial.SetInt("_Count", count);
        cdfMaterial.SetTexture("_Events", events);
        cdfMaterial.SetTexture("_Initial", initialCdf);
        VRCGraphics.Blit(Texture2D.blackTexture, sortedA, cdfMaterial, 0);
        for (int run = 1; run < count; run *= 2)
        {
            cdfMaterial.SetTexture("_Sorted", sortedA);
            cdfMaterial.SetInt("_Run", run);
            VRCGraphics.Blit(Texture2D.blackTexture, sortedB, cdfMaterial, 1);
            RenderTexture swap = sortedA;
            sortedA = sortedB;
            sortedB = swap;
        }
        cdfMaterial.SetTexture("_Sorted", sortedA);
        cdfMaterial.SetTexture("_Checkpoints", cdfCheckpoints);
        cdfMaterial.SetTexture("_Intervals", intervals);
        VRCGraphics.Blit(Texture2D.blackTexture, cdfCheckpoints, cdfMaterial, 2);
        VRCGraphics.Blit(Texture2D.blackTexture, intervals, cdfMaterial, 3);
        VRCGraphics.Blit(Texture2D.blackTexture, resolvedTokens, cdfMaterial, 4);
        rangeCheckpoints = CodecBuffers.Surface(rangeCheckpoints, 2 * ((count + 2047) / 2048 + 1), 1,
                                                RenderTextureFormat.ARGBFloat);
        rangeCount = CodecBuffers.Surface(rangeCount, 1, 1, RenderTextureFormat.ARGBFloat);
        rangeDigits = CodecBuffers.Surface(rangeDigits, 8192, 4, RenderTextureFormat.ARGBFloat);
        rangeOutput = CodecBuffers.Surface(rangeOutput, 8192, 4, RenderTextureFormat.ARGB32);
        rangeMaterial.SetInt("_TokenCount", count);
        rangeMaterial.SetTexture("_Tokens", resolvedTokens);
        rangeMaterial.SetTexture("_Checkpoints", rangeCheckpoints);
        rangeMaterial.SetTexture("_Counts", rangeCount);
        rangeMaterial.SetTexture("_Digits", rangeDigits);
        VRCGraphics.Blit(Texture2D.blackTexture, rangeCheckpoints, rangeMaterial, 0);
        VRCGraphics.Blit(Texture2D.blackTexture, rangeCount, rangeMaterial, 1);
        VRCGraphics.Blit(Texture2D.blackTexture, rangeDigits, rangeMaterial, 2);
        VRCGraphics.Blit(Texture2D.blackTexture, rangeOutput, rangeMaterial, 3);
        waitingForCount = false;
        readbackPending = true;
        VRCAsyncGPUReadback.Request(rangeOutput, 0, TextureFormat.RGBA32, this);
    }

    public void CancelEncoding()
    {
        busy = complete = false;
        result = null;
        error = "";
        // An outstanding readback must drain before reusing resources.
    }

    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        readbackPending = false;
        if (!busy) return;
        byte[] bytes = new byte[waitingForCount ? 4 : 8192 * 4 * 4];
        if (request.hasError || !request.TryGetData(bytes))
        {
            busy = false;
            error = "AVIF GPU readback failed.";
            return;
        }
        int length = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
        if (waitingForCount)
        {
            length |= bytes[3] << 24;
            // SM5 texture width (16384) bounds two RGBA checkpoint records per 2048 tokens.
            if (length < 1 || length > (16384 / 2 - 1) * 2048)
            {
                busy = false;
                error = "AV1 syntax exceeds GPU texture capacity.";
                return;
            }
            EncodeRecords(length);
            return;
        }
        busy = false;
        if (length < 1 || length > 100000)
        {
            error = "AVIF exceeds the 100000-byte limit.";
            return;
        }
        result = new byte[length];
        Array.Copy(bytes, 4, result, 0, length);
        complete = true;
    }

    private void OnDestroy()
    {
        RenderTexture[] textures = { prefixA,        prefixB,          countTexture,   events,
                                     sortedA,        sortedB,          cdfCheckpoints, intervals,
                                     resolvedTokens, rangeCheckpoints, rangeCount,     rangeDigits,
                                     rangeOutput };
        CodecBuffers.Release(textures);
    }
}
}
