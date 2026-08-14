#ifndef FUR_SHELL_SHADOW_PASS_INCLUDED
#define FUR_SHELL_SHADOW_PASS_INCLUDED

// ---------------------------------------------------------------------------
//  ShadowCaster - fur shells also write into the shadow map.
//  Each shell casting its own shadow produces believable self-shadowing inside
//  the fur volume. For performance, the C# side can switch to a
//  "base shell only" shadow mode.
// ---------------------------------------------------------------------------

#include "FurShellCommon.hlsl"

// Set by URP's ShadowUtils:
// _LightDirection for directional lights, _LightPosition for punctual lights.
float3 _LightDirection;
float3 _LightPosition;

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 texcoord   : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float2 uv         : TEXCOORD0;
    float  layerT     : TEXCOORD1;
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings FurShellShadowVertex(Attributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    FurShellVertex fur = ComputeFurShell(input.positionOS.xyz, input.normalOS, GetFurShellIndex());

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - fur.positionWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif

    output.positionCS = GetFurShadowPositionHClip(fur.positionWS, fur.normalWS, lightDirectionWS);
    output.uv         = TRANSFORM_TEX(input.texcoord, _BaseMap);
    output.layerT     = fur.layerT;

    return output;
}

half4 FurShellShadowFragment(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);

    FurStrandSample strand = SampleFurStrand(input.uv, input.layerT, float3(0.0, 0.0, 1.0));
    FurAlphaClip(strand, input.layerT);

    return 0;
}

#endif // FUR_SHELL_SHADOW_PASS_INCLUDED
