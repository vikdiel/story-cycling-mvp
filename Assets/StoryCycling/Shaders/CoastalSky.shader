Shader "CapeCrown/CoastalSky"
{
    Properties {
        _Zenith("Zenith", Color) = (.28,.56,.71,1)
        _Horizon("Horizon", Color) = (1,.78,.52,1)
        _SunDirection("Sun Direction", Vector) = (.8,.18,.45,0)
    }
    SubShader {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex:POSITION; };
            struct v2f { float4 vertex:SV_POSITION; float3 dir:TEXCOORD0; };
            float4 _Zenith,_Horizon,_SunDirection;
            v2f vert(appdata v) { v2f o; o.vertex=UnityObjectToClipPos(v.vertex);o.dir=v.vertex.xyz;return o; }
            fixed4 frag(v2f i):SV_Target {
                float3 d=normalize(i.dir);float h=smoothstep(-.03,.32,d.y);
                float3 color=lerp(_Horizon.rgb,_Zenith.rgb,h);
                float facing=saturate(dot(d,normalize(_SunDirection.xyz)));
                color+=float3(1,.61,.27)*pow(facing,32)*.18;
                color=lerp(color,float3(1,.96,.79),smoothstep(.9994,.9997,facing));
                return fixed4(color,1);
            }
            ENDCG
        }
    }
}
