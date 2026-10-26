using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Rendering;

namespace VrcImageCodec
{
// JPEG image computation stays on the GPU; one readback returns its length and encoded bytes.
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
        // Baseline 8-bit JPEG: DC <= 11 amplitude bits, AC <= 10, Huffman codes <= 16.
        // Each block therefore needs at most 27 + 63 * 26 bits. Pad each restart segment to
        // a byte, allow FF/00 stuffing for every entropy byte, then add its restart marker.
        // This is a capacity bound, not a limit on accepted image/output sizes.
        int blocksPerMcu = subsampleChroma ? 6 : 3;
        int segmentBytes = ((blocksPerMcu * (27 + 63 * 26) + 7) / 8) * 2 + 2;
        // The fixed JFIF header is 629 bytes; the end-of-image marker occupies two bytes.
        int capacity = 629 + mcuCount * segmentBytes + 2;
        output = CodecBuffers.EncodedOutput(output, capacity, RenderTextureFormat.ARGB32);
        material.SetInt("_OutputWidth", output.width);
        VRCGraphics.Blit(Texture2D.blackTexture, output, material, 12);
        int size = output.width * output.height * 4;
        if (readbackBuffer == null || readbackBuffer.Length != size) readbackBuffer = new byte[size];
        readbackPending = true;
        progress = .9f;
        VRCAsyncGPUReadback.Request(output, 0, TextureFormat.RGBA32, this);
    }

    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        readbackPending = false;
        if (!busy) return;
        if (request.hasError || !request.TryGetData(readbackBuffer))
        {
            busy = false;
            error = "JPEG GPU readback failed.";
            return;
        }
        int encodedLength = readbackBuffer[0] | (readbackBuffer[1] << 8) | (readbackBuffer[2] << 16) |
                            (readbackBuffer[3] << 24);
        resultJpeg = new byte[encodedLength];
        Array.Copy(readbackBuffer, CodecBuffers.LengthPrefixBytes, resultJpeg, 0, encodedLength);
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
