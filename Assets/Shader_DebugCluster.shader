Shader "ClusterBasedLightingGit/Shader_DebugCluster"
{
    Properties {}
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex   main_VS
            #pragma geometry main_GS
            #pragma fragment main_PS
            #pragma target 5.0
            #pragma enable_d3d11_debug_symbols

            #include "UnityCG.cginc"

            struct VertexShaderOutput
            {
                float4 Min   : AABB_MIN;   // view-space AABB min
                float4 Max   : AABB_MAX;   // view-space AABB max
                float4 Color : COLOR;
                uint   ID    : TEXCOORD0;  // ★ 传递 cluster ID
            };

            struct GeometryShaderOutput
            {
                float4 Color    : COLOR;
                float4 Position : SV_POSITION;
            };

            struct AABB { float4 Min; float4 Max; };
            StructuredBuffer<AABB> ClusterAABBs;
			float4x4 _AABBViewToWorld;

            float4 WorldToProject(float4 posWorld)
            {
                return mul(UNITY_MATRIX_VP,posWorld);
            }

			float4 ViewToClip(float4 posVS)
			{
				posVS.z = -posVS.z;                  // ★ 把“正距 z”翻回 Unity 视空间的 -Z 前向
				float4 posWS = mul(_AABBViewToWorld, posVS);
				return UnityObjectToClipPos(posWS);
			}

            VertexShaderOutput main_VS(uint VertexID : SV_VertexID)
            {
                VertexShaderOutput vsOutput = (VertexShaderOutput)0;

                AABB aabb = ClusterAABBs[VertexID];
                vsOutput.Min   = aabb.Min;
                vsOutput.Max   = aabb.Max;
                vsOutput.Color = float4(1,1,1,1);
                vsOutput.ID    = VertexID;          // ★ 传给 GS

                return vsOutput;
            }

            // 只显示第一个 AABB（ID==0）
            [maxvertexcount(16)]
            void main_GS(point VertexShaderOutput IN[1], inout TriangleStream<GeometryShaderOutput> OutputStream)
            {
                // ★ 非0的直接不输出任何顶点
                //if (IN[0].ID > 64u) return;

                float4 min = IN[0].Min;
                float4 max = IN[0].Max;

                GeometryShaderOutput OUT = (GeometryShaderOutput)0;

                const float4 Pos[8] = {
                    float4(min.x, min.y, min.z, 1.0f),
                    float4(min.x, min.y, max.z, 1.0f),
                    float4(min.x, max.y, min.z, 1.0f),
                    float4(min.x, max.y, max.z, 1.0f),
                    float4(max.x, min.y, min.z, 1.0f),
                    float4(max.x, min.y, max.z, 1.0f),
                    float4(max.x, max.y, min.z, 1.0f),
                    float4(max.x, max.y, max.z, 1.0f)
                };

                const uint Index[18] = {
                    0, 1, 2,
                    3, 6, 7,
                    4, 5, (uint)-1,
                    2, 6, 0,
                    4, 1, 5,
                    3, 7, (uint)-1
                };

                [unroll]
                for (uint i = 0; i < 18; ++i)
                {
                    if (Index[i] == (uint)-1)
                    {
                        OutputStream.RestartStrip();
                    }
                    else
                    {
                        OUT.Position = ViewToClip(Pos[Index[i]]);
                        OUT.Color = IN[0].Color;
                        OutputStream.Append(OUT);
                    }
                }
            }

            float4 main_PS(GeometryShaderOutput IN) : SV_Target
            {
                return IN.Color;
            }
            ENDCG
        }
    }
}
