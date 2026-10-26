using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Rendering;

namespace VrcImageCodec
{
// JPEG image computation stays on the GPU. Read the exact length before allocating its output.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class JpegEncoder : UdonSharpBehaviour
{
    public Texture inputTexture;
    public Material material;
    public int width = 640, height = 360, quality = 60;
    public bool linearInput = true;
    public bool subsampleChroma;
    [HideInInspector]
    public bool busy, complete, readbackPending;
    [HideInInspector]
    public string error = "";
    [HideInInspector]
    public byte[] resultJpeg;
    [HideInInspector]
    public float progress, elapsedSeconds;
    private RenderTexture rowTransforms, coefficients, segmentOffsets, scanScratch, output,
        outputLengthTexture;
    private float startedAt;
    private byte[] readbackBuffer;
    private byte[] lengthReadback = new byte[4];
    private bool waitingForLength;
    private int encodedLength;

    public void BeginEncoding()
    {
        if (busy || readbackPending) return;
        CancelEncoding();
        if (!material || !inputTexture || quality < 1 || quality > 100 || width < 1 || height < 1 ||
            width > 1920 || height > 1920 || width * height > 2073600)
        {
            error = "JPEG requires a material, input texture, quality 1..100 and at most 1920x1080 pixels.";
            return;
        }
        if (inputTexture.width != width || inputTexture.height != height)
        {
            error = "JPEG input texture dimensions must match width and height.";
            return;
        }
        startedAt = Time.realtimeSinceStartup;
        busy = true;
        int mcuSize = subsampleChroma ? 16 : 8;
        int mcuColumns = (width + mcuSize - 1) / mcuSize, mcuRows = (height + mcuSize - 1) / mcuSize,
            mcuCount = mcuColumns * mcuRows;
        rowTransforms = CodecBuffers.Surface(rowTransforms, mcuColumns * mcuSize, mcuRows * mcuSize,
                                             RenderTextureFormat.ARGBFloat);
        coefficients = CodecBuffers.Surface(coefficients, mcuColumns * mcuSize, mcuRows * mcuSize,
                                            RenderTextureFormat.ARGBFloat);
        segmentOffsets = CodecBuffers.Surface(segmentOffsets, 1024, (mcuCount + 1023) / 1024,
                                              RenderTextureFormat.ARGBFloat);
        scanScratch =
            CodecBuffers.Surface(scanScratch, 1024, segmentOffsets.height, RenderTextureFormat.ARGBFloat);
        material.SetInt("_Subsampled", subsampleChroma ? 1 : 0);
        material.SetInt("_Width", width);
        material.SetInt("_Height", height);
        material.SetInt("_Cols", mcuColumns);
        material.SetInt("_Rows", mcuRows);
        material.SetInt("_Count", mcuCount);
        material.SetInt("_Quality", quality);
        material.SetInt("_Linear", linearInput ? 1 : 0);
        material.SetTexture("_Input", inputTexture);
        material.SetTexture("_Work", rowTransforms);
        material.SetTexture("_Coefficients", coefficients);
        VRCGraphics.Blit(Texture2D.blackTexture, rowTransforms, material, 0);
        VRCGraphics.Blit(Texture2D.blackTexture, coefficients, material, 1);
        VRCGraphics.Blit(Texture2D.blackTexture, segmentOffsets, material, 2);
        for (int step = 1; step < mcuCount; step *= 2)
        {
            material.SetTexture("_Prefix", segmentOffsets);
            material.SetInt("_Stride", step);
            VRCGraphics.Blit(Texture2D.blackTexture, scanScratch, material, 3);
            RenderTexture swap = segmentOffsets;
            segmentOffsets = scanScratch;
            scanScratch = swap;
        }
        material.SetTexture("_Prefix", segmentOffsets);
        // DirectAssemble writes the length in its first pixel without emitting any JPEG payload.
        outputLengthTexture = CodecBuffers.Surface(outputLengthTexture, 1, 1, RenderTextureFormat.ARGB32);
        VRCGraphics.Blit(Texture2D.blackTexture, outputLengthTexture, material, 12);
        waitingForLength = true;
        readbackPending = true;
        progress = .8f;
        VRCAsyncGPUReadback.Request(outputLengthTexture, 0, TextureFormat.RGBA32, this);
    }

    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        readbackPending = false;
        if (!busy) return;
        byte[] bytes = waitingForLength ? lengthReadback : readbackBuffer;
        if (request.hasError || !request.TryGetData(bytes))
        {
            busy = false;
            error = "JPEG GPU readback failed.";
            return;
        }
        if (waitingForLength)
        {
            encodedLength = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
            output = CodecBuffers.EncodedOutput(output, encodedLength, RenderTextureFormat.ARGB32);
            material.SetInt("_OutputWidth", output.width);
            VRCGraphics.Blit(Texture2D.blackTexture, output, material, 12);
            int size = output.width * output.height * 4;
            if (readbackBuffer == null || readbackBuffer.Length != size) readbackBuffer = new byte[size];
            waitingForLength = false;
            readbackPending = true;
            progress = .9f;
            VRCAsyncGPUReadback.Request(output, 0, TextureFormat.RGBA32, this);
            return;
        }
        resultJpeg = new byte[encodedLength];
        Array.Copy(bytes, CodecBuffers.LengthPrefixBytes, resultJpeg, 0, encodedLength);
        busy = false;
        complete = true;
        progress = 1;
        elapsedSeconds = Time.realtimeSinceStartup - startedAt;
    }

    public void CancelEncoding()
    {
        busy = complete = false;
        resultJpeg = null;
        error = "";
        progress = elapsedSeconds = 0;
        // Pending callbacks drain before another job may reuse these textures.
    }

    private void OnDestroy()
    {
        RenderTexture[] textures = { rowTransforms, coefficients, segmentOffsets,
                                     scanScratch,   output,       outputLengthTexture };
        CodecBuffers.Release(textures);
    }
}
}
