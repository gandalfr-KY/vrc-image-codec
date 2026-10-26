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
    public ImageEncodingFormat format;
    public bool subsampleChroma = true;
    public RenderTexture inputTexture;
    public int width = 640, height = 360, quality = 60;
    [HideInInspector]
    public AvifEncoder avifEncoder;
    [HideInInspector]
    public JpegEncoder jpegEncoder;
    [HideInInspector]
    public bool busy, complete, readbackPending;
    [HideInInspector]
    public byte[] resultBytes;
    [HideInInspector]
    public string error = "";
    private ImageEncodingFormat activeFormat;

    public string ConfigurationError()
    {
        if (!inputTexture) return "Assign an input RenderTexture.";
        if (quality < 1 || quality > 100) return "Quality must be between 1 and 100.";
        if (width < 1 || height < 1 || width > MaximumDimension || height > MaximumDimension ||
            width * height > MaximumPixels)
            return "Resolution must fit within 1920 pixels per side and 2,073,600 pixels in total.";
        if (format == ImageEncodingFormat.AVIF &&
            (width < 16 || height < 16 || width % 8 != 0 || height % 8 != 0))
            return "AVIF requires dimensions of at least 16 pixels, in multiples of 8.";
        if (width * inputTexture.height != height * inputTexture.width)
            return "Resolution must keep the input RenderTexture's aspect ratio. The image will not be " +
                   "stretched or cropped.";
        return "";
    }

    public void BeginEncoding()
    {
        RefreshReadbackState();
        if (busy || readbackPending) return;
        complete = false;
        resultBytes = null;
        error = ConfigurationError();
        if (error != "") return;
        activeFormat = format;
        RenderTexture destination;
        if (activeFormat == ImageEncodingFormat.AVIF)
        {
            if (!avifEncoder || avifEncoder.width != width || avifEncoder.height != height ||
                avifEncoder.subsampleChroma != subsampleChroma)
            {
                error = "Configure the encoder in its Inspector before encoding.";
                return;
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
                return;
            }
            destination = (RenderTexture)jpegEncoder.inputTexture;
            jpegEncoder.quality = quality;
        }
        if (inputTexture != destination) VRCGraphics.Blit(inputTexture, destination);
        if (activeFormat == ImageEncodingFormat.AVIF)
        {
            avifEncoder.BeginEncoding();
            busy = avifEncoder.busy;
            error = avifEncoder.error;
        }
        else
        {
            jpegEncoder.BeginEncoding();
            busy = jpegEncoder.busy;
            error = jpegEncoder.error;
        }
        RefreshReadbackState();
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
}
}
