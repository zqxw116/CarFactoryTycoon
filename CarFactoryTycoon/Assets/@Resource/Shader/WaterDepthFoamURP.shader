Shader "Custom/URP/WaterDepthFoam_VerticalY"
{
    Properties
    {
        _ShallowColor ("Shallow Color", Color) = (0.20, 0.60, 0.85, 1)
        _DeepColor    ("Deep Color",    Color) = (0.05, 0.20, 0.35, 1)

        // DepthRange는 이제 "수심(미터 느낌)"에 대한 변화 범위로 사용됨
        _DepthRange   ("Depth Range (meters)", Float) = 4.0
        _AlphaShallow ("Alpha Shallow", Range(0,1)) = 0.25
        _AlphaDeep    ("Alpha Deep",    Range(0,1)) = 0.70

        // === 핵심: 수면/바닥 월드Y ===
        // 큐브(수조)로 쓸 때 안정적으로 위->아래 깊이를 만들기 위해 고정값으로 둠
        _WaterSurfaceY ("Water Surface Y (world)", Float) = 1.0
        _WaterBottomY  ("Water Bottom Y (world)",  Float) = 0.0

        _FoamColor    ("Foam Color", Color) = (0.90, 0.95, 1.00, 1)
        _FoamRange    ("Foam Range (meters)", Float) = 0.15
        _FoamStrength ("Foam Strength", Range(0,2)) = 0.6
        _FoamPower    ("Foam Power", Range(0.5,8)) = 3.0

        _WaveTex      ("Wave Noise (R)", 2D) = "gray" {}
        _WaveTiling   ("Wave Tiling", Vector) = (1,1,0,0)
        _WaveSpeed1   ("Wave Speed 1", Vector) = (0.02,0.01,0,0)
        _WaveSpeed2   ("Wave Speed 2", Vector) = (-0.015,0.02,0,0)
        _WaveStrength ("Wave Strength", Range(0,0.5)) = 0.08
        _WaveThreshold("Wave Threshold", Range(0,1)) = 0.65
        _WaveColor    ("Wave Color", Color) = (0.80, 0.95, 1.0, 1)

        // 옵션: 물 탁도/오염 같은 게임플레이 연동용
        _Clarity      ("Clarity (0 clear - 1 murky)", Range(0,1)) = 0.35
        _Contam       ("Contamination (0 normal - 1 bad)", Range(0,1)) = 0.0
        _ContamColor  ("Contam Color", Color) = (0.20, 0.28, 0.10, 1)

        // === 깊이 변화 부드럽게 ===
        // 0이면 기존처럼(선형), 1이면 더 부드럽게(smoothstep)
        _DepthSmooth  ("Depth Smooth (0 linear - 1 smooth)", Range(0,1)) = 1.0

        // 1이면 기본, 2~4면 천천히 깊어지다가(상부 완만), 아래로 갈수록 더 진해짐
        _DepthExponent ("Depth Exponent (1 normal)", Range(0.5,6)) = 2.0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
            "Queue"="Transparent"
            "RenderType"="Transparent"
        }

        Pass
        {
            Name "Forward"
            Tags { "LightMode"="UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off

            // 네가 "모든 보이는 면에 파도"를 원해서 그대로 둠
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // 중요: SampleSceneDepth / ComputeWorldSpacePosition 사용
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_WaveTex);
            SAMPLER(sampler_WaveTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _ShallowColor;
                float4 _DeepColor;

                float  _DepthRange;
                float  _AlphaShallow;
                float  _AlphaDeep;

                float  _WaterSurfaceY;
                float  _WaterBottomY;

                float4 _FoamColor;
                float  _FoamRange;
                float  _FoamStrength;
                float  _FoamPower;

                float4 _WaveTiling;
                float4 _WaveSpeed1;
                float4 _WaveSpeed2;
                float  _WaveStrength;
                float  _WaveThreshold;
                float4 _WaveColor;

                float  _Clarity;
                float  _Contam;
                float4 _ContamColor;

                float  _DepthSmooth;
                float  _DepthExponent;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float4 screenPos   : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionHCS = pos.positionCS;
                o.uv = v.uv;
                o.screenPos = ComputeScreenPos(o.positionHCS);
                return o;
            }

            float WaveMask(float2 uv)
            {
                float t = _Time.y;
                float2 uv1 = uv * _WaveTiling.xy + t * _WaveSpeed1.xy;
                float2 uv2 = uv * _WaveTiling.xy + t * _WaveSpeed2.xy;

                float n1 = SAMPLE_TEXTURE2D(_WaveTex, sampler_WaveTex, uv1).r;
                float n2 = SAMPLE_TEXTURE2D(_WaveTex, sampler_WaveTex, uv2).r;
                float n  = (n1 + n2) * 0.5;

                // threshold 기반 하이라이트
                return saturate((n - _WaveThreshold) * 50.0);
            }

            half4 frag(Varyings i) : SV_Target
            {
                // ======================================================
                // 1) "월드 Y축 수심" 계산 (카메라 방향에 안 흔들림)
                // - 화면 depth로 뒤에 있는 장면 픽셀의 월드좌표를 복원
                // - 수심 = 수면Y - 장면월드Y
                // ======================================================

                float2 screenUV = i.screenPos.xy / i.screenPos.w;

                // raw depth (0~1). 아무것도 없으면 1에 가까움
                float rawDepth = SampleSceneDepth(screenUV);

                // 물 뒤에 아무것도 없을 때(하늘/클리어) 튀는 것 방지:
                float maxDepth = max(_WaterSurfaceY - _WaterBottomY, 0.001);

                float depthY;
                if (rawDepth > 0.9999)
                {
                    // 뒤에 지오메트리가 없으면 "최대 수심"으로 취급(안 튐)
                    depthY = maxDepth;
                }
                else
                {
                    float3 sceneWS = ComputeWorldSpacePosition(screenUV, rawDepth, UNITY_MATRIX_I_VP);
                    depthY = clamp(_WaterSurfaceY - sceneWS.y, 0.0, maxDepth);
                }

                // ======================================================
                // 2) 수심 -> 0~1 매핑 + 자연스럽게(완만하게) 만들기
                // ======================================================

                // 탁도에 따라 depthRange 체감 조정
                float depthRange = max(_DepthRange * lerp(1.4, 0.6, _Clarity), 0.001);

                float d = saturate(depthY / depthRange);

                // 부드러운 변화(smoothstep)
                float dSmooth = d * d * (3.0 - 2.0 * d); // smoothstep(0,1,d)
                d = lerp(d, dSmooth, _DepthSmooth);

                // 곡선(상부 완만, 하부 진하게)
                d = pow(max(d, 1e-5), _DepthExponent);

                float depth01 = saturate(d);

                // ======================================================
                // 3) 색/알파(수심 기반)
                // ======================================================
                float4 deepCol  = lerp(_DeepColor, _ContamColor, _Contam);
                float4 waterCol = lerp(_ShallowColor, deepCol, depth01);

                float alphaDeep = lerp(_AlphaDeep * 0.7, _AlphaDeep * 1.15, _Clarity);
                float alpha = lerp(_AlphaShallow, alphaDeep, depth01);

                // ======================================================
                // 4) Foam(수면 근처 강조)
                // - 수심이 얕을수록(수면 가까울수록) foam 강하게
                // ======================================================
                float foamRange = max(_FoamRange, 0.0001);
                float foamMask = 1.0 - saturate(depthY / foamRange);
                foamMask = pow(saturate(foamMask), _FoamPower);
                float foam = foamMask * _FoamStrength;

                // ======================================================
                // 5) 잔물결 하이라이트(모든 면)
                // ======================================================
                float w = WaveMask(i.uv) * _WaveStrength;

                float3 rgb = waterCol.rgb;
                rgb += foam * _FoamColor.rgb;
                rgb += w * _WaveColor.rgb;

                return half4(rgb, alpha);
            }
            ENDHLSL
        }
    }
}
