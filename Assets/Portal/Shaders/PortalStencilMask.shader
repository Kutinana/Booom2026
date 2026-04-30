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
                Ref [_StencilRef]
                ReadMask [_StencilReadMask]
                WriteMask 255
                Comp [_StencilComp]
                Pass [_StencilPass]
            }
        }
    }
}
