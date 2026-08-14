#ifndef FUR_SHELL_INPUT_INCLUDED
#define FUR_SHELL_INPUT_INCLUDED

// ---------------------------------------------------------------------------
//  Shell Fur - material constants (Unity 6 / URP 17+)
//
//  Every field is deliberately declared as "float". Mixing half and float in a
//  constant buffer makes some compilers pack the buffer differently, which
//  silently breaks binary layout compatibility.
//
//  --- A note on SRP Batcher (deliberate trade-off) ---
//  Fur is always drawn through Graphics.RenderMeshInstanced. That path uses GPU
//  instancing, not the SRP Batcher, so SRP Batcher compatibility buys this
//  shader nothing.
//
//  Keeping _ShellCount / _Density / _ExternalForce inside UnityPerMaterial, on
//  the other hand, carried a real architectural cost: constants in that buffer
//  cannot be overridden per draw with a MaterialPropertyBlock, so every furred
//  object was forced to own a runtime *copy* of its material. In an open world
//  that means one Material allocation per furred creature plus a
//  CopyPropertiesFromMaterial every frame.
//
//  Those three constants therefore live outside the buffer. A single shared
//  material now serves any number of objects, and per-object values (including
//  the LOD shell count) are supplied per draw - and therefore per camera.
// ---------------------------------------------------------------------------

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

CBUFFER_START(UnityPerMaterial)
    // --- Base surface (the skin underneath the fur) ---
    float4 _BaseMap_ST;
    float4 _BaseColor;
    float  _Smoothness;
    float  _NormalScale;

    // --- Fur shape ---
    float4 _FurMask_ST;
    float4 _RootColor;              // Tint at the strand root (multiplier)
    float4 _TipColor;               // Tint at the strand tip (multiplier)
    float  _FurLength;              // Total fur length, in meters
    float  _Thickness;              // Strand width, as a fraction of one cell
    float  _StrandJitter;           // Random offset of a strand inside its cell
    float  _LengthRandom;           // Per-strand length variation
    float  _AlphaCutout;            // Coverage below this value is discarded
    float  _DiscBillboard;          // Turns the strand cross-section toward the camera
    float  _DiscBillboardLimit;     // Clamp on that correction near the silhouette

    // --- Motion ---
    float  _Stiffness;              // Bend curve exponent (higher = stiffer roots)
    float  _Gravity;                // Downward bend
    float4 _WindDirection;          // World-space wind direction (xyz)
    float  _WindStrength;
    float  _WindFrequency;
    float  _WindTurbulence;         // Position-dependent phase shift (wave length)

    // --- Lighting ---
    float  _Occlusion;              // Darkening toward the fur base
    float  _OcclusionCurve;         // Falloff curve for that darkening
    float  _StrandNormalStrength;   // Per-strand normal deviation
    float4 _SheenColor;             // Kajiya-Kay anisotropic highlight color
    float  _SheenIntensity;
    float  _SheenExponent;
    float  _RimIntensity;           // Back-scatter / translucency at the silhouette
    float  _RimPower;
    float  _ShadowExtraBias;        // Extra shadow bias for the offset shells

    // --- Keyword drivers and render state ---
    // (Every constant in the Properties block must also appear here.)
    float  _NormalMapToggle;
    float  _FurMaskToggle;
    float  _Cull;
CBUFFER_END

// ---------------------------------------------------------------------------
//  Per-object constants, written per draw through a MaterialPropertyBlock.
//  They sit outside the buffer on purpose - see the note at the top of the file.
// ---------------------------------------------------------------------------
float  _ShellCount;             // Shell count for this draw (the LOD result)
float  _Density;                // Procedural strand density (cells per UV unit)
float4 _ExternalForce;          // Inertia force pushed in from FurController
float  _Wetness;                // Wetness level (0 = dry, 1 = fully wet)
float  _GravityMultiplierOnWet; // Gravity multiplier when fully wet (1 = no change)
float  _StiffnessMultiplierOnWet; // Stiffness multiplier when fully wet (1 = no change)
float4 _CollisionForce;         // Force from nearby colliders, pushed to fur

// --- Textures (always outside the constant buffer) ---
TEXTURE2D(_FurMask);        SAMPLER(sampler_FurMask);
TEXTURE2D(_StrandMask);     SAMPLER(sampler_StrandMask);
// _BaseMap, sampler_BaseMap, _BumpMap and sampler_BumpMap come from SurfaceInput.hlsl

#endif // FUR_SHELL_INPUT_INCLUDED
