using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Rendering;

namespace VrcImageCodec
{
// JPEG image computation stays on the GPU; one readback returns length and JPEG bytes.
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
    private RenderTexture rowTransforms, coefficients, segmentOffsets, scanScratch, output;
    private float startedAt;
    private byte[] readbackBuffer;

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
        // Baseline Huffman limits: DC <= 27 bits, each AC <= 26 bits, plus byte stuffing.
        // Reserve once to avoid waiting for a separate GPU length readback.
        int maximumEncodedBytes = 631 + mcuCount * (((((subsampleChroma ? 6 : 3) * 1665) + 7) / 8) * 2 + 2);
        int capacity = Mathf.Min(8388608, maximumEncodedBytes);
        output = CodecBuffers.Surface(output, 1024, (capacity + 4 + 4095) / 4096, RenderTextureFormat.ARGB32);
        VRCGraphics.Blit(Texture2D.blackTexture, output, material, 12);
        readbackPending = true;
        progress = .9f;
        VRCAsyncGPUReadback.Request(output, 0, TextureFormat.RGBA32, this);
    }

    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        readbackPending = false;
        if (!busy) return;
        int size = output.width * output.height * 4;
        if (readbackBuffer == null || readbackBuffer.Length != size) readbackBuffer = new byte[size];
        byte[] bytes = readbackBuffer;
        if (request.hasError || !request.TryGetData(bytes))
        {
            busy = false;
            error = "JPEG GPU readback failed.";
            return;
        }
        int length = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
        if (length > bytes.Length - 4)
        {
            // The first reservation avoids a length-only readback; larger results grow on demand.
            output =
                CodecBuffers.Surface(output, 1024, (length + 4 + 4095) / 4096, RenderTextureFormat.ARGB32);
            VRCGraphics.Blit(Texture2D.blackTexture, output, material, 12);
            readbackPending = true;
            VRCAsyncGPUReadback.Request(output, 0, TextureFormat.RGBA32, this);
            return;
        }
        resultJpeg = new byte[length];
        Array.Copy(bytes, 4, resultJpeg, 0, length);
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
        RenderTexture[] textures = { rowTransforms, coefficients, segmentOffsets, scanScratch, output };
        CodecBuffers.Release(textures);
    }
}
}
