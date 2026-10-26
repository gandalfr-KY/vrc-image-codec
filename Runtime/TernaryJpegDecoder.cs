using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Rendering;

namespace TernaryStars
{
// Restricted Baseline JPEG profile matching TernaryJpegEncoder, including GPU header validation.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class TernaryJpegDecoder : UdonSharpBehaviour
{
    public byte[] inputJpeg;
    public Material material;
    public Texture2D huffman;
    public bool linearOutput = true;
    [HideInInspector]
    public bool busy, complete, readbackPending;
    [HideInInspector]
    public string error = "";
    [HideInInspector]
    public RenderTexture resultTexture;
    [HideInInspector]
    public int width, height;
    [HideInInspector]
    public float progress, elapsedSeconds;
    private Texture2D upload;
    private RenderTexture metadata, restartA, restartB, starts, coefficients, horizontal, output, errorsA,
        errorsB;
    private bool waitingForHeader;
    private float startedAt;

    public void BeginDecoding()
    {
        if (busy || readbackPending) return;
        CancelDecoding();
        if (!material || !huffman || inputJpeg == null || inputJpeg.Length < 631 ||
            inputJpeg.Length > 8388608)
        {
            error = "JPEG requires configured material/tables and a 631-byte..8 MiB input.";
            return;
        }
        startedAt = Time.realtimeSinceStartup;
        busy = true;
        int rows = (inputJpeg.Length + 4095) / 4096;
        if (!upload || upload.height != rows)
        {
            if (upload) Destroy(upload);
            upload = new Texture2D(1024, rows, TextureFormat.RGBA32, false, true);
            upload.filterMode = FilterMode.Point;
        }
        byte[] bytes = new byte[4096 * rows];
        Array.Copy(inputJpeg, bytes, inputJpeg.Length);
        upload.LoadRawTextureData(bytes);
        upload.Apply(false, false);
        material.SetTexture("_Bytes", upload);
        material.SetTexture("_Huffman", huffman);
        material.SetInt("_Length", inputJpeg.Length);
        material.SetInt("_Linear", linearOutput ? 1 : 0);
        metadata = Surface(metadata, 1, 1, RenderTextureFormat.ARGB32);
        VRCGraphics.Blit(Texture2D.blackTexture, metadata, material, 7);
        waitingForHeader = readbackPending = true;
        progress = .1f;
        VRCAsyncGPUReadback.Request(metadata, 0, TextureFormat.RGBA32, this);
    }

    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        readbackPending = false;
        if (!busy) return;
        byte[] bytes = new byte[4];
        if (request.hasError || !request.TryGetData(bytes))
        {
            busy = false;
            error = "JPEG GPU readback failed.";
            return;
        }
        if (!waitingForHeader)
        {
            busy = false;
            if (bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 0 || bytes[3] != 0)
            {
                error = "JPEG entropy data or restart markers are invalid.";
                return;
            }
            resultTexture = output;
            complete = true;
            progress = 1;
            elapsedSeconds = Time.realtimeSinceStartup - startedAt;
            return;
        }
        width = bytes[0] | (bytes[1] << 8);
        height = bytes[2] | (bytes[3] << 8);
        if (width == 0 || height == 0)
        {
            busy = false;
            error = "Unsupported JPEG: use this codec's 8-bit 4:4:4 Baseline/restart profile.";
            return;
        }
        int cols = (width + 7) / 8, rows = (height + 7) / 8, mcus = cols * rows;
        int words = (inputJpeg.Length + 3) / 4;
        restartA = Surface(restartA, 1024, (words + 1023) / 1024, RenderTextureFormat.RFloat);
        restartB = Surface(restartB, 1024, restartA.height, RenderTextureFormat.RFloat);
        starts = Surface(starts, 1024, (mcus * 4 + 1023) / 1024, RenderTextureFormat.ARGBFloat);
        coefficients = Surface(coefficients, 1024, (mcus * 48 + 1023) / 1024, RenderTextureFormat.ARGBFloat);
        horizontal = Surface(horizontal, cols * 8, rows * 8, RenderTextureFormat.ARGBFloat);
        output = Surface(output, width, height,
                         linearOutput ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32);
        errorsA = Surface(errorsA, 1024, (mcus + 1023) / 1024, RenderTextureFormat.RFloat);
        errorsB = Surface(errorsB, 1024, errorsA.height, RenderTextureFormat.RFloat);
        material.SetInt("_Cols", cols);
        material.SetInt("_Rows", rows);
        material.SetInt("_Width", width);
        material.SetInt("_Height", height);
        material.SetInt("_Count", words);
        VRCGraphics.Blit(Texture2D.blackTexture, restartA, material, 8);
        for (int step = 1; step < words; step *= 2)
        {
            material.SetTexture("_Prefix", restartA);
            material.SetInt("_Stride", step);
            VRCGraphics.Blit(Texture2D.blackTexture, restartB, material, 3);
            RenderTexture swap = restartA;
            restartA = restartB;
            restartB = swap;
        }
        material.SetTexture("_Prefix", restartA);
        material.SetTexture("_RestartPrefix", restartA);
        material.SetTexture("_Starts", starts);
        material.SetTexture("_Coefficients", coefficients);
        material.SetTexture("_Work", horizontal);
        VRCGraphics.Blit(Texture2D.blackTexture, starts, material, 9);
        VRCGraphics.Blit(Texture2D.blackTexture, coefficients, material, 10);
        VRCGraphics.Blit(Texture2D.blackTexture, horizontal, material, 13);
        VRCGraphics.Blit(Texture2D.blackTexture, output, material, 14);
        VRCGraphics.Blit(Texture2D.blackTexture, errorsA, material, 11);
        material.SetInt("_Count", mcus);
        for (int step = 1; step < mcus; step *= 2)
        {
            material.SetTexture("_Prefix", errorsA);
            material.SetInt("_Stride", step);
            VRCGraphics.Blit(Texture2D.blackTexture, errorsB, material, 3);
            RenderTexture swap = errorsA;
            errorsA = errorsB;
            errorsB = swap;
        }
        material.SetTexture("_Prefix", errorsA);
        VRCGraphics.Blit(Texture2D.blackTexture, metadata, material, 12);
        waitingForHeader = false;
        readbackPending = true;
        progress = .9f;
        VRCAsyncGPUReadback.Request(metadata, 0, TextureFormat.RGBA32, this);
    }

    public void CancelDecoding()
    {
        busy = complete = false;
        error = "";
        resultTexture = null;
        width = height = 0;
        progress = elapsedSeconds = 0;
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
        if (upload) Destroy(upload);
        RenderTexture[] textures = { metadata,   restartA, restartB, starts, coefficients,
                                     horizontal, output,   errorsA,  errorsB };
        for (int i = 0; i < textures.Length; i++)
            if (textures[i])
            {
                textures[i].Release();
                Destroy(textures[i]);
            }
    }
}
}
