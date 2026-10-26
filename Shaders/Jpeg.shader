Shader "VrcAvif/JPEG"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "UnityCG.cginc"
#include "CodecCommon.hlsl"
        #include "JpegTables.hlsl"
        Texture2D<float4> _Input, _Work, _Coefficients, _Prefix, _Bytes, _Starts, _Huffman, _RestartPrefix, _Header;
        int _Width, _Height, _Cols, _Rows, _Count, _Quality, _Stride, _Length, _Linear, _Subsampled;
        // Linear buffers use 1024 pixels and store four bytes per pixel.
        int Index(v2f_img i) { return (int)i.pos.y * 1024 + (int)i.pos.x; }
        uint Byte(int index)
        {
            if (index < 0 || index >= _Length) return 0;
            uint4 bytes = (uint4)round(CodecCell(_Bytes, index / 4) * 255);
            return bytes[index % 4];
        }
        float Basis(int frequency, int sample)
        {
            return (frequency == 0 ? 0.3535533905932738 : 0.5) * cos((2 * sample + 1) * frequency * 0.1963495408493621);
        }
int Quant(int index, int component)
        {
            int scale = _Quality < 50 ? 5000 / _Quality : 200 - 2 * _Quality;
            return clamp(((component == 0 ? Quant0[index] : Quant1[index]) * scale + 50) / 100, 1, 255);
        }
        float3 SourceYuv(int2 p)
        {
            p = min(p,int2(_Width-1,_Height-1)); p.y = _Height-1-p.y;
            float3 rgb = _Input.Load(int3(p,0)).rgb;
            if (_Linear != 0) rgb = float3(LinearToGammaSpaceExact(rgb.r),LinearToGammaSpaceExact(rgb.g),LinearToGammaSpaceExact(rgb.b));
            return float3(dot(rgb,float3(.299,.587,.114))*255-128,
                          dot(rgb,float3(-.168736,-.331264,.5))*255,
                          dot(rgb,float3(.5,-.418688,-.081312))*255);
        }
        float4 Horizontal(v2f_img i) : SV_Target
        {
            int2 p = (int2)i.pos.xy;
            float3 sum = 0;
            [unroll] for (int x = 0; x < 8; x++)
            {
                int2 sample = int2(p.x/8*8+x,p.y);
                float3 value = SourceYuv(sample);
                if (_Subsampled != 0)
                {
                    int2 q = sample*2;
                    float3 down = (SourceYuv(q)+SourceYuv(q+int2(1,0))+SourceYuv(q+int2(0,1))+SourceYuv(q+int2(1,1)))*.25;
                    value.yz = down.yz;
                }
                sum += value * Basis(p.x%8,x);
            }
            return float4(sum,1);
        }
        int BlocksPerMcu() { return _Subsampled != 0 ? 6 : 3; }
        int Component(int block) { return _Subsampled != 0 ? max(0,block-3) : block; }
        float4 Vertical(v2f_img i) : SV_Target
        {
            int2 p = (int2)i.pos.xy;
            float3 sum = 0;
            [unroll] for (int y = 0; y < 8; y++) sum += _Work.Load(int3(p.x, p.y / 8 * 8 + y, 0)).rgb * Basis(p.y % 8, y);
            int k = p.y % 8 * 8 + p.x % 8;
            float3 q = float3(Quant(k,0), Quant(k,1), Quant(k,2));
            float3 v = sum / q;
            return float4(sign(v) * floor(abs(v) + .5), 1);
        }
        int Coefficient(int mcu, int block, int k)
        {
            int z = Zigzag[k];
            int component = Component(block);
            int2 p = int2(mcu%_Cols,mcu/_Cols)*8;
            if (_Subsampled != 0 && component == 0) p = p*2+int2(block%2,block/2)*8;
            return (int)_Coefficients.Load(int3(p+int2(z%8,z/8),0))[component];
        }
        uint Huffman(int component, bool ac, int symbol)
        {
            if (component == 0) return ac ? AcY[symbol] : DcY[symbol];
            return ac ? AcC[symbol] : DcC[symbol];
        }
        struct Writer { uint bits; int fill; int position; int target; uint4 bytes; };
        void WriteByte(inout Writer w, uint value)
        {
            int at = w.position++ - w.target;
            w.bytes = uint4(at == 0 ? value : w.bytes.x, at == 1 ? value : w.bytes.y,
                            at == 2 ? value : w.bytes.z, at == 3 ? value : w.bytes.w);
        }
        void WriteBits(inout Writer w, uint bits, int length)
        {
            w.bits = (w.bits << length) | bits;
            w.fill += length;
            [loop] while (w.fill >= 8)
            {
                w.fill -= 8;
                uint b = (w.bits >> w.fill) & 255;
                WriteByte(w, b);
                if (b == 255) WriteByte(w, 0);
            }
        }
        void WriteSymbol(inout Writer w, int component, bool ac, int symbol)
        {
            uint code = Huffman(component, ac, symbol);
            WriteBits(w, code >> 5, code & 31);
        }
        int Category(int value) { return value == 0 ? 0 : firstbithigh((uint)abs(value)) + 1; }
        Writer EncodeSegment(int mcu, int target)
        {
            Writer w = (Writer)0; w.target = target;
            int previousDc = 0;
            [loop] for (int block = 0; block < BlocksPerMcu(); block++)
            {
                int component = Component(block), actualDc = Coefficient(mcu,block,0);
                int dc = component == 0 ? actualDc-previousDc : actualDc, size = Category(dc);
                if (component == 0) previousDc = actualDc;
                WriteSymbol(w, component, false, size);
                if (size != 0) WriteBits(w, (uint)(dc < 0 ? dc + (1 << size) - 1 : dc), size);
                int run = 0;
                [loop] for (int k = 1; k < 64; k++)
                {
                    int value = Coefficient(mcu, block, k);
                    if (value == 0) { run++; continue; }
                    [loop] while (run >= 16) { WriteSymbol(w, component, true, 240); run -= 16; }
                    size = Category(value);
                    WriteSymbol(w, component, true, run * 16 + size);
                    WriteBits(w, (uint)(value < 0 ? value + (1 << size) - 1 : value), size);
                    run = 0;
                }
                if (run != 0) WriteSymbol(w, component, true, 0);
            }
            if (w.fill != 0) WriteBits(w, (1u << (8 - w.fill)) - 1, 8 - w.fill);
            if (mcu + 1 < _Count) { WriteByte(w, 255); WriteByte(w, 208 + (mcu % 8)); }
            return w;
        }
        float4 SegmentCount(v2f_img i) : SV_Target
        {
            int mcu = Index(i);
            int count = mcu < _Count ? EncodeSegment(mcu, -4).position : 0;
            return float4(count,0,0,0);
        }
        float4 Scan(v2f_img i) : SV_Target
        {
            int at = Index(i);
            if (at >= _Count) return 0;
            float4 value = CodecCell(_Prefix, at);
            if (at >= _Stride)
            {
                float4 previous = CodecCell(_Prefix, at - _Stride);
                value.r += previous.r;
            }
            return value;
        }


        uint HeaderByte(int at)
        {
            if (at == DimensionAt + 1) return (_Height >> 8) & 255;
            if (at == DimensionAt + 2) return _Height & 255;
            if (at == DimensionAt + 3) return (_Width >> 8) & 255;
            if (at == DimensionAt + 4) return _Width & 255;
            if (at == DimensionAt+7) return _Subsampled != 0 ? 34 : 17;
            [unroll] for (int c = 0; c < 2; c++) if (at >= QuantAt[c] && at < QuantAt[c] + 64) return Quant(Zigzag[at - QuantAt[c]], c);
            return HeaderBytes[at];
        }


        float4 DirectAssemble(v2f_img i) : SV_Target
        {
            int pixel = Index(i), total = (int)CodecCell(_Prefix,_Count-1).r;
            int length = HeaderSize + total + 2;
            if (pixel == 0) return CodecBytes(length);
            int at = (pixel-1)*4;
            if (at >= length) return 0;
            uint4 result = 0;
            int cachedMcu = -1, cachedLocal = 0;
            uint4 cachedBytes = 0;
            [loop] for (int k = 0; k < 4; k++)
            {
                int absolute = at+k;
                uint value=0;
                if (absolute < HeaderSize) value=HeaderByte(absolute);
                else if(absolute < length)
                {
                    int index=absolute-HeaderSize;
                    if(index>=total) value=index==total ? 255 : 217;
                    else
                    {
                        if(cachedMcu<0 || index>=(int)CodecCell(_Prefix,cachedMcu).r)
                        {
                            int low=0,high=_Count-1;
                            [loop] while(low<high){int mid=(low+high)/2;if(CodecCell(_Prefix,mid).r<=index)low=mid+1;else high=mid;}
                            cachedMcu=low;
                            cachedLocal=index-(low==0 ? 0 : (int)CodecCell(_Prefix,low-1).r);
                            Writer segment=EncodeSegment(low,cachedLocal);
                            cachedBytes=segment.bytes;
                        }
                        int local=index-(cachedMcu==0 ? 0 : (int)CodecCell(_Prefix,cachedMcu-1).r);
                        value=cachedBytes[local-cachedLocal];
                    }
                }
                result=uint4(k==0?value:result.x,k==1?value:result.y,k==2?value:result.z,k==3?value:result.w);
            }
            return (float4)result/255.;
        }
        float4 Header(v2f_img i) : SV_Target
        {
            int width = (Byte(DimensionAt+3) << 8) | Byte(DimensionAt+4);
            int height = (Byte(DimensionAt+1) << 8) | Byte(DimensionAt+2);
            bool valid = _Length >= HeaderSize + 2 && width > 0 && height > 0 && width <= 1920 && height <= 1920 && width * height <= 2073600;
            int sampling = Byte(DimensionAt+7) == 34 ? 1 : 0;
            if (Byte(DimensionAt+7) != 17 && sampling == 0) valid = false;
            [loop] for (int at = 0; at < HeaderSize; at++)
            {
                if ((at >= DimensionAt+1 && at <= DimensionAt+4) || at == DimensionAt+7) continue;
                if ((at >= QuantAt[0] && at < QuantAt[0]+64) || (at >= QuantAt[1] && at < QuantAt[1]+64))
                { if (Byte(at) == 0) valid = false; continue; }
                if (Byte(at) != HeaderBytes[at]) valid = false;
            }
            if (Byte(_Length-2) != 255 || Byte(_Length-1) != 217) valid = false;
            if ((int)i.pos.x == 1) return CodecBytes(sampling);
            return valid ? float4(width & 255, width >> 8, height & 255, height >> 8) / 255. : 0;
        }
        float4 RestartCount(v2f_img i) : SV_Target
        {
            int at = Index(i) * 4;
            int count = 0;
            [unroll] for (int k = 0; k < 4; k++) if (at+k >= HeaderSize && at+k+1 < _Length-2 && Byte(at+k) == 255 && Byte(at+k+1) >= 208 && Byte(at+k+1) <= 215) count++;
            return count;
        }
        int Marker(int ordinal)
        {
            int low = HeaderSize / 4, high = (_Length - 1) / 4;
            [loop] while (low < high)
            {
                int mid = (low + high) / 2;
                if (CodecCell(_Prefix, mid).r <= ordinal) low = mid+1; else high = mid;
            }
            int seen = low == 0 ? 0 : (int)CodecCell(_Prefix, low-1).r;
            [unroll] for (int k = 0; k < 4; k++)
            {
                int at = low * 4 + k;
                if (at >= HeaderSize && Byte(at) == 255 && Byte(at+1) >= 208 && Byte(at+1) <= 215)
                { if (seen++ == ordinal) return at; }
            }
            return _Length;
        }
        struct Reader { int position; uint bits; int fill; int padding; int dc; bool invalid; };
        void Fill(inout Reader r, int end)
        {
            [loop] while (r.fill < 16)
            {
                uint b = 255;
                if (r.position < end)
                {
                    b = Byte(r.position++);
                    if (b == 255)
                    {
                        if (r.position >= end || Byte(r.position) != 0) r.invalid = true;
                        r.position++;
                    }
                }
                else r.padding += 8;
                r.bits = (r.bits << 8) | b; r.fill += 8;
            }
        }
        uint ReadBits(inout Reader r, int end, int size)
        {
            if (size == 0) return 0;
            Fill(r,end); r.fill -= size;
            return (r.bits >> r.fill) & ((1u << size) - 1);
        }
        int ReadSymbol(inout Reader r, int end, int channel)
        {
            Fill(r,end);
            uint look = (r.bits >> (r.fill-16)) & 65535;
            uint entry = (uint)_Huffman.Load(int3(look % 256, look / 256, 0))[channel];
            int size = entry & 31;
            if (size == 0) r.invalid = true;
            r.fill -= size;
            return entry >> 5;
        }
        int Receive(inout Reader r, int end, int size)
        {
            int value = (int)ReadBits(r,end,size);
            return size == 0 || value >= (1 << (size-1)) ? value : value - (1 << size) + 1;
        }
        float4 DecodeBlock(inout Reader r, int end, int component, int group)
        {
            float4 result = 0;
            int size = ReadSymbol(r,end,component == 0 ? 0 : 2);
            if (size > 11) { r.invalid = true; return 0; }
            int dc = Receive(r,end,size);
            if (component == 0) { r.dc += dc; dc = r.dc; }
            if (group == 0) result.x = dc;
            int k = 1;
            [loop] for (int symbol = 0; symbol < 64 && k < 64 && !r.invalid; symbol++)
            {
                int value = ReadSymbol(r,end,component == 0 ? 1 : 3);
                if (value == 0) break;
                int run = value >> 4; size = value & 15;
                if (size == 0 && run == 15) { k += 16; continue; }
                k += run;
                if (size == 0 || size > 10 || k >= 64) { r.invalid = true; break; }
                int coefficient = Receive(r,end,size), z = Zigzag[k++];
                result = float4(z == group*4 ? coefficient : result.x,
                                z == group*4+1 ? coefficient : result.y,
                                z == group*4+2 ? coefficient : result.z,
                                z == group*4+3 ? coefficient : result.w);
            }
            if (k > 64) r.invalid = true;
            return result;
        }
        float4 BlockStarts(v2f_img i) : SV_Target
        {
            int index = Index(i), states = BlocksPerMcu()+1, mcu = index/states, block = index%states;
            if (mcu >= _Cols * _Rows) return 0;
            int start = mcu == 0 ? HeaderSize : Marker(mcu-1) + 2;
            int end = mcu+1 == _Cols * _Rows ? _Length-2 : Marker(mcu);
            Reader r = (Reader)0; r.position = start;
            r.invalid = start >= end || end > _Length - 2;
            if (mcu+1 < _Cols * _Rows && Byte(end+1) != 208 + mcu % 8) r.invalid = true;
            [loop] for (int c = 0; c < block && !r.invalid; c++) DecodeBlock(r,end,Component(c),-1);
            if (block == BlocksPerMcu())
            {
                // Fill may append one all-one padding byte for lookahead; real unread data is invalid.
                if (r.position != end || r.fill-r.padding < 0 || r.fill-r.padding > 7 || (r.bits & ((1u << r.fill)-1)) != (1u << r.fill)-1) r.invalid = true;
                return r.invalid ? 1 : 0;
            }
            return float4(r.invalid ? -1 : r.position, r.bits & 65535, r.bits >> 16, r.fill+r.padding*32+(r.dc+2048)*1024);
        }
        float4 DecodeCoefficients(v2f_img i) : SV_Target
        {
            int index = Index(i), stride = BlocksPerMcu()*16, mcu = index/stride, block = (index%stride)/16, group = index%16;
            if (mcu >= _Cols*_Rows) return 0;
            float4 state = CodecCell(_Starts, mcu*(BlocksPerMcu()+1)+block);
            Reader r; r.position = (int)state.x; r.bits = (uint)state.y | ((uint)state.z << 16); r.fill = (int)state.w % 32; r.padding = ((int)state.w%1024)/32; r.dc = (int)state.w/1024-2048; r.invalid = r.position < 0;
            int end = mcu+1 == _Cols*_Rows ? _Length-2 : Marker(mcu);
            return r.invalid ? 0 : DecodeBlock(r,end,Component(block),group);
        }
        float4 DecodeError(v2f_img i) : SV_Target
        {
            int mcu = Index(i);
            if (mcu >= _Cols*_Rows) return 0;
            return CodecCell(_Starts,mcu*(BlocksPerMcu()+1)+BlocksPerMcu()).r;
        }
        float4 DecodeStatus(v2f_img i) : SV_Target
        {
            uint errors = (uint)CodecCell(_Prefix,_Count-1).r;
            uint4 header=(uint4)round(_Header.Load(int3(0,0,0))*255);
            int width=header.x | (header.y<<8), height=header.z | (header.w<<8);
            int sampling=(int)round(_Header.Load(int3(1,0,0)).r*255);
            if(width!=_Width || height!=_Height || sampling!=_Subsampled) errors++;
            if ((int)CodecCell(_RestartPrefix, (_Length-1)/4).r != _Count-1) errors++;
            return CodecBytes(errors);
        }
        float4 InverseHorizontal(v2f_img i) : SV_Target
        {
            int2 p = (int2)i.pos.xy;
            float3 sum = 0;
            [unroll] for (int c = 0; c < 3; c++)
            {
                int mcu,block;
                if (_Subsampled != 0 && c == 0) { mcu = p.y/16*_Cols+p.x/16; block = p.y%16/8*2+p.x%16/8; }
                else { mcu = p.y/8*_Cols+p.x/8; block = _Subsampled != 0 ? c+3 : c; }
                [unroll] for (int u = 0; u < 8; u++)
                {
                    int k = p.y%8*8+u;
                    int q = (int)Byte(QuantAt[c == 0 ? 0 : 1]+NaturalToZigzag(k));
                    sum[c] += CodecCell(_Coefficients,(mcu*BlocksPerMcu()+block)*16+k/4)[k%4]*q*Basis(u,p.x%8);
                }
            }
            return float4(sum,1);
        }
        float4 InverseVertical(v2f_img i) : SV_Target
        {
            int2 p = (int2)i.pos.xy;
            p.y = _Height-1-p.y;
            float3 sum = 0;
            [unroll] for (int c = 0; c < 3; c++)
            {
                int2 q = c != 0 && _Subsampled != 0 ? p/2 : p;
                [unroll] for (int v = 0; v < 8; v++) sum[c] += _Work.Load(int3(q.x,q.y/8*8+v,0))[c]*Basis(v,q.y%8);
            }
            float3 yuv = clamp(floor(sum+128.5),0,255);
            float y=yuv.x,cb=yuv.y-128,cr=yuv.z-128;
            float3 rgb = saturate(float3(y+1.402*cr,y-.344136*cb-.714136*cr,y+1.772*cb)/255.);
            if (_Linear != 0) rgb = float3(CodecSrgbToLinear(rgb.r),CodecSrgbToLinear(rgb.g),CodecSrgbToLinear(rgb.b));
            return float4(rgb,1);
        }
        ENDCG
        Pass { CGPROGRAM
        #pragma target 5.0
        #pragma vertex vert_img
        #pragma fragment Horizontal
        ENDCG
        }
        Pass { CGPROGRAM
        #pragma target 5.0
        #pragma vertex vert_img
        #pragma fragment Vertical
        ENDCG
        }
        Pass { CGPROGRAM
        #pragma target 5.0
        #pragma vertex vert_img
        #pragma fragment SegmentCount
        ENDCG
        }
        Pass { CGPROGRAM
        #pragma target 5.0
        #pragma vertex vert_img
        #pragma fragment Scan
        ENDCG
        }



        Pass { CGPROGRAM
        #pragma target 5.0
        #pragma vertex vert_img
        #pragma fragment Header
        ENDCG
        }
        Pass { CGPROGRAM
        #pragma target 5.0
        #pragma vertex vert_img
        #pragma fragment RestartCount
        ENDCG
        }
        Pass { CGPROGRAM
        #pragma target 5.0
        #pragma vertex vert_img
        #pragma fragment BlockStarts
        ENDCG
        }
        Pass { CGPROGRAM
        #pragma target 5.0
        #pragma vertex vert_img
        #pragma fragment DecodeCoefficients
        ENDCG
        }
        Pass { CGPROGRAM
        #pragma target 5.0
        #pragma vertex vert_img
        #pragma fragment DecodeError
        ENDCG
        }
        Pass { CGPROGRAM
        #pragma target 5.0
        #pragma vertex vert_img
        #pragma fragment DecodeStatus
        ENDCG
        }
        Pass { CGPROGRAM
        #pragma target 5.0
        #pragma vertex vert_img
        #pragma fragment InverseHorizontal
        ENDCG
        }
        Pass { CGPROGRAM
        #pragma target 5.0
        #pragma vertex vert_img
        #pragma fragment InverseVertical
        ENDCG
        }
        Pass { CGPROGRAM
        #pragma target 5.0
        #pragma vertex vert_img
        #pragma fragment DirectAssemble
        ENDCG
        }
    }
}
