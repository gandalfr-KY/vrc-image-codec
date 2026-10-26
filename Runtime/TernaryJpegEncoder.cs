using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Rendering;

namespace TernaryStars
{
// JPEG image computation stays on the GPU; only byte count and completed JPEG are read back.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class TernaryJpegEncoder : UdonSharpBehaviour
{
    public Texture inputTexture;
    public Material material;
    public int width = 640, height = 360, quality = 60;
    public bool linearInput = true;
    [HideInInspector]
    public bool busy, complete, readbackPending;
    [HideInInspector]
    public string error = "";
    [HideInInspector]
    public byte[] resultJpeg;
    [HideInInspector]
    public float progress, elapsedSeconds;
    private RenderTexture horizontal, coefficients, prefixA, prefixB, metadata, segments, output;
    private bool waitingForLength;
    private int length;
    private float startedAt;

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
        int cols = (width + 7) / 8, rows = (height + 7) / 8, count = cols * rows;
        horizontal = Surface(horizontal, cols * 8, rows * 8, RenderTextureFormat.ARGBFloat);
        coefficients = Surface(coefficients, cols * 8, rows * 8, RenderTextureFormat.ARGBFloat);
        prefixA = Surface(prefixA, 1024, (count + 1023) / 1024, RenderTextureFormat.ARGBFloat);
        prefixB = Surface(prefixB, 1024, prefixA.height, RenderTextureFormat.ARGBFloat);
        metadata = Surface(metadata, 2, 1, RenderTextureFormat.ARGB32);
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
        VRCGraphics.Blit(Texture2D.blackTexture, metadata, material, 4);
        waitingForLength = readbackPending = true;
        progress = .5f;
        VRCAsyncGPUReadback.Request(metadata, 0, TextureFormat.RGBA32, this);
    }

    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        readbackPending = false;
        if (!busy) return;
        byte[] bytes = new byte[waitingForLength ? 8 : output.width * output.height * 4];
        if (request.hasError || !request.TryGetData(bytes))
        {
            busy = false;
            error = "JPEG GPU readback failed.";
            return;
        }
        if (waitingForLength)
        {
            length = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
            int slotWords = bytes[4] | (bytes[5] << 8) | (bytes[6] << 16) | (bytes[7] << 24);
            if (length < 631 || length > 8388608 || slotWords < 1 || slotWords > 512)
            {
                busy = false;
                error = "JPEG exceeds the 8 MiB byte-buffer limit.";
                return;
            }
            int count = ((width + 7) / 8) * ((height + 7) / 8);
            material.SetInt("_SlotWords", slotWords);
            segments = Surface(segments, 8192, (count * slotWords + 8191) / 8192, RenderTextureFormat.ARGB32);
            output = Surface(output, 1024, (length + 4095) / 4096, RenderTextureFormat.ARGB32);
            material.SetInt("_Length", length);
            material.SetTexture("_Segments", segments);
            VRCGraphics.Blit(Texture2D.blackTexture, segments, material, 5);
            VRCGraphics.Blit(Texture2D.blackTexture, output, material, 6);
            waitingForLength = false;
            readbackPending = true;
            progress = .9f;
            VRCAsyncGPUReadback.Request(output, 0, TextureFormat.RGBA32, this);
            return;
        }
        resultJpeg = new byte[length];
        Array.Copy(bytes, resultJpeg, length);
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

    private RenderTexture Surface(RenderTexture texture, int w, int h, RenderTextureFormat format)
    {
        if (texture && texture.width == w && texture.height == h && texture.format == format) return texture;
        if (texture)
        {
            texture.Release();
            Destroy(texture);
        }
        texture = new RenderTexture(w, h, 0, format, RenderTextureReadWrite.Linear);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.Create();
        return texture;
    }
    private void OnDestroy()
    {
        RenderTexture[] textures = { horizontal, coefficients, prefixA, prefixB, metadata, segments, output };
        for (int i = 0; i < textures.Length; i++)
            if (textures[i])
            {
                textures[i].Release();
                Destroy(textures[i]);
            }
    }
}
}
