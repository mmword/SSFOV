#ifndef URP_UNLIT_SS_ANIMINC_INCLUDED
#define URP_UNLIT_SS_ANIMINC_INCLUDED


float Anim(float pos)
{
	float val = sin(_Time.y *4* abs(pos*0.01));
	pos += sin(val*8) ;
	return pos;
}

#ifdef USE_ANIM_METHODS
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Unlit.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

struct AnimAttributes
{
    float4 positionOS : POSITION;
	float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct AnimVaryings
{
    float2 uv : TEXCOORD0;
	float nl : TEXCOORD1;
	float3 wPos : TEXCOORD2;
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

AnimVaryings AnimUnlitPassVertex(AnimAttributes input)
{
    AnimVaryings output = (AnimVaryings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
	
	float3 normal = input.normal;
	
	#if defined(_ANIM)
    float3 np1 = input.positionOS.xyz + float3(0,0,Anim(input.positionOS.x));
	float3 np2 = input.positionOS.xyz + float3(0,0,Anim(input.positionOS.x+0.05));
    float3 tan = normalize(np2-np1);
	normal =  normalize(normal - dot(tan,normal) * tan);
	input.positionOS.xyz = np1;
	#endif

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);

    output.positionCS = vertexInput.positionCS;
    output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
	output.nl = max(0.1,dot(TransformObjectToWorldNormal(normal),normalize(_MainLightPosition.xyz)));
	output.wPos = vertexInput.positionWS;

    return output;
}

half4 AnimUnlitPassFragment(AnimVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    half2 uv = input.uv;
    half4 texColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
    half3 color = texColor.rgb * _BaseColor.rgb;
    half alpha = texColor.a * _BaseColor.a;

    AlphaDiscard(alpha, _Cutoff);

   // InputData inputData;
   //InitializeInputData(input, inputData);
   // SETUP_DEBUG_TEXTURE_DATA(inputData, input.uv, _BaseMap);
	
	half simpleLighting = input.nl;

   // half4 finalColor = UniversalFragmentUnlit(inputData, color, alpha);
   // half3 finalColor = color * alpha;
    half3 finalColor = color * simpleLighting;

    return half4(finalColor,alpha);
}


AnimVaryings AnimDepthOnlyVertex(AnimAttributes input)
{
    AnimVaryings output = (AnimVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
	
	#if defined(_ANIM)
    float3 np1 = input.positionOS.xyz + float3(0,0,Anim(input.positionOS.x));
	input.positionOS.xyz = np1;
	#endif

    output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    return output;
}

half4 AnimDepthOnlyFragment(AnimVaryings input) : SV_TARGET
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
    return 0;
}
#endif

#endif

