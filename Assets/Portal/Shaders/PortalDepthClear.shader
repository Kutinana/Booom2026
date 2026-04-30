Shader "Hidden/PortalDepthClear"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+11" }

        Pass
        {
            ZWrite On
            ZTest Always
            ColorMask 0

            Stencil
            {
                Ref [_StencilRef]
                Comp Equal
                Pass Keep
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);

                #if UNITY_REVERSED_Z
                output.positionHCS.z = 0.0;
                #else
                output.positionHCS.z = output.positionHCS.w;
                #endif

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
