// FakeShadow defines standalone vert/frag functions instead of the normal
// lilToon hooks. Its original projection, UV, color and fog code stays intact.
#ifndef AR_MESH_ENCRYPTED_LIL_FAKESHADOW_INCLUDED
#define AR_MESH_ENCRYPTED_LIL_FAKESHADOW_INCLUDED

#include "MeshEncryptedVertex.hlsl"

v2f ARMeshLilFakeShadowVert(appdata input)
{
    LIL_SETUP_INSTANCE_ID(input);
    bool valid = ARMeshDecodeVertex(input.vertexID, input.positionOS, input.normalOS, input.tangentOS);
    if (!valid)
    {
        v2f refused;
        LIL_INITIALIZE_STRUCT(v2f, refused);
        LIL_TRANSFER_INSTANCE_ID(input, refused);
        LIL_INITIALIZE_VERTEX_OUTPUT_STEREO(refused);
        refused.positionCS = float4(2.0, 2.0, 2.0, 1.0);
        refused._AR_MeshValid = 0.0;
        return refused;
    }
    v2f output = vert(input);
    output._AR_MeshValid = 1.0;
    return output;
}

float4 ARMeshLilFakeShadowFrag(v2f input) : SV_Target
{
    LIL_SETUP_INSTANCE_ID(input);
    LIL_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    clip(input._AR_MeshValid - 0.5);
    return frag(input);
}

#endif
