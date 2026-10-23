#ifndef AR_MESH_VIEW_DIAGNOSTIC_INCLUDED
#define AR_MESH_VIEW_DIAGNOSTIC_INCLUDED
#include "UnityCG.cginc"
#include "MeshEncryptedPalette.hlsl"

// Checkout-only opt-in pass. No cipher read, mesh decode, key read or source position is used.
float _AR_ViewDiagnostic, _AR_ViewDiagnosticVertex;
struct ARVDStatus
{
    uint4 a; uint4 b; uint4 c; uint4 d;
    uint4 e; uint4 f; uint4 g; uint4 h;
};
uint ARVDMode()
{
#if defined(UNITY_STEREO_INSTANCING_ENABLED)
    return 2u;
#elif defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    return 3u;
#elif defined(UNITY_SINGLE_PASS_STEREO)
    return 1u;
#else
    return 0u;
#endif
}
uint ARVDEye()
{
#if defined(UNITY_SINGLE_PASS_STEREO) || defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    return (uint)unity_StereoEyeIndex;
#else
    return 0u;
#endif
}
float4 ARVDRect()
{
#if defined(UNITY_SINGLE_PASS_STEREO) || defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    return ARVDEye() <= 1u ? unity_StereoScaleOffset[ARVDEye()] : -1.0;
#else
    return float4(1, 1, 0, 0);
#endif
}
// The diagnostic explains refusal without changing any product acceptance check.
uint ARVDLayoutFailure(uint2 original, uint originalSlices)
{
    if (ARVDEye() > 1u) return 1u;
#if defined(UNITY_SINGLE_PASS_STEREO) && !defined(UNITY_STEREO_INSTANCING_ENABLED) && !defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    if (any(abs(ARVDRect() - float4(.5, 1, .5 * ARVDEye(), 0)) > .000001) || !all(ARMPFinite(ARVDRect()))) return 2u;
#endif
    if (any(original != ARMPTargetSize()) || (ARVDMode() >= 2u && originalSlices <= ARVDEye())) return 3u;
    float2 touches = float2(_AR_PaletteTouch0, _AR_PaletteTouch1);
    if (!all(ARMPFinite(touches)) || any(touches < 0) || any(touches > 1)) return 4u;
    if (!all(ARMPFinite(_AR_MeshInfo)) || _AR_MeshInfo.w != 1 || any(_AR_MeshInfo.xyz < 0) ||
        _AR_MeshInfo.x <= 0 || _AR_MeshInfo.y <= 0 || _AR_MeshInfo.y > 512 || _AR_MeshInfo.z > 512) return 5u;
    if (ARMPSlotCount() == 0u) return 6u;
    if (ARMP_TILE_SIZE + ARMPPayloadWords() * 3u > ARMP_TILE_SIZE * ARMP_TILE_SIZE) return 7u;
    return ARMPLayoutFits() ? 0u : 8u;
}
ARVDStatus ARVDSample(uint requestedRow)
{
    ARVDStatus o;
    uint ow, oh, os = 1u, pw, ph, ps = 1u;
#if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    _ARMeshOriginalV1.GetDimensions(ow, oh, os);
    _ARMeshPaletteV1.GetDimensions(pw, ph, ps);
#else
    _ARMeshOriginalV1.GetDimensions(ow, oh);
    _ARMeshPaletteV1.GetDimensions(pw, ph);
#endif
    uint eye = ARVDEye();
    uint failure = ARVDLayoutFailure(uint2(ow, oh), os);
    bool safe = eye <= 1u && ARMPLayoutFits() && (ARVDMode() < 2u || ps > eye) && all(uint2(pw, ph) == ARMPTargetSize());
    bool packetRow = requestedRow == 32u || requestedRow == 2u || (requestedRow >= 16u && requestedRow <= 21u) || requestedRow >= 30u;
    uint slot = safe && packetRow ? ARMPResolvedSlot() : ARMP_NO_SLOT;
    uint3 flag = 99u;
    uint mismatch = 99u, bone = 0u, headerWidth = 0u, headerHeight = 0u;
    bool valid = false;
    if (slot != ARMP_NO_SLOT)
    {
        flag = ARMPReadSymbols(ARMPTileOrigin(slot), false);
        if (requestedRow == 32u || requestedRow == 20u)
        {
            mismatch = 0u;
            [loop] for (uint word = 0u; word < ARMP_HEADER_WORDS; word++)
                if (mismatch == 0u && ARMPReadWord(slot, word, false) != ARMPHeaderWord(word)) mismatch = word + 1u;
        }
        if (requestedRow == 32u || requestedRow == 30u) headerWidth = ARMPReadWord(slot, 2u, false);
        if (requestedRow == 32u || requestedRow == 31u) headerHeight = ARMPReadWord(slot, 3u, false);
        if (requestedRow == 32u || requestedRow == 21u)
        {
            float4 r0, r1, r2;
            bone = ARMeshBoneRowsAt(slot, ARMPOwner(), 0u, r0, r1, r2) ? 1u : 0u;
        }
        if (requestedRow == 32u || requestedRow == 2u) valid = ARMeshPaletteValid();
    }
    o.a = uint4(ARVDMode(), eye, valid ? 1u : 0u, failure);
    o.b = uint4((uint2)_ScreenParams.xy, ow, oh);
    o.c = uint4(os, pw, ph, ps);
    // Scale/offset are shown in ten-thousandths; 999999 denotes invalid/negative.
    float4 rect = ARVDRect();
    o.d = (uint4)round(clamp(rect, 0, 99.9999) * 10000);
    o.d = rect < 0 ? 999999u : o.d;
    o.d = !ARMPFinite(rect) ? 999999u : o.d;
    o.e = uint4(slot == ARMP_NO_SLOT ? 999999u : slot, flag);
    o.f = uint4(mismatch, bone, (all(uint2(ow, oh) == ARMPTargetSize()) ? 1u : 0u) +
        (all(uint2(pw, ph) == ARMPTargetSize()) ? 2u : 0u), ARMPSlotCount());
    o.g = uint4(ARMPScreenSize(), ARMPTargetSize());
    o.h = uint4((uint2)round(saturate(float2(_AR_PaletteTouch0, _AR_PaletteTouch1)) * 10000), headerWidth, headerHeight);
    return o;
}
struct ARVDInput { float4 vertex : POSITION; uint vertexID : SV_VertexID; UNITY_VERTEX_INPUT_INSTANCE_ID };
struct ARVDV2G
{
    float4 pos : POSITION;
    nointerpolation uint4 v0 : TEXCOORD0; nointerpolation uint4 v1 : TEXCOORD1;
    nointerpolation uint4 v2 : TEXCOORD2; nointerpolation uint4 v3 : TEXCOORD3;
    nointerpolation uint4 v4 : TEXCOORD4; nointerpolation uint4 v5 : TEXCOORD5;
    nointerpolation uint4 v6 : TEXCOORD6; nointerpolation uint4 v7 : TEXCOORD7;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};
struct ARVDG2F
{
    float4 pos : SV_POSITION; float2 uv : TEXCOORD0;
    nointerpolation uint4 v0 : TEXCOORD1; nointerpolation uint4 v1 : TEXCOORD2;
    nointerpolation uint4 v2 : TEXCOORD3; nointerpolation uint4 v3 : TEXCOORD4;
    nointerpolation uint4 v4 : TEXCOORD5; nointerpolation uint4 v5 : TEXCOORD6;
    nointerpolation uint4 v6 : TEXCOORD7; nointerpolation uint4 v7 : TEXCOORD8;
    nointerpolation uint4 g0 : TEXCOORD9; nointerpolation uint4 g1 : TEXCOORD10;
    nointerpolation uint4 g2 : TEXCOORD11; nointerpolation uint4 g3 : TEXCOORD12;
    nointerpolation uint4 g4 : TEXCOORD13; nointerpolation uint4 g5 : TEXCOORD14;
    nointerpolation uint4 g6 : TEXCOORD15; nointerpolation uint4 g7 : TEXCOORD16;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};
ARVDV2G ARVDVertex(ARVDInput input)
{
    ARVDV2G o;
    UNITY_SETUP_INSTANCE_ID(input); UNITY_INITIALIZE_OUTPUT(ARVDV2G, o);
    UNITY_TRANSFER_INSTANCE_ID(input, o); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    o.pos = float4(0, 0, .5, 1);
    [branch] if (_AR_ViewDiagnostic < .5 || input.vertexID != (uint)round(_AR_ViewDiagnosticVertex)) return o;
    ARVDStatus s = ARVDSample(32u);
    o.v0=s.a; o.v1=s.b; o.v2=s.c; o.v3=s.d; o.v4=s.e; o.v5=s.f; o.v6=s.g; o.v7=s.h;
    return o;
}
[maxvertexcount(4)]
void ARVDGeometry(triangle ARVDV2G input[3], uint primitive : SV_PrimitiveID, inout TriangleStream<ARVDG2F> stream)
{
    UNITY_SETUP_INSTANCE_ID(input[0]); UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input[0]);
    if (_AR_ViewDiagnostic < .5 || primitive != 0u) return;
    ARVDStatus s = ARVDSample(32u);
    [unroll] for (uint corner = 0u; corner < 4u; corner++)
    {
        ARVDG2F o; UNITY_INITIALIZE_OUTPUT(ARVDG2F, o);
        UNITY_TRANSFER_VERTEX_OUTPUT_STEREO(input[0], o);
        UNITY_TRANSFER_INSTANCE_ID(input[0], o);
        o.uv = float2(corner & 1u, corner >> 1u);
        o.pos = float4(lerp(-.96, .96, o.uv.x), lerp(.92, -.92, o.uv.y), .5, 1);
#if UNITY_UV_STARTS_AT_TOP
        // D3D clip-space raster orientation is opposite the panel's text rows.
        o.uv.y = 1.0 - o.uv.y;
#endif
        o.v0=input[0].v0; o.v1=input[0].v1; o.v2=input[0].v2; o.v3=input[0].v3;
        o.v4=input[0].v4; o.v5=input[0].v5; o.v6=input[0].v6; o.v7=input[0].v7;
        o.g0=s.a; o.g1=s.b; o.g2=s.c; o.g3=s.d; o.g4=s.e; o.g5=s.f; o.g6=s.g; o.g7=s.h;
        stream.Append(o);
    }
    stream.RestartStrip();
}
uint ARVDValue(ARVDStatus s, uint row)
{
    uint group = row >> 2u, component = row & 3u;
    if (group==0u) return s.a[component]; if (group==1u) return s.b[component];
    if (group==2u) return s.c[component]; if (group==3u) return s.d[component];
    if (group==4u) return s.e[component]; if (group==5u) return s.f[component];
    if (group==6u) return s.g[component]; return s.h[component];
}
bool ARVDBox(float2 p, float2 center, float2 halfSize) { return all(abs(p-center)<halfSize); }
bool ARVDDigit(float2 p, uint digit)
{
    uint bits = digit==0u?63u:digit==1u?6u:digit==2u?91u:digit==3u?79u:digit==4u?102u:
        digit==5u?109u:digit==6u?125u:digit==7u?7u:digit==8u?127u:111u;
    bool on = false;
    on = on || ((bits&1u)!=0u && ARVDBox(p,float2(.5,.1),float2(.29,.055)));
    on = on || ((bits&2u)!=0u && ARVDBox(p,float2(.84,.29),float2(.055,.15)));
    on = on || ((bits&4u)!=0u && ARVDBox(p,float2(.84,.71),float2(.055,.15)));
    on = on || ((bits&8u)!=0u && ARVDBox(p,float2(.5,.9),float2(.29,.055)));
    on = on || ((bits&16u)!=0u && ARVDBox(p,float2(.16,.71),float2(.055,.15)));
    on = on || ((bits&32u)!=0u && ARVDBox(p,float2(.16,.29),float2(.055,.15)));
    on = on || ((bits&64u)!=0u && ARVDBox(p,float2(.5,.5),float2(.29,.055)));
    return on;
}
bool ARVDNumber(float2 uv, uint value, uint digits)
{
    uint cell = min(digits-1u, (uint)(uv.x*digits));
    uint divisor=1u;
    [loop] for(uint n=cell+1u;n<digits;n++) divisor*=10u;
    return ARVDDigit(float2(frac(uv.x*digits),uv.y), min(value,999999u)/divisor%10u);
}
float4 ARVDFragment(ARVDG2F input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input); UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    ARVDStatus vs,gs,fs;
    vs.a=input.v0; vs.b=input.v1; vs.c=input.v2; vs.d=input.v3; vs.e=input.v4; vs.f=input.v5; vs.g=input.v6; vs.h=input.v7;
    gs.a=input.g0; gs.b=input.g1; gs.c=input.g2; gs.d=input.g3; gs.e=input.g4; gs.f=input.g5; gs.g=input.g6; gs.h=input.g7;
    uint row=min(31u,(uint)(input.uv.y*32)); float y=frac(input.uv.y*32);
    float3 background = (row&1u)!=0u ? float3(.015,.025,.04) : float3(.025,.035,.055);
    if(input.uv.x<.08)
        return float4(ARVDNumber(float2(input.uv.x/.08,y),row,2u)?float3(.7,.7,.7):background,1);
    float x=(input.uv.x-.1)/.9;
    uint column=min(2u,(uint)(saturate(x)*3)); float local=frac(x*3);
    ARVDStatus selected=vs;
    if(column==1u) selected=gs;
    // Only its own column needs genuine fragment-stage texture reads.
    [branch] if(column==2u) { fs=ARVDSample(row); selected=fs; }
    float3 color=column==0u?float3(.2,.85,1):column==1u?float3(1,.85,.2):float3(.3,1,.4);
    if(row==2u && selected.a.z==0u) color=float3(1,.2,.2);
    bool ink=x>=0 && local<.93 && ARVDNumber(float2(local/.93,y),ARVDValue(selected,row),6u);
    return float4(ink?color:background,1);
}
#endif
