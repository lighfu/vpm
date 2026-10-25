Shader "Hidden/AntiRipping/MeshEncryptedRestore"
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
        Tags { "Queue"="Background-998" "DisableBatching"="True" "VRCFallback"="Hidden" }
        Cull Off ZTest Always ZWrite Off Blend Off ColorMask RGBA
        CGINCLUDE
        #include "UnityCG.cginc"
        #define AR_MESH_PALETTE_PRODUCER
        #include "MeshEncryptedPalette.hlsl"
        ENDCG
        GrabPass { "_ARMeshPaletteV1" }
        Pass
        {
            // All restores precede every surface. Preserve camera depth and
            // stencil as well as restoring the original framebuffer color.
            CGPROGRAM
            #pragma target 5.0
            #pragma only_renderers d3d11
            #pragma multi_compile_instancing
            #pragma multi_compile __ STEREO_INSTANCING_ON
            #pragma vertex ARMPVertex
            #pragma geometry ARMPRestoreGeom
            #pragma fragment ARMPRestoreColor
            ENDCG
        }
    }
    Fallback Off
}
