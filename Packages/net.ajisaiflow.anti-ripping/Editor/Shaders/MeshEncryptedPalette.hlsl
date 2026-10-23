#ifndef AR_MESH_ENCRYPTED_PALETTE_INCLUDED
#define AR_MESH_ENCRYPTED_PALETTE_INCLUDED

// Version 1 reserves identical tiles for every encrypted renderer. The atlas
// contains only generic bone transforms, public identifiers and morph weights.
// Coordinate and morph-delta ciphertext lives in the separate mesh payload.
#define ARMP_TILE_SIZE 128u
#define ARMP_HEADER_WORDS 8u
#define ARMP_BONE_WORDS 13u
#define ARMP_SHAPE_WORDS 2u
#define ARMP_MAX_PROBES 8u
#define ARMP_NO_SLOT 0xffffffffu

// ARMPOwner, ARMPReadWord and ARMPFindSlot are shared by the consumer decode and
// the producer geometry shaders. The consumer compiles them as real loops so FXC
// does not expand the owner hash and the 3-part word read at every call site.
// The producer keeps the original unrolled loops and its compiled output.
#ifdef AR_MESH_PALETTE_PRODUCER
#define AR_MP_SHARED_LOOP [unroll]
#define AR_MP_SHARED_PROBE_LOOP [loop]
#else
#define AR_MP_SHARED_LOOP [loop] [fastopt]
#define AR_MP_SHARED_PROBE_LOOP [loop] [fastopt]
#endif

#ifndef AR_MESH_UNIFORMS_INCLUDED
#define AR_MESH_UNIFORMS_INCLUDED
sampler2D _AR_MeshCipher, _AR_MeshMeta;
float4 _AR_MeshCipherSize, _AR_MeshMetaSize;
float4 _AR_MeshNonce01, _AR_MeshNonce2, _AR_MeshCheck, _AR_MeshInfo;
float _AR_MK0, _AR_MK1, _AR_MK2, _AR_MK3, _AR_MK4, _AR_MK5, _AR_MK6, _AR_MK7;
#endif

// UnityCG is intentionally supplied by the producer shader. Consumer pipelines
// may define Unity built-ins themselves and must not include UnityCG twice.
#if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
Texture2DArray<float4> _ARMeshOriginalV1;
Texture2DArray<float4> _ARMeshPaletteV1;
#ifdef AR_MESH_PALETTE_PRODUCER
Texture2DArray<float4> _GrabTexture;
#endif
#else
Texture2D<float4> _ARMeshOriginalV1;
Texture2D<float4> _ARMeshPaletteV1;
#ifdef AR_MESH_PALETTE_PRODUCER
Texture2D<float4> _GrabTexture;
#endif
#endif
SamplerState ARMP_point_clamp_sampler;
// Public per-viewer instance values; independent of every drawing key.
float _AR_PaletteTouch0, _AR_PaletteTouch1;

// FXC may assume float uniforms are finite under normal optimization. Inspect
// their IEEE exponent bits so the NaN/Infinity refusal remains in compiled code.
bool2 ARMPFinite(float2 value)
{ return (asuint(value) & 0x7f800000u) != 0x7f800000u; }
bool4 ARMPFinite(float4 value)
{ return (asuint(value) & 0x7f800000u) != 0x7f800000u; }

uint ARMPJoin16(float2 v) { uint2 p = (uint2)round(v); return p.x | (p.y << 16u); }
uint ARMPMix(uint h, uint word) { return (h ^ word) * 16777619u; }
uint ARMPAvalanche(uint h)
{
    h ^= h >> 16u; h *= 0x7feb352du; h ^= h >> 15u;
    h *= 0x846ca68bu; return h ^ (h >> 16u);
}
static bool ARMP_ownerReady = false;
static uint2 ARMP_ownerCache = 0u;
uint2 ARMPOwner()
{
    if (ARMP_ownerReady) return ARMP_ownerCache;
    uint3 nonce = uint3(ARMPJoin16(_AR_MeshNonce01.xy), ARMPJoin16(_AR_MeshNonce01.zw), ARMPJoin16(_AR_MeshNonce2.xy));
    uint a = 2166136261u, b = 0x9e3779b9u;
    AR_MP_SHARED_LOOP for (uint n = 0u; n < 3u; n++) { a = ARMPMix(a, nonce[n]); b = ARMPMix(b, nonce[n] ^ 0xa5a5a5a5u); }
    AR_MP_SHARED_LOOP for (uint r = 0u; r < 4u; r++)
        AR_MP_SHARED_LOOP for (uint c = 0u; c < 4u; c++)
        {
            uint word = asuint(unity_ObjectToWorld[r][c]);
            a = ARMPMix(a, word); b = ARMPMix(b, word ^ (r * 4u + c));
        }
    ARMP_ownerCache = uint2(ARMPAvalanche(a), ARMPAvalanche(b));
    ARMP_ownerReady = true;
    return ARMP_ownerCache;
}
// VRChat's classic single-pass stereo supplies an eye-sized _ScreenParams,
// while GrabPass is a double-wide 2D target. Atlas cells and clip positions use
// eye dimensions; only capture dimensions and texture UVs use the packed target.
uint2 ARMPScreenSize() { return (uint2)_ScreenParams.xy; }
uint2 ARMPTargetSize()
{
#if defined(UNITY_SINGLE_PASS_STEREO) && !defined(UNITY_STEREO_INSTANCING_ENABLED) && !defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    return ARMPScreenSize() * uint2(2u, 1u);
#else
    return ARMPScreenSize();
#endif
}
uint2 ARMPTileGrid() { return ARMPScreenSize() / ARMP_TILE_SIZE; }
uint ARMPSlotCount() { uint2 grid = ARMPTileGrid(); return grid.x * grid.y; }
uint ARMPPayloadWords()
{
    return ARMP_HEADER_WORDS + ARMP_BONE_WORDS * (uint)round(_AR_MeshInfo.y) + ARMP_SHAPE_WORDS * (uint)round(_AR_MeshInfo.z);
}
bool ARMPLayoutFits()
{
    // VRChat's classic SPS uses two equal, horizontal eye viewports. Refuse
    // other packed layouts before drawing: an unverified viewport conversion
    // could overwrite pixels that the restore cannot put back correctly.
#if defined(UNITY_SINGLE_PASS_STEREO) && !defined(UNITY_STEREO_INSTANCING_ENABLED) && !defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    if ((uint)unity_StereoEyeIndex > 1u || (ARMPTargetSize().x & 1u) != 0u) return false;
    float4 eyeRect = unity_StereoScaleOffset[unity_StereoEyeIndex];
    float4 expectedRect = float4(0.5, 1.0, 0.5 * unity_StereoEyeIndex, 0.0);
    if (!all(ARMPFinite(eyeRect)) || any(abs(eyeRect - expectedRect) > 0.000001)) return false;
#endif
    // Native GrabPass can allocate the full target while _ScreenParams gives
    // a cropped camera's viewport size. Consumers already refuse that packet;
    // producers must refuse BEFORE touching the framebuffer as well. Otherwise
    // even the restore samples different pixels and corrupts the camera image.
#ifdef AR_MESH_PALETTE_PRODUCER
    uint captureWidth, captureHeight;
#if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    uint captureSlices;
    _ARMeshOriginalV1.GetDimensions(captureWidth, captureHeight, captureSlices);
    if (captureSlices <= unity_StereoEyeIndex) return false;
#else
    _ARMeshOriginalV1.GetDimensions(captureWidth, captureHeight);
#endif
    if (any(uint2(captureWidth, captureHeight) != ARMPTargetSize())) return false;
#endif
    // A scalar && chain here overflows the D3D11 optimizer stack when this
    // helper is repeatedly inlined. The vector test has identical bounds.
    float2 publicValues = float2(_AR_PaletteTouch0, _AR_PaletteTouch1);
    if (!all(ARMPFinite(publicValues)) || any(publicValues < 0.0) || any(publicValues > 1.0)) return false;
    return _AR_MeshInfo.w == 1.0 && _AR_MeshInfo.x > 0.0 && _AR_MeshInfo.y > 0.0 &&
        all(ARMPFinite(_AR_MeshInfo)) && all(_AR_MeshInfo.xyz >= 0.0) &&
        _AR_MeshInfo.y <= 512.0 && _AR_MeshInfo.z <= 512.0 && ARMPSlotCount() > 0u &&
        ARMP_TILE_SIZE + ARMPPayloadWords() * 3u <= ARMP_TILE_SIZE * ARMP_TILE_SIZE;
}
float4 ARMPPixelClip(float2 pixel) { return float4(pixel / float2(ARMPScreenSize()) * 2.0 - 1.0, 0.5, 1.0); }
uint2 ARMPTileOrigin(uint slot)
{
    uint columns = max(1u, ARMPTileGrid().x);
    return uint2(slot % columns, slot / columns) * ARMP_TILE_SIZE;
}
uint2 ARMPCell(uint slot, uint word, uint part)
{
    uint pixel = ARMP_TILE_SIZE + word * 3u + part;
    return ARMPTileOrigin(slot) + uint2(pixel % ARMP_TILE_SIZE, pixel / ARMP_TILE_SIZE);
}
float2 ARMPPixelUV(uint2 pixel)
{
    float2 uv = (float2(pixel) + 0.5) / float2(ARMPScreenSize());
#if UNITY_UV_STARTS_AT_TOP
    uv.y = 1.0 - uv.y;
#endif
#if defined(UNITY_SINGLE_PASS_STEREO) && !defined(UNITY_STEREO_INSTANCING_ENABLED) && !defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    float4 eyeRect = unity_StereoScaleOffset[unity_StereoEyeIndex];
    uv = uv * eyeRect.xy + eyeRect.zw;
#endif
    return uv;
}
float4 ARMPReadPixel(uint2 pixel, bool previous)
{
    float2 uv = ARMPPixelUV(pixel);
#if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    float3 coord = float3(uv, unity_StereoEyeIndex);
#ifdef AR_MESH_PALETTE_PRODUCER
    if (previous) return _GrabTexture.SampleLevel(ARMP_point_clamp_sampler, coord, 0);
#endif
    return _ARMeshPaletteV1.SampleLevel(ARMP_point_clamp_sampler, coord, 0);
#else
#ifdef AR_MESH_PALETTE_PRODUCER
    if (previous) return _GrabTexture.SampleLevel(ARMP_point_clamp_sampler, uv, 0);
#endif
    return _ARMeshPaletteV1.SampleLevel(ARMP_point_clamp_sampler, uv, 0);
#endif
}
uint3 ARMPReadSymbols(uint2 pixel, bool previous)
{ return (uint3)round(saturate(ARMPReadPixel(pixel, previous).rgb) * 31.0); }
uint ARMPReadWord(uint slot, uint word, bool previous)
{
    uint bits = 0u;
    AR_MP_SHARED_LOOP for (uint part = 0u; part < 3u; part++)
    {
        uint3 symbols = ARMPReadSymbols(ARMPCell(slot, word, part), previous);
        uint shift = part * 15u;
        if (shift < 32u) bits |= symbols.x << shift;
        if (shift + 5u < 32u) bits |= symbols.y << (shift + 5u);
        if (shift + 10u < 32u) bits |= symbols.z << (shift + 10u);
    }
    return bits;
}
bool ARMPOccupied(uint slot, bool previous)
{ return all(ARMPReadSymbols(ARMPTileOrigin(slot) + uint2(1u, 0u), previous) == uint3(11u, 23u, 5u)); }
uint ARMPFindSlot(bool previous, bool allocate)
{
    // Refuse unsupported eye indices and rectangles before atlas access.
    if (!ARMPLayoutFits()) return ARMP_NO_SLOT;
    uint count = ARMPSlotCount();
    if (count == 0u) return ARMP_NO_SLOT;
    uint2 owner = ARMPOwner(); uint start = owner.x % count;
    AR_MP_SHARED_PROBE_LOOP for (uint probe = 0u; probe < min(ARMP_MAX_PROBES, count); probe++)
    {
        uint slot = (start + probe) % count;
        if (!ARMPOccupied(slot, previous)) return allocate ? slot : ARMP_NO_SLOT;
        // Reuse a duplicate owner's slot so the final flag invalidates both
        // copies, including identical pose and position.
        if (ARMPReadWord(slot, 0u, previous) == owner.x && ARMPReadWord(slot, 1u, previous) == owner.y) return slot;
    }
    return ARMP_NO_SLOT;
}
static bool ARMP_slotReady = false;
static uint ARMP_slotCache = ARMP_NO_SLOT;
uint ARMPResolvedSlot()
{
    if (!ARMP_slotReady) { ARMP_slotCache = ARMPFindSlot(false, false); ARMP_slotReady = true; }
    return ARMP_slotCache;
}
uint ARMPHeaderWord(uint word)
{
    uint2 owner = ARMPOwner();
    if (word == 0u) return owner.x;
    if (word == 1u) return owner.y;
    uint2 screenSize = ARMPScreenSize();
    if (word == 2u) return screenSize.x;
    if (word == 3u) return screenSize.y;
    if (word == 4u) return (uint)round(_AR_MeshInfo.x);
    if (word == 5u) return (uint)round(_AR_MeshInfo.y);
    if (word == 6u) return (uint)round(_AR_MeshInfo.z);
    // Refuse packets with a different sampled time. Equal/pause time does not
    // identify a unique frame. Prefix and body receive the same sampled time.
    uint h = ARMPMix(ARMPMix(2166136261u, 1u), asuint(_Time.y));
    h = ARMPMix(h, owner.x); h = ARMPMix(h, owner.y);
    h = ARMPMix(h, screenSize.x); h = ARMPMix(h, screenSize.y);
    h = ARMPMix(h, (uint)round(_AR_MeshInfo.x)); h = ARMPMix(h, (uint)round(_AR_MeshInfo.y));
    h = ARMPMix(h, asuint(_AR_PaletteTouch0)); h = ARMPMix(h, asuint(_AR_PaletteTouch1));
    return ARMPMix(h, (uint)round(_AR_MeshInfo.z));
}
bool ARMeshPaletteValid()
{
    if (!ARMPLayoutFits()) return false;
    uint width, height;
#if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    uint slices; _ARMeshPaletteV1.GetDimensions(width, height, slices);
    if (slices <= unity_StereoEyeIndex) return false;
#else
    _ARMeshPaletteV1.GetDimensions(width, height);
#endif
    if (any(uint2(width, height) != ARMPTargetSize())) return false;
    uint slot = ARMPResolvedSlot();
    if (slot == ARMP_NO_SLOT || any(ARMPReadSymbols(ARMPTileOrigin(slot), false) != uint3(19u, 7u, 29u))) return false;
    [loop] [fastopt] for (uint word = 0u; word < ARMP_HEADER_WORDS; word++)
        if (ARMPReadWord(slot, word, false) != ARMPHeaderWord(word)) return false;
    return true;
}
uint ARMPPacketSeedFrom(uint2 owner, uint index, bool shape)
{
    uint h = ARMPMix(ARMPMix(2166136261u, owner.x), owner.y);
    return ARMPMix(ARMPMix(h, shape ? 0x53485045u : 0x424f4e45u), index);
}
uint ARMPPacketSeed(uint index, bool shape) { return ARMPPacketSeedFrom(ARMPOwner(), index, shape); }
// The *At variants take the slot and owner resolved once by the caller. FXC
// inlines every call, so re-resolving them inside per-bone and per-shape loops
// multiplies the compile time. Results are identical to the wrappers below.
bool ARMeshBoneRowsAt(uint slot, uint2 owner, uint bone, out float4 r0, out float4 r1, out float4 r2)
{
    float refusal = asfloat(0x7fc00000u);
    r0 = refusal.xxxx; r1 = refusal.xxxx; r2 = refusal.xxxx;
    if (slot == ARMP_NO_SLOT || bone >= (uint)round(_AR_MeshInfo.y)) return false;
    uint first = ARMP_HEADER_WORDS + bone * ARMP_BONE_WORDS;
    uint h = ARMPPacketSeedFrom(owner, bone, false);
    // Component masks keep the word bits without a private array that FXC unrolls.
    uint4 bits0 = 0u, bits1 = 0u, bits2 = 0u;
    [loop] [fastopt] for (uint word = 0u; word < 12u; word++)
    {
        uint bits = ARMPReadWord(slot, first + word, false); h = ARMPMix(h, bits);
        if (!isfinite(asfloat(bits))) return false;
        bits0 = word.xxxx == uint4(0u, 1u, 2u, 3u) ? bits.xxxx : bits0;
        bits1 = word.xxxx == uint4(4u, 5u, 6u, 7u) ? bits.xxxx : bits1;
        bits2 = word.xxxx == uint4(8u, 9u, 10u, 11u) ? bits.xxxx : bits2;
    }
    if (h != ARMPReadWord(slot, first + 12u, false)) return false;
    r0 = asfloat(bits0); r1 = asfloat(bits1); r2 = asfloat(bits2); return true;
}
bool ARMeshBoneRows(uint bone, out float4 r0, out float4 r1, out float4 r2)
{ return ARMeshBoneRowsAt(ARMPResolvedSlot(), ARMPOwner(), bone, r0, r1, r2); }
float4 ARMeshBoneRow(uint bone, uint row)
{
    float4 r0, r1, r2;
    if (row >= 3u || !ARMeshBoneRows(bone, r0, r1, r2)) return asfloat(0x7fc00000u).xxxx;
    return row == 0u ? r0 : row == 1u ? r1 : r2;
}
float ARMeshShapeWeightAt(uint slot, uint2 owner, uint shape)
{
    if (slot == ARMP_NO_SLOT || shape >= (uint)round(_AR_MeshInfo.z)) return asfloat(0x7fc00000u);
    uint first = ARMP_HEADER_WORDS + (uint)round(_AR_MeshInfo.y) * ARMP_BONE_WORDS + shape * ARMP_SHAPE_WORDS;
    uint bits = ARMPReadWord(slot, first, false);
    if (ARMPMix(ARMPPacketSeedFrom(owner, shape, true), bits) != ARMPReadWord(slot, first + 1u, false)) return asfloat(0x7fc00000u);
    return asfloat(bits) * 100.0;
}
float ARMeshShapeWeight(uint shape)
{ return ARMeshShapeWeightAt(ARMPResolvedSlot(), ARMPOwner(), shape); }

#ifdef AR_MESH_PALETTE_PRODUCER
struct ARMPInput
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float4 tangent : TANGENT;
    uint vertexID : SV_VertexID;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};
struct ARMPV2G
{
    float4 pos : POSITION;
    float3 normal : TEXCOORD0;
    float3 tangent : TEXCOORD1;
    nointerpolation uint vertexID : TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};
struct ARMPG2F
{
    float4 pos : SV_POSITION;
    // Packed RGB symbols keep the SPI geometry output below 1024 scalars:
    // 144 vertices * (position4 + symbols1 + stereo2) = 1008.
    nointerpolation uint symbols : TEXCOORD0;
    UNITY_VERTEX_OUTPUT_STEREO
};
ARMPV2G ARMPVertex(ARMPInput v)
{
    ARMPV2G o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_OUTPUT(ARMPV2G, o);
    UNITY_TRANSFER_INSTANCE_ID(v, o); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    o.pos = v.vertex; o.normal = v.normal; o.tangent = v.tangent.xyz; o.vertexID = v.vertexID;
    return o;
}
bool ARMPIsHeader(ARMPV2G v)
{ return v.vertexID == (uint)round(_AR_MeshInfo.x) + 3u * ((uint)round(_AR_MeshInfo.y) + (uint)round(_AR_MeshInfo.z)); }
void ARMPEmitPixel(uint2 pixel, uint symbols, ARMPV2G input, inout TriangleStream<ARMPG2F> output)
{
    ARMPG2F o; UNITY_INITIALIZE_OUTPUT(ARMPG2F, o); UNITY_TRANSFER_VERTEX_OUTPUT_STEREO(input, o);
    o.symbols = symbols;
    o.pos = ARMPPixelClip(pixel); output.Append(o);
    o.pos = ARMPPixelClip(pixel + uint2(1u, 0u)); output.Append(o);
    o.pos = ARMPPixelClip(pixel + uint2(0u, 1u)); output.Append(o);
    o.pos = ARMPPixelClip(pixel + uint2(1u, 1u)); output.Append(o); output.RestartStrip();
}
void ARMPEmitWord(uint slot, uint word, uint bits, ARMPV2G input, inout TriangleStream<ARMPG2F> output)
{
    [unroll] for (uint part = 0u; part < 3u; part++)
        ARMPEmitPixel(ARMPCell(slot, word, part), (bits >> (part * 15u)) & 32767u, input, output);
}
float4 ARMPColor(ARMPG2F i) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    return float4(float3(i.symbols & 31u, (i.symbols >> 5u) & 31u, (i.symbols >> 10u) & 31u) / 31.0, 1.0);
}
float ARMPMatrixValue(ARMPV2G a, ARMPV2G b, uint word)
{
    float3 columns[4] = {a.normal, a.tangent, b.pos.xyz - a.pos.xyz, a.pos.xyz};
    return columns[word % 4u][word / 4u];
}
uint ARMPMatrixCheck(ARMPV2G a, ARMPV2G b, uint bone)
{
    uint h = ARMPPacketSeed(bone, false);
    [unroll] for (uint word = 0u; word < 12u; word++) h = ARMPMix(h, asuint(ARMPMatrixValue(a, b, word)));
    return h;
}
[maxvertexcount(4)]
void ARMPFlagGeom(triangle ARMPV2G i[3], inout TriangleStream<ARMPG2F> output)
{
    UNITY_SETUP_INSTANCE_ID(i[0]); UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i[0]);
    if (!ARMPIsHeader(i[0])) return;
    uint slot = ARMPFindSlot(true, true); if (slot == ARMP_NO_SLOT) return;
    // Prepare clears the atlas before ALL carriers. The flag pass adds this
    // marker: two writes cannot equal one marker, even when Unity reuses an
    // allocation snapshot for identical materials/public values. This also
    // rejects repeated header triangles within one draw, without camera stencil.
    ARMPEmitPixel(ARMPTileOrigin(slot), 19u | (7u << 5u) | (29u << 10u), i[0], output);
}
[maxvertexcount(100)]
void ARMPHeaderGeom(triangle ARMPV2G i[3], inout TriangleStream<ARMPG2F> output)
{
    UNITY_SETUP_INSTANCE_ID(i[0]); UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i[0]);
    if (!ARMPIsHeader(i[0])) return;
    uint slot = ARMPFindSlot(true, true); if (slot == ARMP_NO_SLOT) return;
    ARMPEmitPixel(ARMPTileOrigin(slot) + uint2(1u, 0u), 11u | (23u << 5u) | (5u << 10u), i[0], output);
    [unroll] for (uint word = 0u; word < ARMP_HEADER_WORDS; word++) ARMPEmitWord(slot, word, ARMPHeaderWord(word), i[0], output);
}
[maxvertexcount(144)]
void ARMPBoneGeom(triangle ARMPV2G i[3], inout TriangleStream<ARMPG2F> output)
{
    UNITY_SETUP_INSTANCE_ID(i[0]); UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i[0]);
    uint original = (uint)round(_AR_MeshInfo.x);
    if (i[0].vertexID < original || (i[0].vertexID - original) % 3u != 0u) return;
    uint bone = (i[0].vertexID - original) / 3u; if (bone >= (uint)round(_AR_MeshInfo.y)) return;
    uint slot = ARMPFindSlot(true, true); if (slot == ARMP_NO_SLOT) return;
    [unroll] for (uint word = 0u; word < 12u; word++)
        ARMPEmitWord(slot, ARMP_HEADER_WORDS + bone * ARMP_BONE_WORDS + word, asuint(ARMPMatrixValue(i[0], i[1], word)), i[0], output);
}
[maxvertexcount(24)]
void ARMPAuxGeom(triangle ARMPV2G i[3], inout TriangleStream<ARMPG2F> output)
{
    UNITY_SETUP_INSTANCE_ID(i[0]); UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i[0]);
    uint original = (uint)round(_AR_MeshInfo.x), bones = (uint)round(_AR_MeshInfo.y);
    if (i[0].vertexID < original || (i[0].vertexID - original) % 3u != 0u) return;
    uint helper = (i[0].vertexID - original) / 3u;
    uint slot = ARMPFindSlot(true, true); if (slot == ARMP_NO_SLOT) return;
    if (helper < bones)
        ARMPEmitWord(slot, ARMP_HEADER_WORDS + helper * ARMP_BONE_WORDS + 12u, ARMPMatrixCheck(i[0], i[1], helper), i[0], output);
    else
    {
        uint shape = helper - bones; if (shape >= (uint)round(_AR_MeshInfo.z)) return;
        float3 reference = i[1].pos.xyz - i[0].pos.xyz, marker = i[2].pos.xyz - i[0].pos.xyz;
        float denominator = dot(reference, reference);
        float ratio = denominator > 1e-20 ? dot(marker, reference) / denominator : asfloat(0x7fc00000u);
        uint bits = asuint(ratio);
        uint first = ARMP_HEADER_WORDS + bones * ARMP_BONE_WORDS + shape * ARMP_SHAPE_WORDS;
        ARMPEmitWord(slot, first, bits, i[0], output);
        ARMPEmitWord(slot, first + 1u, ARMPMix(ARMPPacketSeed(shape, true), bits), i[0], output);
    }
}
[maxvertexcount(4)]
void ARMPRestoreGeom(triangle ARMPV2G i[3], inout TriangleStream<ARMPG2F> output)
{
    UNITY_SETUP_INSTANCE_ID(i[0]); UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i[0]);
    if (!ARMPIsHeader(i[0]) || !ARMPLayoutFits()) return;
    ARMPG2F o; UNITY_INITIALIZE_OUTPUT(ARMPG2F, o); UNITY_TRANSFER_VERTEX_OUTPUT_STEREO(i[0], o);
    uint2 size = ARMPTileGrid() * ARMP_TILE_SIZE;
    o.pos = ARMPPixelClip(0); output.Append(o);
    o.pos = ARMPPixelClip(uint2(size.x, 0u)); output.Append(o);
    o.pos = ARMPPixelClip(uint2(0u, size.y)); output.Append(o);
    o.pos = ARMPPixelClip(size); output.Append(o); output.RestartStrip();
}
float4 ARMPRestoreColor(ARMPG2F i) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    // SV_POSITION already includes the eye viewport's offset in a packed SPS
    // target. Applying the eye transform again would shift the right restore.
    float2 uv = i.pos.xy / float2(ARMPTargetSize());
#if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    return _ARMeshOriginalV1.SampleLevel(ARMP_point_clamp_sampler, float3(uv, unity_StereoEyeIndex), 0);
#else
    return _ARMeshOriginalV1.SampleLevel(ARMP_point_clamp_sampler, uv, 0);
#endif
}
#endif
#endif
