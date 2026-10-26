using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Rendering;

namespace VrcImageCodec
{
// Restricted Baseline JPEG profile matching JpegEncoder, including GPU header validation.
[AddComponentMenu("")]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class JpegDecoder : UdonSharpBehaviour
{
    public byte[] inputJpeg;
    public Material material;
    [HideInInspector]
    public Renderer materialSource;
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
    private byte[] statusReadback = new byte[8];
    private RenderTexture header, metadata, restartOffsets, restartScanScratch, blockStarts, coefficients,
        inverseRows, output, errorCounts, errorScanScratch;
    private float startedAt;
    private bool ownsMaterial;

    public void BeginDecoding()
    {
        if (busy || readbackPending) return;
        CancelDecoding();
        if (!material || !materialSource || !huffman || inputJpeg == null || inputJpeg.Length < 631)
        {
            error = "JPEG requires configured material/tables and a complete Baseline JPEG input.";
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
        if (!ownsMaterial)
        {
            material = materialSource.material;
            ownsMaterial = true;
        }
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
        Array.Copy(inputJpeg, uploadBuffer, inputJpeg.Length);
        upload.LoadRawTextureData(uploadBuffer);
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
        int mcuColumns = (width + mcuSize - 1) / mcuSize, mcuRows = (height + mcuSize - 1) / mcuSize,
            mcuCount = mcuColumns * mcuRows;
        material.SetInt("_Subsampled", subsampleChroma ? 1 : 0);
        int inputWords = (inputJpeg.Length + 3) / 4;
        restartOffsets = CodecBuffers.Surface(restartOffsets, 1024, (inputWords + 1023) / 1024,
                                              RenderTextureFormat.RFloat);
        restartScanScratch =
            CodecBuffers.Surface(restartScanScratch, 1024, restartOffsets.height, RenderTextureFormat.RFloat);
        blockStarts = CodecBuffers.Surface(blockStarts, 1024, (mcuCount * (blocks + 1) + 1023) / 1024,
                                           RenderTextureFormat.ARGBFloat);
        coefficients = CodecBuffers.Surface(coefficients, 1024, (mcuCount * blocks * 16 + 1023) / 1024,
                                            RenderTextureFormat.ARGBFloat);
        inverseRows = CodecBuffers.Surface(inverseRows, mcuColumns * mcuSize, mcuRows * mcuSize,
                                           RenderTextureFormat.ARGBFloat);
        output = CodecBuffers.Surface(
            output, width, height, linearOutput ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32);
        errorCounts =
            CodecBuffers.Surface(errorCounts, 1024, (mcuCount + 1023) / 1024, RenderTextureFormat.RFloat);
        errorScanScratch =
            CodecBuffers.Surface(errorScanScratch, 1024, errorCounts.height, RenderTextureFormat.RFloat);
        material.SetInt("_Cols", mcuColumns);
        material.SetInt("_Rows", mcuRows);
        material.SetInt("_Width", width);
        material.SetInt("_Height", height);
        material.SetInt("_Count", inputWords);
        VRCGraphics.Blit(Texture2D.blackTexture, restartOffsets, material, 5);
        for (int step = 1; step < inputWords; step *= 2)
        {
            material.SetTexture("_Prefix", restartOffsets);
            material.SetInt("_Stride", step);
            VRCGraphics.Blit(Texture2D.blackTexture, restartScanScratch, material, 3);
            RenderTexture swap = restartOffsets;
            restartOffsets = restartScanScratch;
            restartScanScratch = swap;
        }
        material.SetTexture("_Prefix", restartOffsets);
        material.SetTexture("_RestartPrefix", restartOffsets);
        material.SetTexture("_Starts", blockStarts);
        material.SetTexture("_Coefficients", coefficients);
        material.SetTexture("_Work", inverseRows);
        VRCGraphics.Blit(Texture2D.blackTexture, blockStarts, material, 6);
        VRCGraphics.Blit(Texture2D.blackTexture, coefficients, material, 7);
        VRCGraphics.Blit(Texture2D.blackTexture, inverseRows, material, 10);
        VRCGraphics.Blit(Texture2D.blackTexture, output, material, 11);
        VRCGraphics.Blit(Texture2D.blackTexture, errorCounts, material, 8);
        material.SetInt("_Count", mcuCount);
        for (int step = 1; step < mcuCount; step *= 2)
        {
            material.SetTexture("_Prefix", errorCounts);
            material.SetInt("_Stride", step);
            VRCGraphics.Blit(Texture2D.blackTexture, errorScanScratch, material, 3);
            RenderTexture swap = errorCounts;
            errorCounts = errorScanScratch;
            errorScanScratch = swap;
        }
        material.SetTexture("_Prefix", errorCounts);
        VRCGraphics.Blit(Texture2D.blackTexture, metadata, material, 9);
        readbackPending = true;
        progress = .9f;
        VRCAsyncGPUReadback.Request(metadata, 0, TextureFormat.RGBA32, this);
    }

    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        readbackPending = false;
        if (!busy) return;
        byte[] bytes = statusReadback;
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
        if (ownsMaterial) Destroy(material);
        if (upload) Destroy(upload);
        RenderTexture[] textures = { header,      metadata,        restartOffsets, restartScanScratch,
                                     blockStarts, coefficients,    inverseRows,    output,
                                     errorCounts, errorScanScratch };
        CodecBuffers.Release(textures);
    }
}
}
