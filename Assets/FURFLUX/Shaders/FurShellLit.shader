// ---------------------------------------------------------------------------
//  Fur/Shell Lit (PC)  -  Unity 6 (6000.3+) / URP 17+
//
//  * No geometry shader. Every fur layer (shell) is a GPU instance; layers are
//    submitted in a single draw call via Graphics.RenderMeshInstanced from
//    FurController.cs.
//  * Target: DirectX 11/12, Vulkan (PC / desktop). Shader Model 4.5.
//  * Shell count and strand density are supplied per draw through a
//    MaterialPropertyBlock, so a single material can serve any number of
//    furred objects - see FurShellInput.hlsl for why.
// ---------------------------------------------------------------------------
Shader "Fur/Shell Lit (PC)"
{
    Properties
    {
        [Header(Base Surface)][Space(4)]
        [MainTexture] _BaseMap("Albedo (Skin)", 2D) = "white" {}
        [MainColor]   _BaseColor("Base Color", Color) = (0.45, 0.32, 0.2, 1)
                      _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.25
        [Toggle(_NORMALMAP)] _NormalMapToggle("Use Normal Map", Float) = 0
        [Normal]      _BumpMap("Normal Map", 2D) = "bump" {}
                      _NormalScale("Normal Strength", Range(0.0, 2.0)) = 1.0

        [Header(Fur Structure)][Space(4)]
        [Toggle(_FURMASK_MAP)] _FurMaskToggle("Use Fur Mask Texture", Float) = 0
        _FurMask("Fur Mask (R = strand density / length)", 2D) = "white" {}
        _StrandMask("Strand Mask (R < 0.5 removes strands)", 2D) = "white" {}
        _RootColor("Root Color (multiplier)", Color) = (0.55, 0.55, 0.55, 1)
        _TipColor("Tip Color (multiplier)", Color) = (1.15, 1.1, 1.0, 1)
        _FurLength("Fur Length (meters)", Range(0.0, 2.0)) = 0.04
        _Density("Strand Density (cells per UV unit)", Range(1.0, 4000.0)) = 250.0
        _Thickness("Strand Thickness (fraction of one cell)", Range(0.0, 5.0)) = 0.75
        _StrandJitter("Strand Placement Randomness", Range(0.0, 1.0)) = 0.85
        _LengthRandom("Strand Length Randomness", Range(0.0, 1.0)) = 0.4
        _AlphaCutout("Alpha Cutout Threshold", Range(0.0, 1.0)) = 0.35
        _DiscBillboard("Disc Billboard (fixes cross-section flattening at grazing angles)", Range(0.0, 1.0)) = 1.0
        _DiscBillboardLimit("Disc Billboard Limit (silhouette clamp)", Range(0.05, 1.0)) = 0.2

        [Header(Physics and Motion)][Space(4)]
        _Stiffness("Stiffness Curve (higher = stiffer roots)", Range(0.5, 6.0)) = 2.0
        _Gravity("Gravity", Range(0.0, 3.0)) = 0.35
        _WindDirection("Wind Direction (world space XYZ)", Vector) = (1, 0, 0.3, 0)
        _WindStrength("Wind Strength", Range(0.0, 3.0)) = 0.25
        _WindFrequency("Wind Frequency", Range(0.0, 10.0)) = 2.0
        _WindTurbulence("Wind Turbulence (spatial variation)", Range(0.0, 20.0)) = 4.0

        [Header(Lighting)][Space(4)]
        _Occlusion("Fur Base Darkening (AO)", Range(0.0, 1.0)) = 0.7
        _OcclusionCurve("AO Falloff Curve", Range(0.1, 4.0)) = 1.4
        _StrandNormalStrength("Per-Strand Normal Deviation", Range(0.0, 1.0)) = 0.35
        [HDR] _SheenColor("Anisotropic Sheen Color", Color) = (1, 0.95, 0.85, 1)
        _SheenIntensity("Sheen Intensity", Range(0.0, 3.0)) = 0.6
        _SheenExponent("Sheen Sharpness", Range(1.0, 128.0)) = 32.0
        _RimIntensity("Backlight Scatter Intensity", Range(0.0, 3.0)) = 0.8
        _RimPower("Backlight Scatter Sharpness", Range(1.0, 20.0)) = 6.0
        _ShadowExtraBias("Extra Shadow Bias (prevents shadow acne on shells)", Range(0.0, 0.05)) = 0.01

        [Header(Render State)][Space(4)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull Mode", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "True"
            "Queue" = "Geometry"
        }

        LOD 300

        // -------------------------------------------------------------------
        //  Forward Lit
        // -------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles3 glcore

            #pragma vertex   FurShellForwardVertex
            #pragma fragment FurShellForwardFragment

            // --- Material keywords ---
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local_fragment _FURMASK_MAP

            // --- URP keywords ---
            //
            // _SCREEN_SPACE_OCCLUSION is deliberately absent.
            // Fur is not a good surface for screen-space AO: every strand disc is a
            // separate depth discontinuity, so SSAO paints a dark halo around each
            // one (most visible when SSAO reconstructs normals from depth). Fur's
            // correct occlusion model already lives in this shader as the
            // depth-based depthAtten / _Occlusion term above.
            // Side benefit: one less fragment branch, one less texture fetch, one
            // less shader variant axis.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            // --- Unity keywords ---
            //
            // The entire lightmap family is deliberately absent too: LIGHTMAP_ON,
            // DIRLIGHTMAP_COMBINED, DYNAMICLIGHTMAP_ON, USE_LEGACY_LIGHTMAPS,
            // LIGHTMAP_BICUBIC_SAMPLING, SHADOWS_SHADOWMASK, LIGHTMAP_SHADOW_MIXING.
            // Fur belongs on moving creatures; moving objects are never lightmapped,
            // so these keywords can never be active while inflating the variant
            // count by 2^7 = 128x. The probe/APV path (ProbeVolumeVariants) is kept -
            // that is where a moving object's ambient lighting comes from.
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"

            // --- GPU instancing: shell layers are drawn as instances ---
            #pragma multi_compile_instancing

            #include "FurShellForwardPass.hlsl"
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  Shadow Caster (self-shadowing within the fur volume)
        // -------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles3 glcore

            #pragma vertex   FurShellShadowVertex
            #pragma fragment FurShellShadowFragment

            #pragma shader_feature_local_fragment _FURMASK_MAP
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing

            #include "FurShellShadowPass.hlsl"
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  Depth Only (depth priming / depth texture)
        // -------------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles3 glcore

            #pragma vertex   FurShellDepthVertex
            #pragma fragment FurShellDepthOnlyFragment

            #pragma shader_feature_local_fragment _FURMASK_MAP
            #pragma multi_compile_instancing

            #include "FurShellDepthPasses.hlsl"
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  Depth Normals (SSAO / decal / other screen-space effects)
        // -------------------------------------------------------------------
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles3 glcore

            #pragma vertex   FurShellDepthVertex
            #pragma fragment FurShellDepthNormalsFragment

            #pragma shader_feature_local_fragment _FURMASK_MAP
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            #define FUR_DEPTH_NORMALS_PASS
            #include "FurShellDepthPasses.hlsl"
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
