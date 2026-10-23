Shader "Hidden/AntiRipping/MeshEncryptedPrepare"
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
        Tags { "Queue"="Background-1000" "DisableBatching"="True" "VRCFallback"="Hidden" }
        Cull Off ZTest Always ZWrite Off Blend Off ColorMask RGB
        // Every prepare precedes every carrier. Repeated clears are harmless;
        // the first named capture saves the camera's original RGBA for restore.
        GrabPass { "_ARMeshOriginalV1" }
        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #pragma only_renderers d3d11
            #pragma multi_compile_instancing
            #pragma multi_compile __ STEREO_INSTANCING_ON
            #pragma vertex ARMPVertex
            #pragma geometry ARMPRestoreGeom
            #pragma fragment ARMPClearColor
            #include "UnityCG.cginc"
            #define AR_MESH_PALETTE_PRODUCER
            #include "MeshEncryptedPalette.hlsl"
            float4 ARMPClearColor(ARMPG2F i) : SV_Target { return 0; }
            ENDCG
        }
    }
    Fallback Off
}
