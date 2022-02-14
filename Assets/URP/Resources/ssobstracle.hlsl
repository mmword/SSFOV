#ifndef SSOBSTRACLE_INCLUDED
#define SSOBSTRACLE_INCLUDED

#include "texencoding.hlsl"

#if defined(_ANIM)
#include "animinc.hlsl"
#endif

float3 _ViewerWorldPos;
float4 _SSBias;


CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    half4 _BaseColor;
    half _Cutoff;
    half _Surface;
	half _EntityID;
	half _EntityID_V;
CBUFFER_END

#ifdef UNITY_DOTS_INSTANCING_ENABLED
UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
    UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
    UNITY_DOTS_INSTANCED_PROP(float , _Cutoff)
    UNITY_DOTS_INSTANCED_PROP(float , _Surface)
	UNITY_DOTS_INSTANCED_PROP(float, _EntityID)
UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

#define _BaseColor          UNITY_ACCESS_DOTS_INSTANCED_PROP_FROM_MACRO(float4 , Metadata_BaseColor)
#define _Cutoff             UNITY_ACCESS_DOTS_INSTANCED_PROP_FROM_MACRO(float  , Metadata_Cutoff)
#define _Surface            UNITY_ACCESS_DOTS_INSTANCED_PROP_FROM_MACRO(float  , Metadata_Surface)
#define _EntityID           UNITY_ACCESS_DOTS_INSTANCED_PROP_FROM_MACRO(float  , Metadata_EntityID)
#endif


/*
CBUFFER_START(UnityPerMaterial)
	float _EntityID;
CBUFFER_END

#ifdef UNITY_DOTS_INSTANCING_ENABLED
UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
	UNITY_DOTS_INSTANCED_PROP(float, _EntityID)
UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

#define _EntityID            UNITY_ACCESS_DOTS_INSTANCED_PROP_FROM_MACRO(float  , Metadata_EntityID)
#endif
*/

struct AttributesObstracle
{
	float4 positionOS : POSITION;
	float2 uv : TEXCOORD0;
	float3 normalOS : NORMAL;
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct VaryingsObstracle
{
	float2 uv : TEXCOORD0;
	float3 posWS : TEXCOORD1;
	float4 positionCS : SV_POSITION;
	
	UNITY_VERTEX_INPUT_INSTANCE_ID
	UNITY_VERTEX_OUTPUT_STEREO
};

float3 ApplyBias(float3 positionWS, float3 normalWS, float3 direction)
{
	float invNdotL = 1.0 - saturate(dot(direction, normalWS));
	//float scale = invNdotL * _SSBias.y;

	// normal bias is negative since we want to apply an inset normal offset
	positionWS = direction * _SSBias.xxx + positionWS;
	//positionWS = normalWS * scale.xxx + positionWS;
	//return positionWS;
	return positionWS;
}


VaryingsObstracle UnlitPassVertexOnstracle(AttributesObstracle input)
{
	VaryingsObstracle output = (VaryingsObstracle)0;
	
	//float3 ws = TransformObjectToWorld(input.positionOS.xyz);
	//float3 dir = normalize(ws-_ViewerWorldPos);
	//input.positionOS.xyz += input.normal * 0.003;
	
	UNITY_SETUP_INSTANCE_ID(input);
	UNITY_TRANSFER_INSTANCE_ID(input, output);
	UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
	
	#if defined(_ANIM)
	float3 np1 = input.positionOS.xyz + float3(0,0,Anim(input.positionOS.x));
	input.positionOS.xyz = np1;
	#endif
	
	float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
	float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
	float3 dir = normalize(_ViewerWorldPos-positionWS);
	float4 positionCS = TransformWorldToHClip(ApplyBias(positionWS, normalWS, dir));
	//VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
	
	#if UNITY_REVERSED_Z
		positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
	#else
		positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
	#endif

	output.positionCS = positionCS;
	output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
	output.posWS = positionWS;

	return output;
}

half4 UnlitPassFragmentObstracle(VaryingsObstracle input) : SV_Target
{
	UNITY_SETUP_INSTANCE_ID(input);
	UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
	float dist = length(input.posWS-_ViewerWorldPos);
	#if UNITY_ANY_INSTANCING_ENABLED
	return OutputObstracle(dist,_EntityID,_SSBias.y);
	#else
	return OutputObstracle(dist,0,_SSBias.y);
	#endif
}


#endif