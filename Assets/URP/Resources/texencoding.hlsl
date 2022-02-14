#ifndef TEXENCODING
#define TEXENCODING

// Encoding/decoding [0..1) floats into 8 bit/channel RG. Note that 1.0 will not be encoded properly.
inline float2 EncodeFloatRG( float v )
{
    float2 kEncodeMul = float2(1.0, 255.0);
    float kEncodeBit = 1.0/255.0;
    float2 enc = kEncodeMul * v;
    enc = frac (enc);
    enc.x -= enc.y * kEncodeBit;
    return enc;
}
inline float DecodeFloatRG( float2 enc )
{
    float2 kDecodeDot = float2(1.0, 1/255.0);
    return dot( enc, kDecodeDot );
}


inline float4 OutputObstracle(float dist,float id,float maxDist)
{
	#if defined(_SSFLOATRGBA)
		return float4(dist,id,0,1);
	#else
		float2 encDist = EncodeFloatRG(dist/maxDist);
		float2 encId = EncodeFloatRG(1/max(id,0.01));
		return float4(encDist,encId);
	#endif
}

inline float2 DecodeObstracle(float4 col,float maxDist)
{
	#if defined(_SSFLOATRGBA)
		return col.rg;
	#else
		float dist = maxDist*DecodeFloatRG(col.xy);
		float id = 1/DecodeFloatRG(col.zw);
		return float2(dist,id);
	#endif
}

#endif