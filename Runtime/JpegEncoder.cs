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
    private RenderTexture horizontal, coefficients, prefixA, prefixB, output;
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
        int cols = (width + mcuSize - 1) / mcuSize, rows = (height + mcuSize - 1) / mcuSize,
            count = cols * rows;
        horizontal =
            CodecBuffers.Surface(horizontal, cols * mcuSize, rows * mcuSize, RenderTextureFormat.ARGBFloat);
        coefficients =
            CodecBuffers.Surface(coefficients, cols * mcuSize, rows * mcuSize, RenderTextureFormat.ARGBFloat);
        prefixA = CodecBuffers.Surface(prefixA, 1024, (count + 1023) / 1024, RenderTextureFormat.ARGBFloat);
        prefixB = CodecBuffers.Surface(prefixB, 1024, prefixA.height, RenderTextureFormat.ARGBFloat);
        material.SetInt("_Subsampled", subsampleChroma ? 1 : 0);
        material.SetInt("_Width", width);
        material.SetInt("_Height", height);
        material.SetInt("_Cols", cols);
        material.SetInt("_Rows", rows);
        material.SetInt("_Count", count);
        material.SetInt("_Quality", quality);
        material.SetInt("_Linear", linearInput ? 1 : 0);
        material.SetTexture("_Input", inputTexture);
        material.SetTexture("_Work", horizontal);
        material.SetTexture("_Coefficients", coefficients);
        VRCGraphics.Blit(Texture2D.blackTexture, horizontal, material, 0);
        VRCGraphics.Blit(Texture2D.blackTexture, coefficients, material, 1);
        VRCGraphics.Blit(Texture2D.blackTexture, prefixA, material, 2);
        for (int step = 1; step < count; step *= 2)
        {
            material.SetTexture("_Prefix", prefixA);
            material.SetInt("_Stride", step);
            VRCGraphics.Blit(Texture2D.blackTexture, prefixB, material, 3);
            RenderTexture swap = prefixA;
            prefixA = prefixB;
            prefixB = swap;
        }
        material.SetTexture("_Prefix", prefixA);
        // Baseline Huffman limits: DC <= 27 bits, each AC <= 26 bits, plus byte stuffing.
        // Reserve once to avoid waiting for a separate GPU length readback.
        int bound = 631 + count * (((((subsampleChroma ? 6 : 3) * 1665) + 7) / 8) * 2 + 2);
        int capacity = Mathf.Min(8388608, bound);
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
        if (length < 631 || length > 8388608 || length > bytes.Length - 4)
        {
            busy = false;
            error = "JPEG exceeds the 8 MiB byte-buffer limit.";
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
        RenderTexture[] textures = { horizontal, coefficients, prefixA, prefixB, output };
        CodecBuffers.Release(textures);
    }
}
}
