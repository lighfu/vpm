Shader "Hidden/AntiRipping/MeshEncryptedCarrier"
{
    Properties
    {
        _AR_MeshInfo("Mesh info", Vector) = (0,0,0,0)
        _AR_PaletteTouch0("Public instance value 0", Float) = 0
        _AR_PaletteTouch1("Public instance value 1", Float) = 0
        _AR_MeshNonce01("Mesh nonce 0/1", Vector) = (0,0,0,0)
        _AR_MeshNonce2("Mesh nonce 2", Vector) = (0,0,0,0)
        _AR_MeshCheck("Key check", Vector) = (0,0,0,0)
    }
    SubShader
    {
        Tags { "Queue"="Background-999" "DisableBatching"="True" "VRCFallback"="Hidden" }
        Cull Off ZTest Always ZWrite Off Blend Off
        CGINCLUDE
        #include "UnityCG.cginc"
        #define AR_MESH_PALETTE_PRODUCER
        #include "MeshEncryptedPalette.hlsl"

        // The encrypted mesh retains every original UV channel. Unity's build
        // stripping considers live vertex inputs; consuming all four components
        // here keeps even channels that the surface shader does not sample.
        struct ARMPUvInput
        {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            float4 tangent : TANGENT;
            float4 uv0 : TEXCOORD0;
            float4 uv1 : TEXCOORD1;
            float4 uv2 : TEXCOORD2;
            float4 uv3 : TEXCOORD3;
            float4 uv4 : TEXCOORD4;
            float4 uv5 : TEXCOORD5;
            float4 uv6 : TEXCOORD6;
            float4 uv7 : TEXCOORD7;
            uint vertexID : SV_VertexID;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        bool ARMPUvFinite(float4 value)
        {
            // FXC's default floating-point optimization can fold isfinite on
            // vertex inputs to true. Inspecting the IEEE exponent bits retains
            // every input component and rejects exactly infinity and NaN.
            return all((asuint(value) & 0x7f800000u) != 0x7f800000u);
        }
        ARMPV2G ARMPUvVertex(ARMPUvInput v)
        {
            ARMPInput basis; UNITY_INITIALIZE_OUTPUT(ARMPInput, basis);
            basis.vertex = v.vertex; basis.normal = v.normal;
            basis.tangent = v.tangent; basis.vertexID = v.vertexID;
            UNITY_TRANSFER_INSTANCE_ID(v, basis);
            ARMPV2G output = ARMPVertex(basis);
            if (!(ARMPUvFinite(v.uv0) && ARMPUvFinite(v.uv1)
                && ARMPUvFinite(v.uv2) && ARMPUvFinite(v.uv3)
                && ARMPUvFinite(v.uv4) && ARMPUvFinite(v.uv5)
                && ARMPUvFinite(v.uv6) && ARMPUvFinite(v.uv7)))
                output.vertexID = ARMP_NO_SLOT;
            return output;
        }
        bool ARMPUvTriangleValid(ARMPV2G a, ARMPV2G b, ARMPV2G c)
        {
            return a.vertexID != ARMP_NO_SLOT && b.vertexID != ARMP_NO_SLOT
                && c.vertexID != ARMP_NO_SLOT;
        }
        // The original geometry functions are called as ordinary helpers here.
        // FXC ignores their entry-only maxvertexcount attribute (X3554); the
        // actual entries below retain the same output limits.
        #pragma warning(disable:3554)
        [maxvertexcount(4)]
        void ARMPUvFlagGeom(triangle ARMPV2G i[3], inout TriangleStream<ARMPG2F> output)
        {
            if (!ARMPUvTriangleValid(i[0], i[1], i[2])) return;
            ARMPFlagGeom(i, output);
        }
        [maxvertexcount(100)]
        void ARMPUvHeaderGeom(triangle ARMPV2G i[3], inout TriangleStream<ARMPG2F> output)
        {
            if (!ARMPUvTriangleValid(i[0], i[1], i[2])) return;
            ARMPHeaderGeom(i, output);
        }
        [maxvertexcount(144)]
        void ARMPUvBoneGeom(triangle ARMPV2G i[3], inout TriangleStream<ARMPG2F> output)
        {
            if (!ARMPUvTriangleValid(i[0], i[1], i[2])) return;
            ARMPBoneGeom(i, output);
        }
        [maxvertexcount(24)]
        void ARMPUvAuxGeom(triangle ARMPV2G i[3], inout TriangleStream<ARMPG2F> output)
        {
            if (!ARMPUvTriangleValid(i[0], i[1], i[2])) return;
            ARMPAuxGeom(i, output);
        }
        ENDCG
        // Prepare already captured this name before clearing the atlas. Keep
        // the cached named binding immediately before the anonymous capture:
        // Unity can reuse stale allocation data when the named binding is absent.
        GrabPass { "_ARMeshOriginalV1" }
        // Snapshot allocation state before this renderer. The bounded probe
        // sequence is shared by all passes and the later surface shader.
        GrabPass { }
        Pass
        {
            ColorMask RGB
            CGPROGRAM
            #pragma target 5.0
            #pragma only_renderers d3d11
            #pragma multi_compile_instancing
            #pragma multi_compile __ STEREO_INSTANCING_ON
            #pragma vertex ARMPUvVertex
            #pragma geometry ARMPUvHeaderGeom
            #pragma fragment ARMPColor
            ENDCG
        }
        Pass
        {
            ColorMask RGB
            CGPROGRAM
            #pragma target 5.0
            #pragma only_renderers d3d11
            #pragma multi_compile_instancing
            #pragma multi_compile __ STEREO_INSTANCING_ON
            #pragma vertex ARMPUvVertex
            #pragma geometry ARMPUvBoneGeom
            #pragma fragment ARMPColor
            ENDCG
        }
        Pass
        {
            ColorMask RGB
            CGPROGRAM
            #pragma target 5.0
            #pragma only_renderers d3d11
            #pragma multi_compile_instancing
            #pragma multi_compile __ STEREO_INSTANCING_ON
            #pragma vertex ARMPUvVertex
            #pragma geometry ARMPUvAuxGeom
            #pragma fragment ARMPColor
            ENDCG
        }
        Pass
        {
            ColorMask RGB
            Blend One One
            CGPROGRAM
            #pragma target 5.0
            #pragma only_renderers d3d11
            #pragma multi_compile_instancing
            #pragma multi_compile __ STEREO_INSTANCING_ON
            #pragma vertex ARMPUvVertex
            #pragma geometry ARMPUvFlagGeom
            #pragma fragment ARMPColor
            ENDCG
        }
    }
    Fallback Off
}
