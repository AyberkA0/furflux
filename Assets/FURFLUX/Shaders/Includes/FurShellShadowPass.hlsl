#ifndef FUR_SHELL_SHADOW_PASS_INCLUDED
#define FUR_SHELL_SHADOW_PASS_INCLUDED

// ---------------------------------------------------------------------------
//  ShadowCaster - fur shells also write into the shadow map.
//  Each shell casting its own shadow produces believable self-shadowing inside
//  the fur volume. For performance, the C# side can switch to a
//  "base shell only" shadow mode.
// ---------------------------------------------------------------------------

#include "FurGeometry.hlsl"

// Set by URP's ShadowUtils:
// _LightDirection for directional lights, _LightPosition for punctual lights.
float3 _LightDirection;
float3 _LightPosition;

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 tangentOS  : TANGENT;    // needed for the light-space disc billboard below
    float2 texcoord   : TEXCOORD0;
    float2 texcoord1  : TEXCOORD1;  // mesh.uv2 - the detail UV set (see _FurDetailUV)
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    // xy: _BaseMap-tiled UV. zw: the detail UV the per-object maps are addressed by
    //                           (see the forward pass and _FurDetailUV).
    float4 uv         : TEXCOORD0;
    float2 layerAndFlatten : TEXCOORD1;   // x: shell height 0..1, y: strand flatten (see forward pass)
    float3 lightDirTS : TEXCOORD2;        // light direction in tangent space
    float4 furStrand  : TEXCOORD3;        // ribbon coverage data - see the forward pass
    float3 rootPositionWS : TEXCOORD4;   // the surface the fur GROWS FROM - see FurShellVertex.rootWS
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings FurShellShadowVertex(Attributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    // Whoever this pass rasterises for is the LIGHT, not the camera. That reaches the
    // geometry as well as the coverage test: a billboarded strand ribbon has to turn
    // its face toward the light here, or it casts the shadow of a silhouette nobody
    // is looking at.
#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float4 facing = float4(_LightPosition, 1.0);
#else
    float4 facing = float4(_LightDirection, 0.0);
#endif

    // The caster has to sag exactly like the lit fur, otherwise a wet patch casts the
    // shadow of its dry silhouette.
    FurVertexResult fur = ResolveFurVertex(input.positionOS.xyz, input.normalOS, input.tangentOS,
                                           input.texcoord, input.texcoord1, GetFurShellIndex(), facing);

    float3 lightDirectionWS = FurFacingDir(fur.positionWS, facing);

    // The disc billboard turns each strand's cross-section to face whoever is looking.
    // Feeding it a fixed (0,0,1) meant shadow coverage was computed for a head-on view
    // that never happens, so the shadow the fur cast did not match the fur that was
    // drawn, most visibly at grazing light angles.
    output.lightDirTS = GetFurDirTS(lightDirectionWS, fur.geoNormalWS,
                                    fur.tangentWS, fur.bitangentSign);

    output.positionCS = GetFurShadowPositionHClip(fur.positionWS, fur.geoNormalWS, lightDirectionWS);
    output.uv         = float4(TRANSFORM_TEX(fur.uv0, _BaseMap), fur.detailUV);
    output.layerAndFlatten = float2(fur.layerT, fur.flatten);
    output.furStrand  = float4(fur.strandUV.x, fur.rnd, fur.alphaComp);
    output.rootPositionWS = fur.rootWS;

    return output;
}

half4 FurShellShadowFragment(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);

    FurStrandSample strand = SampleFurCoverage(input.uv.xy, input.uv.zw, input.rootPositionWS,
                                               input.layerAndFlatten.x,
                                               normalize(input.lightDirTS),
                                               SampleFurWetness(input.uv.zw), input.layerAndFlatten.y,
                                               float2(input.furStrand.x, input.layerAndFlatten.x),
                                               input.furStrand.yz, input.furStrand.w);
    // Shadow-map texel coordinates rather than screen ones - a different pattern, but
    // the same statistics, which is all a stochastic coverage test needs.
    FurCoverageClip(strand, input.layerAndFlatten.x, input.positionCS.xy);

    return 0;
}

#endif // FUR_SHELL_SHADOW_PASS_INCLUDED
