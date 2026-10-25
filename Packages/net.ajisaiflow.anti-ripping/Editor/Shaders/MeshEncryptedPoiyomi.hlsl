// Poiyomi's appdata, VertexOut, vert and frag are defined by the retained
// source pass. Decode before its vertex modifiers and gate before any of its
// fragment code, including the EarlyZ and outline paths.
#ifndef AR_MESH_ENCRYPTED_POIYOMI_INCLUDED
#define AR_MESH_ENCRYPTED_POIYOMI_INCLUDED

#include "MeshEncryptedVertex.hlsl"

#ifndef AR_POI_FACING_TYPE
#error The Poiyomi adapter must supply the source fragment facing type.
#endif

VertexOut ARMeshPoiVert(appdata v)
{
    UNITY_SETUP_INSTANCE_ID(v);
    bool valid = ARMeshDecodeVertex(v.vertexId, v.vertex, v.normal, v.tangent);
    if (!valid)
    {
        VertexOut refused;
        UNITY_INITIALIZE_OUTPUT(VertexOut, refused);
        UNITY_TRANSFER_INSTANCE_ID(v, refused);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(refused);
        // All refusal vertices lie beyond the same clip plane. The fragment
        // validity gate remains mandatory even if a platform clips them first.
        refused.pos = float4(2.0, 2.0, 2.0, 1.0);
        refused._AR_MeshValid = 0.0;
        return refused;
    }

    VertexOut output = vert(v);
    output._AR_MeshValid = 1.0;
    return output;
}

float4 ARMeshPoiFrag(VertexOut i, AR_POI_FACING_TYPE facing : SV_IsFrontFace) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    clip(i._AR_MeshValid - 0.5);
    return frag(i, facing);
}

#endif
