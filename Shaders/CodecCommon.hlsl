// Shared byte layout and color conversion; format-specific transforms stay in their own kernels.
#ifndef VRC_IMAGE_CODEC_COMMON
#define VRC_IMAGE_CODEC_COMMON
float4 CodecBytes(uint value)
{
    return float4(value & 255, (value >> 8) & 255, (value >> 16) & 255, value >> 24) / 255.;
}
float4 CodecCell(Texture2D<float4> tex, int index)
{
    return tex.Load(int3(index % 1024, index / 1024, 0));
}
float CodecSrgbToLinear(float value)
{
    return value <= .04045 ? value / 12.92 : pow((value + .055) / 1.055, 2.4);
}
#endif
