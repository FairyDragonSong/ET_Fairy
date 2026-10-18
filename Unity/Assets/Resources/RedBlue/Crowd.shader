Shader "ET/RedBlue/Crowd"
{
    Properties { _SoldierColor ("Soldier color", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR0; };
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _SoldierColor)
            UNITY_INSTANCING_BUFFER_END(Props)
            Varyings vert(Attributes v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv * 2 - 1;
                o.color = UNITY_ACCESS_INSTANCED_PROP(Props, _SoldierColor);
                return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                // Procedural top-view head, shoulders and forward-pointing weapon.
                float body = dot(i.uv * float2(1.25, 1.65), i.uv * float2(1.25, 1.65));
                float head = length(i.uv - float2(.48, 0)) / .30;
                float weapon = max(abs(i.uv.y + .28) / .09, abs(i.uv.x - .42) / .56);
                clip(1 - min(min(body, head), weapon));
                return half4(i.color.rgb * (head < 1 ? 1.1 : .85), i.color.a);
            }
            ENDHLSL
        }
    }
}
