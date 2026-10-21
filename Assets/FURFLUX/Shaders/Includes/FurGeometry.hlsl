#ifndef FUR_GEOMETRY_INCLUDED
#define FUR_GEOMETRY_INCLUDED

// ---------------------------------------------------------------------------
//  Fur geometry - the one place the two fur types differ in the vertex stage.
//
//  Shells        : the source mesh, redrawn once per layer. The strand pattern is
//                  procedural and lives entirely in the fragment stage.
//  Strands       : one ribbon per hair, planted on the surface by
//                  FurStrandDistribution and skinned on the GPU. Real geometry, so
//                  it holds up in close-up and at a silhouette, and costs per hair.
//  Both share ComputeFurStrandWS for the physics, so gravity, wind, inertia,
//  collision and the whole wetness response behave identically across them. What
//  changes here is only where a vertex IS, never how it moves.
//
//  The strand type draws twice per object: once for the SKIN (the source mesh, the
//  surface the fur grows out of) and once for the FUR (a batch mesh of ribbons,
//  instanced). _FurGeometryMode says which draw this is. It is a per-draw uniform,
//  so the branch on it is coherent across the whole draw.
// ---------------------------------------------------------------------------

#include "FurShellCommon.hlsl"

#define FUR_TYPE_SHELLS  0
#define FUR_TYPE_STRANDS 1

struct FurVertexResult
{
    float3 positionWS;      // Final vertex position
    float3 rootWS;          // The surface this vertex grows from, before any shell offset.
                            // The strand pattern's cell size is measured here and nowhere
                            // else - see FurShellCommon's note on fur.rootWS.
    float3 normalWS;        // Shading normal
    float3 geoNormalWS;     // Surface normal at the root - the shadow bias needs this one
    float3 strandDirWS;     // Growth direction, doubles as the anisotropy tangent
    float3 tangentWS;       // Surface tangent (for the skin's normal map / disc billboard)
    float  bitangentSign;
    float  layerT;          // Height along the strand, 0 at the skin, 1 at the tip
    float  flatten;         // How far contact has pressed this strand down
    float2 uv0;             // Raw UV0 at the root - the strand grid and _BaseMap live here
    float2 detailUV;        // The UV every per-object map is indexed by. UV0 or UV1
                            // depending on _FurDetailUV, resolved once here so no pass
                            // downstream has to know which.
    float2 strandUV;        // x across the ribbon (0..1), y along it. Ribbon draws only.
    float2 rnd;             // Per-strand randomness
    float  alphaComp;       // Coverage compensation from the distance-thickness widening
};

// ---------------------------------------------------------------------------
//  Direction from a point toward whoever this pass is rasterising for.
//  facing.w picks how to read xyz: 1 = a position (the camera, a punctual light),
//  0 = a direction that is already the answer (a directional light).
// ---------------------------------------------------------------------------
float3 FurFacingDir(float3 positionWS, float4 facing)
{
    return facing.w > 0.5 ? SafeNormalize(facing.xyz - positionWS) : SafeNormalize(facing.xyz);
}

// ---------------------------------------------------------------------------
//  A stable axis perpendicular to the strand, used as the ribbon plane when the
//  ribbon is not billboarded. Derived from the surface tangent so neighbouring
//  strands agree with each other and with the way the coat is combed.
// ---------------------------------------------------------------------------
float3 FurPerpendicular(float3 dirWS, float3 hintWS)
{
    float3 side = hintWS - dirWS * dot(hintWS, dirWS);

    // Degenerate when the hint ended up parallel to the strand. Any perpendicular
    // will do at that point, and this one cannot itself degenerate.
    if (dot(side, side) < 1e-8)
    {
        float3 alternate = abs(dirWS.y) < 0.9 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
        side = alternate - dirWS * dot(alternate, dirWS);
    }

    return SafeNormalize(side);
}

// ---------------------------------------------------------------------------
//  Removed: the end-on width gain.
//
//  Kept as a note because it records a real cost, not a cleanup. A hair is a
//  cylinder and covers its own width from every angle; a ribbon is a flat quad, so
//  turned end-on it projects to a line. A coat seen from directly above is exactly
//  that - every hair pointing at the camera - and measured on a test sphere 51% of
//  the surface was bare skin showing between hairs that were each covering almost
//  nothing. Dividing the width by the sine of the angle to the view put that
//  coverage back.
//
//  It cannot survive a fixed ribbon plane, which is what this fur type now has (see
//  FUR_STRAND_RIBBON_BILLBOARD). The correction is derived from the ribbon's own
//  PLANE turning end-on but is measured from the STRAND, and those are the same
//  question only while the plane is being held toward the viewer. On a fixed,
//  randomly twisted plane a ribbon that happens to lie edge-on already projects to
//  a line whose screen length is its own width, and widening it twentyfold restores
//  no coverage at all - it draws a needle twenty times too long. Not too strong:
//  invalid.
//
//  So the coverage loss at grazing and top-down angles is now simply accepted, and
//  the dial that answers it is _Density. If a strand coat reads thin from above,
//  that is this, and adding hairs is the fix that stays true from every angle.
// ---------------------------------------------------------------------------

float3 FurRotateAbout(float3 v, float3 axis, float angle)
{
    float s, c;
    sincos(angle, s, c);
    return v * c + cross(axis, v) * s + axis * (dot(axis, v) * (1.0 - c));
}

// ---------------------------------------------------------------------------
//  How many world meters one screen pixel covers at this depth.
//
//  Used to keep a ribbon at least _MinPixelWidth wide on screen - the same job the
//  shell path's distance-thickness block does in the fragment stage, and it matters
//  more here: a shell strand that falls below a pixel merely dims, while a ribbon
//  below a pixel wide MISSES the pixel centre and disappears outright, which is
//  what makes untreated instanced fur sparkle at distance.
// ---------------------------------------------------------------------------
float FurWorldUnitsPerPixel(float3 positionWS)
{
    float viewDepth = max(-mul(UNITY_MATRIX_V, float4(positionWS, 1.0)).z, 1e-4);

    // P._m11 is 1/tan(fov/2) under a perspective projection, so this is the world
    // height of the frustum at this depth divided by the screen height in pixels.
    float pixelsPerUnit = max(_ScreenParams.y * abs(UNITY_MATRIX_P._m11) * 0.5, 1e-4);
    return viewDepth / pixelsPerUnit;
}

// ---------------------------------------------------------------------------
//  Skinning. Roots are stored in the SOURCE mesh's object space with their bone
//  influences, and resolved straight to world space here.
//
//  This is what keeps the hair count off the CPU entirely: the skeleton is applied
//  to a few thousand roots in the vertex shader, so a coat of a hundred thousand
//  hairs costs the CPU exactly what a coat of ten costs it - one matrix per bone.
//  What it does not carry is blend shapes - see FurStrandDistribution.
// ---------------------------------------------------------------------------
#if defined(_FURTYPE_STRANDS)
void FurAccumulateSkin(inout FurSkinMatrix accumulated, uint bone, float weight)
{
    FurSkinMatrix m = _FurSkinMatrices[bone];
    accumulated.row0 += m.row0 * weight;
    accumulated.row1 += m.row1 * weight;
    accumulated.row2 += m.row2 * weight;
}

FurSkinMatrix FurResolveSkinMatrix(FurRootData root)
{
    FurSkinMatrix skin;
    skin.row0 = 0.0;
    skin.row1 = 0.0;
    skin.row2 = 0.0;

    FurAccumulateSkin(skin, root.boneIndex.x, root.boneWeight.x);
    FurAccumulateSkin(skin, root.boneIndex.y, root.boneWeight.y);
    FurAccumulateSkin(skin, root.boneIndex.z, root.boneWeight.z);
    FurAccumulateSkin(skin, root.boneIndex.w, root.boneWeight.w);

    return skin;
}

float3 FurSkinPoint(FurSkinMatrix skin, float3 positionOS)
{
    float4 p = float4(positionOS, 1.0);
    return float3(dot(skin.row0, p), dot(skin.row1, p), dot(skin.row2, p));
}

float3 FurSkinDirection(FurSkinMatrix skin, float3 directionOS)
{
    return float3(dot(skin.row0.xyz, directionOS),
                  dot(skin.row1.xyz, directionOS),
                  dot(skin.row2.xyz, directionOS));
}
#endif

// ---------------------------------------------------------------------------
//  The shell / skin path: an object-space vertex of the source mesh.
// ---------------------------------------------------------------------------
FurVertexResult ResolveFurShellVertex(float3 positionOS, float3 normalOS, float4 tangentOS,
                                      float2 texcoord, float2 texcoord1, uint instanceID)
{
    FurVertexResult result = (FurVertexResult)0;

    float2 detailUV = ResolveFurDetailUV(texcoord, texcoord1);

    float wetness = SampleFurWetness(detailUV);

    // The tangent frame is needed BEFORE the pose is solved now, because the comb is a
    // direction in it - so it is resolved here rather than at the assignments below.
    float3 tangentWS     = TransformObjectToWorldDir(tangentOS.xyz);
    float  bitangentSign = tangentOS.w * GetOddNegativeScale();

    FurShellVertex fur = ComputeFurShell(positionOS, normalOS, instanceID,
                                         wetness, SampleFurLengthScale(detailUV),
                                         SampleFurComb(detailUV), tangentWS, bitangentSign);

    result.positionWS    = fur.positionWS;
    result.rootWS        = fur.rootWS;
    result.normalWS      = fur.normalWS;
    result.geoNormalWS   = fur.normalWS;
    result.strandDirWS   = fur.shellDirWS;
    result.layerT        = fur.layerT;
    result.flatten       = fur.flatten;
    result.uv0           = texcoord;
    result.detailUV      = detailUV;
    result.strandUV      = float2(0.5, fur.layerT);
    result.rnd           = float2(0.5, 0.5);
    result.alphaComp     = 1.0;
    result.bitangentSign = bitangentSign;
    result.tangentWS     = tangentWS;

    return result;
}

#if defined(_FURTYPE_STRANDS)
// ---------------------------------------------------------------------------
//  The ribbon path: a vertex of the batch mesh.
//
//  The batch mesh carries its addressing in POSITION rather than in extra vertex
//  streams: x is the side of the ribbon (-1..1), y the height along it (0..1) and
//  z which root inside this batch it belongs to. Nothing else is read from it, so
//  the same mesh serves every pass and no pass needs a vertex layout of its own.
// ---------------------------------------------------------------------------
FurVertexResult ResolveFurRibbonVertex(float3 positionOS, float3 normalOS, float4 facing)
{
    FurVertexResult result = (FurVertexResult)0;

    uint rootsPerBatch = (uint)max(_FurRootsPerBatch, 1.0);
    uint count         = (uint)max(_FurInstanceCount, 1.0);
    uint rootIndex     = GetFurShellIndex() * rootsPerBatch + (uint)(positionOS.z + 0.5);

    // The last batch is normally only partly used - the root count is a LOD result
    // and does not land on a batch boundary. Those slots are collapsed to a point
    // below rather than clipped in the fragment stage, so they cost no fill at all.
    bool live = rootIndex < count;
    FurRootData root = _FurRoots[min(rootIndex, count - 1u)];

    float3 rootPosOS = root.positionAndSeedX.xyz;
    float3 rootNrmOS = root.normalAndSeedY.xyz;
    float3 rootTanOS = root.tangentOS.xyz;

    float3 rootWS, normalWS, tangentWS;
    if (_FurSkinned > 0.5)
    {
        FurSkinMatrix skin = FurResolveSkinMatrix(root);
        rootWS    = FurSkinPoint(skin, rootPosOS);
        normalWS  = SafeNormalize(FurSkinDirection(skin, rootNrmOS));
        tangentWS = FurSkinDirection(skin, rootTanOS);
    }
    else
    {
        rootWS    = mul(_FurObjectToWorld, float4(rootPosOS, 1.0)).xyz;
        normalWS  = SafeNormalize(mul((float3x3)_FurObjectToWorld, rootNrmOS));
        tangentWS = mul((float3x3)_FurObjectToWorld, rootTanOS);
    }

    // Both UVs come out of the root buffer already resolved - a ribbon is planted once
    // and carries the pair from then on, so there is no vertex UV to interpolate here.
    float2 uv0      = root.uvAndDetailUV.xy;
    float2 detailUV = ResolveFurDetailUV(uv0, root.uvAndDetailUV.zw);
    float2 rnd  = float2(root.positionAndSeedX.w, root.normalAndSeedY.w);
    float  t    = saturate(positionOS.y);
    float  side = positionOS.x;

    float wetness = SampleFurWetness(detailUV);

    // --- Length ---
    //
    // A strand varies its length as geometry, because there is one ribbon per hair and a
    // short hair has to be a short ribbon.
    float lengthScale = SampleFurLengthScale(detailUV);
#if defined(_FURTYPE_STRANDS)
    lengthScale *= lerp(1.0 - saturate(_LengthRandom), 1.0, rnd.y);
#endif

    // The ribbon's own tangent, skinned along with its root - so a combed coat follows the
    // skeleton exactly as the hair it combs does.
    float ribbonBitangentSign = root.tangentOS.w * GetOddNegativeScale();
    FurShellVertex fur = ComputeFurStrandWS(rootWS, normalWS, t, wetness, lengthScale,
                                            SampleFurComb(detailUV), tangentWS, ribbonBitangentSign);

    float3 viewDirWS = FurFacingDir(fur.positionWS, facing);
    float3 strandDir = fur.shellDirWS;

    // --- The ribbon plane ---
    //
    // _StrandJitter is the randomness knob for all three types. In shell fur it offsets a
    // strand inside its cell; here there is no cell to sit inside, so it does the same
    // job on the one degree of freedom a ribbon has left - which way its plane faces.
    float3 fixedSide = FurPerpendicular(strandDir, tangentWS);
    float  twist     = rnd.x * TWO_PI * saturate(_StrandJitter);
    float3 twistedSide = FurRotateAbout(fixedSide, strandDir, twist);

    // The plane stays where the twist put it, and the flattening at grazing angles is
    // accepted. See FUR_STRAND_RIBBON_BILLBOARD: a shell's cross-section is a disc
    // resolved in UV and has to be turned toward the viewer or it stripes; a ribbon is
    // real geometry whose twist is what gives the coat its variation, and spending that
    // twist to buy roundness a lit ribbon mostly already reads as is not the same trade.
    //
    // The billboarded branch that used to stand here is gone rather than switched off,
    // and so is the end-on width gain it fed - that correction is derived FROM the
    // ribbon's plane facing the viewer, so on a fixed plane it is not merely unnecessary,
    // it is invalid. It measured the angle from the STRAND while the quad lay somewhere
    // else entirely, and an edge-on ribbon widened twentyfold is a needle, not a hair.
    float3 sideDir = SafeNormalize(twistedSide);

    // --- Width ---
    //
    // _Thickness is a fraction of the SPACING between neighbours, exactly as it is for
    // shells, where a strand's radius is measured in cells. Spacing here is the real
    // distance between roots, which the density fixes: N strands per meter leaves 1/N
    // meters between them.
    //
    // The point of expressing it this way rather than as an absolute width is that the
    // two settings stay independent. Doubling the density halves the spacing and halves
    // the strand with it, so the coat gets FINER rather than turning into a solid mat -
    // which is what happens to any parameterisation that fixes the width in meters.
    float spacing        = 1.0 / FurDensityPerMeter();
    float thicknessScale = SampleFurThicknessScale(detailUV);
    float wetThinning    = lerp(1.0, saturate(_WetThinning), wetness);

    float width = FurRibbonThickness() * spacing * thicknessScale * wetThinning
                * FurFuzzWidthGain();

#if defined(_FURTYPE_STRANDS)
    // Kept before the taper: the end-on bound below has to be ONE number for the whole
    // ribbon, and the taper drives the tip's width to zero.
    float baseWidth = width;

    // The same parabolic taper the shell pattern uses, so a strand narrows toward its tip
    // by the same curve whichever type is drawing it.
    width *= saturate(1.0 - pow(t, 1.35));

    // --- A flat ribbon standing in for a round hair ---
    //
    // A hair is a cylinder, so however you look at it, it covers its own width. A RIBBON
    // is a flat quad: turn it end-on and it projects to a line, and a coat seen from above
    // is exactly that - every hair pointing at the camera. That is why an instanced coat
    // could read as a furry silhouette around a bare body while every hair in it was being
    // drawn: measured on a test sphere, 51% of the surface was skin showing between hairs
    // that were each covering almost nothing.
    //
    // NOT for the fuzz layer, and this compounding is what made it throw rays.
    //
    // The gain exists to keep a HAIR covering what a cylinder would: a ribbon turned end-on
    // projects to a line, so its width is divided by the sine of its angle to the view -
    // up to twenty times at the clamp. The fuzz layer is not a hair, it is a blur kernel,
    // and it is already several times wider than the strand it stands for. Multiplying the
    // two gives quads a couple of hundred times the strand's width, and seen nearly end-on
    // those project as long straight bright lines: a starburst of spikes wherever hairs
    // happen to point at the camera, which on a head is everywhere.
    //
    // The gain is gone entirely now that the ribbon keeps a fixed plane: it is derived
    // from the ribbon's plane FACING the viewer, and measured off the strand instead, so
    // on an unbillboarded ribbon it widens quads by up to twenty times from an angle that
    // says nothing about how the quad actually lies. See FUR_STRAND_RIBBON_BILLBOARD.

    // --- Distance aggregation, the strand type's half of it ---
    //
    // The LOD thins a ribbon coat by drawing fewer hairs. Left alone that is exactly the
    // failure this whole mechanism exists to stop: half the hairs removed is half the
    // coverage, so the coat pales toward the skin instead of staying a coat.
    //
    // Widening what is left by 1/sqrt(fraction) puts the coverage back exactly - area
    // goes as width, and the count went down by the same factor the width goes up by its
    // square root. So the coat holds its density all the way out while its cost falls
    // linearly with the hair count, which is the entire point of thinning it.
    width *= lerp(1.0, rsqrt(max(_FurCountFraction, 1e-3)), FurDistanceFix());
#endif

    // --- Distance thickness ---
    float alphaComp = 1.0;
#if defined(_FURTYPE_STRANDS)
    {
        // The same one-pixel floor the shell pattern uses, for the same reason: a ribbon
        // narrower than a pixel does not dim, it hits some pixel centres along its length
        // and misses the others, so a hair that should read as a line renders as a dotted
        // trail of specks.
        float minWidth  = FurWorldUnitsPerPixel(fur.positionWS) * FUR_MIN_PIXEL_WIDTH;
        float maxWidth  = width * FUR_MAX_THICKNESS_GAIN;
        float wideWidth = clamp(max(width, minWidth), width, max(maxWidth, width));

        // What the widening actually cost, measured against the SPACING rather than
        // against the widening factor.
        //
        // The obvious formula - true width over widened width - is wrong once hairs are
        // closer together than a pixel, and wrong in a way that gets worse the denser the
        // coat is. It assumes the rest of the pixel is skin, so it dims the hair to make
        // room for it; but at that spacing the rest of the pixel is OTHER HAIRS, and there
        // is nothing to make room for. A coat measured on a test sphere lost a third of
        // its brightness every time its density doubled, which is the exact opposite of
        // what adding hair should do.
        //
        // Coverage against the spacing has no such assumption in it. A hair three quarters
        // as wide as the gap to its neighbour covers three quarters of the ground whatever
        // the camera is doing, and widening it only over-covers to the extent that the
        // widened hairs do not already overlap.
        float trueCoverage  = saturate(width / max(spacing, 1e-6));
        float drawnCoverage = saturate(wideWidth / max(spacing, 1e-6));

        alphaComp = drawnCoverage > 1e-6 ? saturate(trueCoverage / drawnCoverage) : 1.0;
        width     = wideWidth;
    }
#endif

    // --- Curl ---
    //
    // A ribbon is one hair, so here the coil is real displacement rather than a moved
    // pattern: the vertex swings out around the strand's own axis as it climbs it.
    //
    // The frame is the ribbon's own - twistedSide and the axis crossed with it - so the
    // coil is built in the plane the hair already has and costs no second basis. rnd.y
    // is the phase, kept off rnd.x so the coil does not correlate with the plane's twist
    // and give every hair the same profile seen from the same side.
    //
    // Radius is a fraction of the hair's own LENGTH - see FUR_CURL_RADIUS_LENGTH for why
    // that differs from the shell path's cells. lengthScale is folded in so a region the
    // length map shortened coils tighter rather than keeping a loop too big for it.
    float3 curledPositionWS = fur.positionWS;
    if (_Curl > 0.0)
    {
        float  a = FurCurlAngle(fur.layerT, rnd.y);
        float3 coilX = twistedSide;
        float3 coilY = cross(strandDir, twistedSide);
        float  reach = FurCurlProfile(fur.layerT) * FUR_CURL_RADIUS_LENGTH
                     * FurLengthMeters() * lengthScale;
        curledPositionWS += (coilX * cos(a) + coilY * sin(a)) * reach;
    }

    float halfWidth = width * 0.5 * (live ? 1.0 : 0.0);
    result.positionWS = curledPositionWS + sideDir * (side * halfWidth);
    result.rootWS     = fur.rootWS;

    // --- Shading normal ---
    //
    // The surface normal underneath, leaned outward across the ribbon. That is the same
    // per-strand deviation _StrandNormalStrength applies in shell fur, and it puts the
    // cylindrical shading cue of a round hair back without any extra geometry.
    //
    // Deliberately NOT the ribbon plane's own normal: on a billboarded strand that points
    // at the camera, which would light every hair in the coat identically.
    result.normalWS = SafeNormalize(fur.normalWS + sideDir * (side * saturate(_StrandNormalStrength)));

    result.geoNormalWS   = fur.normalWS;
    result.strandDirWS   = strandDir;
    result.tangentWS     = tangentWS;
    result.bitangentSign = root.tangentOS.w * GetOddNegativeScale();
    result.layerT        = fur.layerT;
    result.flatten       = fur.flatten;
    result.uv0           = uv0;
    result.detailUV      = detailUV;
    result.strandUV      = float2(side * 0.5 + 0.5, t);
    result.rnd           = rnd;
    result.alphaComp     = alphaComp;

    return result;
}

#endif  // strands

// ---------------------------------------------------------------------------
//  The one entry point every pass calls.
// ---------------------------------------------------------------------------
FurVertexResult ResolveFurVertex(float3 positionOS, float3 normalOS, float4 tangentOS,
                                 float2 texcoord, float2 texcoord1, uint instanceID, float4 facing)
{
    // Single exit, and the result zeroed before either branch writes it. Returning
    // straight out of the branch reads better but makes the compiler warn about a
    // potentially uninitialised return value on every ribbon variant, and a warning
    // that fires on a correct program is one nobody reads the next time.
    FurVertexResult result = (FurVertexResult)0;
    bool ribbon = false;

#if defined(_FURTYPE_STRANDS)
    ribbon = _FurGeometryMode > 0.5;
    if (ribbon)
        result = ResolveFurRibbonVertex(positionOS, normalOS, facing);
#endif

    if (!ribbon)
        result = ResolveFurShellVertex(positionOS, normalOS, tangentOS, texcoord, texcoord1, instanceID);

    return result;
}

// True for every draw of a strand COAT, the skin draw included. Distinct from
// IsFurRibbonDraw below, which is true only for the ribbon geometry itself.
bool IsFurRibbonCoat()
{
#if defined(_FURTYPE_STRANDS)
    return true;
#else
    return false;
#endif
}

// True only for the draws that are actually made of ribbons. The skin draw of a
// strand coat takes the shell path, because the skin IS the base shell.
bool IsFurRibbonDraw()
{
#if defined(_FURTYPE_STRANDS)
    return _FurGeometryMode > 0.5;
#else
    return false;
#endif
}

// ---------------------------------------------------------------------------
//  Undo _BaseMap's tiling. The strand grid is cut from the tiled UV, so anything
//  the grid hands back arrives in that space while the mesh's own unwrap is what
//  addresses the surface.
// ---------------------------------------------------------------------------
float2 FurUntileUV(float2 tiled)
{
    return (tiled - _BaseMap_ST.zw) / max(abs(_BaseMap_ST.xy), 1e-6)
         * sign(_BaseMap_ST.xy + 1e-9);
}

// ---------------------------------------------------------------------------
//  A small offset measured in UV0, re-expressed in the detail UV set.
//
//  Needed for exactly one thing, and it is worth the arithmetic. The shell pattern
//  samples the colour map at the STRAND'S OWN CENTRE rather than at the fragment's
//  UV, so a hair lying across the border of a marking comes out one colour instead
//  of half each. That centre is produced by the strand grid, which lives in UV0 -
//  and once the maps move to a second UV set there is no direct way to ask where
//  that centre is in the new one. A cell centre is not a vertex, so it has no
//  authored UV of its own in any set.
//
//  What both sets DO have here is their screen-space derivatives, and those give
//  the local map between them exactly. Find the step across the screen that moves
//  UV0 by the offset, then read off where that same step lands in the detail set.
//  The offset is at most half a cell, which is far inside the range a first-order
//  map is exact enough for.
//
//  Degenerate where UV0 stops varying across the pixel quad - a fully collapsed
//  unwrap, or a triangle seen exactly edge-on. Returning no offset there falls back
//  to the fragment's own detail UV, which is the pre-existing behaviour for a coat
//  with no colour map worth speaking of.
// ---------------------------------------------------------------------------
float2 FurUV0OffsetInDetailUV(float2 offsetUV0, float2 uv0, float2 detailUV)
{
    float2 du0 = ddx(uv0),      dv0 = ddy(uv0);
    float2 duD = ddx(detailUV), dvD = ddy(detailUV);

    float det = du0.x * dv0.y - du0.y * dv0.x;
    if (abs(det) < 1e-12)
        return float2(0.0, 0.0);

    float2 step = float2( dv0.y * offsetUV0.x - dv0.x * offsetUV0.y,
                         -du0.y * offsetUV0.x + du0.x * offsetUV0.y) / det;

    return duD * step.x + dvD * step.y;
}

// ---------------------------------------------------------------------------
//  Coverage, per fragment.
//
//  Shells resolve a procedural strand pattern in UV space; a ribbon already IS the
//  strand, so all that is left is its own analytic edge.
// ---------------------------------------------------------------------------
FurStrandSample SampleFurCoverage(float2 tiledUV, float2 detailUV, float3 rootPositionWS, float layerT,
                                  float3 viewDirTS, float wetness, float flatten,
                                  float2 strandUV, float2 rnd, float alphaComp)
{
    FurStrandSample result = (FurStrandSample)0;
    bool ribbon = false;

#if defined(_FURTYPE_STRANDS)
    ribbon = _FurGeometryMode > 0.5;
#endif

#if defined(_FURTYPE_STRANDS)
    if (ribbon)
    {
        result.rnd       = rnd;
        result.strandLen = 1.0;
        result.lodBlend  = 0.0;
        result.footprint = 0.0;

        // A ribbon carries its root's UV along its whole length already - it comes from the
        // root buffer and does not vary from root to tip - so this is simply it. Nothing to
        // re-express here: the root was planted with both sets and this IS the detail one.
        result.rootUV    = detailUV;

        // A strand ribbon IS one hair, so all that is left is its own edge. Antialiased
        // against its own screen-space derivative, so a hair widened to the minimum pixel
        // width still fades at its edges instead of turning into a hard bar.
        // The widening compensation is deliberately NOT folded into alpha here. Alpha
        // decides whether the fragment is drawn at all, and rolling a die on it is the
        // wrong answer for a hair thinner than a pixel: what comes out is a hair that is
        // either fully bright or entirely absent, which reads as sparkle over a bare body
        // rather than as fur. It is handed to the shading instead, as an opacity.
        // The SAME signed-distance form the shell pattern uses, including the 0.5 offset -
        // and that offset is the whole of it, not a detail.
        //
        // Without it the edge falls to zero AT the edge, so the value never reaches 1
        // anywhere on a ribbon thinner than a pixel: the antialiasing width is then wider
        // than the ribbon itself, and the alpha cutout eats everything but a sliver down
        // the middle. A hair a pixel wide ended up drawn a third of a pixel wide, which is
        // most of a coat thrown away for the sake of a soft edge nobody can see at that
        // size.
        float across = abs(strandUV.x * 2.0 - 1.0);
        float aa     = max(fwidth(across), 1e-4);
        result.alpha   = saturate(0.5 + (1.0 - across) / (2.0 * aa));
        result.opacity = alphaComp;

        // The fuzz layer trades that edge for a falloff - see FurFuzzProfile. strandUV.y
        // runs 0 at the root to 1 at the tip, which is the fade that stops the widened
        // ribbons from reading as spikes past the silhouette.
        if (IsFurFuzzDraw())
        {
            result.alpha   = FurFuzzProfile(across, strandUV.y);
            result.opacity = 1.0;
        }
    }
#endif

    if (!ribbon)
    {
        // Strands per meter, converted into cells per UV unit for the grid this pattern
        // is laid out on. That conversion is the whole of the fix for strand size
        // following the unwrap - see FurMetersPerUV.
        // Measured at the ROOT, not at this shell's own height - see FurShellVertex.rootWS.
        // Every layer then divides the surface into the same cells and hashes to the same
        // strand, which is what makes a stack of shells read as hair instead of as speckle.
        result = SampleFurStrand(tiledUV, FurDensityPerMeter() * FurMetersPerUV(rootPositionWS, tiledUV),
                                 layerT, viewDirTS, wetness, flatten,
                                 SampleFurThicknessScale(detailUV) * FurFuzzWidthGain());

        // The cell centre, back out of the tiled grid space and into the set the maps are
        // addressed by.
        //
        // Untiling alone answers it whenever the maps live in UV0, and that path is left
        // arithmetically identical to what it always was. On the detail set there is one
        // more step: the centre has to be carried across, which is what
        // FurUV0OffsetInDetailUV is for. The branch is on a per-draw uniform, so a coat
        // that never leaves UV0 does not pay for the transform.
        float2 rootUV0 = FurUntileUV(result.rootUV);

        if (_FurDetailUV > 0.5)
        {
            float2 fragUV0 = FurUntileUV(tiledUV);
            result.rootUV  = detailUV
                           + FurUV0OffsetInDetailUV(rootUV0 - fragUV0, fragUV0, detailUV);
        }
        else
        {
            result.rootUV = rootUV0;
        }
    }

    // The root and tip colours' alpha, resolved along the strand. One multiply here rather
    // than one per path, so the shell pattern and the ribbon edge get it on the same terms
    // and every pass that calls this function agrees about which tips exist.
    result.alpha *= FurHeightAlpha(layerT);

    return result;
}

// ---------------------------------------------------------------------------
//  The clip test.
//
//  Ribbons always run it - unlike a shell stack, a ribbon draw has no base layer that
//  is really the skin, so there is nothing to exempt.
// ---------------------------------------------------------------------------
void FurCoverageClip(FurStrandSample s, float layerT, float2 positionSS)
{
#if defined(_FURTYPE_STRANDS)
    if (_FurGeometryMode > 0.5)
    {
        // The ribbon's own edge, and nothing else.
        //
        // This used to test against a screen-space dither, on the reasoning that a hair
        // covering a third of a pixel should be kept a third of the time. Two things were
        // wrong with it. The noise is per PIXEL, so every hair landing on a pixel got the
        // same threshold and overlapping hairs stopped accumulating - twenty-six thousand
        // hairs and four hundred thousand measured the same coverage. And even decorrelated
        // per hair it answers the wrong question: a hair thinner than a pixel is not
        // sometimes-there, it is always there and DIMMER, and drawing it at full brightness
        // a third of the time reads as sparkle over a bare body.
        //
        // What that partial coverage actually means is handled where it belongs, in the
        // shading, as an opacity blended toward the skin the hair is standing on.
        // The fuzz layer is blended, not cut. Testing it against the coat's cutout would
        // throw away exactly the faint coverage it exists to add.
        if (s.alpha < (IsFurFuzzDraw() ? FUR_FUZZ_CLIP : FurAlphaCutout()))
            discard;
        return;
    }
#endif

    FurAlphaClip(s, layerT, positionSS);
}

#endif // FUR_GEOMETRY_INCLUDED
