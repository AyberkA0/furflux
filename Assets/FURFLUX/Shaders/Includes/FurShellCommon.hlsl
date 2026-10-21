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
#include "FurShellCollision.hlsl"
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
//  Curl.
//
//  A hair coils around its own growth axis, so the curl is a rotation carried
//  ALONG the strand rather than a displacement of it: the angle advances with
//  height, and the radius says how far out the coil reaches.
//
//  Both fur types use these two, and then part company on how they spend the
//  answer - a shell coat has no per-hair geometry to bend, so it moves the
//  strand PATTERN, while a ribbon is the hair and moves in world space. What
//  the two share is the phase and the profile, which is what makes the same
//  slider produce the same coat on either.
// ---------------------------------------------------------------------------

/// Where this hair is in its coil at this height. rnd gives each hair its own
/// starting angle - without it every hair in a cell coils in lockstep and the
/// coat ripples like a field of wheat instead of tangling.
float FurCurlAngle(float layerT, float phase01)
{
    return (layerT * FUR_CURL_TURNS + phase01) * TWO_PI;
}

/// The coil's reach at this height, as a 0..1 profile the caller scales by
/// whatever its own type measures a coil against.
///
/// Zero at the root and full by a third of the way up, rather than growing all
/// the way to the tip. A radius proportional to height is a CONE, which reads as
/// a hair that got wavier toward the end; a real curl is a cylinder with its
/// bottom tucked in, and the follicle is what tucks it - a hair leaves the skin
/// where it was planted however tightly it coils above.
float FurCurlProfile(float layerT)
{
    return saturate(_Curl) * saturate(layerT * 3.0);
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
    float3 rootWS;          // Where the strand GROWS FROM, before any shell offset
    float  layerT;          // Normalized shell height, 0..1
    float  flatten;         // 0 = strand standing free, 1 = pressed flat against the skin
};

// ---------------------------------------------------------------------------
//  Caps how far a strand may lean away from the surface it grows out of.
//
//  The bend is built as normalize(normal + force * bend), which quietly assumes
//  the force is small next to the unit normal. It is not: gravity, wind and the
//  inertia force each reach several units on their own, and together they can
//  swamp the normal completely. Past 90 degrees the normalised result points
//  BACK INTO the mesh, so the shells invert - they stack inward from the skin
//  instead of outward, z-fight the body and render in the wrong order. It looks
//  like the fur is being sucked into the model, and no amount of tuning the
//  individual force sliders prevents it, because the failure is in the sum.
//
//  Clamping the angle rather than the force keeps the artistic response of every
//  slider intact right up to the limit, and only bounds the geometry.
// ---------------------------------------------------------------------------
float3 FurClampBend(float3 dirWS, float3 normalWS, float cosMaxBend)
{
    float cosBend = dot(dirWS, normalWS);
    if (cosBend >= cosMaxBend)
        return dirWS;

    // Keep the direction the strand was leaning, just not that far over.
    float3 tangent       = dirWS - normalWS * cosBend;
    float  tangentLength = length(tangent);

    // Degenerate only when the force drove the strand exactly antiparallel to the
    // normal, leaving no lean to preserve. Standing it back up is the one answer
    // that cannot be inside the mesh.
    if (tangentLength < 1e-5)
        return normalWS;

    float sinMaxBend = sqrt(saturate(1.0 - cosMaxBend * cosMaxBend));
    return normalWS * cosMaxBend + (tangent / tangentLength) * sinMaxBend;
}

// ---------------------------------------------------------------------------
//  How much airflow actually reaches this piece of surface.
//
//  Wind used to be applied at full strength to every vertex, including the ones
//  facing directly away from it. A real body shelters its own lee side: the
//  windward fur takes the push, the far side sits in the wake. Without this the
//  whole coat leans in lockstep, which is the giveaway that the wind is a uniform
//  rather than a flow.
//
//  Not a hard cut-off - the lee side is sheltered, not still. _WindShelter is what
//  it keeps, and turbulence in a real wake means that is never zero.
// ---------------------------------------------------------------------------
float FurAirflowExposure(float3 normalWS, float3 flowForce)
{
    float magnitude = length(flowForce);
    if (magnitude < 1e-5)
        return 1.0;

    // flowForce pushes downwind, so the surfaces turned INTO the flow are the ones
    // whose normal opposes it.
    float facing = saturate(dot(normalWS, -flowForce / magnitude));
    return lerp(saturate(_WindShelter), 1.0, facing);
}

// ---------------------------------------------------------------------------
//  Force contributed by bones moving independently of the body.
//
//  The transform terms describe the creature as a whole: where it travelled and how
//  it turned. They are blind to the skeleton. A squirrel standing still and flicking
//  its tail moves no transform at all, so root velocity and root angular velocity are
//  both zero and the tail's fur is told nothing is happening - while the tail sweeps
//  through the air.
//
//  Each bone's force was measured in the BODY's local space, so being carried along
//  by a running creature contributes nothing and cannot double up with the transform
//  terms. What survives is only what the skeleton adds on top.
//
//  Falloff is squared so a bone's influence fades out smoothly rather than ending on
//  a visible ring.
// ---------------------------------------------------------------------------
float3 FurBoneMotionForce(float3 positionWS)
{
    int count = min((int)_BoneMotionCount, FUR_MAX_MOTION_BONES);
    if (count <= 0)
        return float3(0.0, 0.0, 0.0);

    float3 total = float3(0.0, 0.0, 0.0);

    for (int i = 0; i < count; i++)
    {
        float reach = _BoneMotionOrigin[i].w;
        float weight = saturate(1.0 - distance(positionWS, _BoneMotionOrigin[i].xyz) / max(reach, 1e-4));
        total += _BoneMotionForce[i].xyz * (weight * weight);
    }

    return total;
}

// ---------------------------------------------------------------------------
//  Chord shortening for a bent strand.
//
//  A strand keeps its ARC length when it bends; what shrinks is the straight line
//  from root to tip. The shell model places each shell at a fixed distance along
//  its own direction, so a strand bent hard by gravity or wind kept the full
//  distance and effectively STRETCHED - heavily bent fur read as too long.
//
//  Modelling the strand as a circular arc through the deflection angle gives the
//  chord as arc * sinc(theta/2), which is exact for that shape and costs one
//  inverse cosine.
// ---------------------------------------------------------------------------
// ---------------------------------------------------------------------------
//  The comb, taken from the surface's tangent frame out into the world.
//
//  What is stored per texel is a 2D direction lying IN the surface - which way the hair
//  has been brushed, and how hard. Turning it into a world vector is just the tangent
//  frame applied, and doing it here rather than at paint time is the whole reason a comb
//  survives animation: the frame follows the skin, so a stroke down an animal's back goes
//  on running down its back however the animal moves.
//
//  The bitangent sign matters as much here as it does for the disc billboard. Mirrored
//  UVs are the norm on anything symmetrical, and without the handedness the comb would
//  run backwards down one whole side of the model - which reads as a bug in the brush
//  rather than as a frame error, because the stroke that made it was perfectly ordinary.
//
//  Re-orthogonalised against the normal, because an interpolated tangent is no longer
//  perpendicular to it: left alone, the comb would carry a small component along the
//  normal and quietly lengthen or shorten the fur it combed.
// ---------------------------------------------------------------------------
float3 ApplyFurComb(float2 combTS, float3 normalWS, float3 tangentWS, float bitangentSign)
{
    // A uniform branch on the map's enable flag has already zeroed this for a coat with no
    // comb, so the early out costs one compare and saves the frame construction.
    if (dot(combTS, combTS) < 1e-8)
        return float3(0.0, 0.0, 0.0);

    float3 t = tangentWS - normalWS * dot(normalWS, tangentWS);
    if (dot(t, t) < 1e-10)
        return float3(0.0, 0.0, 0.0);

    t = SafeNormalize(t);
    float3 b = bitangentSign * cross(normalWS, t);

    return (t * combTS.x + b * combTS.y) * max(_CombStrength, 0.0);
}

float FurChordScale(float3 strandDirWS, float3 normalWS)
{
    float halfAngle = acos(clamp(dot(strandDirWS, normalWS), -1.0, 1.0)) * 0.5;
    return halfAngle > 1e-4 ? sin(halfAngle) / halfAngle : 1.0;
}

// `wetness` is the RESOLVED value at this vertex (see SampleFurWetness). It is
// passed in rather than sampled here so the fragment stage can reuse the exact
// same value, and so the caller controls which UV set it comes from.
//
// `lengthScale` is the detail-map multiplier on _FurLength (see
// SampleFurLengthScale), passed in for the same reason: this function never sees
// a UV, and the per-pass call sites are the only place that knows which
// interpolator carries raw UV0.
// Takes the root already in WORLD space and an explicit height along the strand,
// rather than an object-space vertex and a shell index.
//
// That split is what lets the three fur types share one piece of physics. A shell
// is a copy of the whole surface lifted to a fixed height, so its height comes from
// the instance index; a strand ribbon carries its height in the VERTEX, and
// its root has already been through GPU skinning by the time it gets here. Both
// arrive at the same question - where is the strand at height t - and both must get
// the same answer, or a coat would change shape when its type changed.
//
// `combTS` is the painted lie of the hair at this root, in the surface's own tangent
// frame, and `tangentWS` / `bitangentSign` are that frame - see ApplyFurComb.
FurShellVertex ComputeFurStrandWS(float3 rootPositionWS, float3 rootNormalWS, float layerT,
                                  float wetness, float lengthScale,
                                  float2 combTS, float3 tangentWS, float bitangentSign)
{
    FurShellVertex fur;

    fur.layerT     = layerT;
    fur.positionWS = rootPositionWS;
    fur.rootWS     = rootPositionWS;
    fur.normalWS   = rootNormalWS;

    // Stiffness curve: strands near the root barely bend, tips are free to move.
    //
    // The wet response is a multiplier so it keeps tracking _Stiffness when that is
    // driven at runtime - see the note on the declaration. The CLAMP is what makes
    // that safe: unbounded, a stiffness of 6 x 2.5 landed at 15 and a gravity of
    // 3 x 2.8 at 8.4, values the material could neither express nor preview, so the
    // wet pose was impossible to reason about or reproduce by hand.
    //
    // Note the clamp costs headroom at the top of the range: with _Stiffness already
    // at its maximum there is nowhere for a multiplier above 1 to go, and wetness
    // stops changing the curve. That is the honest trade - a bounded value that does
    // nothing at the extreme, rather than an unbounded one that does something
    // nobody can see or author.
    float stiffnessAdjusted = min(lerp(_Stiffness, _Stiffness * _StiffnessMultiplierOnWet, wetness),
                                  FUR_STIFFNESS_MAX);
    float bend = pow(saturate(fur.layerT), max(stiffnessAdjusted, 0.01));

    // Wind: two sine octaves plus a position-dependent phase shift for a travelling-wave feel.
    float3 windDir = SafeNormalize(_WindDirection.xyz);
    float  phase   = _Time.y * _WindFrequency + dot(fur.positionWS, float3(1.0, 0.7, 1.3)) * _WindTurbulence;
    float  gust    = sin(phase) * 0.6 + sin(phase * 1.7 + 1.3) * 0.4;   // -1 .. 1

    // Wind has to have a POSITIVE MEAN. The two sines alone average to zero, so the
    // fur oscillated about its rest pose and spent half of every cycle bending
    // INTO the wind - it read as a shiver, not as being blown. Real wind lays a coat
    // over and gusts modulate around that steady lean.
    //
    // Clamped at zero so a strong gust setting can drop to a lull without the flow
    // reversing, which would be a different wind rather than a weaker one.
    float strength = max(1.0 + gust * saturate(_WindGustiness), 0.0);

    // Soaked fur does not blow about. Water binds the strands into heavy tufts with
    // a fraction of the surface area, so the same breeze barely moves them - and a
    // wet creature whose fur still ripples like dry fur is one of the tells that
    // reads as wrong immediately, even when everything else about it is right.
    float3 wind = windDir * (strength * _WindStrength * lerp(1.0, saturate(_WetWindResponse), wetness));
    wind *= FurAirflowExposure(fur.normalWS, wind);

    // Gravity (world -Y) + wind + the inertia force pushed in from FurController.
    // Wet fur is heavier: water weight makes strands sag more downward.
    float gravityAdjusted = min(lerp(_Gravity, _Gravity * _GravityMultiplierOnWet, wetness),
                                FUR_GRAVITY_MAX);

    // The rotational term has to be evaluated HERE, per vertex, rather than folded into
    // a single force on the CPU: cross(axis, r) points a different way for every point on
    // the body, which is exactly what a turn does to fur. Vertices on the rotation axis
    // get a zero lever arm and correctly stay put, and the effect grows with distance
    // from it - so a roll now sweeps the flanks instead of doing nothing at all.
    float3 leverArm = fur.positionWS - _RotationCenterWS.xyz;
    float3 rotationForce = cross(_AngularForce.xyz, leverArm);

    // Drag is airflow, so the object shelters its own lee side from it exactly as it
    // does from wind. _ExternalForce is NOT sheltered: it carries the inertial
    // pseudo-force and any scripted push, and those act on every strand alike
    // regardless of which way it faces - they are body forces, like gravity.
    float3 drag = _DragForce.xyz;
    drag *= FurAirflowExposure(fur.normalWS, drag);

    float3 force = float3(0.0, -gravityAdjusted, 0.0) + wind + drag
                 + _ExternalForce.xyz + rotationForce
                 + FurBoneMotionForce(fur.positionWS);

    // --- Free (uncollided) strand shape ---
    float3 rootWS  = fur.positionWS;

    // Wet fur is visibly SHORTER. Strands slick down against each other and against
    // the skin, so the coat loses height - a soaked animal looks smaller. Sagging
    // from the heavier gravity alone does not produce that, because bending here
    // keeps the strand's full length; the loft has to be taken out explicitly.
    //
    // The length map multiplies in HERE, at the strand's own height, rather than
    // being folded into the shell spacing. Every shell of a given vertex therefore
    // shortens by the same factor and the layers stay evenly distributed through
    // the coat - scaling the spacing instead would have bunched them at the root.
    //
    // This vertex's own height is deliberately NOT folded in here. Everything between this
    // line and the chord scale below is a multiplication by a scalar that does not depend
    // on the length, so taking layerT out to the end is exactly the same arithmetic.
    float  strandLength = FurLengthMeters() * lengthScale
                        * lerp(1.0, saturate(_WetLengthScale), wetness);

    // --- LOD fade ---
    //
    // At one shell the fur is simply gone, in a single frame, from full height to bare
    // skin. Shrinking it to nothing first turns that step into a fade.
    //
    // _LodFade comes from the CPU, where the LOD already knows how tall the fur is IN
    // PIXELS - which is the only measure of whether shortening it is invisible. Deriving
    // it from the shell count instead was wrong: a low count means the fur is cheap, not
    // that it is small, so mid-distance coats were being shortened while still large on
    // screen. That left the body bald with a dark fringe of stubs around the silhouette.
    strandLength *= _LodFade;

    // The fuzz layer stands taller than the coat it covers - that reach past the
    // silhouette is the soft edge a clipped coat cannot have.
    strandLength *= FurFuzzLengthGain();

    // No lean cap: FUR_MAX_BEND_COS is zero, so the clamp below keeps only the
    // invariant that earns its cost - a strand may lie down flat, but never bends
    // below the surface it grows from, which would put shells inside the mesh.
    float cosMaxBend = FUR_MAX_BEND_COS;

    // The comb goes in with the forces rather than after them, and that is deliberate: a
    // combed coat should still answer gravity and wind, just from a different resting
    // pose. Applying it afterwards would have made a combed region rigid - the one thing
    // a coat that has been brushed is not.
    //
    // It shares the same `bend` curve too, so the root stays planted and the lean grows
    // toward the tip. That is what a comb does to real hair: it does not rotate the follicle.
    // Only the part of the force that is PERPENDICULAR to the surface steers the strand,
    // and dropping the rest is not a refinement - it is what stops the pose degenerating.
    //
    // A body force turns a hair through the torque it exerts, and a force along the hair's
    // own axis exerts none: it compresses or stretches, it does not steer. The additive
    // pose below has no way to know that, so the axial part used to be folded straight into
    // the direction, where it scales the normal term instead of leaning it.
    //
    // On most of a body that is harmless, because the axial part is small. Where the
    // surface faces straight INTO the force it is fatal. Take the top of a body under
    // gravity: the normal is +Y, the force is -Y, and the vector being normalised is
    // (0, 1 - g*bend, 0). As g*bend approaches one that collapses toward ZERO, and
    // normalising a vector that has lost its magnitude leaves the direction to whatever
    // rounding and the per-strand comb happen to contribute - so every hair in the cap
    // picks a different arbitrary direction. Past one it flips outright and the strand
    // points into the mesh. What you see is a starburst of splayed hairs at the crown,
    // sitting inside a coat that is combed smoothly flat everywhere else, and it tracks
    // the FORCE rather than the model - rotating the mesh does not move it.
    //
    // Projected out, the normal component of the sum is exactly 1 whatever the force does.
    // The vector can no longer collapse or invert, the bend angle is atan(|Ft| * bend) and
    // therefore always under ninety degrees, and the strength falls off as the sine of the
    // angle between surface and force - so the crown stands up and its neighbours lean
    // progressively further, which is what a real whorl looks like.
    //
    // The equator is untouched: there the force is already perpendicular to the normal and
    // the projection removes nothing. The pose only changes where it was wrong.
    //
    // The comb is already tangential by construction (see ApplyFurComb), so it does not
    // go through the projection - projecting it would be a no-op that only cost
    // instructions.
    //
    // --- Why the comb builds a REST POSE instead of joining the sum ---
    //
    // It used to be added straight into the tangential force, and the comment here said
    // that was what let a combed coat still answer gravity and wind. It was not. The lean
    // angle is atan(|tangential| * bend), and an arctangent saturates: at a comb strength
    // of 4 the coat already sits at 76 degrees, and a wind of 0.25 on top of it moves the
    // strand by less than one degree. The same wind on an UNCOMBED strand moves it
    // fourteen. So combing a region did not give it a different resting pose to blow
    // about from, it made it rigid - the exact failure the old comment claimed to avoid.
    //
    // Combing therefore happens FIRST and on its own, producing the direction the hair
    // rests in. The forces then bend the strand away from THAT, through the same
    // arithmetic and with the same projection that protects it: perpendicular to the rest
    // direction rather than to the surface normal, so the vector being normalised keeps a
    // component of exactly 1 along it and can neither collapse nor invert.
    //
    // The result is that wind, gravity and inertia turn a combed hair by the same angle
    // they turn an uncombed one. A brushed coat ripples; it just ripples from where the
    // brush left it.
    float3 combTangent = ApplyFurComb(combTS, fur.normalWS, tangentWS, bitangentSign);
    float3 restDirWS   = SafeNormalize(fur.normalWS + combTangent * bend);

    float3 steering = force - restDirWS * dot(force, restDirWS);

    fur.shellDirWS = SafeNormalize(restDirWS + steering * bend);
    fur.shellDirWS = FurClampBend(fur.shellDirWS, fur.normalWS, cosMaxBend);

    // Bending curves the strand, and a curve covers less straight-line distance than
    // the same length of strand held straight. Without this the shell sits at its
    // full length along the bent direction, so hard-bent fur silently grew.
    strandLength *= FurChordScale(fur.shellDirWS, fur.normalWS);

    // This vertex's share of the strand: the physics below places the tip at the share.
    //
    // The whole strand's length used to be carried out of here alongside it, for the
    // ribbon width bound in the end-on gain. That gain is gone with the ribbon billboard
    // it depended on, and nothing else ever needed to measure a strand against itself.
    strandLength *= fur.layerT;

    // --- Collision ---
    // Resolved here, AFTER the strand has taken its natural shape, and per vertex
    // against the real collider surfaces. Contact is a geometric fact about where
    // this particular strand tip is, not a force to be blended into the pose, so
    // it cannot be expressed as another term in `force` above: two colliders on
    // opposite sides would cancel, and no force value prevents penetration.
    float3 tipWS = ResolveFurCollision(rootWS, rootWS + fur.shellDirWS * strandLength,
                                       strandLength, fur.shellDirWS);

    // Contact may legitimately press a strand flat against the surface, but never
    // through it. A collider that overlaps the body itself would otherwise drive
    // tips out the far side and put shells inside the mesh - the same inversion
    // the bend clamp above prevents, arriving by a different route. Removing only
    // the inward component leaves the sideways comb untouched.
    float alongNormal = dot(tipWS - rootWS, fur.normalWS);
    if (alongNormal < 0.0)
        tipWS -= fur.normalWS * alongNormal;

    fur.positionWS = tipWS;

    // The base surface, kept alongside the offset one.
    //
    // The strand pattern's cell size has to be measured on the surface the fur grows out
    // of, never on the shell being drawn. An offset shell is a bigger surface than the one
    // under it - by (1 + h*k1)(1 + h*k2) for curvature k - so measuring there gives every
    // shell a slightly different cell size. "Slightly" is enough: the grid index is
    // uv * densityCells with densityCells in the hundreds, so a one percent change moves
    // the index by most of a cell and each layer hashes to a DIFFERENT strand. The stack
    // stops lining up into hairs and reads as randomly speckled layers, which is exactly
    // what shell fur stopped looking like when the cell size was made per-fragment.
    fur.rootWS = rootWS;

    // How far the strand lost along its OWN growth direction, which is what makes
    // a strand read as pressed down. Measuring the chord length instead would be
    // wrong: combing a strand over sideways keeps its length intact, so the chord
    // barely changes even though the strand is lying flat.
    fur.flatten = 0.0;
    if (strandLength > 1e-6)
    {
        // max() rather than relying on the branch: the compiler flattens this into
        // a select and evaluates both sides, so the base shell's strandLength of 0
        // would produce a 0/0 NaN before the select ever discarded it.
        fur.flatten = saturate(1.0 - dot(tipWS - rootWS, fur.shellDirWS) / max(strandLength, 1e-6));

        // Re-derive the strand direction from the resolved tip. It doubles as the
        // anisotropy tangent for the Kajiya-Kay sheen, so leaving it pointing along
        // the pre-collision pose would light flattened fur as though it were still
        // standing up.
        fur.shellDirWS = SafeNormalize(tipWS - rootWS);
    }

    return fur;
}

// ---------------------------------------------------------------------------
//  Shell entry point: an object-space vertex lifted to the height its INSTANCE
//  stands for. Everything past the first two lines is shared with the strand and
//  strand type.
// ---------------------------------------------------------------------------
FurShellVertex ComputeFurShell(float3 positionOS, float3 normalOS, uint shellIndex,
                               float wetness, float lengthScale,
                               float2 combTS, float3 tangentWS, float bitangentSign)
{
    return ComputeFurStrandWS(TransformObjectToWorld(positionOS),
                              TransformObjectToWorldNormal(normalOS),
                              GetFurLayerT(shellIndex), wetness, lengthScale,
                              combTS, tangentWS, bitangentSign);
}

// ---------------------------------------------------------------------------
//  Strand sampling. The UV plane is split into cells, one strand per cell, and
//  the strand's coverage at this shell height is resolved analytically.
//  alpha carries that coverage, which the clip test reads.
// ---------------------------------------------------------------------------
struct FurStrandSample
{
    float  alpha;       // Coverage value used by the alpha-clip test
    float  strandLen;   // This strand's normalized length (0..1)
    float2 rnd;         // Per-strand randomness (normal deviation / color variation)
    float  lodBlend;    // 0 = per-strand coverage, 1 = regional average (see FurAlphaClip)
    float  footprint;   // Pixel footprint in cell space (for distance filtering)
    float  meanCoverage;// Analytic average coverage of this strand over its own cell
    float  aggregate;   // 0 = strands resolved individually, 1 = fully aggregated
    float  opacity;     // How much of its pixel a widened ribbon really covers (ribbons only)
    float2 rootUV;      // UV of the point this strand GROWS FROM - see SampleFurCoverage
    float  dither;      // 0..1 clip threshold for distant fur, fixed to this strand and layer
};

// ---------------------------------------------------------------------------
//  Distance aggregation - why distant fur disappears, and what to do about it.
//
//  A pixel that covers several strands has ONE sample to spend on them. Point-sampling
//  the pattern there answers "is the centre of this pixel inside a strand", and the
//  honest answer to that is no most of the time - so the alpha test throws the fragment
//  away, and it throws away a different fragment every frame as the camera moves. The
//  coat does not gently fade at distance; it develops holes, and then it is gone, while
//  every strand that made it up is still being drawn. Widening the strands (distance
//  thickness) does not fix it, because the problem is not that the strands are too thin -
//  it is that a binary test is the wrong question once a pixel contains many of them.
//
//  The right answer at that scale is the EXPECTED value over the pixel: how much of it
//  the fur covers on average, and what colour it averages to. That is a number this
//  shader can compute analytically - the strand's mean coverage over its own cell - so
//  the fix is to blend toward it rather than to keep rolling the dice.
//
//  Two things follow from that, and they happen together:
//    * The layers above the base stop being sampled and fade out, because at that
//      distance they carry no information a pixel can hold. They also stop costing fill.
//    * The BASE layer takes over the coat's appearance - its average tint and its
//      average self-shadowing - so what remains on screen is a fur-coloured object
//      rather than the bare skin the fur was hiding. That is the step that used to be
//      visible as the fur "disappearing": the coat was not fading out, it was being
//      replaced by a differently coloured surface underneath.
//
//  Driven from two measurements, no authored distances anywhere:
//    _FurAggregation - the CPU's view: how much height and how many layers the LOD has
//                      already taken away. Known exactly, because the LOD decided it.
//    footprint       - the fragment's view: how many strand cells fall inside this pixel.
//                      Above one, the pattern is past what the pixel can resolve.
// ---------------------------------------------------------------------------

/// The CPU term alone. Used where a footprint would be meaningless - the base layer of a
/// ribbon coat has a strand grid that nothing is drawn from.
float FurBaseAggregation()
{
    return saturate(_FurAggregation) * FurDistanceFix();
}

/// The full measure, for anywhere the strand pattern is actually being resolved.
///
/// Ramped over four cells per pixel rather than switched at one. A footprint just past a
/// pixel is still mostly resolvable, and hurrying the aggregation there costs the coat
/// the wispy tips at its silhouette - those have a low mean coverage by construction, so
/// converging to the mean is exactly what removes them.
float FurResolveAggregation(float footprint)
{
    float unresolved = saturate((footprint - 1.0) / 3.0);
    return max(FurBaseAggregation(), unresolved * FurDistanceFix());
}

// ---------------------------------------------------------------------------
//  View direction expressed in tangent (UV) space.
//  x,y: the view vector's projection onto the surface, z: N·V.
//  Used by the disc-billboard correction below. Computed once per vertex and
//  interpolated.
// ---------------------------------------------------------------------------
// `bitangentSign` is tangentOS.w * GetOddNegativeScale(). Passing it in matters:
// the lit pass builds its tangent frame WITH the handedness sign, so omitting it
// here gave the disc billboard a bitangent pointing the opposite way wherever the
// UVs are mirrored - and mirrored UVs are the norm on anything symmetrical, which
// is most creatures. The correction then leaned the discs the wrong way down one
// whole side of the model.
float3 GetFurDirTS(float3 dirWS, float3 normalWS, float3 tangentWS, float bitangentSign)
{
    float3 bitangentWS = bitangentSign * cross(normalWS, tangentWS);

    return float3(dot(tangentWS, dirWS),
                  dot(bitangentWS, dirWS),
                  dot(normalWS, dirWS));
}

float3 GetFurViewDirTS(float3 positionWS, float3 normalWS, float3 tangentWS, float bitangentSign)
{
    return GetFurDirTS(SafeNormalize(GetCameraPositionWS() - positionWS),
                       normalWS, tangentWS, bitangentSign);
}

// ---------------------------------------------------------------------------
//  `wetness` is the RESOLVED value at this fragment (see SampleFurWetness), so
//  clumping varies across the surface: a paw that stepped in water compacts
//  while the back stays dry.
//
//  `flatten` is how far this strand has been pressed down (0 free, 1 flat).
//
//  `thicknessScale` is the detail-map multiplier on _Thickness (see
//  SampleFurThicknessScale). Passed in rather than sampled here because this
//  function receives the _BaseMap-TILED uv, and the map is indexed by raw UV0.
//
//  Why flatten has to reach coverage and not just geometry
//  ------------------------------------------------------
//  A strand's footprint here is a disc in UV space - it models the strand seen
//  END-ON, which is correct while it stands up. Press it flat and you are
//  looking along its LENGTH instead, so the same strand covers far more surface.
//  Without that, pressed fur keeps its standing-up coverage while its shells
//  collapse into one plane; every shell then samples the same UV pattern at
//  almost the same place, the gaps between strands line up instead of covering
//  for each other, and the skin shows through. That is the "contact patch goes
//  bald" look - the strands never left, their coverage just never grew to match
//  the fact that they are now lying down.
// ---------------------------------------------------------------------------
// ---------------------------------------------------------------------------
//  How many meters of surface one UV unit covers, here.
//
//  This is what makes a strand a fixed SIZE rather than a fixed fraction of the unwrap.
//
//  Shell fur lays its strand grid over UV space, so without this the world size of a
//  strand is decided by however the model happens to be unwrapped: where an island is
//  packed tight the strands come out fine, where it is stretched they come out coarse.
//  On a real character that is most visible exactly where the unwrap is worst - the
//  folds, the armpits, the crotch, the insides of joints - which is where fur was
//  measurably up to twice the thickness of the fur on an open flank.
//
//  This used to be measured PER FRAGMENT, from the ratio of the world and UV areas a
//  pixel quad spans, which is exact and local and needs nothing authored. It also broke
//  shell fur outright, and the way it broke is worth writing down because the measurement
//  itself is not wrong - it is only unusable for THIS.
//
//  A screen-space derivative is a difference across a 2x2 pixel QUAD. On a dense mesh in
//  a close-up the triangles are a handful of pixels across, so nearly every quad straddles
//  two or three of them and ddx(uv) ends up differencing one triangle's UV gradient
//  against another's. The result is not a measurement of the surface, it is noise.
//
//  Noise there would be harmless if it only sized the strand. It does not - it also PLACES
//  it. The grid index is uv * densityCells, and densityCells runs to several hundred, so a
//  one percent wobble moves the index by more than a whole cell and the fragment lands on
//  a different strand entirely.
//
//  And every shell is a separate, slightly larger copy of the surface, rasterised at its
//  own screen scale - so each of the thirty-two layers of one hair drew a DIFFERENT random
//  strand. What came out was not fur at all: a scatter of single-pixel specks over bare
//  skin, dense enough to look like video noise. Measuring at the root position rather than
//  at the offset shell, which the note on fur.rootWS describes, fixes the position fed to
//  the derivative and cannot fix the derivative itself.
//
//  So the grid is anchored to the mesh's own area-weighted average instead: one number for
//  the whole object, resolved on the CPU where the geometry is actually available. Stable
//  across layers, across quads and across the camera, which is the entire requirement.
//
//  WHAT THIS COSTS, stated plainly because it is a real regression and not a rounding
//  error: strand size once again follows the unwrap. Where an island is packed tight the
//  strands come out fine and where it is stretched they come out coarse, by whatever range
//  that particular unwrap contains - on this squirrel, about seven and a half times between
//  the average and the high percentile. That is a cosmetic unevenness in the folds and the
//  armpits. It is worth paying to stop the coat being noise.
//
//  The measurement that WOULD do both is the same ratio taken against UV rather than
//  against the screen - which is exactly what the attribute bake pass computes when it
//  rasterises the mesh into UV space. Baked into a per-object map it would be smooth,
//  view-independent and identical across shells, and the per-region correction would come
//  back. That is the proper fix; this is the correct floor under it.
// ---------------------------------------------------------------------------
float FurMetersPerUV(float3 positionWS, float2 uv)
{
    return max(_FurMetersPerUV, 1e-4);
}

// `densityCells` is _Density already converted into cells per unit of the UV set this
// call is sampling - _Density * FurMetersPerUV, strands per meter times meters per UV
// unit.
FurStrandSample SampleFurStrand(float2 uv, float densityCells, float layerT, float3 viewDirTS,
                                float wetness, float flatten, float thicknessScale)
{
    FurStrandSample s;

    // Widening toward the cell size drives coverage toward solid, which is what a
    // mat of strands lying over each other actually is.
    float flattenGain = lerp(1.0, FUR_CONTACT_DENSITY_GAIN, saturate(flatten));

    // Procedural mode: split the UV into cells, one strand per cell.
    float2 grid = uv * densityCells;
    float2 cell = floor(grid);
    s.rnd       = FurHash22(cell);

    // Jitter the strand's center inside its cell to break up the grid pattern.
    float2 strandCenter = cell + 0.5 + (s.rnd - 0.5) * _StrandJitter;

    // Where this strand comes out of the surface, back in UV.
    //
    // A shell fragment's own UV is the point on the SURFACE under it, which drifts across a
    // strand from one side to the other - so anything sampled with it varies within a single
    // hair, and a hair lying across a marking's border comes out half one colour and half the
    // other. The strand's own centre is the one UV that belongs to the whole hair.
    s.rootUV = strandCenter / max(densityCells, 1e-6);
    float2 local = grid - strandCenter;

    // --- Wet clumping ---
    //
    // Water does not make fur thinner, it makes it CLUMP: surface tension pulls
    // neighbouring strands into a smaller number of thicker, spikier tufts. The
    // old model only shrank every strand's radius uniformly, which reads as fur
    // that has been shaved rather than soaked - the density drops everywhere
    // instead of redistributing.
    //
    // A second, coarser grid gives each group of strands a shared tuft centre;
    // wetness drags the strands toward it. Density falls where the tufts pull
    // apart and rises inside them, which is the actual look.
    //
    // Scaled by layerT because real tufts are joined at the tip and separate at
    // the root - clumping at the skin would just look like the fur was planted
    // in clusters.
    if (wetness > 0.0 && _WetClumpStrength > 0.0)
    {
        float  clumpSize = max(_WetClumpSize, 1.0);
        float2 clumpCell = floor(grid / clumpSize);
        float2 clumpRnd  = FurHash22(clumpCell + 37.0);   // offset so tufts do not correlate with strands
        float2 clumpCenter = (clumpCell + 0.5 + (clumpRnd - 0.5) * _StrandJitter) * clumpSize;

        float pull = saturate(wetness) * saturate(_WetClumpStrength) * saturate(layerT);
        local -= (clumpCenter - strandCenter) * pull;
    }

    // --- Disc billboard ---
    // At a grazing angle, a circle in UV space projects to an ELLIPSE on screen:
    // strand cross-sections flatten, gaps open between shells and the fur reads
    // as "striped". Fix: shrink the distance measured along the view vector's
    // projection axis by N·V. That stretches the accepted region by 1/(N·V) on
    // the same axis, so after projection it is a circle again (a disc that
    // always faces the camera).
    float2 viewAxis = viewDirTS.xy;
    float  axisLen  = length(viewAxis);
    if (axisLen > 1e-4)
    {
        viewAxis /= axisLen;

        // As N·V -> 0 (silhouette) the stretch factor diverges; clamp it.
        float ndotv  = max(abs(viewDirTS.z), FUR_DISC_BILLBOARD_LIMIT);
        float squash = lerp(1.0, ndotv, FUR_DISC_BILLBOARD);

        float  along  = dot(local, viewAxis);
        float2 across = local - viewAxis * along;
        local = viewAxis * (along * squash) + across;
    }

    // Length variation plus organic parabolic tapering (full volume core with wispy fine tips).
    //
    // Resolved BEFORE the coverage is placed, because the curl below has to know how wide
    // the hair is at this height - see there.
    s.strandLen  = lerp(1.0 - _LengthRandom, 1.0, s.rnd.y);
    float normHeight = saturate(layerT / max(s.strandLen, 1e-4));
    float taper  = saturate(1.0 - pow(normHeight, 1.35));
    // The radius is in CELL units, and a cell shrinks as density rises - so holding the
    // strand's real width steady means growing it in cell units by the same factor. See
    // FurThicknessDensityGain: density decides how many strands there are, and nothing else.
    //
    // Kept in two pieces: the width the hair would have with no taper on it, and this
    // height's share of that. The untapered one is what the curl below scales itself
    // against - it is the same at every height, which is exactly the property that makes
    // it safe to scale a coil by.
    float strandWidth = FurThickness() * FurThicknessDensityGain() * thicknessScale
                      * lerp(1.0, saturate(_WetThinning), wetness) * flattenGain;
    float radius = strandWidth * taper;

    // A strand cannot usefully be wider than its own cell.
    //
    // Radius is in cell units and the cell is one strand's worth of ground, so at 1 the
    // strand already covers all of it and neighbours have begun to overlap. Past that there
    // is no more coverage to win - but the pattern keeps growing, every cell stays solid,
    // and what is left visible is the clumping of merged strands rather than hair. Raising
    // density with thickness held steady walks straight into it, which is the one rough
    // edge of separating the two.
    radius = min(radius, FUR_MAX_CELL_RADIUS);

    // --- Curl ---
    //
    // Shell fur has no geometry per hair - the hair IS this pattern, and a stack of shells
    // reads as one strand because every layer resolves the same cell. Sweeping where that
    // cell's coverage sits as the layer rises is therefore what coiling a hair is here.
    //
    // Applied to `local` and NOT to s.rootUV, which is the same split the wet clumping
    // below makes. rootUV is the one UV belonging to the WHOLE hair - it is what the colour
    // and detail maps are sampled with, so a hair lying across a marking's border comes out
    // one colour rather than half of each. Coiling it would put that back.
    //
    // The coil is scaled by two things, and both exist because a shell hair is only a hair
    // while consecutive layers OVERLAP. The coil steps the coverage sideways by
    // reach * dAngle per layer; two discs stop touching once that step passes `radius`,
    // and the hair renders as two pieces with a hole between them.
    if (_Curl > 0.0)
    {
        // The angle one layer advances. _ShellCount is the per-draw count, so a coat the
        // LOD has thinned coils less rather than falling apart.
        float dAngle = (FUR_CURL_TURNS * TWO_PI) / max(_ShellCount - 1.0, 1.0);

        // How much coil this hair can carry, from the width it has at the root and how
        // far a layer advances. Constant along the hair, and that is the whole point: a
        // per-height clamp on the reach looks like it should work and does not, because
        // clamping makes the reach itself jump between layers and the coil's RADIAL step
        // then breaks the stack just as its sideways step did.
        float carry = saturate(FUR_CURL_STEP_SAFETY * min(strandWidth, FUR_MAX_CELL_RADIUS)
                             / max(dAngle * FUR_CURL_RADIUS_CELLS, 1e-4));

        // The coil closes as the hair thins. Without this the sideways step stays the
        // width of a full-thickness hair all the way to a tip that has none, the layers
        // stop overlapping partway up, and the hair renders as two pieces with a hole
        // between them - solid below, scattered above. Which is not a tip artefact: with
        // length randomness on, a short hair's tip IS the middle of the shell stack, so
        // the break landed halfway up the coat.
        //
        // Eased rather than proportional - see FUR_CURL_TAPER_FULL, which is what keeps
        // the curl at the amplitude it had before any of this bounded it.
        float reach = FurCurlProfile(layerT) * FUR_CURL_RADIUS_CELLS * carry
                    * saturate(taper / FUR_CURL_TAPER_FULL);

        float a = FurCurlAngle(layerT, s.rnd.x);
        local -= float2(cos(a), sin(a)) * reach;
    }

    float dist = length(local) * 2.0;

    // One pixel's footprint in cell space (grid = uv * _Density).
    float footprint = length(fwidth(uv) * densityCells);
    s.footprint = footprint;

    // Hold the strand to a minimum screen width, so a strand narrower than a pixel dims
    // instead of missing pixel centres along its length and breaking into speckles.
    //
    // This used to sit behind a shader keyword whose two branches contained the same code
    // character for character - it doubled the variant count of every pass in this shader
    // to pick between two copies of one thing.
    float minTargetRadius  = footprint * (FUR_MIN_PIXEL_WIDTH * 0.5);
    float maxAllowedRadius = radius * FUR_MAX_THICKNESS_GAIN;
    float effectiveRadius = clamp(max(radius, minTargetRadius), radius, max(maxAllowedRadius, radius));

    // A widened strand covers more of the screen than the coat actually does. Taking
    // part of that back out of its coverage is what stops distant fur from darkening
    // into a solid mass.
    float areaExpansion = (radius * radius) / max(effectiveRadius * effectiveRadius, 1e-4);
    float alphaCompensation = lerp(1.0, saturate(sqrt(areaExpansion)), FUR_ALPHA_COMPENSATION);

    // Stable signed distance field edge antialiasing:
    float aaWidth = max(footprint * 0.5, 1e-4);
    s.alpha = saturate(0.5 + (effectiveRadius - dist) / (2.0 * aaWidth)) * alphaCompensation;

    // The fuzz layer is not a sharper-edged strand drawn faint - it is a soft one. Its
    // coverage comes from the blur profile instead of the signed distance edge, so it
    // falls off from the middle of the strand outward and from the root to the tip.
    if (IsFurFuzzDraw())
        s.alpha = FurFuzzProfile(saturate(dist / max(effectiveRadius, 1e-5)), normHeight);

    // --- Distance aggregation ---
    //
    // The strand's true mean coverage over its own cell, from the area of a disc of that
    // radius inside a unit cell. Deliberately the AUTHORED radius rather than the widened
    // one: this is what the coat actually covers, and it is the value the fragment should
    // converge to once the individual strand stops being resolvable.
    // Fully opaque unless a ribbon says otherwise: the shell pattern's partial coverage
    // is already carried by alpha, which for a stack of layers is the right place for it.
    s.opacity = 1.0;

    s.meanCoverage = saturate(PI * radius * radius * 0.25);

    // When to aggregate is measured on the pixel's AREA in cell space, not on fwidth.
    //
    // fwidth adds the two screen derivatives' magnitudes, so it reports the pixel's
    // LONGEST stretch - and at a silhouette, where the surface turns away, one axis
    // stretches without bound while the other stays put. That pushed every contour
    // into aggregation (and so into the dither) while the coat was still large on
    // screen, which read as a crawling, sparkling outline. The square root of the
    // Jacobian's determinant is the pixel's real coverage of the pattern: it grows as
    // the square root of that stretch rather than linearly, and agrees with fwidth
    // everywhere the surface faces the camera.
    float2 cellDx = ddx(uv) * densityCells;
    float2 cellDy = ddy(uv) * densityCells;
    float  footprintArea = sqrt(abs(cellDx.x * cellDy.y - cellDx.y * cellDy.x));
    s.aggregate = FurResolveAggregation(footprintArea);

    // The dither threshold, fixed to the STRAND rather than to the screen.
    //
    // A screen-space noise stays put while the fur moves through it, so on a coat in
    // wind or in motion the pattern slides across the hairs and the whole coat crawls.
    // A value hashed from the strand's own cell moves with the strand. Different per
    // layer, or every shell of a strand would pass or fail together and the stack
    // would cover only as much as one layer. Under TAA the camera jitter samples a
    // different cell each frame, so the history averages this out to the true
    // coverage rather than holding on to a fixed grain.
    float layerIndex = layerT * max(_ShellCount - 1.0, 1.0);
    s.dither = FurHash22(cell + float2(layerIndex * 17.0, layerIndex * 31.0)).x;

    // Once strands are smaller than a pixel, alpha converges to the strand's MEAN coverage
    // and the clip test moves from the fixed cutout to a dithered one - see FurAlphaClip.
    // The two move together, and that pairing is the distance fix that kept coats visible
    // from mid-August until the August 22 clean-up removed it:
    //
    //   * With a FIXED cutout, a regional mean is a cliff. Every fragment of the region
    //     carries the same value, so once the mean is below the cutout the whole coat fails
    //     at once - for thin strands or a high cutout, a few metres out. Raising only the
    //     gaps to the mean (what replaced it) cannot help: a mean below the cutout is still
    //     discarded.
    //   * With a DITHERED cutout, a fragment of mean coverage c survives with probability
    //     c, so a pixel shows the fraction of fur it should at any distance, whatever the
    //     material's cutout says. TAA, or the eye, averages the pattern.
    //
    // No setting. The old version had a strength, a start footprint and a range to tune by
    // hand; the ramp here is the one aggregation already measures, at full strength,
    // because anything less leaves part of the cliff in place. Close up the aggregate is
    // 0, so a near coat is untouched.
    //
    // Not for the fuzz layer: it is blended rather than clipped, and its soft profile is
    // the effect.
    if (!IsFurFuzzDraw())
    {
        s.alpha    = lerp(s.alpha, s.meanCoverage, s.aggregate);
        s.lodBlend = s.aggregate;
    }
    else
    {
        s.lodBlend = 0.0;
    }

    return s;
}

// ---------------------------------------------------------------------------
//  Interleaved gradient noise - a cheap, well-distributed screen-space dither.
//
//  Deliberately NOT animated. With TAA running, the camera's own sub-pixel jitter
//  already moves a given surface point across different pixels each frame, so the
//  pattern averages out on its own; adding time on top would only make it flicker
//  for anyone running without a temporal filter.
// ---------------------------------------------------------------------------
float FurDitherNoise(float2 positionSS)
{
    return frac(52.9829189 * frac(dot(positionSS, float2(0.06711056, 0.00583715))));
}

// ---------------------------------------------------------------------------
//  The base shell (the skin) is never clipped; only shells above it run the test.
// ---------------------------------------------------------------------------
void FurAlphaClip(FurStrandSample s, float layerT, float2 positionSS)
{
    if (layerT <= 0.0)
    {
        // Shell layer 0 is not fur, it is the surface the fur grows out of - and it is only
        // this component's to draw when it has hidden the object's own renderer. Otherwise
        // the object is already drawing itself with its own material, and this would put a
        // second copy of the same surface at the same depth, tinted by the fur material's
        // Base Colour. Dropping the layer here rather than skipping the instance keeps every
        // other layer's height where it was: shell height comes from the instance index.
        if (_FurDrawSkin < 0.5)
            discard;

        return;
    }

    // The fuzz layer is blended rather than cut, so it keeps everything above a hair of
    // nothing. Applying the coat's cutout to it would discard the faint coverage that IS
    // the effect.
    if (IsFurFuzzDraw())
    {
        if (s.alpha < FUR_FUZZ_CLIP)
            discard;
        return;
    }

    // Fixed cutout up close, dithered once the strands are sub-pixel - see the end of
    // SampleFurStrand for why the two have to switch together.
    //
    // min(), not the noise outright: only the DEFICIT is dithered. A fragment that
    // already clears the cutout is always drawn, exactly as up close, so the strong
    // part of the coat stays solid and stable; only coverage the fixed test would
    // have thrown away is kept stochastically, in proportion to how much there is.
    // That is the smallest amount of noise that still stops the coat vanishing.
    float cutout    = FurAlphaCutout();
    float threshold = lerp(cutout, min(cutout, s.dither), saturate(s.lodBlend));

    if (s.alpha < threshold)
        discard;
}

// ---------------------------------------------------------------------------
//  Biased clip-space position for the shadow pass.
//  Shells extend outside the base surface, so the standard shadow bias can be
//  insufficient on its own. lightDirectionWS points from the surface TOWARD the
//  light; pushing the caster the opposite way removes shadow acne (dark,
//  patchy artifacts across the fur). Increasing FUR_SHADOW_EXTRA_BIAS reduces acne;
//  too high a value peels the shadow away from the surface.
// ---------------------------------------------------------------------------
float4 GetFurShadowPositionHClip(float3 positionWS, float3 normalWS, float3 lightDirectionWS)
{
    positionWS = ApplyShadowBias(positionWS, normalWS, lightDirectionWS);
    positionWS -= lightDirectionWS * FUR_SHADOW_EXTRA_BIAS;

    float4 positionCS = TransformWorldToHClip(positionWS);
    return ApplyShadowClamping(positionCS);
}

#endif // FUR_SHELL_COMMON_INCLUDED
