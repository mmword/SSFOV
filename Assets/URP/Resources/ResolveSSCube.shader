Shader "Hidden/ResolveSSCube"
{
    HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/EntityLighting.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ImageBasedLighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
		
		float4 _SrcSize;
		float4 _BlurParams;

		SamplerState sampler_PointClamp;
		SamplerState sampler_LinearClamp;
		SamplerState sampler_PointRepeat;
		SamplerState sampler_LinearRepeat;
		
		TEXTURE2D_X(_RTCamOnstraclesProp);
		TEXTURE2D_X(_SceneTexParam);
		TEXTURE2D_X(_BlurSrcMap);
		
		#define SAMPLE_BLURMAP(uv)   SAMPLE_TEXTURE2D_X(_BlurSrcMap, sampler_LinearClamp, uv)
		#define SAMPLE_OBSTRACLE(uv) SAMPLE_TEXTURE2D_X_LOD(_RTCamOnstraclesProp, sampler_PointClamp, uv, 0)
		#define SAMPLE_SCENE(uv) SAMPLE_TEXTURE2D_X_LOD(_SceneTexParam, sampler_PointClamp, uv, 0)

        struct Attributes
        {
            float4 positionHCS   : POSITION;
            float2 uv           : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4  positionCS  : SV_POSITION;
            float2  uv          : TEXCOORD0;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings VertDefault(Attributes input)
        {
            Varyings output;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

            // Note: The pass is setup with a mesh already in CS
            // Therefore, we can just output vertex position
            output.positionCS = float4(input.positionHCS.xyz, 1.0);

            #if UNITY_UV_STARTS_AT_TOP
            output.positionCS.y *= -1;
            #endif

            output.uv = input.uv;

            // Add a small epsilon to avoid artifacts when reconstructing the normals
            output.uv += 1.0e-6;

            return output;
        }
		
		
		half4 BlurWithObstracles(float2 uv, float2 delta) : SV_Target
		{
			half4 p0 =  SAMPLE_BLURMAP(uv);
			half4 p1 = SAMPLE_BLURMAP(uv - delta * 1.3846153846);
			half4 p2 = SAMPLE_BLURMAP(uv + delta * 1.3846153846);
			half4 p3 = SAMPLE_BLURMAP(uv - delta * 3.2307692308);
			half4 p4 = SAMPLE_BLURMAP(uv + delta * 3.2307692308);

			half w0 = 1-p0.w;
			half w1 = 1-p1.w;
			half w2 = 1-p2.w;
			half w3 = 1-p3.w;
			half w4 = 1-p4.w;
			
			half sum = w0+w1+w2+w3+w4;
			if(sum < 5)
				return p0;

			half s = half(0.0);
			s += p0.r * w0;
			s += p1.r * w1;
			s += p2.r * w2;
			s += p3.r * w3;
			s += p4.r * w4;
			//s *= rcp(sum);
			s *= 0.2f;

			return half4(s,s,s,p0.a);
		}
		
		half BlurSmall(float2 uv, float2 delta)
		{
			half4 p0 = SAMPLE_BLURMAP(uv);
			half4 p1 = SAMPLE_BLURMAP(uv + float2(-delta.x, -delta.y));
			half4 p2 = SAMPLE_BLURMAP(uv + float2( delta.x, -delta.y));
			half4 p3 = SAMPLE_BLURMAP(uv + float2(-delta.x,  delta.y));
			half4 p4 = SAMPLE_BLURMAP(uv + float2( delta.x,  delta.y));
			
			half w0 = 1-p0.w;
			half w1 = 1-p1.w;
			half w2 = 1-p2.w;
			half w3 = 1-p3.w;
			half w4 = 1-p4.w;

			half sum = w0+w1+w2+w3+w4;
			if(sum < 5)
				return p0.r;

			half s = half(0.0);
			s += p0.r * w0;
			s += p1.r * w1;
			s += p2.r * w2;
			s += p3.r * w3;
			s += p4.r * w4;
			//s *= rcp(sum);
			s *= 0.2f;

			return s;
		}
		

    ENDHLSL
	
	SubShader
    {
        Tags{ "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline"}
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "Resolve SSCube"

            HLSLPROGRAM
                #pragma vertex VertDefault
                #pragma fragment ResolveCube
				#pragma multi_compile_local _ _ORTHOGRAPHIC
				#pragma multi_compile_local _ _SAMPLECOLOR
				#pragma multi_compile_local _ _SSFLOATRGBA
				#pragma multi_compile_local _ _SIXSLICES
				
				#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
				#include "texencoding.hlsl"
				
				#define PIDX 5.497787143782138
				
				half4 _CameraViewTopLeftCorner;
				half4x4 _CameraViewProjections; // This is different from UNITY_MATRIX_VP (platform-agnostic projection matrix is used). Handle both non-XR and XR modes.
				float4 _SSBias;

				float4 _ProjectionParams2;
				float4 _CameraViewXExtent;
				float4 _CameraViewYExtent;
				float4 _CameraViewZExtent;
				float3 _ViewerWorldPos;
				float3 _ViewerWorldOffset;
				
			    float2 GetAtlas4UV(float3 dir)
				{
					float ang = atan2(dir.z, dir.x);
					float w = (ang + PIDX) / TWO_PI;
					float x = 1.0f - w + floor(w);

					ang = asin(dir.y) + PI_DIV_FOUR;
					float y = ang / HALF_PI;

					return float2(x,y);
				}
				
				
				float2 CubeSlices6UV(const float3 v)
				{
					float3 vAbs = abs(v);
					float ma;
					float2 uv;
					float faceIndex = 0.0f;
					if(vAbs.z >= vAbs.x && vAbs.z >= vAbs.y)
					{
					    faceIndex = v.z < 0.0f ? 2.0f : 0.0f;
						ma = 0.5f / vAbs.z;
						uv = float2(v.z < 0.0f ? -v.x : v.x, -v.y);
					}
					else if(vAbs.y >= vAbs.x)
					{
						faceIndex = v.y < 0.0f ? 5.0f : 4.0f;
						ma = 0.5f / vAbs.y;
						uv = float2(v.x, v.y < 0.0f ? -v.z : v.z);
					}
					else
					{
						faceIndex = v.x < 0.0f ? 3.0f : 1.0f;
						ma = 0.5f / vAbs.x;
						uv = float2(v.x < 0.0f ? v.z : -v.z, -v.y);
					}
					uv = uv * ma + 0.5f;
					uv.x *= (1.0f/6.0f);
					uv.x += (1.0f/6.0f) * faceIndex;
					return uv;
				}
				
				float2 CubeSlices4UV(const float3 v)
				{
					float3 vAbs = abs(v);
					float ma;
					float2 uv;
					float faceIndex = 0.0f;
					if(vAbs.z >= vAbs.x)
					{
					    faceIndex = v.z < 0.0f ? 2.0f : 0.0f;
						ma = 0.5f / vAbs.z;
						uv = float2(v.z < 0.0f ? -v.x : v.x, -v.y);
					}
					else
					{
						faceIndex = v.x < 0.0f ? 3.0f : 1.0f;
						ma = 0.5f / vAbs.x;
						uv = float2(v.x < 0.0f ? v.z : -v.z, -v.y);
					}
					uv = uv * ma + 0.5f;
					uv.x *= 0.25f;
					uv.x += 0.25f * faceIndex;
					return uv;
				}
				
	
				// Textures & Samplers
				//TEXTURECUBE(_RTCubeTexProp);
				//#define SAMPLE_CUBE(dir) SAMPLE_TEXTURECUBE_LOD(_RTCubeTexProp, sampler_PointClamp, dir, 0)
				TEXTURE2D_X(_RTCubeTexProp);
				#if defined(_SIXSLICES)
				#define SAMPLE_CUBE(dir) SAMPLE_TEXTURE2D_X_LOD(_RTCubeTexProp, sampler_PointClamp, CubeSlices6UV(dir), 0)
				#else
				#define SAMPLE_CUBE(dir) SAMPLE_TEXTURE2D_X_LOD(_RTCubeTexProp, sampler_PointClamp, CubeSlices4UV(dir), 0)
				#endif
				
				
				float SampleAndGetLinearEyeDepth(float2 uv)
				{
					float rawDepth = SampleSceneDepth(uv.xy);
					#if defined(_ORTHOGRAPHIC)
						return LinearDepthToEyeDepth(rawDepth);
					#else
						return LinearEyeDepth(rawDepth, _ZBufferParams);
					#endif
				}
				
				half3 ReconstructViewPos(float2 uv, float depth)
				{
					// Screen is y-inverted.
					uv.y = 1.0f - uv.y;

					// view pos in world space
					#if defined(_ORTHOGRAPHIC)
						float zScale = depth * _ProjectionParams.w; // divide by far plane
						float3 viewPos = _CameraViewTopLeftCorner.xyz
											+ _CameraViewXExtent.xyz * uv.x
											+ _CameraViewYExtent.xyz * uv.y
											+ _CameraViewZExtent.xyz * zScale;
					#else
						float zScale = depth * _ProjectionParams2.x; // divide by near plane
						float3 viewPos = _CameraViewTopLeftCorner.xyz
											+ _CameraViewXExtent.xyz * uv.x
											+ _CameraViewYExtent.xyz * uv.y;
						viewPos *= zScale;
					#endif

					return half3(viewPos);
				}
								
				half4 ResolveCube(Varyings input) : SV_Target
				{
					// Depth at the sample point
					float depth_s1 = SampleAndGetLinearEyeDepth(input.uv);
					half3 vpos_s2 = ReconstructViewPos(input.uv, depth_s1);
					//float3 dir = normalize(vpos_s2-_ViewerWorldOffset);
					float3 dir = vpos_s2-_ViewerWorldOffset;
					float d0 = length(dir);
					dir *= (1.0f/d0);
					
					float4 cubeCol = SAMPLE_CUBE(dir);
					float4 camObstracleCol = SAMPLE_OBSTRACLE(input.uv);
					
					float2 cubeData = DecodeObstracle(cubeCol,_SSBias.y);
					float2 camData = DecodeObstracle(camObstracleCol,_SSBias.y);
					
					float d1 = cubeData.r;
					float cube_id = cubeData.g;
					float cam_id = camData.g;
					
					#if defined(_SSFLOATRGBA)
					float alpha = cam_id > 0.0f ? 1.0f : 0.0f;
					#else
					float alpha = 1/cam_id > 0.0f ? 1.0f : 0.0f;
					#endif

					#if defined(_SAMPLECOLOR)
					half3 sceneCol = SAMPLE_SCENE(input.uv).rgb;
					#else
					half3 sceneCol = float3(1.0f,1.0f,1.0f);
					#endif
					
					//return cam_id;
					if(abs(cam_id-cube_id) < 1.0f)
						return half4(sceneCol,alpha);
					
					//if(d1 > 0.99)
					//	return half4(1,0,0,0);

					if(d1 > 0.0f)
					{
						if(d0 < d1+_SSBias.z)
						{
							return half4(sceneCol,alpha);
						}
					    return half4(0.0f,0.0f,0.0f,alpha);;
					}
	
				    return half4(sceneCol,alpha);

				}

            ENDHLSL
        }
		
		
		Pass
        {
            Name "SSBlur Horizontal"

            HLSLPROGRAM
			#pragma vertex VertDefault
            #pragma fragment HorizontalBlur
			
			half4 HorizontalBlur(Varyings input) : SV_Target
			{
				//UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
				const float2 uv = input.uv;
				const float2 delta = float2(_SrcSize.z, 0.0f);
				return BlurWithObstracles(uv, delta);
			}
			ENDHLSL
			
		}
		
		Pass
        {
            Name "SSBlur Vertical"

            HLSLPROGRAM
                #pragma vertex VertDefault
                #pragma fragment VerticalBlur
				
				half4 VerticalBlur(Varyings input) : SV_Target
				{
					//UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

					const float2 uv = input.uv;
					const float2 delta = float2(0.0f, _SrcSize.w * rcp(_BlurParams.x));
					return BlurWithObstracles(uv, delta);
				}
				
            ENDHLSL
        }
		
		Pass
        {
            Name "SSBlur Final"

            HLSLPROGRAM
                #pragma vertex VertDefault
                #pragma fragment FinalBlur
				
				half4 FinalBlur(Varyings input) : SV_Target
				{
					//UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
					
					float4 sceneCol = SAMPLE_SCENE(input.uv);

					const float2 uv = input.uv;
					const float2 delta = _SrcSize.zw;
					float res = BlurSmall(uv, delta );
					return sceneCol * res;
				}
				
            ENDHLSL
        }
		
		
	}

}
