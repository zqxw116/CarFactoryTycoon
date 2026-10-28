Shader "CarFactory/PrototypePartOverlay"
{
    Properties
    {
        _OverlayColor("Color", Color) = (1,0.12,0.06,0.9)
        _OutlinePixels("Outline pixels", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+10" "RenderType"="Transparent" }
        Pass
        {
            Name "PartFeedback"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull [_Cull]
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
            half4 _OverlayColor;
            float _OutlinePixels;
            CBUFFER_END
            struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; };
            struct Varyings { float4 positionHCS:SV_POSITION; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 world=TransformObjectToWorld(input.positionOS.xyz);
                output.positionHCS=TransformWorldToHClip(world);
                if(_OutlinePixels>0)
                {
                    float3 n=TransformObjectToWorldNormal(input.normalOS);
                    float2 projected=mul((float3x3)UNITY_MATRIX_VP,n).xy;
                    float lengthSquared=dot(projected,projected);
                    if(lengthSquared>0.000001)
                        output.positionHCS.xy+=projected*rsqrt(lengthSquared)*(_OutlinePixels*2/_ScreenParams.xy)*output.positionHCS.w;
                }
                return output;
            }
            half4 Frag(Varyings input):SV_Target { return _OverlayColor; }
            ENDHLSL
        }
    }
}
