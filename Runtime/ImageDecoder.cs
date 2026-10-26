using UdonSharp;
using UnityEngine;

namespace VrcImageCodec
{
[AddComponentMenu("Image Codec/Image Decoder")]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ImageDecoder : UdonSharpBehaviour
{
    [HideInInspector]
    public byte[] inputBytes;
    [HideInInspector]
    public RenderTexture resultTexture;
    [HideInInspector]
    public bool busy, complete, readbackPending;
    [HideInInspector]
    public string error = "";
    [HideInInspector]
    public AvifDecoder avifDecoder;
    [HideInInspector]
    public JpegDecoder jpegDecoder;
    private bool decodingJpeg;

    public void BeginDecoding()
    {
        RefreshReadbackState();
        if (busy || readbackPending) return;
        complete = false;
        resultTexture = null;
        error = "";
        if (inputBytes == null || inputBytes.Length < 2)
        {
            error = "Provide complete AVIF or JPEG image bytes.";
            return;
        }
        decodingJpeg = inputBytes[0] == 255 && inputBytes[1] == 216;
        if (!decodingJpeg && !(inputBytes.Length >= 8 && inputBytes[4] == 102 && inputBytes[5] == 116 &&
                               inputBytes[6] == 121 && inputBytes[7] == 112))
        {
            error = "The image format is not supported. Provide AVIF or JPEG bytes.";
            return;
        }
        // Signature selects the implementation; its parser owns full profile validation.
        if (!avifDecoder || !jpegDecoder)
        {
            error = "Create a configured decoder using Tools > Image Codec > Create decoder.";
            return;
        }
        if (decodingJpeg)
        {
            jpegDecoder.inputJpeg = inputBytes;
            jpegDecoder.BeginDecoding();
            busy = jpegDecoder.busy;
            error = jpegDecoder.error;
        }
        else
        {
            avifDecoder.inputAvif = inputBytes;
            avifDecoder.BeginDecoding();
            busy = avifDecoder.busy;
            error = avifDecoder.error;
        }
        RefreshReadbackState();
    }

    private void LateUpdate()
    {
        RefreshReadbackState();
        if (!busy) return;
        if (decodingJpeg)
        {
            if (jpegDecoder.busy) return;
            complete = jpegDecoder.complete;
            resultTexture = jpegDecoder.resultTexture;
            error = jpegDecoder.error;
        }
        else
        {
            if (avifDecoder.busy) return;
            complete = avifDecoder.complete;
            resultTexture = avifDecoder.resultTexture;
            error = avifDecoder.error;
        }
        busy = false;
    }

    public void CancelDecoding()
    {
        if (avifDecoder) avifDecoder.CancelDecoding();
        if (jpegDecoder) jpegDecoder.CancelDecoding();
        busy = complete = false;
        resultTexture = null;
        error = "";
        RefreshReadbackState();
    }

    private void RefreshReadbackState()
    {
        readbackPending =
            (avifDecoder && avifDecoder.readbackPending) || (jpegDecoder && jpegDecoder.readbackPending);
    }
}
}
