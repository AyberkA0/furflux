#ifndef FUR_SHELL_DEPTH_PASSES_INCLUDED
#define FUR_SHELL_DEPTH_PASSES_INCLUDED

// ---------------------------------------------------------------------------
//  DepthOnly and DepthNormals passes.
//  Without these, URP's depth priming, SSAO, decals and depth-texture effects
//  all sample the fur incorrectly.
//  Both passes are generated from this single file; FUR_DEPTH_NORMALS_PASS
//  (defined in the .shader file) selects the DepthNormals variant.
// ---------------------------------------------------------------------------

#include "FurShellCommon.hlsl"

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 tangentOS  : TANGENT;    // needed for the tangent-space view direction below
    float2 texcoord   : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float2 uv         : TEXCOORD0;
    float  layerT     : TEXCOORD1;
    float3 viewDirTS  : TEXCOORD3;
#if defined(FUR_DEPTH_NORMALS_PASS)
    float3 normalWS   : TEXCOORD2;
#endif
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

Varyings FurShellDepthVertex(Attributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    FurShellVertex fur = ComputeFurShell(input.positionOS.xyz, input.normalOS, GetFurShellIndex());

    output.positionCS = TransformWorldToHClip(fur.positionWS);
    output.uv         = TRANSFORM_TEX(input.texcoord, _BaseMap);
    output.layerT     = fur.layerT;
    output.viewDirTS  = GetFurViewDirTS(fur.positionWS, fur.normalWS, TransformObjectToWorldDir(input.tangentOS.xyz));
#if defined(FUR_DEPTH_NORMALS_PASS)
    output.normalWS   = fur.normalWS;
#endif

    return output;
}

#if defined(FUR_DEPTH_NORMALS_PASS)

void FurShellDepthNormalsFragment(
    Varyings input
    , out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    FurStrandSample strand = SampleFurStrand(input.uv, input.layerT, normalize(input.viewDirTS));
    FurAlphaClip(strand, input.layerT);

    #if defined(_GBUFFER_NORMALS_OCT)
        float3 normalWS = normalize(input.normalWS);
        float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
        float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
        outNormalWS = half4(PackFloat2To888(remappedOctNormalWS), 0.0);
    #else
        outNormalWS = half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
    #endif

    #ifdef _WRITE_RENDERING_LAYERS
        outRenderingLayers = EncodeMeshRenderingLayer();
    #endif
}

#else // DepthOnly

half4 FurShellDepthOnlyFragment(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    FurStrandSample strand = SampleFurStrand(input.uv, input.layerT, normalize(input.viewDirTS));
    FurAlphaClip(strand, input.layerT);

    return 0;
}

#endif

#endif // FUR_SHELL_DEPTH_PASSES_INCLUDED
