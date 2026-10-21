#ifndef FUR_SHELL_DEPTH_PASSES_INCLUDED
#define FUR_SHELL_DEPTH_PASSES_INCLUDED

// ---------------------------------------------------------------------------
//  DepthOnly and DepthNormals passes.
//  Without these, URP's depth priming, SSAO, decals and depth-texture effects
//  all sample the fur incorrectly.
//  Both passes are generated from this single file; FUR_DEPTH_NORMALS_PASS
//  (defined in the .shader file) selects the DepthNormals variant.
// ---------------------------------------------------------------------------

#include "FurGeometry.hlsl"

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 tangentOS  : TANGENT;    // needed for the tangent-space view direction below
    float2 texcoord   : TEXCOORD0;
    float2 texcoord1  : TEXCOORD1;  // mesh.uv2 - the detail UV set (see _FurDetailUV)
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    // xy: _BaseMap-tiled UV. zw: the detail UV the per-object maps are addressed by
    //                           (see the forward pass and _FurDetailUV).
    float4 uv         : TEXCOORD0;
    float  layerT     : TEXCOORD1;
    float4 viewDirTS  : TEXCOORD3;   // xyz: tangent-space view dir, w: strand flatten (see forward pass)
    float4 furStrand  : TEXCOORD5;   // ribbon coverage data - see the forward pass
    float3 rootPositionWS : TEXCOORD6;   // the surface the fur GROWS FROM - see FurShellVertex.rootWS
#if defined(FUR_DEPTH_NORMALS_PASS)
    float3 normalWS   : TEXCOORD2;
    float4 tangentWS  : TEXCOORD4;   // xyz: tangent, w: handedness - only this pass shades a normal
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

    // Resolved exactly as the forward pass resolves it - the fur must bend identically
    // here or depth priming rejects the fragments the forward pass then draws.
    FurVertexResult fur = ResolveFurVertex(input.positionOS.xyz, input.normalOS, input.tangentOS,
                                           input.texcoord, input.texcoord1, GetFurShellIndex(),
                                           float4(GetCameraPositionWS(), 1.0));

    output.positionCS = TransformWorldToHClip(fur.positionWS);
    output.uv         = float4(TRANSFORM_TEX(fur.uv0, _BaseMap), fur.detailUV);
    output.layerT     = fur.layerT;
    output.furStrand  = float4(fur.strandUV.x, fur.rnd, fur.alphaComp);
    output.rootPositionWS = fur.rootWS;

    output.viewDirTS  = float4(GetFurViewDirTS(fur.positionWS, fur.geoNormalWS,
                                               fur.tangentWS, fur.bitangentSign), fur.flatten);
#if defined(FUR_DEPTH_NORMALS_PASS)
    output.normalWS   = fur.normalWS;
    output.tangentWS  = float4(fur.tangentWS, fur.bitangentSign);
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

    FurStrandSample strand = SampleFurCoverage(input.uv.xy, input.uv.zw, input.rootPositionWS,
                                               input.layerT, normalize(input.viewDirTS.xyz),
                                               SampleFurWetness(input.uv.zw), input.viewDirTS.w,
                                               float2(input.furStrand.x, input.layerT),
                                               input.furStrand.yz, input.furStrand.w);
    FurCoverageClip(strand, input.layerT, input.positionCS.xy);

    // The same normal the lit pass will shade with, including the normal map and the
    // per-strand deviation. This pass used to emit the raw geometric normal while the
    // forward pass shaded a perturbed one, so every screen-space effect reading this
    // buffer - SSAO, decals - was working from a surface that is not the one on screen.
    float3 normalWS;
    {
        half3 normalTS = SampleNormal(input.uv.xy, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _NormalScale);
        float detailFade = saturate(1.0 - (strand.footprint - 0.5) * 1.5);
        normalTS.xy += (strand.rnd * 2.0 - 1.0) * (_StrandNormalStrength * detailFade) * input.layerT;
        normalTS = normalize(normalTS);

        // Ribbons already carry their shading normal per vertex - see the forward pass.
        if (IsFurRibbonDraw())
            normalTS = half3(0.0h, 0.0h, 1.0h);

        float3 geoNormalWS = normalize(input.normalWS);
        float3 tangent     = input.tangentWS.xyz;
        float3 bitangent   = input.tangentWS.w * cross(geoNormalWS, tangent);

        normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(
            normalTS, half3x3(tangent, bitangent, geoNormalWS)));
    }

    #if defined(_GBUFFER_NORMALS_OCT)
        float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
        float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
        outNormalWS = half4(PackFloat2To888(remappedOctNormalWS), 0.0);
    #else
        outNormalWS = half4(normalWS, 0.0);
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

    FurStrandSample strand = SampleFurCoverage(input.uv.xy, input.uv.zw, input.rootPositionWS,
                                               input.layerT, normalize(input.viewDirTS.xyz),
                                               SampleFurWetness(input.uv.zw), input.viewDirTS.w,
                                               float2(input.furStrand.x, input.layerT),
                                               input.furStrand.yz, input.furStrand.w);
    FurCoverageClip(strand, input.layerT, input.positionCS.xy);

    return 0;
}

#endif

#endif // FUR_SHELL_DEPTH_PASSES_INCLUDED
