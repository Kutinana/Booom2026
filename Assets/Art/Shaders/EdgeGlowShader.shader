Shader "Custom/SpriteEdgeGlow"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        _GlowColor ("Outline Color", Color) = (0, 0, 0, 1)
        _GlowWidth ("Outline Width (Pixels)", Range(1, 16)) = 2
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
        HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS   : POSITION;
                float4 color        : COLOR;
                float2 uv           : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float4 color        : COLOR;
                float2 uv           : TEXCOORD0;
            };

            Texture2D _MainTex;
            SamplerState sampler_MainTex;

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _GlowColor;
                float _GlowWidth;
                float4 _MainTex_TexelSize;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                output.color = input.color * _Color;
                return output;
            }

            half fragMaxNeighborAlpha(float2 uv, float2 ts, uint stepIndex)
            {
                float2 o = ts * (float)stepIndex;
                half m = 0;
                m = max(m, _MainTex.Sample(sampler_MainTex, uv + float2( o.x,  0)).a);
                m = max(m, _MainTex.Sample(sampler_MainTex, uv + float2(-o.x,  0)).a);
                m = max(m, _MainTex.Sample(sampler_MainTex, uv + float2( 0,  o.y)).a);
                m = max(m, _MainTex.Sample(sampler_MainTex, uv + float2( 0, -o.y)).a);
                m = max(m, _MainTex.Sample(sampler_MainTex, uv + float2( o.x,  o.y)).a);
                m = max(m, _MainTex.Sample(sampler_MainTex, uv + float2( o.x, -o.y)).a);
                m = max(m, _MainTex.Sample(sampler_MainTex, uv + float2(-o.x,  o.y)).a);
                m = max(m, _MainTex.Sample(sampler_MainTex, uv + float2(-o.x, -o.y)).a);
                return m;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half4 mainColor = _MainTex.Sample(sampler_MainTex, input.uv) * input.color;

                float2 ts = abs(_MainTex_TexelSize.xy);
                uint iw = (uint)clamp((int)round(_GlowWidth), 1, 8);

                half maxNeighborA = 0;
                [unroll]
                for (uint i = 1u; i <= 8u; i++)
                {
                    if (i > iw)
                        continue;
                    maxNeighborA = max(maxNeighborA, fragMaxNeighborAlpha(input.uv, ts, i));
                }

                // 当前透明、邻近有不透明 → 描边区域
                half outlineMask = saturate(maxNeighborA - mainColor.a);
                half oa = outlineMask * _GlowColor.a;
                half4 outlinePM = half4(_GlowColor.rgb * oa, oa);
                half4 spritePM = half4(mainColor.rgb * mainColor.a, mainColor.a);

                // 描边在下、精灵在上（预乘 Alpha 合成）
                half4 res;
                res.rgb = spritePM.rgb + outlinePM.rgb * (1.0h - spritePM.a);
                res.a   = spritePM.a + outlinePM.a * (1.0h - spritePM.a);
                return res;
            }
        ENDHLSL
        }
    }
}
