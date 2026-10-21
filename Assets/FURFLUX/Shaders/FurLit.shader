// ---------------------------------------------------------------------------
//  FURFLUX/Fur Lit  -  Unity 6 (6000.3+) / URP 17+
//
//  * No geometry shader. Every fur layer (shell) is a GPU instance; layers are
//    submitted in a single draw call via Graphics.RenderMeshInstanced from
//    FurController.cs.
//  * Target: DirectX 11/12, Vulkan (PC / desktop). Shader Model 4.5.
//  * Shell count and strand density are supplied per draw through a
//    MaterialPropertyBlock, so a single material can serve any number of
//    furred objects - see FurShellInput.hlsl for why.
// ---------------------------------------------------------------------------
Shader "FURFLUX/Fur Lit"
{
    Properties
    {
        [Enum(Shells, 0, Strands, 1)]
        _FurType("Fur Type", Float) = 0

        // --- Base Surface ---
        // The fur's own albedo. Not the skin: the object's own renderer draws that.
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor]   _BaseColor("Base Color", Color) = (0.45, 0.32, 0.2, 1)
                      _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.25
        [Toggle(_NORMALMAP)] _NormalMapToggle("Use Normal Map", Float) = 0
        [Normal]      _BumpMap("Normal Map", 2D) = "bump" {}
                      _NormalScale("Normal Strength", Range(0.0, 2.0)) = 1.0

        // --- Fur Structure ---
        //
        // Every value here is RELATIVE and there is no absolute distance among them:
        // length is a fraction of the object's size, density is strands ACROSS it,
        // thickness is a fraction of the spacing density implies. That is what lets one
        // material clothe a mouse and a bear at any import scale. See FurObjectSize in
        // FurShellInput.hlsl.
        //
        // Alpha on the two colours is how solid the hair is at that height. Tip alpha
        // below 1 is the useful end: it removes the cut-off edge of a clipped coat.
        _RootColor("Root Color", Color) = (0.55, 0.55, 0.55, 1)
        _TipColor("Tip Color", Color) = (1.15, 1.1, 1.0, 1)
        _FurLength("Fur Length", Range(0.0, 1.0)) = 0.04
        // Neither floor is cosmetic: a coat with no strands, or strands with no width, is
        // not a thin coat, it is one that looks like a broken shader.
        _Density("Strand Density", Range(1.0, 4000.0)) = 250.0
        _Thickness("Strand Thickness", Range(0.01, 20.0)) = 0.75
        _StrandJitter("Placement Randomness", Range(0.0, 1.0)) = 0.85
        _LengthRandom("Length Randomness", Range(0.0, 1.0)) = 0.4
        // How tightly the hair coils around its own growth axis. Every hair gets its
        // own phase, so a curled coat reads as a tangle rather than as a ripple.
        _Curl("Curl", Range(0.0, 1.0)) = 0.0
        // Capped at 0.9 on purpose. At 1.0 only fragments at exactly full coverage
        // survive, which floating point makes almost none of them - the coat vanishes
        // while every hair is still being submitted.
        _AlphaCutout("Alpha Cutout", Range(0.0, 0.9)) = 0.35

        // --- Fuzz layer ---
        //
        // A second, softer copy of the coat over the sharp one: same hairs, longer, much
        // wider, translucent. At Amount 0 the extra draw is not submitted at all.
        _FuzzAmount("Fuzz Amount", Range(0.0, 1.0)) = 0.0
        _FuzzExtent("Fuzz Reach", Range(0.0, 1.0)) = 0.35
        _FuzzSoftness("Fuzz Softness", Range(1.0, 12.0)) = 4.0

        // Geometric costs a second coat and is lit like one. Screen Space blurs the
        // coat's drawn pixels instead - one full-screen pass, carrying their real colour
        // and lighting - and needs the Fur Screen Fuzz renderer feature, which the
        // inspector offers to add.
        [Enum(Geometric, 0, Screen Space, 1)] _FuzzMode("Fuzz Mode", Float) = 0.0

        // The stencil bit screen-space fuzz marks coat pixels with. Exposed because a
        // project already spending that bit has to be able to move it. The write mask is
        // 0 until the mode is on, so the write costs nothing meanwhile.
        _FuzzStencilRef("Fuzz Stencil Ref", Float) = 8
        _FuzzStencilWriteMask("Fuzz Stencil Write Mask", Float) = 0

        // --- Physics and Motion ---
        _Stiffness("Stiffness", Range(0.5, 6.0)) = 2.0
        _Gravity("Gravity", Range(0.0, 3.0)) = 0.35
        // Multipliers of the two above rather than absolute wet values, so they keep
        // tracking when those are animated.
        _GravityMultiplierOnWet("Gravity when Wet", Range(0.0, 5.0)) = 3.0
        _StiffnessMultiplierOnWet("Stiffness when Wet", Range(0.1, 3.0)) = 0.7
        _CombStrength("Comb Strength", Range(0.0, 4.0)) = 1.5
        _WindDirection("Wind Direction", Vector) = (1, 0, 0.3, 0)
        _WindStrength("Wind Strength", Range(0.0, 3.0)) = 0.25
        _WindFrequency("Wind Frequency", Range(0.0, 10.0)) = 2.0
        _WindTurbulence("Wind Turbulence", Range(0.0, 20.0)) = 4.0
        _WindGustiness("Wind Gustiness", Range(0.0, 1.0)) = 0.6
        // How much airflow the side facing away from the wind still gets.
        _WindShelter("Leeward Shelter", Range(0.0, 1.0)) = 0.35

        // --- Lighting ---
        _Occlusion("Root Darkening", Range(0.0, 1.0)) = 0.7
        _OcclusionCurve("Root Darkening Falloff", Range(0.1, 4.0)) = 1.4
        _StrandNormalStrength("Strand Normal Deviation", Range(0.0, 1.0)) = 0.35
        [HDR] _SheenColor("Sheen Color", Color) = (1, 0.95, 0.85, 1)
        _SheenIntensity("Sheen Intensity", Range(0.0, 2.0)) = 0.35
        _SheenExponent("Sheen Sharpness", Range(1.0, 128.0)) = 32.0
        [HDR] _RimColor("Backlight Color", Color) = (1, 1, 1, 1)
        _RimIntensity("Backlight Intensity", Range(0.0, 3.0)) = 1.0
        _RimPower("Backlight Sharpness", Range(0.5, 10.0)) = 3.5

        // --- Wetness Response ---
        // Each of these is the WET end of a value whose dry end is declared above.
        _WetClumpSize("Clump Size", Range(1.0, 8.0)) = 3.0
        _WetClumpStrength("Clumping", Range(0.0, 1.0)) = 0.8
        _WetThinning("Strand Thinning", Range(0.2, 1.0)) = 0.65
        _WetLengthScale("Fur Length when Wet", Range(0.4, 1.0)) = 0.85
        _WetDarkening("Darkening", Range(1.0, 3.0)) = 1.8
        _WetSmoothness("Water Film Smoothness", Range(0.0, 1.0)) = 0.85
        _WetWindResponse("Wind Response when Wet", Range(0.0, 1.0)) = 0.15
        _WetRimScale("Backlight Scatter when Wet", Range(0.0, 1.0)) = 0.25
        _WetSheenScale("Anisotropic Sheen when Wet", Range(0.0, 1.0)) = 0.35
        _WetOcclusionScale("Root Darkening when Wet", Range(1.0, 2.0)) = 1.35

        // --- Per-object maps ---
        // All four are written per object by the Fur Controller; never assign them by
        // hand. They are declared here only to pin the material DEFAULT for previews and
        // for anything the controller is not driving: black reads as dry, white reads as
        // "exactly what the material says" for the three multipliers.
        [HideInInspector] [NoScaleOffset] _WetnessMap("Wetness Map", 2D) = "black" {}
        [HideInInspector] [NoScaleOffset] _FurLengthMap("Fur Length Map", 2D) = "white" {}
        [HideInInspector] [NoScaleOffset] _FurThicknessMap("Fur Thickness Map", 2D) = "white" {}
        [HideInInspector] [NoScaleOffset] _FurColorMap("Fur Color Map", 2D) = "white" {}

        // --- Render State ---
        // Off, not Back, and on both fur types. A shell coat extends past the silhouette
        // on back-facing triangles, so culling removes the fur that reads as the outline.
        // A strand coat needs it far more: its ribbons keep a fixed twisted plane, so
        // about half the hairs face away from any camera and culling deletes them.
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull Mode", Float) = 0

        // Set by the Fur Controller, not by hand. The coat is opaque and keeps these; the
        // fuzz layer is drawn from a runtime copy with them switched to alpha blending.
        [HideInInspector] _SrcBlend("__src", Float) = 1
        [HideInInspector] _DstBlend("__dst", Float) = 0
        [HideInInspector] _ZWrite("__zw", Float) = 1
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

            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            ZTest LEqual
            Cull [_Cull]

            // The coat marking its own pixels for the screen-space fuzz pass.
            //
            // Only the forward pass, because it is the only one that writes the colour
            // that pass goes on to blur. And only where a fragment actually SURVIVED -
            // the strand clip runs before this, so the mark follows the hairs rather than
            // the triangles they were cut out of, which is the whole point: the gaps
            // between hairs stay unmarked and are therefore free to receive the haze.
            //
            // With the write mask at its default of 0 this is not a stencil write at all,
            // so a material that never turns screen-space fuzz on pays nothing for it.
            Stencil
            {
                Ref       [_FuzzStencilRef]
                WriteMask [_FuzzStencilWriteMask]
                Comp      Always
                Pass      Replace
            }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles3 glcore

            // Fur type. Off = shells, which is what a material that predates the other
            // two types resolves to, so no existing asset changes appearance.
            #pragma shader_feature_local _ _FURTYPE_STRANDS

            #pragma vertex   FurShellForwardVertex
            #pragma fragment FurShellForwardFragment

            // --- Material keywords ---
            #pragma shader_feature_local _NORMALMAP

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

            #include "Includes/FurShellForwardPass.hlsl"
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

            // Fur type. Off = shells, which is what a material that predates the other
            // two types resolves to, so no existing asset changes appearance.
            #pragma shader_feature_local _ _FURTYPE_STRANDS

            #pragma vertex   FurShellShadowVertex
            #pragma fragment FurShellShadowFragment

            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing

            #include "Includes/FurShellShadowPass.hlsl"
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

            // Fur type. Off = shells, which is what a material that predates the other
            // two types resolves to, so no existing asset changes appearance.
            #pragma shader_feature_local _ _FURTYPE_STRANDS

            #pragma vertex   FurShellDepthVertex
            #pragma fragment FurShellDepthOnlyFragment

            #pragma multi_compile_instancing

            #include "Includes/FurShellDepthPasses.hlsl"
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  Motion Vectors (TAA, motion blur, temporal upscalers)
        // -------------------------------------------------------------------
        Pass
        {
            Name "MotionVectors"
            Tags { "LightMode" = "MotionVectors" }

            ColorMask RG
            ZWrite On
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles3 glcore

            // Fur type. Off = shells, which is what a material that predates the other
            // two types resolves to, so no existing asset changes appearance.
            #pragma shader_feature_local _ _FURTYPE_STRANDS

            #pragma vertex   FurShellMotionVectorVertex
            #pragma fragment FurShellMotionVectorFragment

            #pragma multi_compile_instancing

            #include "Includes/FurShellMotionVectorPass.hlsl"
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

            // Fur type. Off = shells, which is what a material that predates the other
            // two types resolves to, so no existing asset changes appearance.
            #pragma shader_feature_local _ _FURTYPE_STRANDS

            #pragma vertex   FurShellDepthVertex
            #pragma fragment FurShellDepthNormalsFragment

            // Declared here so this pass shades the SAME normal the lit pass does.
            // Without it the keyword was always off here and the buffer carried the raw
            // geometric normal, which every screen-space consumer then disagreed with.
            #pragma shader_feature_local _NORMALMAP
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            #define FUR_DEPTH_NORMALS_PASS
            #include "Includes/FurShellDepthPasses.hlsl"
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"

    // Around seventy properties across three fur types is far too many for a flat list.
    // The custom inspector groups them into collapsible sections that summarise
    // themselves while shut, and greys out whatever the chosen fur type cannot use - see
    // FurShellShaderGUI.
    //
    // The grouping above is written as COMMENTS rather than as [Header] attributes.
    // A header attribute is a decorator on the property that follows it, and
    // MaterialEditor.ShaderProperty draws it wherever that property is drawn - so once
    // the custom inspector regroups the list, every one of those labels reappears in the
    // middle of some other section.
    CustomEditor "FurRendering.EditorTools.FurShellShaderGUI"
}
