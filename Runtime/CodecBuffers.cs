using UnityEngine;

namespace VrcImageCodec
{
// Allocation policy shared by codecs; callers retain ownership and release their own buffers.
public static class CodecBuffers
{
    public static void Release(RenderTexture[] textures)
    {
        for (int i = 0; i < textures.Length; i++)
            if (textures[i])
            {
                textures[i].Release();
                Object.Destroy(textures[i]);
            }
    }
    public static RenderTexture Surface(RenderTexture previous, int width, int height,
                                        RenderTextureFormat format, bool srgb = false)
    {
        if (previous && previous.width == width && previous.height == height && previous.format == format)
            return previous;
        if (previous)
        {
            previous.Release();
            Object.Destroy(previous);
        }
        var texture = new RenderTexture(width, height, 0, format,
                                        srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.Create();
        return texture;
    }
}
}
