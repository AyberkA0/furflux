#ifndef FUR_SHELL_COMMON_INCLUDED
#define FUR_SHELL_COMMON_INCLUDED

// ---------------------------------------------------------------------------
//  Shell Fur - shared math
//
//  Architecture: no geometry shader. Every fur layer (shell) is a separate GPU
//  instance. The C# side submits N instances through Graphics.RenderMeshInstanced
//  in a single draw call, and the shell index reaches the shader through
//  unity_InstanceID. This is substantially faster than a geometry-shader
//  approach on DX11/DX12/Vulkan and avoids the tessellation/geometry stages
//  entirely.
// ---------------------------------------------------------------------------

#include "FurShellInput.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

// ---------------------------------------------------------------------------
//  Shell index.
//  With instancing disabled (the material assigned directly to a MeshRenderer)
//  only the base shell is drawn, so the shader still previews correctly inside
//  the material editor.
// ---------------------------------------------------------------------------
uint GetFurShellIndex()
{
#if defined(UNITY_INSTANCING_ENABLED) || defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) || defined(UNITY_STEREO_INSTANCING_ENABLED)
    return unity_InstanceID;
#else
    return 0u;
#endif
}

// Normalized shell height in 0..1 (0 = skin, 1 = strand tip).
float GetFurLayerT(uint shellIndex)
{
    return (float)shellIndex / max(_ShellCount - 1.0, 1.0);
}

// ---------------------------------------------------------------------------
//  Hash / noise - drives the procedural strand placement without a texture.
// ---------------------------------------------------------------------------
float2 FurHash22(float2 p)
{
    float3 p3 = frac(p.xyx * float3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.xx + p3.yz) * p3.zy);
}

// ---------------------------------------------------------------------------
//  Per-shell vertex offset: gravity + wind + external force bend the shell
//  outward along the surface normal.
// ---------------------------------------------------------------------------
struct FurShellVertex
{
    float3 positionWS;      // Offset shell position (world space)
    float3 normalWS;        // Surface normal (world space)
    float3 shellDirWS;      // Strand direction - reused as the tangent for anisotropic sheen
    float  layerT;          // Normalized shell height, 0..1
};

FurShellVertex ComputeFurShell(float3 positionOS, float3 normalOS, uint shellIndex)
{
    FurShellVertex fur;

    fur.layerT   = GetFurLayerT(shellIndex);
    fur.positionWS = TransformObjectToWorld(positionOS);
    fur.normalWS   = TransformObjectToWorldNormal(normalOS);

    // Stiffness curve: strands near the root barely bend, tips are free to move.
    // Wet fur is stiffer: water reduces strand flexibility, making them more rigid.
    float stiffnessAdjusted = lerp(_Stiffness, _Stiffness * _StiffnessMultiplierOnWet, _Wetness);
    float bend = pow(saturate(fur.layerT), max(stiffnessAdjusted, 0.01));

    // Wind: two sine octaves plus a position-dependent phase shift for a travelling-wave feel.
    float3 windDir = SafeNormalize(_WindDirection.xyz);
    float  phase   = _Time.y * _WindFrequency + dot(fur.positionWS, float3(1.0, 0.7, 1.3)) * _WindTurbulence;
    float  wave    = sin(phase) * 0.6 + sin(phase * 1.7 + 1.3) * 0.4;
    float3 wind    = windDir * (wave * _WindStrength);

    // Gravity (world -Y) + wind + the inertia force pushed in from FurController + collision pushback.
    // Wet fur is heavier: water weight makes strands sag more downward.
    float gravityAdjusted = lerp(_Gravity, _Gravity * _GravityMultiplierOnWet, _Wetness);

    // Collision must only affect the side of the mesh actually facing the collider, not the
    // whole object - layerT (root vs tip) says nothing about WHERE on the surface a strand
    // sits, only how far up its own strand it is. _CollisionForce points away from the
    // collider (out of the contact surface), so a vertex whose normal points roughly the
    // opposite way (toward the collider) is the one actually in contact.
    float3 collisionForce = _CollisionForce.xyz;
    float  collisionMag   = length(collisionForce);
    float  contactMask    = 0.0;
    if (collisionMag > 1e-5)
        contactMask = saturate(dot(fur.normalWS, -collisionForce / collisionMag));

    float3 force = float3(0.0, -gravityAdjusted, 0.0) + wind + _ExternalForce.xyz
                   + collisionForce * contactMask;

    fur.shellDirWS = SafeNormalize(fur.normalWS + force * bend);
    fur.positionWS += fur.shellDirWS * (_FurLength * fur.layerT);

    return fur;
}

// ---------------------------------------------------------------------------
//  Strand mask.
//  Texture-driven when _FURMASK_MAP is enabled, procedural cell hashing otherwise.
//  alpha carries the analytic edge coverage used for the clip test.
// ---------------------------------------------------------------------------
struct FurStrandSample
{
    float  alpha;       // Coverage value used by the alpha-clip test
    float  strandLen;   // This strand's normalized length (0..1)
    float2 rnd;         // Per-strand randomness (normal deviation / color variation)
};

// ---------------------------------------------------------------------------
//  View direction expressed in tangent (UV) space.
//  x,y: the view vector's projection onto the surface, z: N·V.
//  Used by the disc-billboard correction below. Computed once per vertex and
//  interpolated.
// ---------------------------------------------------------------------------
float3 GetFurViewDirTS(float3 positionWS, float3 normalWS, float3 tangentWS)
{
    float3 viewDirWS = SafeNormalize(GetCameraPositionWS() - positionWS);
    float3 bitangentWS = cross(normalWS, tangentWS);

    return float3(dot(tangentWS, viewDirWS),
                  dot(bitangentWS, viewDirWS),
                  dot(normalWS, viewDirWS));
}

FurStrandSample SampleFurStrand(float2 uv, float layerT, float3 viewDirTS)
{
    FurStrandSample s;

#if defined(_FURMASK_MAP)
    // Texture mode: the red channel drives strand density / length.
    float2 maskUV = uv * _FurMask_ST.xy + _FurMask_ST.zw;
    float  mask   = SAMPLE_TEXTURE2D(_FurMask, sampler_FurMask, maskUV).r;

    s.strandLen = lerp(1.0 - _LengthRandom, 1.0, mask);
    s.alpha     = saturate((mask - layerT * (1.0 / max(s.strandLen, 1e-4))) * (1.0 + _Thickness));
    s.rnd       = float2(mask, frac(mask * 7.13));

    // No disc geometry in texture mode; compensate grazing angles by scaling
    // coverage instead, which keeps inter-shell gaps from opening up.
    if (_DiscBillboard > 0.0)
    {
        float ndotv = max(abs(viewDirTS.z), _DiscBillboardLimit);
        s.alpha = saturate(s.alpha * lerp(1.0, 1.0 / ndotv, saturate(_DiscBillboard)));
    }
#else
    // Procedural mode: split the UV into cells, one strand per cell.
    float2 grid = uv * _Density;
    float2 cell = floor(grid);
    s.rnd       = FurHash22(cell);

    // Jitter the strand's center inside its cell to break up the grid pattern.
    float2 local = (frac(grid) - 0.5) - (s.rnd - 0.5) * _StrandJitter;

    // --- Disc billboard ---
    // At a grazing angle, a circle in UV space projects to an ELLIPSE on screen:
    // strand cross-sections flatten, gaps open between shells and the fur reads
    // as "striped". Fix: shrink the distance measured along the view vector's
    // projection axis by N·V. That stretches the accepted region by 1/(N·V) on
    // the same axis, so after projection it is a circle again (a disc that
    // always faces the camera).
    float2 viewAxis = viewDirTS.xy;
    float  axisLen  = length(viewAxis);
    if (_DiscBillboard > 0.0 && axisLen > 1e-4)
    {
        viewAxis /= axisLen;

        // As N·V -> 0 (silhouette) the stretch factor diverges; clamp with _DiscBillboardLimit.
        float ndotv  = max(abs(viewDirTS.z), _DiscBillboardLimit);
        float squash = lerp(1.0, ndotv, saturate(_DiscBillboard));

        float  along  = dot(local, viewAxis);
        float2 across = local - viewAxis * along;
        local = viewAxis * (along * squash) + across;
    }

    float dist = length(local) * 2.0;

    // Length variation plus height-based tapering (a conical strand silhouette).
    s.strandLen  = lerp(1.0 - _LengthRandom, 1.0, s.rnd.y);
    float taper  = saturate(1.0 - layerT / max(s.strandLen, 1e-4));
    // Wet fur clumps and compacts: strands stick together and appear thinner.
    float radius = _Thickness * taper * lerp(1.0, 0.4, _Wetness);

    // One pixel's footprint in cell space (grid = uv * _Density).
    // Used for both the edge antialiasing below and the procedural LOD further down.
    float footprint = length(fwidth(uv) * _Density);

    // Analytic edge antialiasing (soft strand tips under MSAA).
    //
    // NOTE: fwidth(dist) must NOT be used here. dist derives from frac(grid) and
    // a per-cell-constant rnd, so it jumps at cell boundaries; fwidth would spike
    // there, coverage would collapse to zero, and every cell boundary would show
    // a one-pixel seam. The derivative is instead taken from uv, which is
    // continuous across cells.
    float width = max(footprint * 2.0, 1e-5);
    s.alpha     = saturate((radius - dist) / width);

    // ------------------------------------------------------------------
    //  Procedural LOD - this pattern's equivalent of texture mipmapping.
    //
    //  Once a cell becomes smaller than a pixel (footprint > 1), each fragment
    //  samples ONLY the strand belonging to the cell its center falls in and
    //  completely misses the other strands sharing that pixel. The result
    //  isn't fur, it's noise that flickers on every camera move - this is the
    //  real obstacle to sub-pixel strands, and MSAA alone does not fix it
    //  (4 samples cannot stably resolve a 0.3-pixel feature).
    //
    //  Fix: blend toward the strand's ANALYTIC MEAN coverage in that region.
    //  A strand disc has radius `radius` inside its cell, so the expected
    //  coverage per cell is pi * radius^2 / 4 (cell area = 1 in cell space).
    //  This lets distant fur resolve to a smooth surface at the correct
    //  density while nearby fur still shows individual strands. The blend
    //  ramps between footprint 0.6 and 1.5.
    // ------------------------------------------------------------------
    float meanCoverage = saturate(PI * radius * radius * 0.25);
    s.alpha = lerp(s.alpha, meanCoverage, saturate((footprint - 0.6) / 0.9));
#endif

    return s;
}

// The base shell (the skin) is never clipped; only shells above it run the alpha test.
void FurAlphaClip(FurStrandSample s, float layerT)
{
    if (layerT > 0.0 && s.alpha < _AlphaCutout)
        discard;
}

// ---------------------------------------------------------------------------
//  Biased clip-space position for the shadow pass.
//  Shells extend outside the base surface, so the standard shadow bias can be
//  insufficient on its own. lightDirectionWS points from the surface TOWARD the
//  light; pushing the caster the opposite way removes shadow acne (dark,
//  patchy artifacts across the fur). Increasing _ShadowExtraBias reduces acne;
//  too high a value peels the shadow away from the surface.
// ---------------------------------------------------------------------------
float4 GetFurShadowPositionHClip(float3 positionWS, float3 normalWS, float3 lightDirectionWS)
{
    positionWS = ApplyShadowBias(positionWS, normalWS, lightDirectionWS);
    positionWS -= lightDirectionWS * _ShadowExtraBias;

    float4 positionCS = TransformWorldToHClip(positionWS);
    return ApplyShadowClamping(positionCS);
}

#endif // FUR_SHELL_COMMON_INCLUDED
