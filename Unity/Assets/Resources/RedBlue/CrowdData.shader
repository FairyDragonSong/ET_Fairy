Shader "ET/RedBlue/CrowdData"
{
    Properties
    {
        _PreviousState ("Previous state", 2D) = "black" {}
        _CurrentState ("Current state", 2D) = "black" {}
        _Interpolation ("Interpolation", Float) = 1
        _Radius ("Radius", Float) = 2
    }
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_PreviousState); SAMPLER(sampler_PreviousState);
            TEXTURE2D(_CurrentState); SAMPLER(sampler_CurrentState);
            CBUFFER_START(UnityPerMaterial)
                float _Interpolation;
                float _Radius;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; float2 stateUV : TEXCOORD1; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; half4 color : COLOR0; };
            Varyings vert(Attributes v)
            {
                float4 a = SAMPLE_TEXTURE2D_LOD(_PreviousState, sampler_PreviousState, v.stateUV, 0);
                float4 b = SAMPLE_TEXTURE2D_LOD(_CurrentState, sampler_CurrentState, v.stateUV, 0);
                float2 p = lerp(a.xy, b.xy, _Interpolation);
                float delta = b.z - a.z;
                delta -= floor((delta + PI) / TWO_PI) * TWO_PI;
                float sn, cs; sincos(a.z + delta * _Interpolation, sn, cs);
                float2 corner = v.positionOS.xy * float2(1.35, 1) * _Radius;
                p += float2(corner.x * cs - corner.y * sn, corner.x * sn + corner.y * cs);
                Varyings o;
                o.positionCS = TransformWorldToHClip(float3(p, 0)); o.uv = v.uv * 2 - 1;
                half magnitude = abs(b.w);
                half3 color = b.w >= 0 ? half3(.95, .20, .22) : half3(.16, .53, 1);
                color = lerp(color, half3(1,1,1), saturate(magnitude - 1) * .7);
                o.color = half4(color, magnitude >= 1 ? 1 : magnitude);
                return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                clip(i.color.a - .001);
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
