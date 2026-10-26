"""Catch D3D11 compilation errors before running an image-quality sweep."""

from pathlib import Path
import ctypes as c, re, sys, time

S = Path(__file__).resolve().parents[1] / "Shaders"
compiler = c.WinDLL("d3dcompiler_47.dll").D3DCompile
compiler.argtypes = [
    c.c_void_p,
    c.c_size_t,
    c.c_char_p,
    c.c_void_p,
    c.c_void_p,
    c.c_char_p,
    c.c_char_p,
    c.c_uint,
    c.c_uint,
    c.POINTER(c.c_void_p),
    c.POINTER(c.c_void_p),
]
compiler.restype = c.c_long


def read(ptr):
    v = c.cast(ptr, c.POINTER(c.POINTER(c.c_void_p))).contents
    address = c.WINFUNCTYPE(c.c_void_p, c.c_void_p)(v[3])(ptr)
    length = c.WINFUNCTYPE(c.c_size_t, c.c_void_p)(v[4])(ptr)
    return c.string_at(address, length)


failed = False
for shader in ["Av1Intra.shader"] if sys.argv[1:] else sorted(p.name for p in S.glob("*.shader")):
    text = (S / shader).read_text(encoding="utf-8")
    source = text.split("CGINCLUDE", 1)[1].split("ENDCG", 1)[0]
    source = source.replace(
        '#include "UnityCG.cginc"',
        "struct v2f_img {float4 pos:SV_Position;float2 uv:TEXCOORD0;};struct appdata_img {float4 vertex:POSITION;float2 texcoord:TEXCOORD0;};v2f_img vert_img(appdata_img v){v2f_img o;o.pos=v.vertex;o.uv=v.texcoord;return o;}\nfloat LinearToGammaSpaceExact(float v){return pow(max(v,0),1./2.2);}\nfloat3 GammaToLinearSpace(float3 v){return pow(max(v,0),2.2);}",
    )
    source = re.sub(
        r'#include "([^"]+)"', lambda m: (S / m[1]).read_text(encoding="utf-8"), source
    )
    entries = sys.argv[1:] or list(
        dict.fromkeys(re.findall(r"#pragma (?:fragment|vertex|geometry) (\w+)", text))
    )
    for entry in entries:
        data = source.encode()
        blob = c.c_void_p()
        err = c.c_void_p()
        start = time.perf_counter()
        hr = compiler(
            data,
            len(data),
            b"rdo-shader",
            None,
            None,
            entry.encode(),
            (b"gs_5_0" if re.search(r"#pragma geometry " + entry + r"\b", text) else b"vs_5_0" if re.search(r"#pragma vertex " + entry + r"\b", text) else b"ps_5_0"),
            4,
            0,
            c.byref(blob),
            c.byref(err),
        )
        if hr < 0:
            failed = True
            print(read(err).decode(errors="replace") if err else "No error text")
        print(
            shader,
            entry,
            hex(hr & 0xFFFFFFFF),
            round(time.perf_counter() - start, 3),
            "seconds",
            flush=True,
        )
sys.exit(1 if failed else 0)
