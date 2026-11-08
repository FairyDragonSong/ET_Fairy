Shader "PingDou/Plastic"
{
    Properties { _Color("Color", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            struct appdata { float4 vertex:POSITION; float3 normal:NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos:SV_POSITION; float3 normal:TEXCOORD0; float3 world:TEXCOORD1; };
            fixed4 _Color;
            v2f vert(appdata v) { UNITY_SETUP_INSTANCE_ID(v); v2f o; o.pos=UnityObjectToClipPos(v.vertex); o.normal=UnityObjectToWorldNormal(v.normal); o.world=mul(unity_ObjectToWorld,v.vertex).xyz; return o; }
            fixed4 frag(v2f i):SV_Target
            {
                float3 n=normalize(i.normal);
                float3 l=normalize(float3(-.45,.65,-1));
                float diffuse=.70+.30*saturate(dot(n,l));
                float3 view=normalize(_WorldSpaceCameraPos-i.world);
                float spec=pow(saturate(dot(n,normalize(l+view))),48)*.20;
                return fixed4(_Color.rgb*diffuse+spec,1);
            }
            ENDCG
        }
    }
}
