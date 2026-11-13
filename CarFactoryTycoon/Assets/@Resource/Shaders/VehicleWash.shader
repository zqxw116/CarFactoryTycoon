Shader "CarFactory/VehicleWash"
{
    Properties
    {
        _BaseMap("Original Base Map", 2D) = "white" {}
        _BaseColor("Original Base Color", Color) = (1,1,1,1)
        _Smoothness("Smoothness", Range(0,1)) = 0
        _Metallic("Metallic", Range(0,1)) = 0
        _EmissionMap("Emission", 2D) = "white" {}
        [HDR] _EmissionColor("Emission Color", Color) = (0,0,0,1)
        _WashMask("Clean Surface Atlas", 2D) = "black" {}
        _DirtColor("Dry Dirt", Color) = (.20,.15,.095,1)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _Smoothness;
            half _Metallic;
            half4 _EmissionColor;
            half4 _DirtColor;
            float4x4 _WashWorldToLocal;
            float4 _WashMin;
            float4 _WashSize;
        CBUFFER_END
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceData.hlsl"
            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_EmissionMap); SAMPLER(sampler_EmissionMap);
            TEXTURE2D(_WashMask); SAMPLER(sampler_WashMask);
            struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; float2 uv:TEXCOORD0; };
            struct Varyings
            {
                float4 positionCS:SV_POSITION;
                float3 positionWS:TEXCOORD0;
                float3 normalWS:TEXCOORD1;
                float2 uv:TEXCOORD2;
                half fog:TEXCOORD3;
            };
            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = pos.positionCS;
                output.positionWS = pos.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fog = ComputeFogFactor(pos.positionCS.z);
                return output;
            }
            half4 frag(Varyings input):SV_Target
            {
                float3 local = mul(_WashWorldToLocal, float4(input.positionWS,1)).xyz;
                float3 q = saturate((local - _WashMin.xyz) / max(_WashSize.xyz, .0001));
                // Use geometric normals, matching MeshCollider triangle projections even when
                // a low-poly panel shares smooth vertex normals with the adjacent face.
                float3 n = normalize(cross(ddx(input.positionWS), ddy(input.positionWS)));
                if (dot(n, input.normalWS) < 0) n = -n;
                n = normalize(mul((float3x3)_WashWorldToLocal, n));
                float3 a = abs(n);
                float face;
                float2 uv;
                half clean = 1;
                if (a.y >= a.x && a.y >= a.z) { face = 4; uv = q.xz; }
                else if (a.x >= a.z) { face = n.x > 0 ? 0 : 1; uv = q.zy; }
                else { face = n.z > 0 ? 2 : 3; uv = q.xy; }
                // Pixel-centred point sampling prevents neighbouring atlas faces bleeding.
                float2 cell = min(floor(uv * 48), 47);
                float2 atlas = float2((face * 48 + cell.x + .5) / 240, (cell.y + .5) / 48);
                if (!(a.y >= a.x && a.y >= a.z && n.y < 0))
                    clean = SAMPLE_TEXTURE2D(_WashMask, sampler_WashMask, atlas).r;
                half4 original = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                float noise = frac(sin(dot(floor(local * 95), float3(12.9898,78.233,37.719))) * 43758.5453);
                half3 dirt = _DirtColor.rgb * lerp(.76,1.2,noise);
                SurfaceData surface = (SurfaceData)0;
                surface.albedo = lerp(dirt, original.rgb, clean);
                surface.metallic = _Metallic * clean;
                surface.smoothness = _Smoothness * clean;
                surface.normalTS = half3(0,0,1);
                surface.emission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, input.uv).rgb * _EmissionColor.rgb * clean;
                surface.occlusion = 1;
                surface.alpha = 1;
                InputData data = (InputData)0;
                data.positionWS = input.positionWS;
                data.normalWS = NormalizeNormalPerPixel(input.normalWS);
                data.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                data.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                data.fogCoord = input.fog;
                data.bakedGI = SampleSH(data.normalWS);
                data.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                data.shadowMask = half4(1,1,1,1);
                half4 color = UniversalFragmentPBR(data, surface);
                color.rgb = MixFog(color.rgb, input.fog);
                return color;
            }
            ENDHLSL
        }
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
        UsePass "Universal Render Pipeline/Lit/DepthNormals"
    }
}
