#ifndef FUR_SHELL_FORWARD_PASS_INCLUDED
#define FUR_SHELL_FORWARD_PASS_INCLUDED

// ---------------------------------------------------------------------------
//  UniversalForward - PBR fur lighting
//    * Standard URP PBR (UniversalFragmentPBR) with main/additional light shadows
//    * Kajiya-Kay anisotropic sheen along the strand direction
//    * Depth-based self-shadowing / ambient occlusion
//    * Back-lit rim scattering
// ---------------------------------------------------------------------------

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "FurGeometry.hlsl"

struct Attributes
{
    float4 positionOS       : POSITION;
    float3 normalOS         : NORMAL;
    float4 tangentOS        : TANGENT;
    float2 texcoord         : TEXCOORD0;
    // mesh.uv2. Bound once and read twice: URP wants it as the static lightmap UV, and
    // it is also the non-overlapping unwrap the per-object maps can be addressed by
    // (see _FurDetailUV). Both readings want the same vertex stream, and a second
    // declaration of TEXCOORD1 is not legal, so the one field serves both.
    float2 staticLightmapUV : TEXCOORD1;
    float2 dynamicLightmapUV: TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    // xy: _BaseMap-tiled UV. zw: the DETAIL UV - the set every per-object map is
    // addressed by (wetness, length, thickness, colour). Resolved in the vertex
    // stage from _FurDetailUV, so nothing downstream has to know which set it is.
    // Never inherits _BaseMap's tiling: those maps are rasterised from the mesh's
    // own unwrap. Packed into the spare half of an existing interpolator rather
    // than costing a new one.
    float4 uv                       : TEXCOORD0;
    float3 positionWS               : TEXCOORD1;
    float3 normalWS                 : TEXCOORD2;
    float4 tangentWS                : TEXCOORD3;    // xyz: tangent, w: handedness sign
    float4 shellDirAndLayer         : TEXCOORD4;    // xyz: strand direction, w: shell height (0..1)
    half4  fogFactorAndVertexLight  : TEXCOORD5;    // x: fog, yzw: vertex lights
    // xyz: view direction in tangent space, for the disc billboard.
    // w:   how flat the strand has been pressed (0 free, 1 flat), which drives
    //      the strand footprint growth in SampleFurStrand.
    float4 viewDirTS                : TEXCOORD9;
    // Ribbon draws only (strand fur). x: position across the ribbon (0..1),
    // yz: this strand's own randomness, w: the coverage compensation owed for having
    // widened the ribbon to the minimum pixel width. Zero for shells, which resolve
    // all four of these in the fragment stage from the UV grid instead.
    float4 furStrand                : TEXCOORD10;
    // The surface the fur grows from, before this shell's offset. Only the strand
    // pattern's cell size uses it, and it cannot share positionWS above: that one is the
    // OFFSET position, which lighting needs and the cell size must not see.
    float3 rootPositionWS           : TEXCOORD11;

    DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 6);
#ifdef DYNAMICLIGHTMAP_ON
    float2 dynamicLightmapUV        : TEXCOORD7;
#endif
#ifdef USE_APV_PROBE_OCCLUSION
    float4 probeOcclusion           : TEXCOORD8;
#endif

    float4 positionCS               : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

// ---------------------------------------------------------------------------
//  Vertex
// ---------------------------------------------------------------------------
Varyings FurShellForwardVertex(Attributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);                 // unity_InstanceID becomes valid here
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    // One call for all three fur types. A shell resolves an object-space vertex of
    // the source mesh; a strand resolves a vertex of the instanced ribbon
    // batch, including its GPU skinning. Both come back in the same shape, and both
    // have been through the same physics - see FurGeometry.hlsl.
    //
    // Wetness is resolved inside it rather than only in the fragment stage: it drives
    // the gravity and stiffness terms that bend the strand, so a wet patch has to sag
    // as geometry, not just darken.
    FurVertexResult fur = ResolveFurVertex(input.positionOS.xyz, input.normalOS, input.tangentOS,
                                           input.texcoord, input.staticLightmapUV, GetFurShellIndex(),
                                           float4(GetCameraPositionWS(), 1.0));

    output.positionWS = fur.positionWS;
    output.rootPositionWS = fur.rootWS;
    output.positionCS = TransformWorldToHClip(fur.positionWS);
    output.normalWS   = fur.normalWS;
    output.shellDirAndLayer = float4(fur.strandDirWS, fur.layerT);
    output.uv = float4(TRANSFORM_TEX(fur.uv0, _BaseMap), fur.detailUV);
    output.furStrand = float4(fur.strandUV.x, fur.rnd, fur.alphaComp);

    output.tangentWS = float4(fur.tangentWS, fur.bitangentSign);
    // The GEOMETRIC normal, not the shading one: this vector exists to place the
    // strand pattern on the surface, and the shading normal has already been leaned
    // sideways to fake a round hair.
    output.viewDirTS = float4(GetFurViewDirTS(fur.positionWS, fur.geoNormalWS,
                                              fur.tangentWS, fur.bitangentSign), fur.flatten);

    half3 vertexLight = VertexLighting(fur.positionWS, fur.normalWS);
    half  fogFactor   = 0;
#if !defined(_FOG_FRAGMENT)
    fogFactor = ComputeFogFactor(output.positionCS.z);
#endif
    output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);

    OUTPUT_LIGHTMAP_UV(input.staticLightmapUV, unity_LightmapST, output.staticLightmapUV);
#ifdef DYNAMICLIGHTMAP_ON
    output.dynamicLightmapUV = input.dynamicLightmapUV.xy * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
#endif
    OUTPUT_SH4(fur.positionWS, fur.normalWS, GetWorldSpaceNormalizeViewDir(fur.positionWS), output.vertexSH, output.probeOcclusion);

    return output;
}

// ---------------------------------------------------------------------------
//  Fragment helpers
// ---------------------------------------------------------------------------
void InitializeFurInputData(Varyings input, half3 normalTS, out InputData inputData)
{
    inputData = (InputData)0;

    inputData.positionWS = input.positionWS;

    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    inputData.viewDirectionWS = viewDirWS;

    float  sgn       = input.tangentWS.w;
    float3 bitangent = sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
    half3x3 tangentToWorld = half3x3(input.tangentWS.xyz, bitangent.xyz, input.normalWS.xyz);

    inputData.tangentToWorld = tangentToWorld;
    inputData.normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalTS, tangentToWorld));

#if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
#else
    inputData.shadowCoord = float4(0, 0, 0, 0);
#endif

    inputData.fogCoord       = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactorAndVertexLight.x);
    inputData.vertexLighting = input.fogFactorAndVertexLight.yzw;
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

    // --- Global illumination (lightmap / APV / SH) ---
#if defined(DYNAMICLIGHTMAP_ON)
    inputData.bakedGI    = SAMPLE_GI(input.staticLightmapUV, input.dynamicLightmapUV, input.vertexSH, inputData.normalWS);
    inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
#elif !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
    inputData.bakedGI = SAMPLE_GI(input.vertexSH,
        GetAbsolutePositionWS(inputData.positionWS),
        inputData.normalWS,
        inputData.viewDirectionWS,
        input.positionCS.xy,
        input.probeOcclusion,
        inputData.shadowMask);
#else
    inputData.bakedGI    = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, inputData.normalWS);
    inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
#endif
}

// Kajiya-Kay: treats the strand direction as the tangent for an anisotropic
// highlight. Softly normalized with a gentle Fresnel term to prevent radioactive glare.
half3 FurAnisotropicSheen(half3 strandDirWS, half3 lightDirWS, half3 viewDirWS, half NdotV)
{
    float TdotL = dot(strandDirWS, lightDirWS);
    float TdotV = dot(strandDirWS, viewDirWS);
    float sinTL = sqrt(saturate(1.0 - TdotL * TdotL));
    float sinTV = sqrt(saturate(1.0 - TdotV * TdotV));

    float cosAngle = saturate(TdotL * TdotV + sinTL * sinTV);
    float spec = pow(cosAngle, max(_SheenExponent, 1.0));

    // Smooth Fresnel & energy scaling:
    float fresnel = lerp(0.35, 1.0, pow(1.0 - NdotV, 3.0));
    return spec * _SheenIntensity * _SheenColor.rgb * fresnel;
}

// ---------------------------------------------------------------------------
//  Fragment
// ---------------------------------------------------------------------------
void FurShellForwardFragment(
    Varyings input
    , out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    float layerT = input.shellDirAndLayer.w;

    // Regional wetness, resampled per fragment rather than interpolated from the
    // vertices: the shells are drawn from a deliberately low-poly proxy mesh, so
    // a per-vertex value would blur the map down to the proxy's own resolution
    // and a wet paw would bleed halfway up the leg.
    float wetness = SampleFurWetness(input.uv.zw);

    // --- Strand mask / alpha test ---
    FurStrandSample strand = SampleFurCoverage(input.uv.xy, input.uv.zw, input.rootPositionWS, layerT,
                                               normalize(input.viewDirTS.xyz), wetness,
                                               input.viewDirTS.w,
                                               float2(input.furStrand.x, layerT),
                                               input.furStrand.yz, input.furStrand.w);
    FurCoverageClip(strand, layerT, input.positionCS.xy);

    // A ribbon IS the strand, so every one of its fragments is fur - there is no
    // base layer standing in for the skin the way shell instance 0 does.
    bool isRibbon = IsFurRibbonDraw();

    // --- Distance aggregation on the base layer ---
    //
    // How far this fragment stands in for the whole coat rather than for the skin under
    // it. Nonzero only on the base layer, and only once the LOD has begun taking the fur
    // away - see the block above FurBaseAggregation in FurShellCommon.hlsl.
    //
    // Scaled by how OPAQUE the coat is: a dense coat hides its skin completely, so at
    // distance the object should be entirely fur-coloured, while a sparse one genuinely
    // is part skin and should stay part skin. Four layers of a strand covering a quarter
    // of its own cell already hide what is under them, which is where the factor comes
    // from - past that the coat is opaque and the term saturates.
    // A shell coat measures it per fragment, where the strand grid has a real screen
    // footprint, so the base layer starts taking the coat over at exactly the distance the
    // layers above it start aggregating. A RIBBON coat cannot: this draw is the source
    // mesh, and the strand grid it would measure against is not what its fur is made of -
    // so it takes the CPU's answer, which for those types already carries the hairs-per-
    // pixel measurement.
    //
    // Getting this wrong is visible and was: reading only the CPU term on a shell coat
    // left the base bare through the whole middle of the range, so the fur aggregated away
    // above a skin that had not yet been told to stand in for it, and the object went dark
    // before it went furry again.
    half aggregateHere = (half)(IsFurRibbonCoat() ? FurBaseAggregation() : strand.aggregate);

    // The base LEADS the fade rather than tracking it one for one.
    //
    // What the layers above lose first is their tips, because a tapered strand has the
    // lowest mean coverage there and converging to the mean is what takes it away - and
    // the tips are the bright end of the root-to-tip gradient. A base that gained the
    // coat's colour in exact step with that would therefore dip in the middle of the
    // transition, brightest at both ends and darkest halfway across, which is worse than
    // either failure it replaces. 1-(1-a)^2 is the same curve run early: it reaches the
    // same place at both ends and gets there sooner in between.
    half lead = aggregateHere * (2.0h - aggregateHere);

    half baseAggregate = layerT > 0.0
        ? 0.0h
        : lead * (half)saturate(strand.meanCoverage * 4.0);

    // --- Depth-based occlusion (the fur base sits in shadow) ---
    // Applied to both direct light and GI: deeper strands are shadowed by the ones above them.
    half occlusionWet = min(_Occlusion * max(_WetOcclusionScale, 1.0), 1.0);
    half occlusionAmount = lerp(_Occlusion, occlusionWet, wetness);
    half depthAtten = lerp(1.0 - occlusionAmount, 1.0, pow(saturate(layerT), max(_OcclusionCurve, 0.01)));

    // The base sits at the bottom of the fur and is shaded as such - almost fully
    // occluded. Once it is standing in for the coat instead, it has to carry the coat's
    // AVERAGE self-shadowing, or a creature that loses its fur to distance turns into a
    // dark silhouette on the way out.
    //
    // The average is the exact integral of the occlusion curve over the coat's height,
    // not a guess at its midpoint: depthAtten is lerp(1-occ, 1, t^curve), and the mean of
    // t^curve over 0..1 is 1/(curve+1). A steep curve keeps most of the coat in shadow
    // and a shallow one does not, and halfway is only right for the one curve where it
    // happens to be.
    half meanDepthAtten = (1.0h - occlusionAmount)
                        + occlusionAmount / (half)(max(_OcclusionCurve, 0.01) + 1.0);

    depthAtten = lerp(depthAtten, meanDepthAtten, baseAggregate);

    // --- A hair thinner than a pixel ---
    //
    // A ribbon held out to the minimum screen width covers more of its pixel than the hair
    // really does, and opacity is how much more. So the hair is blended toward what the
    // REST of that pixel holds, weighted by the part it does not genuinely cover.
    //
    // What the rest of the pixel holds is other hairs - not skin. That is the whole point,
    // and getting it wrong is what made a coat vanish at very ordinary distances: this used
    // to blend toward the root colour and the fully-occluded root shading, which is exactly
    // the appearance of the skin underneath, so as hairs went sub-pixel every one of them
    // faded into the body and the creature turned smooth. Measured on the test scene, a
    // squirrel had no visible coat left at four bounding radii, and turning the distance
    // switch ON made it worse rather than better, because the switch is what widens the
    // hairs and therefore what drives opacity down.
    //
    // A hair too small to resolve is still a hair, so it converges to the coat's MEAN - the
    // same target aggregation already uses two lines up, for the same reason. The two
    // mechanisms now agree instead of pulling opposite ways.
    half subPixel = isRibbon ? (half)saturate(strand.opacity) : 1.0h;

    // What the rest of the pixel holds, weighted by how much ground the coat covers: mean
    // hair where there is hair, the shaded floor of the coat where there are gaps. At full
    // coverage this is the coat's mean and nothing else; at low coverage it is mostly the
    // body underneath, which is the honest answer for a sparse coat.
    half coatCover = isRibbon ? (half)FurCoatCoverage() : 1.0h;
    half restAtten = lerp(1.0h - occlusionAmount, meanDepthAtten, coatCover);

    depthAtten = lerp(restAtten, depthAtten, subPixel);

    // --- Albedo ---
    half4 albedoAlpha = SampleAlbedoAlpha(input.uv.xy, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap));
    half3 albedo = albedoAlpha.rgb * _BaseColor.rgb;

    // The SKIN, told apart from the fur once and used for everything below.
    //
    // Shell layer 0 is the source mesh itself, and a ribbon coat draws that same mesh as a
    // separate pass; both arrive here with layerT at 0 and isRibbon false. Everything else
    // is hair.
    bool isSkin = !(layerT > 0.0 || isRibbon);

    // The colour along the hair - two stops, or as many as the band gradient carries. A
    // hair too thin to resolve takes the strand's area-weighted mean of it rather than its
    // root or its midpoint. See FUR_STRAND_MEAN_HEIGHT, and SampleFurBand for what happens
    // to that reasoning once a hair can have rings in it.
    half3 gradient = (half3)SampleFurBand(layerT).rgb;

    // Sub-pixel hairs converge to the mean of the gradient, not to its root - see the note
    // above. Converging to the root is what turned a distant coat into bare skin.
    half3 strandMean = (half3)FurBandMean().rgb;
    half3 restColor  = lerp((half3)SampleFurBand(0.0).rgb, strandMean, coatCover);
    gradient = lerp(restColor, gradient, subPixel);

    // Hair only, and this is the whole of the fix for a mesh that came out fur-coloured.
    //
    // At layerT 0 the gradient evaluates to the root stop, so multiplying it in
    // unconditionally tinted the SKIN with the coat's root colour - visibly, because the
    // skin is exactly what shows through the gaps between strands. The mesh keeps its own
    // colour now; the hair gradient describes hair and reaches nothing else.
    if (!isSkin)
        albedo *= lerp(gradient, strandMean, baseAggregate);

    // Regional colour: markings, a white bib, a mask. Applied BEFORE the wet
    // darkening below and before lighting, so a dyed patch darkens when it soaks
    // exactly as the rest of the coat does - a tint applied afterwards would sit
    // on top of the water film instead of underneath it.
    //
    // FUR SHELLS ONLY, until the coat starts aggregating. The base shell is not fur, it
    // is the SKIN the fur grows out of, and tinting it dyes the animal's hide along with
    // its coat: the skin is exactly what shows through the gaps between strands, so a
    // painted marking would read as a flat coloured patch of body with fur over it rather
    // than as coloured fur.
    //
    // It used to fade in on the base at distance, on the argument that an aggregated coat
    // IS the base. That was true when the LOD could shorten a coat out of existence; it no
    // longer can - Keep Fur Visible At Distance floors it - so the base is never standing
    // in for the coat, and dyeing it only ever tinted the hide.
    // Sampled at the strand's ROOT, not at this fragment.
    //
    // The fragment's own UV is the point on the surface beneath it, which drifts from one
    // side of a hair to the other - so a hair lying across the border of a marking came out
    // half one colour and half the other, and a coat over a patterned mesh read as speckle
    // rather than as the pattern. One hair, one colour, taken where it grows from.
    half3 regionTint = (half3)SampleFurColorTint(strand.rootUV);
    if (!isSkin)
        albedo *= regionTint;
    half strandTint = lerp(0.9, 1.1, strand.rnd.x);
    float detailFade = saturate(1.0 - (strand.footprint - 0.5) * 1.5);
    strandTint = lerp(1.0h, strandTint, detailFade);
    if (!isSkin)
        albedo *= lerp(1.0h, strandTint, saturate(layerT));
    albedo *= depthAtten;

    // --- Wet darkening ---
    albedo = pow(max(albedo, 0.0h), lerp(1.0, max(_WetDarkening, 1.0), wetness));

    // --- Normal: surface normal map plus a per-strand deviation ---
    half3 normalTS = SampleNormal(input.uv.xy, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _NormalScale);
    normalTS.xy += (strand.rnd * 2.0 - 1.0) * (_StrandNormalStrength * detailFade) * layerT;
    normalTS = normalize(normalTS);

    // Ribbons carry a real, per-vertex shading normal already - the round-hair lean
    // for a strand. Perturbing it again in
    // tangent space would apply the same idea twice, and the skin's normal map has
    // nothing to say about a hair standing in front of it.
    if (isRibbon)
        normalTS = half3(0.0h, 0.0h, 1.0h);

    SurfaceData surfaceData = (SurfaceData)0;
    surfaceData.albedo     = albedo;
    surfaceData.metallic   = 0.0;   // Fur is a dielectric
    surfaceData.smoothness = lerp(_Smoothness, saturate(_WetSmoothness), wetness);
    surfaceData.occlusion  = depthAtten;
    surfaceData.normalTS   = normalTS;
    surfaceData.alpha      = 1.0;

    InputData inputData;
    InitializeFurInputData(input, normalTS, inputData);

    half4 color = UniversalFragmentPBR(inputData, surfaceData);

    // --- Anisotropic sheen and back-scatter / rim lighting ---
    Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
    half  atten = mainLight.shadowAttenuation * mainLight.distanceAttenuation;
    // Against the GEOMETRIC surface normal for a ribbon, and the shaded one for a shell.
    //
    // The rim term below exists to light the fur at the BODY's silhouette, where a coat
    // is thin enough to see through. A ribbon's shading normal has been leaned sideways
    // to fake a round hair, so measuring N.V against it makes every hair its own
    // silhouette - and a hair is two pixels wide, so the whole coat is edge and the
    // whole coat glows white. viewDirTS.z is exactly the answer wanted here: it is
    // already dot(geometric normal, view) and every pass already computes it.
    half NdotV = IsFurRibbonDraw()
        ? saturate(normalize(input.viewDirTS.xyz).z)
        : saturate(dot(inputData.normalWS, inputData.viewDirectionWS));

    // Anisotropic Kajiya-Kay sheen:
    half3 strandDirWS = normalize(input.shellDirAndLayer.xyz);
    half3 sheen = FurAnisotropicSheen(strandDirWS, mainLight.direction, inputData.viewDirectionWS, NdotV);
    color.rgb += sheen * mainLight.color * atten * saturate(layerT + 0.1h) * depthAtten
               * lerp(1.0, saturate(_WetSheenScale), wetness);

    // ------------------------------------------------------------------
    //  Multi-Component Fur Rim & Backlight Scattering
    //  1. Fresnel Edge Silhouette Halo (Strand Tips catching ambient + light)
    //  2. Forward/Backlight Transmission through thin outer strands
    //  3. Additional Lights (Point & Spot lights behind the model)
    // ------------------------------------------------------------------
    half fresnelRim = pow(1.0h - NdotV, max(_RimPower, 0.5h));
    half rimScale   = _RimIntensity * lerp(1.0h, saturate(_WetRimScale), wetness) * saturate(layerT * 0.8h + 0.2h);

    // Main light rim + ambient tip scatter
    half backLight = pow(saturate(dot(-mainLight.direction, inputData.viewDirectionWS) * 0.5h + 0.5h), max(_RimPower * 0.75h, 1.0h));
    half3 rimLighting = (mainLight.color * (backLight * 0.6h + 0.4h) * atten + inputData.bakedGI * 0.5h) * (fresnelRim * rimScale);

    // Additional lights (e.g. backlights / rim spots behind the showcase spheres)
#if defined(_ADDITIONAL_LIGHTS)
    uint pixelLightCount = GetAdditionalLightsCount();
    for (uint lightIdx = 0; lightIdx < min(pixelLightCount, 8u); lightIdx++)
    {
        Light addLight = GetAdditionalLight(lightIdx, inputData.positionWS, inputData.shadowMask);
        half addAtten = addLight.distanceAttenuation * addLight.shadowAttenuation;
        half addBack  = pow(saturate(dot(-addLight.direction, inputData.viewDirectionWS) * 0.5h + 0.5h), max(_RimPower * 0.75h, 1.0h));
        rimLighting += addLight.color * (addBack * 0.7h + 0.3h) * (fresnelRim * rimScale * addAtten);
    }
#endif

    color.rgb += rimLighting * _RimColor.rgb;

    color.rgb = MixFog(color.rgb, inputData.fogCoord);

    half outAlpha = (layerT > 0.0 || isRibbon) ? strand.alpha : 1.0h;

    // The fuzz layer's whole contribution is its opacity. The coat underneath is opaque and
    // ignores this - its blend is One/Zero - so the multiply costs nothing there.
    if (IsFurFuzzDraw())
        outAlpha *= (half)saturate(_FuzzAmount);

    outColor = half4(color.rgb, outAlpha);

#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

#endif // FUR_SHELL_FORWARD_PASS_INCLUDED
