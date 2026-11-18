Shader "Drift Fog/Sample/Twilight Sky"
{
    Properties
    {
        _Horizon ("Horizon", Color) = (0.58, 0.63, 0.64, 1)
        _Zenith ("Zenith", Color) = (0.09, 0.19, 0.27, 1)
        _Moon ("Moonlight", Color) = (0.95, 0.86, 0.67, 1)
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Horizon;
                half4 _Zenith;
                half4 _Moon;
            CBUFFER_END

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 direction : TEXCOORD0;
            };

            Varyings Vert(float3 vertex : POSITION)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(vertex);
                output.direction = vertex;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 direction = normalize(input.direction);
                float altitude = saturate(direction.y);
                half3 sky = lerp(_Horizon.rgb, _Zenith.rgb, pow(altitude, 0.55));
                float angle = max(0.0, 1.0 - dot(direction, normalize(float3(-0.135, 0.192, 0.973))));
                sky += _Moon.rgb * (0.13 * exp(-angle * 90.0) + 0.17 * exp(-angle * 1300.0));
                sky = lerp(sky, _Moon.rgb * 1.3, 1.0 - smoothstep(0.00012, 0.000155, angle));
                return half4(sky, 1);
            }
            ENDHLSL
        }
    }
}
