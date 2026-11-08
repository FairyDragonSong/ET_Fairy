Shader "PingDou/Translucent"
{
    Properties { _Color("Color",Color)=(1,.97,.85,.16) }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
            fixed4 _Color;
            v2f vert(float4 vertex:POSITION) { v2f o; o.pos=UnityObjectToClipPos(vertex); o.uv=vertex.xy; return o; }
            fixed4 frag(v2f i):SV_Target { float grain=frac(sin(dot(i.uv,float2(12.9898,78.233)))*43758.5453); return fixed4(_Color.rgb,_Color.a*(.8+grain*.2)); }
            ENDCG
        }
    }
}
