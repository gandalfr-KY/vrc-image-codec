using UdonSharp;
using UnityEngine;

namespace VrcImageCodec
{
[AddComponentMenu("Image Codec/Image Decoder")]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ImageDecoder : UdonSharpBehaviour
{
    [SerializeField, HideInInspector]
    private AvifDecoder avifDecoder;
    [SerializeField, HideInInspector]
    private JpegDecoder jpegDecoder;
    private RenderTexture resultTexture;
    private bool busy, complete, readbackPending;
    private string error = "";
    private bool decodingJpeg;

    public bool IsBusy => busy;
    public bool IsComplete => complete;
    public bool HasPendingReadback => readbackPending;
    /// <summary>A decoder-owned texture. Blit a copy before decoding another image to retain it.</summary>
    public RenderTexture ResultTexture => resultTexture;
    public string LastError => error;

    /// <summary>Starts decoding this library's AVIF/JPEG profile. True means accepted, not
    /// completed.</summary> <remarks>Keep bytes unchanged until processing and pending GPU readbacks
    /// finish.</remarks>
    public bool TryBeginDecoding(byte[] bytes)
    {
        RefreshReadbackState();
        if (busy || readbackPending) return false;
        complete = false;
        resultTexture = null;
        error = "";
        if (bytes == null || bytes.Length < 2)
        {
            error = "Provide complete AVIF or JPEG image bytes.";
            return false;
        }
        decodingJpeg = bytes[0] == 255 && bytes[1] == 216;
        if (!decodingJpeg &&
            !(bytes.Length >= 8 && bytes[4] == 102 && bytes[5] == 116 && bytes[6] == 121 && bytes[7] == 112))
        {
            error = "The image format is not supported. Provide AVIF or JPEG bytes.";
            return false;
        }
        // Signature selects the implementation; its parser owns full profile validation.
        if (!avifDecoder || !jpegDecoder)
        {
            error = "Create a configured decoder using Tools > Image Codec > Create decoder.";
            return false;
        }
        if (decodingJpeg)
        {
            jpegDecoder.inputJpeg = bytes;
            jpegDecoder.BeginDecoding();
            busy = jpegDecoder.busy;
            error = jpegDecoder.error;
        }
        else
        {
            avifDecoder.inputAvif = bytes;
            avifDecoder.BeginDecoding();
            busy = avifDecoder.busy;
            error = avifDecoder.error;
        }
        RefreshReadbackState();
        return busy;
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
