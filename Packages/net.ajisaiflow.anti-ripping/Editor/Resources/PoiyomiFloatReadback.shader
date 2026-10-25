Shader "Hidden/AjisaiAR/PoiyomiFloatReadback"
{
    Properties
    {
        _Source2D("Source", 2D) = "black" {}
        _SourceCube("Cube", Cube) = "" {}
        _SourceArray("Array", 2DArray) = "" {}
        _SourceVolume("Volume", 3D) = "" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            Texture2D _Source2D;
            TextureCube _SourceCube;
            Texture2DArray _SourceArray;
            Texture3D _SourceVolume;
            SamplerState sampler_linear_clamp;
            SamplerState sampler_point_clamp;
            float _SourceMip, _SliceZ;
            float4 _SourceCube_HDR;
            int _Shape, _Layer, _ExactRead, _ReadHdr;
            float3 CubeDirection(int face, float2 uv)
            {
                float2 p = uv * 2.0 - 1.0;
                if (face == 0) return float3(1, -p.y, -p.x);
                if (face == 1) return float3(-1, -p.y, p.x);
                if (face == 2) return float3(p.x, 1, p.y);
                if (face == 3) return float3(p.x, -1, -p.y);
                if (face == 4) return float3(p.x, -p.y, 1);
                return float3(-p.x, -p.y, -1);
            }
            float4 frag(v2f_img input) : SV_Target
            {
                if (_ReadHdr != 0) return _SourceCube_HDR;
                if (_Shape == 1)
                {
                    if (_ExactRead != 0) return _SourceCube.SampleLevel(sampler_point_clamp, CubeDirection(_Layer, input.uv), _SourceMip);
                    return _SourceCube.SampleLevel(sampler_linear_clamp, CubeDirection(_Layer, input.uv), _SourceMip);
                }
                if (_ExactRead != 0)
                {
                    uint width, height, depth, levels;
                    if (_Shape == 2)
                    {
                        _SourceArray.GetDimensions((uint)_SourceMip, width, height, depth, levels);
                        return _SourceArray.Load(int4((int2)(input.uv * float2(width, height)), _Layer, (int)_SourceMip));
                    }
                    if (_Shape == 3)
                    {
                        _SourceVolume.GetDimensions((uint)_SourceMip, width, height, depth, levels);
                        return _SourceVolume.Load(int4((int2)(input.uv * float2(width, height)), _Layer, (int)_SourceMip));
                    }
                    _Source2D.GetDimensions((uint)_SourceMip, width, height, levels);
                    return _Source2D.Load(int3((int2)(input.uv * float2(width, height)), (int)_SourceMip));
                }
                if (_Shape == 2) return _SourceArray.SampleLevel(sampler_linear_clamp, float3(input.uv, _Layer), _SourceMip);
                if (_Shape == 3) return _SourceVolume.SampleLevel(sampler_linear_clamp, float3(input.uv, _SliceZ), _SourceMip);
                return _Source2D.SampleLevel(sampler_linear_clamp, input.uv, _SourceMip);
            }
            ENDCG
        }
    }
}
