using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace VrcImageCodec
{
public enum ImageEncodingFormat
{
    AVIF,
    JPEG
}

// User-facing settings and texture resizing; format-specific codecs own compression.
[AddComponentMenu("Image Codec/Image Encoder")]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ImageEncoder : UdonSharpBehaviour
{
    public const int MaximumDimension = 1920, MaximumPixels = 1920 * 1080;
    [SerializeField]
    private ImageEncodingFormat format;
    [SerializeField]
    private bool subsampleChroma = true;
    [SerializeField]
    private RenderTexture inputTexture;
    [SerializeField]
    private int width = 640, height = 360, quality = 60;
    [SerializeField, HideInInspector]
    private AvifEncoder avifEncoder;
    [SerializeField, HideInInspector]
    private JpegEncoder jpegEncoder;
    private bool busy, complete, readbackPending;
    private byte[] resultBytes;
    private string error = "";
    private ImageEncodingFormat activeFormat;
    private RenderTexture snapshot;

    public ImageEncodingFormat Format => format;
    public bool SubsampleChroma => subsampleChroma;
    public int Width => width;
    public int Height => height;
    public RenderTexture InputTexture => inputTexture;
    public int Quality
    {
        get => quality;
        set => quality = value;
    }
    public bool IsBusy => busy;
    public bool IsComplete => complete;
    public bool HasPendingReadback => readbackPending;
    /// <summary>A new array for each completed image. Retained arrays are not reused or modified.</summary>
    public byte[] ResultBytes => resultBytes;
    public string LastError => error;

    public string GetConfigurationError()
    {
        return ValidateInput(inputTexture);
    }

    private string ValidateInput(RenderTexture source)
    {
        if (!source) return "Assign an input RenderTexture.";
        if (source.width < 16 || source.height < 16)
            return "Set both dimensions of the input RenderTexture to at least 16 pixels.";
        if (source.width % 8 != 0 || source.height % 8 != 0)
            return "Set both dimensions of the input RenderTexture to multiples of 8.";
        if (quality < 1 || quality > 100) return "Quality must be between 1 and 100.";
        if (width < 16 || height < 16 || width > MaximumDimension || height > MaximumDimension ||
            width * height > MaximumPixels)
            return "Resolution must be at least 16 pixels per side, at most 1920 pixels per side, " +
                   "and at most 2,073,600 pixels in total.";
        if (width % 8 != 0 || height % 8 != 0) return "Set both output dimensions to multiples of 8.";
        if (format != ImageEncodingFormat.AVIF && format != ImageEncodingFormat.JPEG)
            return "Choose AVIF or JPEG.";
        if ((long)width * source.height != (long)height * source.width)
            return "Resolution must keep the input RenderTexture's aspect ratio. The image will not be " +
                   "stretched or cropped.";
        return "";
    }

    public void BeginEncoding()
    {
        TryBeginEncoding(inputTexture);
    }

    /// <summary>Snapshots the source and starts encoding. True means accepted, not completed.</summary>
    /// <remarks>A busy/readback rejection preserves the current operation and its result/error.</remarks>
    public bool TryBeginEncoding(RenderTexture source)
    {
        RefreshReadbackState();
        if (busy || readbackPending) return false;
        complete = false;
        resultBytes = null;
        error = ValidateInput(source);
        if (error != "") return false;
        activeFormat = format;
        RenderTexture destination;
        if (activeFormat == ImageEncodingFormat.AVIF)
        {
            if (!avifEncoder || avifEncoder.width != width || avifEncoder.height != height ||
                avifEncoder.subsampleChroma != subsampleChroma)
            {
                error = "Configure the encoder in its Inspector before encoding.";
                return false;
            }
            destination = avifEncoder.gpuTransform.inputTexture;
            avifEncoder.quality = quality;
        }
        else
        {
            if (!jpegEncoder || jpegEncoder.width != width || jpegEncoder.height != height ||
                jpegEncoder.subsampleChroma != subsampleChroma)
            {
                error = "Configure the encoder in its Inspector before encoding.";
                return false;
            }
            destination = (RenderTexture)jpegEncoder.inputTexture;
            jpegEncoder.quality = quality;
        }
        // Serialized layout assets can be shared by prefab instances; image snapshots cannot.
        if (!snapshot) snapshot = CodecBuffers.AllocateLike(destination);
        if (source != snapshot) VRCGraphics.Blit(source, snapshot);
        if (activeFormat == ImageEncodingFormat.AVIF)
        {
            avifEncoder.gpuTransform.inputTexture = snapshot;
            avifEncoder.BeginEncoding();
            busy = avifEncoder.busy;
            error = avifEncoder.error;
        }
        else
        {
            jpegEncoder.inputTexture = snapshot;
            jpegEncoder.BeginEncoding();
            busy = jpegEncoder.busy;
            error = jpegEncoder.error;
        }
        RefreshReadbackState();
        return busy;
    }

    private void LateUpdate()
    {
        RefreshReadbackState();
        if (!busy) return;
        if (activeFormat == ImageEncodingFormat.AVIF)
        {
            if (avifEncoder.busy) return;
            complete = avifEncoder.complete;
            resultBytes = avifEncoder.resultAvif;
            error = avifEncoder.error;
        }
        else
        {
            if (jpegEncoder.busy) return;
            complete = jpegEncoder.complete;
            resultBytes = jpegEncoder.resultJpeg;
            error = jpegEncoder.error;
        }
        busy = false;
    }

    public void CancelEncoding()
    {
        if (avifEncoder) avifEncoder.CancelEncoding();
        if (jpegEncoder) jpegEncoder.CancelEncoding();
        busy = complete = false;
        resultBytes = null;
        error = "";
        RefreshReadbackState();
    }

    private void RefreshReadbackState()
    {
        readbackPending = (avifEncoder && avifEncoder.gpuEntropy && avifEncoder.gpuEntropy.readbackPending) ||
                          (jpegEncoder && jpegEncoder.readbackPending);
    }

    private void OnDestroy()
    {
        CodecBuffers.Release(new RenderTexture[] { snapshot });
    }
}
}
