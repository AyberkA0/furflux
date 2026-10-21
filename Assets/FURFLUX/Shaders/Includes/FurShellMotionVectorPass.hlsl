#ifndef FUR_SHELL_MOTION_VECTOR_PASS_INCLUDED
#define FUR_SHELL_MOTION_VECTOR_PASS_INCLUDED

// ---------------------------------------------------------------------------
//  MotionVectors - where each fur fragment was last frame.
//
//  Why the fur needs its own pass at all
//  -------------------------------------
//  Temporal effects - TAA above all - reproject last frame's image using this
//  buffer. Without it the fur reports only the camera's own movement, so a
//  creature walking across the screen leaves its history exactly where it was and
//  TAA blends the two together: a smear trailing the animal. Fur is the worst
//  possible content for that, because it is entirely high-frequency detail and
//  there is nothing for the filter to lock onto.
//
//  Switching RenderParams.motionVectorMode alone could never have fixed it. Shells
//  are displaced procedurally in the vertex stage, so no transform Unity tracks
//  describes where a strand actually was - the answer has to be computed here,
//  the same way the position was.
//
//  What is and is not tracked
//  --------------------------
//  The previous position is this vertex reconstructed through the object's
//  PREVIOUS transform, carrying its current shell offset. That covers the
//  dominant motion by a wide margin: the creature travelling and turning.
//
//  It does not track the sway itself - a strand bending under a gust while the
//  body holds still reports no motion. That residual is bounded by one fur length
//  and changes slowly, whereas body motion is unbounded and fast, which is why
//  this split is worth making rather than double-buffering every force uniform.
//  Skinned deformation is likewise not tracked; that is the same gap as the
//  bone-driven inertia approximation.
// ---------------------------------------------------------------------------

#include "FurGeometry.hlsl"

// Object-to-world from the frame before this one, pushed in per object. Unity's own
// UNITY_PREV_MATRIX_M is not dependable for Graphics.RenderMeshInstanced draws, and
// every shell here shares one transform anyway, so carrying it explicitly is both
// simpler and something this code can actually guarantee.
float4x4 _FurPrevObjectToWorld;

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 tangentOS  : TANGENT;
    float2 texcoord   : TEXCOORD0;
    float2 texcoord1  : TEXCOORD1;  // mesh.uv2 - the detail UV set (see _FurDetailUV)
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    // xy: _BaseMap-tiled UV. zw: the detail UV the per-object maps are addressed by
    //                           (see the forward pass and _FurDetailUV).
    float4 uv                         : TEXCOORD0;
    float2 layerAndFlatten            : TEXCOORD1;
    float4 viewDirTS                  : TEXCOORD2;   // xyz: tangent-space view dir, w: unused
    float4 furStrand                  : TEXCOORD5;   // ribbon coverage data - see the forward pass
    float3 rootPositionWS             : TEXCOORD6;   // the surface the fur GROWS FROM - see FurShellVertex.rootWS
    float4 positionCSNoJitter         : TEXCOORD3;
    float4 previousPositionCSNoJitter : TEXCOORD4;

    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

Varyings FurShellMotionVectorVertex(Attributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    FurVertexResult fur = ResolveFurVertex(input.positionOS.xyz, input.normalOS, input.tangentOS,
                                           input.texcoord, input.texcoord1, GetFurShellIndex(),
                                           float4(GetCameraPositionWS(), 1.0));

    output.uv              = float4(TRANSFORM_TEX(fur.uv0, _BaseMap), fur.detailUV);
    output.layerAndFlatten = float2(fur.layerT, fur.flatten);
    output.furStrand       = float4(fur.strandUV.x, fur.rnd, fur.alphaComp);
    output.rootPositionWS  = fur.rootWS;
    output.viewDirTS       = float4(GetFurViewDirTS(fur.positionWS, fur.geoNormalWS,
                                                    fur.tangentWS, fur.bitangentSign), 0.0);

    // Jittered for the depth test, so this pass rasterises the same pixels the rest of
    // the frame did. The motion vector itself is computed from the UNJITTERED pair -
    // the jitter is a camera trick and must not show up as scene movement.
    output.positionCS         = TransformWorldToHClip(fur.positionWS);
    output.positionCSNoJitter = mul(_NonJitteredViewProjMatrix, float4(fur.positionWS, 1.0));

    // Where this vertex was last frame, reconstructed by taking it back into object
    // space and forward again through the PREVIOUS transform. That covers the dominant
    // motion by a wide margin - the creature travelling and turning - and is the same
    // approximation for all three fur types.
    //
    // A ribbon draw has to use the explicitly supplied inverse rather than
    // unity_WorldToObject: it carries no per-instance transform, so Unity has no
    // matrix for it to invert. Shells go through the same code with the same matrices,
    // which is one path instead of two.
    float3 positionOS_now = IsFurRibbonDraw()
        ? mul(_FurWorldToObject, float4(fur.positionWS, 1.0)).xyz
        : TransformWorldToObject(fur.positionWS);

    float3 previousPositionWS = mul(_FurPrevObjectToWorld, float4(positionOS_now, 1.0)).xyz;

    output.previousPositionCSNoJitter = mul(_PrevViewProjMatrix, float4(previousPositionWS, 1.0));

    return output;
}

float4 FurShellMotionVectorFragment(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    // Clipped exactly as the lit pass clips, so motion vectors exist only where fur was
    // actually drawn. A vector left behind in a gap between strands would tell TAA to
    // drag the background along with the coat.
    FurStrandSample strand = SampleFurCoverage(input.uv.xy, input.uv.zw, input.rootPositionWS,
                                               input.layerAndFlatten.x,
                                               normalize(input.viewDirTS.xyz),
                                               SampleFurWetness(input.uv.zw), input.layerAndFlatten.y,
                                               float2(input.furStrand.x, input.layerAndFlatten.x),
                                               input.furStrand.yz, input.furStrand.w);
    FurCoverageClip(strand, input.layerAndFlatten.x, input.positionCS.xy);

    // URP's CalcNdcMotionVectorFromCsPositions is deliberately not used: it early-outs on
    // unity_MotionVectorsParams.y, which the pipeline fills in for renderer-driven draws
    // and not for these procedural ones. Reading it would silently zero every vector.
    // The arithmetic below is its non-foveated path verbatim.
    float2 positionNDC         = input.positionCSNoJitter.xy         * rcp(input.positionCSNoJitter.w);
    float2 previousPositionNDC = input.previousPositionCSNoJitter.xy * rcp(input.previousPositionCSNoJitter.w);

    float2 velocity = positionNDC - previousPositionNDC;
#if UNITY_UV_STARTS_AT_TOP
    velocity.y = -velocity.y;
#endif

    // NDC spans -1..1 and the buffer stores UV-space offsets, which span 0..1.
    return float4(velocity * 0.5, 0.0, 0.0);
}

#endif // FUR_SHELL_MOTION_VECTOR_PASS_INCLUDED
