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
#include "FurShellCommon.hlsl"

struct Attributes
{
    float4 positionOS       : POSITION;
    float3 normalOS         : NORMAL;
    float4 tangentOS        : TANGENT;
    float2 texcoord         : TEXCOORD0;
    float2 staticLightmapUV : TEXCOORD1;
    float2 dynamicLightmapUV: TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float2 uv                       : TEXCOORD0;
    float3 positionWS               : TEXCOORD1;
    float3 normalWS                 : TEXCOORD2;
    float4 tangentWS                : TEXCOORD3;    // xyz: tangent, w: handedness sign
    float4 shellDirAndLayer         : TEXCOORD4;    // xyz: strand direction, w: shell height (0..1)
    half4  fogFactorAndVertexLight  : TEXCOORD5;    // x: fog, yzw: vertex lights
    float3 viewDirTS                : TEXCOORD9;    // view direction in tangent space, for disc billboard

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

    // Compute the fur shell this instance represents.
    FurShellVertex fur = ComputeFurShell(input.positionOS.xyz, input.normalOS, GetFurShellIndex());

    output.positionWS = fur.positionWS;
    output.positionCS = TransformWorldToHClip(fur.positionWS);
    output.normalWS   = fur.normalWS;
    output.shellDirAndLayer = float4(fur.shellDirWS, fur.layerT);
    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);

    real sign = input.tangentOS.w * GetOddNegativeScale();
    float3 tangentWS = TransformObjectToWorldDir(input.tangentOS.xyz);
    output.tangentWS = float4(tangentWS, sign);
    output.viewDirTS = GetFurViewDirTS(fur.positionWS, fur.normalWS, tangentWS);

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
// highlight. Isotropic GGX alone cannot reproduce fur's silky sheen.
half3 FurAnisotropicSheen(half3 strandDirWS, half3 lightDirWS, half3 viewDirWS)
{
    float TdotL = dot(strandDirWS, lightDirWS);
    float TdotV = dot(strandDirWS, viewDirWS);
    float sinTL = sqrt(saturate(1.0 - TdotL * TdotL));
    float sinTV = sqrt(saturate(1.0 - TdotV * TdotV));

    float spec = pow(saturate(TdotL * TdotV + sinTL * sinTV), max(_SheenExponent, 1.0));
    return spec * _SheenIntensity * _SheenColor.rgb;
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

    // --- Strand mask / alpha test ---
    FurStrandSample strand = SampleFurStrand(input.uv, layerT, normalize(input.viewDirTS));

    // Apply strand mask texture: removes strands in transparent regions.
    if (SAMPLE_TEXTURE2D(_StrandMask, sampler_StrandMask, input.uv).r < 0.5)
        discard;

    FurAlphaClip(strand, layerT);

    // --- Depth-based occlusion (the fur base sits in shadow) ---
    // Applied to both direct light and GI: deeper strands are shadowed by the ones above them.
    half depthAtten = lerp(1.0 - _Occlusion, 1.0, pow(saturate(layerT), max(_OcclusionCurve, 0.01)));

    // --- Albedo ---
    half4 albedoAlpha = SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap));
    half3 albedo = albedoAlpha.rgb * _BaseColor.rgb;
    albedo *= lerp(_RootColor.rgb, _TipColor.rgb, layerT);           // root -> tip color gradient
    // The base shell is the visible skin/albedo surface. Never apply the
    // procedural cell hash to it: otherwise every Base Map looks pixelated
    // even when fur is disabled or reduced to a single shell by LOD.
    half strandTint = lerp(0.9, 1.1, strand.rnd.x);
    albedo *= lerp(1.0h, strandTint, saturate(layerT));
    albedo *= depthAtten;

    // Wet fur appears much darker due to light absorption and water darkening the color.
    albedo *= lerp(1.0, 0.35, _Wetness);

    // --- Normal: surface normal map plus a per-strand deviation ---
    half3 normalTS = SampleNormal(input.uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _NormalScale);
    normalTS.xy += (strand.rnd * 2.0 - 1.0) * _StrandNormalStrength * layerT;
    normalTS = normalize(normalTS);

    SurfaceData surfaceData = (SurfaceData)0;
    surfaceData.albedo     = albedo;
    surfaceData.metallic   = 0.0;   // Fur is a dielectric; a metallic control has no physical use here.
    // Wet fur is slightly more reflective, but not dramatically so (subtle highlight increase).
    surfaceData.smoothness = lerp(_Smoothness, 1.0, _Wetness * 0.25);
    surfaceData.occlusion  = depthAtten;
    surfaceData.normalTS   = normalTS;
    surfaceData.alpha      = 1.0;   // Opaque geometry; edges are resolved by the alpha-clip test above.

    InputData inputData;
    InitializeFurInputData(input, normalTS, inputData);

    half4 color = UniversalFragmentPBR(inputData, surfaceData);

    // --- Anisotropic sheen and back-scatter from the main light ---
    Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
    half  atten = mainLight.shadowAttenuation * mainLight.distanceAttenuation;

    half3 sheen = FurAnisotropicSheen(normalize(input.shellDirAndLayer.xyz), mainLight.direction, inputData.viewDirectionWS);
    color.rgb += sheen * mainLight.color * atten * layerT * depthAtten;

    // Light transmitting through thin strands when the light sits behind the object.
    half backLight = pow(saturate(dot(-mainLight.direction, inputData.viewDirectionWS)), max(_RimPower, 1.0));
    half rimMask   = pow(saturate(1.0 - abs(dot(inputData.normalWS, inputData.viewDirectionWS))), 2.0);
    color.rgb += mainLight.color * (backLight * rimMask * _RimIntensity * layerT * atten);

    color.rgb = MixFog(color.rgb, inputData.fogCoord);

    outColor = half4(color.rgb, layerT > 0.0 ? strand.alpha : 1.0);

#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

#endif // FUR_SHELL_FORWARD_PASS_INCLUDED
