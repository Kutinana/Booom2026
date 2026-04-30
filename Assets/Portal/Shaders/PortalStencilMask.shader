Shader "Custom/PortalStencilMask"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+10" }

        Pass
        {
            ZWrite Off
            ZTest LEqual
            ColorMask 0

            Stencil
            {
                Ref [_StencilRef]     // 从 C# 传进来
                Comp Always
                Pass Replace
            }
        }
    }
}
