// The distributed mesh contains generic carriers. Surface positions, normals,
// tangents and morph deltas are decoded only with the independent drawing key.
#ifndef AR_MESH_ENCRYPTED_VERTEX_INCLUDED
#define AR_MESH_ENCRYPTED_VERTEX_INCLUDED

#ifndef AR_MESH_UNIFORMS_INCLUDED
#define AR_MESH_UNIFORMS_INCLUDED
sampler2D _AR_MeshCipher, _AR_MeshMeta;
float4 _AR_MeshCipherSize, _AR_MeshMetaSize;
float4 _AR_MeshNonce01, _AR_MeshNonce2, _AR_MeshCheck, _AR_MeshInfo;
float _AR_MK0, _AR_MK1, _AR_MK2, _AR_MK3, _AR_MK4, _AR_MK5, _AR_MK6, _AR_MK7;
#endif
#include "MeshEncryptedPalette.hlsl"

uint ARMCJoin16(float2 v) { uint2 p = (uint2)round(v); return p.x | (p.y << 16u); }
uint ARMCRotate(uint x, uint s) { return (x << s) | (x >> (32u - s)); }
void ARMCRound(inout uint4 s)
{
    s.x += s.y; s.y = ARMCRotate(s.y, 5u) ^ s.x; s.x = ARMCRotate(s.x, 16u);
    s.z += s.w; s.w = ARMCRotate(s.w, 8u) ^ s.z;
    s.x += s.w; s.w = ARMCRotate(s.w, 7u) ^ s.x;
    s.z += s.y; s.y = ARMCRotate(s.y, 13u) ^ s.z; s.z = ARMCRotate(s.z, 16u);
}
void ARMCAbsorb(inout uint4 s, uint word)
{ s.w ^= word; ARMCRound(s); ARMCRound(s); s.x ^= word; }
uint4 ARMCPrefix(uint2 key, uint3 nonce, uint domain)
{
    uint4 s = uint4(key.x, key.y, 0x6c796765u ^ key.x, 0x74656462u ^ key.y);
    ARMCAbsorb(s, domain); ARMCAbsorb(s, nonce.x); ARMCAbsorb(s, nonce.y); ARMCAbsorb(s, nonce.z);
    return s;
}
uint ARMCWord(uint4 s, uint index)
{
    ARMCAbsorb(s, index); ARMCAbsorb(s, 0x14000000u); s.z ^= 0xffu;
    ARMCRound(s); ARMCRound(s); ARMCRound(s); ARMCRound(s); return s.y ^ s.w;
}
uint ARMCRead(sampler2D data, uint2 size, uint address)
{
    uint2 p = uint2(address % size.x, address / size.x);
    uint4 b = (uint4)round(saturate(tex2Dlod(data, float4((float2(p) + 0.5) / float2(size), 0, 0))) * 255.0);
    return b.x | (b.y << 8u) | (b.z << 16u) | (b.w << 24u);
}
uint ARMCMeta(uint address) { return ARMCRead(_AR_MeshMeta, (uint2)_AR_MeshMetaSize.xy, address); }
float ARMCAttribute(uint4 prefix, uint word)
{ return asfloat(ARMCRead(_AR_MeshCipher, (uint2)_AR_MeshCipherSize.xy, word) ^ ARMCWord(prefix, word)); }
void ARMCRecord(uint4 prefix, uint record, out float3 p, out float3 n, out float4 t)
{
    uint first = record * 10u;
    // Keep one cipher expansion per record. Component masks preserve the word
    // bits without private arrays or dynamic vector writes that FXC unrolls.
    uint3 positionBits = 0, normalBits = 0;
    uint4 tangentBits = 0;
    [loop] [fastopt] for (uint word = 0u; word < 10u; word++)
    {
        uint value = asuint(ARMCAttribute(prefix, first + word));
        positionBits = word.xxx == uint3(0u, 1u, 2u) ? value.xxx : positionBits;
        normalBits = word.xxx == uint3(3u, 4u, 5u) ? value.xxx : normalBits;
        tangentBits = word.xxxx == uint4(6u, 7u, 8u, 9u) ? value.xxxx : tangentBits;
    }
    p = asfloat(positionBits); n = asfloat(normalBits); t = asfloat(tangentBits);
}
float3 ARMCPoint(float3 p, float4 r0, float4 r1, float4 r2)
{ return float3(dot(r0, float4(p,1)), dot(r1, float4(p,1)), dot(r2, float4(p,1))); }
float3 ARMCVector(float3 p, float4 r0, float4 r1, float4 r2)
{ return float3(dot(r0.xyz,p), dot(r1.xyz,p), dot(r2.xyz,p)); }
float4 ARMCBindRow(uint bone, uint row, uint table)
{
    uint first = table + bone * 12u + row * 4u;
    return asfloat(uint4(ARMCMeta(first), ARMCMeta(first+1u), ARMCMeta(first+2u), ARMCMeta(first+3u)));
}

bool ARMeshDecodeVertex(uint vertexID, inout float4 position, inout float3 normal, inout float4 tangent)
{
    // Refusal values are finite and harmless even if a custom vertex modifier
    // runs before the fragment validity clip.
    position = float4(0,0,0,1); normal = float3(0,1,0); tangent = float4(1,0,0,1);
    uint vertices = (uint)round(_AR_MeshInfo.x), bones = (uint)round(_AR_MeshInfo.y);
    uint shapes = (uint)round(_AR_MeshInfo.z);
    uint2 metaSize = (uint2)_AR_MeshMetaSize.xy, cipherSize = (uint2)_AR_MeshCipherSize.xy;
    if (vertexID >= vertices || bones == 0u || bones > 512u || shapes > 512u ||
        _AR_MeshInfo.w != 1 || any(metaSize == 0u) || any(cipherSize == 0u)) return false;
    float4 lo = float4(_AR_MK0, _AR_MK1, _AR_MK2, _AR_MK3);
    float4 hi = float4(_AR_MK4, _AR_MK5, _AR_MK6, _AR_MK7);
    if (!all(ARMPFinite(lo)) || !all(ARMPFinite(hi)) || any(lo < 0) || any(lo > 255) || any(hi < 0) || any(hi > 255)) return false;
    uint4 a = (uint4)round(lo), b = (uint4)round(hi);
    uint2 key = uint2(a.x | (a.y<<8u) | (a.z<<16u) | (a.w<<24u), b.x | (b.y<<8u) | (b.z<<16u) | (b.w<<24u));
    if (all(key == 0u)) return false;
    uint3 nonce = uint3(ARMCJoin16(_AR_MeshNonce01.xy), ARMCJoin16(_AR_MeshNonce01.zw), ARMCJoin16(_AR_MeshNonce2.xy));
    uint4 check = ARMCPrefix(key, nonce, 0x4b48434du);
    if (ARMCWord(check,0u) != ARMCJoin16(_AR_MeshCheck.xy) || ARMCWord(check,1u) != ARMCJoin16(_AR_MeshCheck.zw)) return false;
    if (!ARMeshPaletteValid() || ARMCMeta(0u) != 0x31454d41u || ARMCMeta(1u) != 1u ||
        ARMCMeta(2u) != vertices || ARMCMeta(3u) != bones || ARMCMeta(4u) != shapes) return false;
    uint words = ARMCMeta(13u), records = ARMCMeta(14u);
    uint vertexTable = ARMCMeta(5u), bindTable = ARMCMeta(6u), shapeTable = ARMCMeta(7u);
    uint frameTable = ARMCMeta(8u), influenceTable = ARMCMeta(9u), groupTable = ARMCMeta(10u);
    uint influences = ARMCMeta(11u), groups = ARMCMeta(12u);
    if (words > metaSize.x * metaSize.y || records > cipherSize.x * cipherSize.y / 10u || records < vertices ||
        vertexTable != 16u || bindTable != vertexTable + vertices * 4u ||
        shapeTable != bindTable + bones * 12u || frameTable != shapeTable + shapes * 2u ||
        influenceTable < frameTable || groupTable != influenceTable + influences * 2u || words != groupTable + groups * 2u) return false;
    // Resolve the palette slot and owner once; the loops below reuse them.
    uint paletteSlot = ARMPResolvedSlot(); uint2 paletteOwner = ARMPOwner();
    uint entry = vertexTable + vertexID * 4u;
    uint influenceStart = ARMCMeta(entry), influenceCount = ARMCMeta(entry+1u);
    uint groupStart = ARMCMeta(entry+2u), groupCount = ARMCMeta(entry+3u);
    if (influenceCount == 0u || influenceCount > 255u || influenceStart > influences || influenceCount > influences-influenceStart ||
        groupCount > shapes || groupStart > groups || groupCount > groups-groupStart) return false;
    uint4 prefix = ARMCPrefix(key, nonce, 0x4853454du);
    float3 p, n; float4 t;
    ARMCRecord(prefix, vertexID, p, n, t);
    [loop] [fastopt] for (uint group = 0u; group < groupCount; group++)
    {
        uint g = groupTable + (groupStart + group) * 2u;
        uint shape = ARMCMeta(g), first = ARMCMeta(g+1u);
        if (shape >= shapes) return false;
        uint frameCount = ARMCMeta(shapeTable + shape * 2u);
        uint frames = frameTable + ARMCMeta(shapeTable + shape * 2u + 1u);
        if (frameCount == 0u || frameCount > 256u || frames > influenceTable || frameCount > influenceTable-frames ||
            first >= records || frameCount > records-first) return false;
        float weight = ARMeshShapeWeightAt(paletteSlot, paletteOwner, shape), firstWeight = asfloat(ARMCMeta(frames));
        if (!isfinite(weight) || !isfinite(firstWeight) || firstWeight == 0.0) return false;
        uint f0 = 0u, f1 = 0u; float c0, c1 = 0;
        if (frameCount == 1u || weight <= firstWeight) c0 = weight / firstWeight;
        else
        {
            uint upper = 1u;
            [loop] [fastopt] while (upper + 1u < frameCount && weight > asfloat(ARMCMeta(frames+upper))) upper++;
            f0 = upper-1u; f1 = upper;
            float lowerWeight = asfloat(ARMCMeta(frames+f0)), upperWeight = asfloat(ARMCMeta(frames+f1));
            if (!isfinite(lowerWeight) || !isfinite(upperWeight) || upperWeight <= lowerWeight) return false;
            c1 = (weight-lowerWeight)/(upperWeight-lowerWeight); c0 = max(0.0,1.0-c1);
        }
        float3 dp, dn; float4 dt;
        // One ARMCRecord call site for both frames, c0 first and then c1. FXC
        // inlines every call, so two call sites would expand the cipher twice.
        [loop] [fastopt] for (uint side = 0u; side < 2u; side++)
        {
            float c = side == 0u ? c0 : c1;
            if (c != 0) { ARMCRecord(prefix, first + (side == 0u ? f0 : f1), dp, dn, dt); p += dp*c; n += dn*c; t.xyz += dt.xyz*c; }
        }
    }
    float3 sp = 0, sn = 0, st = 0; float total = 0;
    [loop] [fastopt] for (uint index = 0u; index < influenceCount; index++)
    {
        uint address = influenceTable + (influenceStart+index)*2u;
        uint bone = ARMCMeta(address); float weight = asfloat(ARMCMeta(address+1u));
        if (bone >= bones || !isfinite(weight) || weight < 0) return false;
        float4 b0 = ARMCBindRow(bone,0u,bindTable), b1 = ARMCBindRow(bone,1u,bindTable), b2 = ARMCBindRow(bone,2u,bindTable);
        float4 r0, r1, r2;
        if (!ARMeshBoneRowsAt(paletteSlot, paletteOwner, bone, r0, r1, r2)) return false;
        sp += ARMCPoint(ARMCPoint(p,b0,b1,b2),r0,r1,r2)*weight;
        sn += ARMCVector(ARMCVector(n,b0,b1,b2),r0,r1,r2)*weight;
        st += ARMCVector(ARMCVector(t.xyz,b0,b1,b2),r0,r1,r2)*weight;
        total += weight;
    }
    if (abs(total-1.0) > 0.0001 || !all(isfinite(sp)) || !all(isfinite(sn)) || !all(isfinite(st)) || !isfinite(t.w)) return false;
    position = float4(sp,1); normal = sn; tangent = float4(st,t.w);
    return true;
}
#endif
