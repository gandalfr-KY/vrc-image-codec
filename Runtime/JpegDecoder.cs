using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Rendering;

namespace VrcImageCodec
{
// Restricted Baseline JPEG profile matching JpegEncoder, including GPU header validation.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class JpegDecoder : UdonSharpBehaviour
{
    public byte[] inputJpeg;
    public Material material;
    public Texture2D huffman;
    public bool linearOutput = true;
    [HideInInspector]
    public bool subsampleChroma;
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
    private byte[] uploadBuffer;
    private RenderTexture header, metadata, restartA, restartB, starts, coefficients, horizontal, output,
        errorsA, errorsB;
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
        // Only the allocation hints are read on CPU. The GPU still validates the complete header.
        width = (inputJpeg[165] << 8) | inputJpeg[166];
        height = (inputJpeg[163] << 8) | inputJpeg[164];
        if (width < 1 || height < 1 || width > 1920 || height > 1920 || width * height > 2073600)
        {
            error = "Unsupported JPEG dimensions.";
            return;
        }
        subsampleChroma = inputJpeg[169] == 34;
        startedAt = Time.realtimeSinceStartup;
        busy = true;
        int uploadRows = (inputJpeg.Length + 4095) / 4096;
        if (!upload || upload.height != uploadRows)
        {
            if (upload) Destroy(upload);
            upload = new Texture2D(1024, uploadRows, TextureFormat.RGBA32, false, true);
            upload.filterMode = FilterMode.Point;
        }
        if (uploadBuffer == null || uploadBuffer.Length != 4096 * uploadRows)
            uploadBuffer = new byte[4096 * uploadRows];
        byte[] bytes = uploadBuffer;
        Array.Copy(inputJpeg, bytes, inputJpeg.Length);
        upload.LoadRawTextureData(bytes);
        upload.Apply(false, false);
        material.SetTexture("_Bytes", upload);
        material.SetTexture("_Huffman", huffman);
        material.SetInt("_Length", inputJpeg.Length);
        material.SetInt("_Linear", linearOutput ? 1 : 0);
        header = CodecBuffers.Surface(header, 2, 1, RenderTextureFormat.ARGB32);
        metadata = CodecBuffers.Surface(metadata, 2, 1, RenderTextureFormat.ARGB32);
        material.SetTexture("_Header", header);
        VRCGraphics.Blit(Texture2D.blackTexture, header, material, 4);
        int mcuSize = subsampleChroma ? 16 : 8, blocks = subsampleChroma ? 6 : 3;
        int cols = (width + mcuSize - 1) / mcuSize, rows = (height + mcuSize - 1) / mcuSize,
            mcus = cols * rows;
        material.SetInt("_Subsampled", subsampleChroma ? 1 : 0);
        int words = (inputJpeg.Length + 3) / 4;
        restartA = CodecBuffers.Surface(restartA, 1024, (words + 1023) / 1024, RenderTextureFormat.RFloat);
        restartB = CodecBuffers.Surface(restartB, 1024, restartA.height, RenderTextureFormat.RFloat);
        starts = CodecBuffers.Surface(starts, 1024, (mcus * (blocks + 1) + 1023) / 1024,
                                      RenderTextureFormat.ARGBFloat);
        coefficients = CodecBuffers.Surface(coefficients, 1024, (mcus * blocks * 16 + 1023) / 1024,
                                            RenderTextureFormat.ARGBFloat);
        horizontal =
            CodecBuffers.Surface(horizontal, cols * mcuSize, rows * mcuSize, RenderTextureFormat.ARGBFloat);
        output = CodecBuffers.Surface(
            output, width, height, linearOutput ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32);
        errorsA = CodecBuffers.Surface(errorsA, 1024, (mcus + 1023) / 1024, RenderTextureFormat.RFloat);
        errorsB = CodecBuffers.Surface(errorsB, 1024, errorsA.height, RenderTextureFormat.RFloat);
        material.SetInt("_Cols", cols);
        material.SetInt("_Rows", rows);
        material.SetInt("_Width", width);
        material.SetInt("_Height", height);
        material.SetInt("_Count", words);
        VRCGraphics.Blit(Texture2D.blackTexture, restartA, material, 5);
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
        VRCGraphics.Blit(Texture2D.blackTexture, starts, material, 6);
        VRCGraphics.Blit(Texture2D.blackTexture, coefficients, material, 7);
        VRCGraphics.Blit(Texture2D.blackTexture, horizontal, material, 10);
        VRCGraphics.Blit(Texture2D.blackTexture, output, material, 11);
        VRCGraphics.Blit(Texture2D.blackTexture, errorsA, material, 8);
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
        VRCGraphics.Blit(Texture2D.blackTexture, metadata, material, 9);
        readbackPending = true;
        progress = .9f;
        VRCAsyncGPUReadback.Request(metadata, 0, TextureFormat.RGBA32, this);
    }

    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        readbackPending = false;
        if (!busy) return;
        byte[] bytes = new byte[8];
        busy = false;
        if (request.hasError || !request.TryGetData(bytes))
        {
            error = "JPEG GPU readback failed.";
            return;
        }
        if (bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 0 || bytes[3] != 0)
        {
            error = "JPEG header, entropy data or restart markers are invalid.";
            return;
        }
        resultTexture = output;
        complete = true;
        progress = 1;
        elapsedSeconds = Time.realtimeSinceStartup - startedAt;
    }

    public void CancelDecoding()
    {
        busy = complete = false;
        error = "";
        resultTexture = null;
        width = height = 0;
        progress = elapsedSeconds = 0;
    }

    private void OnDestroy()
    {
        if (upload) Destroy(upload);
        RenderTexture[] textures = { header,       metadata,   restartA, restartB, starts,
                                     coefficients, horizontal, output,   errorsA,  errorsB };
        CodecBuffers.Release(textures);
    }
}
}
