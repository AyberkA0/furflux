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

// Upper bounds for the wet-multiplied motion values. These MUST match the slider
// ranges declared for _Stiffness and _Gravity in FurShellLit.shader: their whole
// purpose is to keep the multiplied result inside the span the material can
// actually author and preview, so a wet value is always one you could have dialled
// in by hand and seen. Change a slider range, change these with it.
#define FUR_STIFFNESS_MAX 6.0
#define FUR_GRAVITY_MAX   3.0

// ---------------------------------------------------------------------------
//  Settled behaviour: four sliders that are now the one value each of them ever
//  wanted to be.
//
//  They came out of the inspector on the same argument, and it is worth stating
//  once because it applies to every number in this file rather than to these
//  four: a control is only worth its slot if the person looking at it can SEE
//  what moving it did and judge whether they wanted that. Each of these failed
//  that test - their effect is real but arrives below the threshold where anyone
//  can attribute it to the slider that caused it. A number nobody can evaluate is
//  not a choice being offered, it is a way to break a coat quietly.
//
//  DISC BILLBOARD, now settled PER FUR TYPE rather than per material, because the
//  two types were never asking the same question of it.
//
//  A SHELL strand has no geometry of its own - it is a procedural disc resolved in
//  UV space, and at a grazing angle a disc in UV projects to an ellipse. That is
//  the striping the correction exists to remove, and it is on: FUR_DISC_BILLBOARD.
//
//  A STRAND ribbon is real geometry with a real plane, and turning that plane is a
//  different act with a different cost - the ribbon's twist is what gives a coat
//  its variation, and billboarding spends it to buy a roundness a lit ribbon
//  mostly already reads as. The ribbon keeps its fixed twisted plane, and the
//  flattening at grazing angles is accepted. Not a shell's answer, and it does not
//  have to be: FurStrandRibbonBillboard.
#define FUR_DISC_BILLBOARD        1.0   // shells
#define FUR_STRAND_RIBBON_BILLBOARD 0.0 // strands

//  MAX BEND ANGLE, now no cap at all (90 degrees from the normal). The clamp it
//  fed still runs and still earns its place, but only as the invariant it always
//  really was: a strand may lie down flat, and may not bend BELOW the surface it
//  grows from, because that puts shells inside the mesh. Any cap tighter than
//  that is a pose limit dressed up as a safety rail - gravity, wind and comb are
//  where a coat's lean belongs, and they are all authorable.
#define FUR_MAX_BEND_COS 0.0

//  CONTACT DENSITY GAIN, now fixed at the value it shipped with. A strand pressed
//  down covers more surface than one standing up, and driving coverage toward
//  solid is what makes a flattened patch read as a MAT of hair rather than as a
//  shaved one. It is live whenever a strand bends - not just under collision -
//  which is exactly why it never looked like a control: it is a correction that
//  tracks the pose, and there is no pose in which some other number is right.
#define FUR_CONTACT_DENSITY_GAIN 3.0

//  KEEP FUR VISIBLE AT DISTANCE, now always on. Off, a strand narrower than a
//  pixel misses pixel centres along its length and the coat breaks into speckle
//  before it fades; the minimum screen width plus its alpha compensation is what
//  holds a distant coat together, and it costs a clamp. There was a checkbox, and
//  beside it an inspector warning telling you to turn it back on - which is two
//  ways of saying it has one correct value.
// ---------------------------------------------------------------------------

// Two technical corrections that used to be material sliders. Both have exactly one
// right answer and neither is an artistic choice, so exposing them only invited
// somebody to move a number whose correct value they had no way to judge.
//
// The silhouette clamp on the disc billboard: as N.V approaches zero the stretch
// factor diverges, and this is where it stops. Lower makes distant silhouettes
// stripe, higher flattens the correction the billboard exists to apply.
#define FUR_DISC_BILLBOARD_LIMIT 0.2
// How far above the material's own value a detail map can push length and thickness.
//
// The map stores 0..1 and is multiplied by this, which puts the NEUTRAL value - the one
// meaning "exactly what the material says" - at MID-GREY rather than at white. That is
// the only encoding that lets a map both shorten and lengthen: with white as neutral
// there is nothing above it to grow into, and a map could only ever take fur away.
//
// Mid-grey as "no change" is the same convention a normal map or a displacement map
// uses, so it is a familiar thing for an authored texture to mean.
//
// 2 rather than something larger to keep the precision honest: 8-bit storage over
// 0..2x gives steps of under one percent of the material's value.
#define FUR_DETAIL_SCALE_MAX 2.0
// Shadow bias for the offset shells, in meters. Shells sit outside the base surface,
// so the pipeline's own bias is not always enough; this pushes the caster back along
// the light. Too little brings acne back, too much detaches the shadow.
#define FUR_SHADOW_EXTRA_BIAS 0.01

// --- Distance thickness, as three constants rather than three sliders ---
//
// A strand narrower than one pixel does not get dimmer, it starts MISSING pixel centres
// along its length - so ONE pixel is the floor, and there is no second answer to argue
// about. The cap stops a strand at extreme distance from growing without bound and
// turning a creature into a smooth blob; four is where the widening has already done its
// job. The compensation takes back half of the coverage the widening added, which is what
// stops distant fur from darkening into a solid mass, and taking back all of it undoes
// the widening entirely while taking back none leaves the mass.

// How much wider a strand ribbon is than _Thickness alone would make it.
//
// A shell coat stacks layers, so at _Thickness 0.75 one layer covers 44% of its cell and
// the stack of them covers everything. A ribbon coat has ONE layer, so the same 0.75 would
// leave 56% of the surface as gaps: measured on a test sphere, shells showed no skin at all
// and strands showed 51% of it. One number meaning two different amounts of fur
// is the thing this package exists not to do, so a ribbon is widened to land where the
// shell stack landed.
//
// A constant rather than the solved union it used to be. The union is the honest model of
// what a shell stack covers, and it saturates: past _Thickness 0.5 it is already within a
// percent of full coverage, so every value above it produced the same strand and most of
// the slider did nothing. This is what that curve returned at the 0.75 default, so a
// material set there is unchanged and every other setting now actually moves.
#define FUR_RIBBON_THICKNESS_GAIN 1.5

// The density _Thickness is measured against.
//
// _Thickness is a fraction of the SPACING between neighbouring strands, which is what keeps
// it scale-invariant - but spacing is set by density, so raising the density used to thin
// every strand at the same time. One slider quietly moved two things, and a coat could not
// be made denser without also being made finer.
//
// Referring the fraction to a FIXED density instead breaks that link: _Thickness is now the
// fraction of the spacing a coat of this density would have, so a strand keeps its width
// whatever the count is and density means only how many there are. Still relative, still
// scale-invariant - the reference is a count across the object, not a distance.
//
// 250 because that is this shader's default density, so a material left at the default is
// unchanged. A material tuned at some other density needs its Thickness multiplied by
// 250/that density once, and then behaves independently.
#define FUR_REFERENCE_DENSITY 250.0

// The widest a shell strand may get, in cell units. At 1 it covers its whole cell and its
// neighbours have started to overlap; beyond that the coat is solid and the extra width buys
// nothing but merged clumps.
#define FUR_MAX_CELL_RADIUS 1.0

// The floors under the two settings that can switch the coat off by being zero.
//
// They match the sliders' own minimums, and they exist because a slider is not the only way
// a value arrives: a script, an animation curve or a material preset can write anything. The
// 1e-4 guards these replace only kept the arithmetic finite - they still let a coat render as
// nothing, which reads as a broken shader rather than as a setting. Clamping to the least
// meaningful value instead means the worst a zero can do is the thinnest fur, not no fur.
#define FUR_MIN_DENSITY   1.0
#define FUR_MIN_THICKNESS 0.01


// The height a strand's AREA is centred on, for anything that has to stand in for the
// whole strand at once.
//
// Not one half. A strand tapers - its width goes as 1 - t^1.35 - so the wide part of it is
// down near the root and the tip contributes almost no area. Weighting height by that
// width puts the centre at 0.35:
//
//     integral t(1 - t^1.35) dt  /  integral (1 - t^1.35) dt  =  0.2015 / 0.5745
//
// Using the midpoint instead reads the tip colour far too strongly, and _TipColor is
// routinely an HDR value above one because a real tip is a thin bright fringe. Averaged as
// though it were half the hair, a coat that aggregates turns pale - measured on the test
// scene, a red squirrel came out cream the moment its hairs went sub-pixel.
#define FUR_STRAND_MEAN_HEIGHT 0.35

// How many texels the band gradient is baked into. MUST match
// FurController.BandResolution - the shader maps height onto texel centres and would
// misplace every stop by half a texel if the two disagreed.
//
// 128 because the thing being stored is a hand-authored gradient a few stops long, and the
// narrowest feature anyone puts in one is a ring perhaps a fiftieth of the hair's length.
// It costs 1 KB at four half-floats a texel.
#define FUR_BAND_RESOLUTION 128.0

// The floor the fuzz layer clips at. Not zero: a fragment contributing less than this is
// below the precision the blend can carry anyway, and skipping it saves the shading.
#define FUR_FUZZ_CLIP         0.004

// --- Curl, as one slider plus two constants ---
//
// TURNS is how many times a hair goes round over its own length. Fixed, because it is
// what separates "curly" from "wavy" and a coat wants one of those, not a dial between
// them - and because a turn count somebody can raise without bound aliases the moment
// the coil is finer than the shells resolving it.
//
// RADIUS is the coil's reach at _Curl 1. Both are relative, so curl means the same thing
// on a mouse and a bear - but they are relative to DIFFERENT things, for the same reason
// FUR_RIBBON_THICKNESS_GAIN exists: the two types resolve a hair by different measures.
//
// A SHELL hair is a pattern, and what makes its coil visible is how far the pattern
// wanders compared to the hair's own WIDTH. So the shell radius is in cells, and just
// under half a cell already sends a hair most of the way to its neighbour's.
//
// A RIBBON hair is geometry, and its coil reads against its own LENGTH. Measured in
// spacing it would be invisible - a coat of hairs forty times longer than the gap between
// them wiggling by a fraction of that gap - so the ribbon radius is a fraction of the
// strand's length instead. That is also what a real curl is: loop size scales with how
// much hair there is to loop.
//
// The root stays planted under either - see FurCurlRadius.
#define FUR_CURL_TURNS         2.5
#define FUR_CURL_RADIUS_CELLS  0.45   // shells, in strand cells
#define FUR_CURL_RADIUS_LENGTH 0.18   // strands, as a fraction of the hair's length

// How much of the coil a shell coat is allowed to spend before its layers stop
// overlapping. See the curl block in SampleFurStrand for what that costs when it is
// exceeded; 0.4 is where a sweep of thickness, density, hair length and shell count
// stopped producing broken hairs, and it still leaves full curl at every setting the
// shipped presets use.
#define FUR_CURL_STEP_SAFETY   0.4

// The taper at which the coil is already at full reach. Below it the coil closes with
// the hair; above it the taper does not scale the curl at all.
//
// Scaling by the raw taper works and costs a quarter of the curl - the coil is narrowed
// along the whole hair to protect the last tenth of it, where the strand has thinned to
// nothing. Holding full reach down to 0.6 and closing from there keeps the amplitude the
// coil had before it was bounded at all, and a sweep of thickness, density, hair length,
// shell count and curl found two broken layers in its worst corner rather than none -
// both at a tip already below the alpha cutout.
#define FUR_CURL_TAPER_FULL    0.6

#define FUR_MIN_PIXEL_WIDTH   1.0
#define FUR_MAX_THICKNESS_GAIN 4.0
#define FUR_ALPHA_COMPENSATION 0.5

CBUFFER_START(UnityPerMaterial)
    // --- Base surface (the skin underneath the fur) ---
    float4 _BaseMap_ST;
    float4 _BaseColor;
    float  _Smoothness;
    float  _NormalScale;

    // --- Fur shape ---
    float4 _RootColor;              // Tint at the strand root (multiplier)
    float4 _TipColor;               // Tint at the strand tip (multiplier)
    float  _FurLength;              // Fur length, as a fraction of the object's size
    float  _Thickness;              // Strand width, as a fraction of one cell
    float  _StrandJitter;           // Random offset of a strand inside its cell
    float  _LengthRandom;           // Per-strand length variation
    float  _Curl;                   // How tightly the hair coils around its growth axis
    float  _AlphaCutout;            // Coverage below this value is discarded

    // --- Motion ---
    float  _Stiffness;              // Bend curve exponent (higher = stiffer roots)
    float  _Gravity;                // Downward bend
    // Wet response, expressed as MULTIPLIERS of the two above rather than as
    // separate wet values. A multiplier states a relationship - "wet fur sags
    // about three times as much" - which keeps holding when the base value is
    // driven at runtime. A pair of absolute values would state two independent
    // poses instead, so animating the dry one silently desynchronises the wet
    // one and the authored intent quietly drifts apart.
    float  _GravityMultiplierOnWet;
    float  _StiffnessMultiplierOnWet;
    // How far a fully combed region leans, as a lean the strand takes rather than as a
    // force. See ApplyFurComb - a comb is a pose the coat is IN, not something pushing it.
    float  _CombStrength;
    float4 _WindDirection;          // World-space wind direction (xyz)
    float  _WindStrength;
    float  _WindFrequency;
    float  _WindTurbulence;         // Position-dependent phase shift (wave length)
    float  _WindGustiness;          // How far gusts swing around the steady wind (0 = constant)
    float  _WindShelter;            // Airflow the lee side still receives (0 = fully sheltered)

    // --- Lighting ---
    float  _Occlusion;              // Darkening toward the fur base
    float  _OcclusionCurve;         // Falloff curve for that darkening
    float  _StrandNormalStrength;   // Per-strand normal deviation
    float4 _SheenColor;             // Kajiya-Kay anisotropic highlight color
    float  _SheenIntensity;
    float  _SheenExponent;
    float4 _RimColor;               // Backlight / rim tint color
    float  _RimIntensity;           // Back-scatter / translucency at the silhouette
    float  _RimPower;

    // --- Wetness response ---
    // Every one of these is the WET end of a value whose dry end lives above, blended
    // by the resolved wetness. They are material properties rather than per-object
    // ones because they describe what this coat is made of, not what is happening to
    // a particular creature right now.
    float  _WetClumpSize;           // Strand cells per tuft
    float  _WetClumpStrength;       // How far strands are pulled into their tuft
    float  _WetThinning;            // Strand radius scale when soaked
    float  _WetLengthScale;         // Fur length scale when soaked
    float  _WetDarkening;           // Albedo gamma when soaked (1 = no change)
    float  _WetSmoothness;          // Smoothness of the water film
    float  _WetWindResponse;        // Wind strength scale when soaked
    float  _WetRimScale;            // Back-scatter scale when soaked
    float  _WetSheenScale;          // Anisotropic sheen scale when soaked
    float  _WetOcclusionScale;      // Occlusion multiplier when soaked (clamped to 1 in use)

    // --- Keyword drivers, procedural LOD and render state ---
    // (Every constant in the Properties block must also appear here.)
    float  _NormalMapToggle;
    float  _Cull;
    float  _SrcBlend;
    float  _DstBlend;
    float  _ZWrite;

    // --- Fuzz layer ---
    float  _FuzzAmount;             // 0 = off. Strength AND switch.
    float  _FuzzExtent;             // How far past the coat it reaches, as a fraction of fur length
    float  _FuzzSoftness;           // How much wider a fuzz strand is than the real one

    // Which of the two fuzz layers this material asks for. 0 = Geometric, the second
    // copy of the coat described above; 1 = Screen Space, where the coat's own drawn
    // PIXELS are blurred instead - see FurScreenFuzzFeature. The geometric value is 0
    // because that is what every material predating this stores, so nothing changes
    // appearance by being opened.
    float  _FuzzMode;

    // The stencil bit the coat marks itself with, so the screen-space pass can find its
    // pixels again. Material state rather than per-draw, because ShaderLab resolves
    // render state from the MATERIAL - a property block cannot reach it, which is also
    // why the skin is drawn from a copy with the write mask cleared.
    float  _FuzzStencilRef;
    float  _FuzzStencilWriteMask;   // 0 = write nothing, which is the geometric default

    // --- Fur type ---
    // 0 = Shells, 1 = Strands. The float is what the material stores; the
    // _FURTYPE_STRANDS keyword derived from it is what the shader branches on. Both exist
    // on purpose: a keyword cannot be read back from a material property drawer, and a
    // float cannot remove code from a variant.
    float  _FurType;
CBUFFER_END

// Per-draw, not per-material: the coat and its fuzz layer are two draws of the SAME
// material, told apart by this. Declared up here rather than with the other per-draw
// uniforms further down because the helpers just below already need it.
float _FurFuzzMode;             // 1 = this draw is the soft layer over the coat, 0 = the coat

// ---------------------------------------------------------------------------
//  Per-object constants, written per draw through a MaterialPropertyBlock.
//  They sit outside the buffer on purpose - see the note at the top of the file.
// ---------------------------------------------------------------------------
float  _ShellCount;             // Shell count for this draw (the LOD result)
float  _Density;                // Strands ACROSS the object (see FurLengthMeters)
// BODY forces: the inertial pseudo-force plus anything a script pushed in. These act
// on every strand alike whichever way it faces, so they are never sheltered.
float4 _ExternalForce;
// Airflow from the object's own travel. Sheltered per vertex like wind, because it
// is the same thing - air moving past the body.
float4 _DragForce;
// Rotational counterpart. A turn cannot be one world-space force - opposite sides of
// the object travel opposite ways - so this is a force PER METRE of lever arm, and the
// vertex stage crosses it with its own offset from the pivot below.
float4 _AngularForce;
float4 _RotationCenterWS;       // Pivot the object rotates about (its transform position)

// Bones that move independently of the body - a flicking tail, a shaking head. Must
// match FurController.MaxMotionBones.
#define FUR_MAX_MOTION_BONES 8
float4 _BoneMotionOrigin[FUR_MAX_MOTION_BONES];   // xyz world position, w reach in meters
float4 _BoneMotionForce [FUR_MAX_MOTION_BONES];   // xyz world force
float  _BoneMotionCount;
float  _Wetness;                // GLOBAL wetness floor (0 = dry, 1 = fully wet)
float  _WetnessEnabled;         // 0 = skip the wetness map entirely (see SampleFurWetness)

// --- Detail map drivers (see the texture declarations below) ---
// Each map has its own enable flag rather than one shared switch: they cost
// different things in different stages - length is a VERTEX fetch per shell,
// thickness runs in four passes, colour in one - so a coat that only needs a
// colour variation should not pay for the other two.
float  _FurLengthMapEnabled;
float  _FurThicknessMapEnabled;
float  _FurColorMapEnabled;
float  _FurCombMapEnabled;
float4 _FurColorMapTint;        // rgb = tint applied to the map, a = blend toward no tint

// ---------------------------------------------------------------------------
//  Which UV set the per-object maps live in. 0 = UV0, 1 = UV1 (mesh.uv2).
//
//  Every one of those maps - wetness, length, thickness, colour - is a picture of
//  the SURFACE, stored one texel per piece of it. That only works while each piece
//  of surface owns a texel of its own, and a great many real unwraps do not give it
//  one: UV0 is authored for TEXTURING, where sharing is a feature. Mirroring an
//  animal down its spine halves the texture budget for free; packing two limbs onto
//  the same island costs nothing when they carry the same paint anyway.
//
//  Shared texels are fatal here and merely thrifty there, and that is the whole
//  reason this switch exists. Paint one side of a mirrored model and the other side
//  changes with it - not a bug in the brush, which resolved the right piece of
//  surface, but two pieces of surface arriving at one texel of storage. No amount of
//  care in the stroke can separate them afterwards.
//
//  UV1 is the way out because Unity already generates exactly what is needed: the
//  lightmap unwrap is guaranteed non-overlapping - lightmapping fails in the same way
//  for the same reason - and the model importer will build one on demand. So the fix
//  for a coat that paints in two places is a checkbox on the mesh and this switch,
//  not a re-unwrap by hand.
//
//  Defaults to 0, so a material that never touches it behaves exactly as it did.
// ---------------------------------------------------------------------------
float  _FurDetailUV;

// The UV the per-object maps are indexed by, chosen per draw. Both are resolved in the
// vertex stage; the fragment stage only ever sees the answer.
float2 ResolveFurDetailUV(float2 uv0, float2 uv1)
{
    return _FurDetailUV > 0.5 ? uv1 : uv0;
}
// 1 = full length. Falls to 0 over the last pixel or two of fur height, so the drop to
// skin-only is a shrink rather than a pop. Resolved on the CPU, where the LOD already
// knows the on-screen size - see FurController.ResolveShellCount.
float  _LodFade;
// How much of the coat the LOD has already given up, 0 = all of it still there, 1 = none.
// Resolved on the CPU, where the shell count, the hair count and the height fade are all
// known at once - see FurController.ResolveAggregation.
float  _FurAggregation;
// Ribbon types: the fraction of the planted hairs this draw is actually using. Hairs
// removed by distance are compensated for by widening the ones that remain, which is what
// keeps the coat's total coverage constant instead of thinning it away.
float  _FurCountFraction;
// Meters of surface per UV unit, averaged over the whole mesh and weighted by area.
// Resolved once per mesh on the CPU. Only used to bound the per-fragment measurement,
// which spikes at UV seams and silhouettes where screen-space derivatives break down.
float  _FurMetersPerUV;
// The high end of that same measurement across the mesh - a percentile, not the peak.
// It is the ceiling the per-fragment measurement is clamped to; see FurMetersPerUV.
float  _FurMetersPerUVMax;
// How big this object is, in meters: the bind-pose bounding radius times the transform's
// scale. Stable under animation, because it comes from the mesh rather than from the
// renderer's live bounds - fur that breathed with a walk cycle would be worse than fur
// that is the wrong length.
float  _FurObjectSize;

// ---------------------------------------------------------------------------
//  Everything about a strand is RELATIVE, and this is what it is relative to.
//
//  A material that says "four centimeters of fur" is a material that works on exactly
//  one creature at exactly one import scale. Say "four percent of the object" instead
//  and the same material clothes a mouse, a squirrel and a bear, whether the artist
//  modelled in meters, in centimeters, or at the 150x an FBX arrives at when its unit
//  setting was wrong. Nothing has to be retuned, and no scale can silently produce fur
//  that is invisible or fur that swallows the model.
//
//  The chain runs all the way down. Length is a fraction of the object; density is how
//  many strands lie across it; thickness is a fraction of the spacing density implies;
//  the minimum on-screen width is in pixels. There is no absolute distance anywhere in
//  the strand, so there is nothing for a change of scale to get hold of.
// ---------------------------------------------------------------------------
float FurObjectSize()
{
    return max(_FurObjectSize, 1e-4);
}

/// The material's fraction, resolved into meters for this object.
float FurLengthMeters()
{
    return _FurLength * FurObjectSize();
}

/// Strands per meter of surface, from strands across the object.
// _Density and _Thickness, as everything downstream should read them - never the raw
// uniforms. See FUR_MIN_DENSITY.
float FurDensity()   { return max(_Density, FUR_MIN_DENSITY); }
float FurThickness() { return max(_Thickness, FUR_MIN_THICKNESS); }

// _AlphaCutout as every clip test should read it: held to the slider's own 0.9 ceiling.
// Materials authored when the slider ran to 1.0 still store 1.0, and Unity does not clamp
// a stored value to the Range - at 1.0 only fragments at exactly full coverage survive,
// so those coats rendered almost bare up close and vanished entirely a few metres out.
#define FUR_MAX_ALPHA_CUTOUT 0.9
float FurAlphaCutout() { return min(_AlphaCutout, FUR_MAX_ALPHA_CUTOUT); }

// How much wider a strand is than the old spacing-relative reading would have made it.
// See FUR_REFERENCE_DENSITY - this is what keeps density from moving thickness.
float FurThicknessDensityGain()
{
    return FurDensity() / FUR_REFERENCE_DENSITY;
}

float FurDensityPerMeter()
{
    return FurDensity() / FurObjectSize();
}

/// <summary>
/// _Thickness, re-solved so a single layer of ribbons covers what a shell stack covers.
///
/// A disc of diameter t inside a unit cell covers pi/4 * t^2 of it. Stacking N of those
/// leaves 1-(1-c)^N covered, and inverting that back to a diameter is the width one
/// ribbon has to be to arrive at the same place. It tops out a little above the spacing,
/// which is what an opaque coat is: hairs that overlap rather than tile.
/// </summary>
// ---------------------------------------------------------------------------
//  Keep Fur Visible At Distance - the whole of it, in one switch.
//
//  Fur vanishing as the camera pulls back is not one bug with one cause, it is three, and
//  they only look like separate problems from inside the code:
//
//    - a hair is well under a millimetre, so past a couple of metres it is thinner than a
//      pixel. It does not fade at that point, it starts MISSING pixel centres, and a coat
//      of them breaks into speckle and then into nothing.
//    - the LOD answers distance by drawing fewer hairs, which is the same disappearance
//      arriving by a second route.
//    - once a pixel spans more hairs than it can resolve, point-sampling the strand
//      pattern inside it returns whichever hair it happened to land on - so the coat's
//      density stops meaning anything and reads as holes.
//
//  Every one of them is fixed by the same decision - stop resolving individual hairs once
//  they are too small to resolve, and draw what they AVERAGE to instead - so shipping them
//  as separate sliders only offers the user ways to half-fix it. Whoever turns this off
//  wants the raw geometry, and that is the only other position worth having.
// ---------------------------------------------------------------------------
float FurDistanceFix()
{
    return 1.0;
}

// How much of the ground a ribbon coat covers, from its own settings.
//
// Needed by anything that stands in for the whole coat at once. A hair too small to resolve
// is replaced by what its pixel AVERAGES to, and that average is not "one mean hair" - it is
// mean hair over the fraction of the ground hair actually occupies, and whatever lies under
// the gaps over the rest. Ignoring the gaps biases the result toward the tip colour and
// turns an aggregating coat pale: a red squirrel came out cream.
float FurCoatCoverage();

float FurRibbonThickness()
{
    // Linear in the slider, and deliberately so.
    //
    // This used to compute the union of FUR_EFFECTIVE_LAYERS overlapping shell layers, on
    // the reasoning that one ribbon has to be as wide as the stack of discs a shell coat
    // draws for the same hair. The arithmetic is right and the result is useless: the union
    // saturates long before the slider does. At the 0.75 default it is already at 0.99 of
    // full coverage, so 0.75 and 3.0 produce a strand the same width to three decimals, and
    // most of the slider's travel does nothing at all.
    //
    // A strand ribbon is ONE hair, not a stack, and _Thickness says what it is: a fraction
    // of the spacing to its neighbour. Taking it at its word makes the control monotonic
    // over its whole range. The constant is what the old curve returned at the default, so
    // an existing material set to 0.75 renders exactly as wide as it did before.
    return FurThickness() * FUR_RIBBON_THICKNESS_GAIN * FurThicknessDensityGain();
}

float FurCoatCoverage()
{
    return saturate(FurRibbonThickness());
}

// ---------------------------------------------------------------------------
//  The fuzz layer.
//
//  A second copy of the coat, drawn over the first one: the same hairs, grown longer and
//  much wider, and blended rather than clipped. Two things come out of that, and they are
//  the two the sharp coat cannot give you at the same time.
//
//  Reach past the coat is a soft halo - the part of a real pelt that has no defined edge.
//  A clipped coat cannot have one: every hair either covers its pixel or does not, so the
//  silhouette is always a hard boundary however fine the hairs are.
//
//  What fills the gaps BETWEEN hairs is the fullness. A coat is mostly gaps at any density
//  cheap enough to ship, and the fuzz layer sits in them at low opacity, so the coat reads
//  as denser without another hair being placed.
//
//  It is off at zero, and off means not drawn: the Fur Controller skips the whole extra
//  draw rather than submitting a transparent pass that contributes nothing.
// ---------------------------------------------------------------------------
// ---------------------------------------------------------------------------
//  Opacity along the strand, from the root and tip colours' ALPHA.
//
//  The two colours are a gradient the coat is tinted by, and their alpha extends that
//  gradient to how solid the hair is at each height. Tip alpha below one is the useful end
//  of it: real hair thins to nothing rather than stopping, and a coat whose tips fade out
//  loses the cut-off edge that gives procedural fur away.
//
//  What it does depends on which draw is reading it, and both are the same idea. The coat
//  is alpha-clipped, so lower alpha near the tip erodes the hair there - it reads as wispy
//  rather than as see-through. The fuzz layer is blended, so the same value is straight
//  translucency. Either way, less alpha means less hair.
//
//  Applied inside SampleFurCoverage, so the shadow, depth and motion passes agree with the
//  forward one: a tip too faint to draw should not cast a shadow or write depth either.
//
//  Alpha 1 on both - the default, and what every existing material has - is exactly the
//  behaviour that was there before this existed.
// ---------------------------------------------------------------------------
// ---------------------------------------------------------------------------
//  The band gradient - the whole colour of a hair, along its length.
//
//  _RootColor and _TipColor are two stops, and two stops can only ever describe a hair
//  that shades from one thing to another. Real coats are not like that. A rodent's guard
//  hair is banded - dark at the root, a pale ring, dark again toward the tip - and that
//  agouti banding is most of what makes a squirrel read as a squirrel rather than as a
//  brown shape. No amount of tuning two colours produces a ring in the middle, because
//  there is no middle to tune.
//
//  So the two stops become a list of them, authored as a Unity Gradient on the
//  FurController - which is exactly the right control for this: click to add a stop, drag
//  to move it, and every stop carries alpha as well as colour, so the hair's OPACITY gets
//  the same freedom. Baked to a small strip of texture here, because a texture fetch
//  indexed by height costs less than a loop over stops and does not care how many there
//  are.
//
//  HDR, and it has to be: _TipColor is routinely above one, since a real tip is a thin
//  bright fringe. An LDR gradient could not express the coat that already exists.
//
//  Off by default and off means exactly the old behaviour - the two-stop lerp below, to
//  the value bit for bit. A material that never turns this on is untouched.
// ---------------------------------------------------------------------------
TEXTURE2D(_FurBandMap);  SAMPLER(sampler_FurBandMap);
float  _FurBandEnabled;

// The band gradient's AREA-WEIGHTED mean, resolved on the CPU when the gradient is baked.
//
// Needed because a hair too small to resolve is replaced by what it averages to, and the
// average of a banded hair is not any point on it. The two-stop path samples the gradient
// at FUR_STRAND_MEAN_HEIGHT and gets the right answer for a straight ramp; do the same to
// a banded hair and whether the coat pales or darkens at distance depends on whether 0.35
// happened to land on a light ring or a dark one. Integrating the whole gradient against
// the strand's own width profile has no such accident in it.
float4 _FurBandMean;

/// Colour and opacity at a height along the hair. rgb is the albedo multiplier, a the
/// opacity - the same meaning the two colours carry, over as many stops as are authored.
float4 SampleFurBand(float heightT)
{
    heightT = saturate(heightT);

    // A uniform branch: a whole draw takes one side, so a coat with no band gradient does
    // not pay for the fetch.
    if (_FurBandEnabled < 0.5)
        return lerp(_RootColor, _TipColor, heightT);

    // Mapped so height 0 and 1 land on the CENTRES of the first and last texels rather
    // than on the texture's outer edges. Sampling at a bare 0 or 1 falls in the outer half
    // of an edge texel, where clamping flattens the interpolation - so the first and last
    // stop would each be smeared across half a texel and read as slightly washed out. The
    // root of a hair is exactly where that is most visible.
    float u = (0.5 + heightT * (FUR_BAND_RESOLUTION - 1.0)) / FUR_BAND_RESOLUTION;
    return SAMPLE_TEXTURE2D_LOD(_FurBandMap, sampler_FurBandMap, float2(u, 0.5), 0);
}

/// What the whole hair averages to, for anything standing in for one it cannot resolve.
float4 FurBandMean()
{
    if (_FurBandEnabled < 0.5)
        return lerp(_RootColor, _TipColor, FUR_STRAND_MEAN_HEIGHT);

    return _FurBandMean;
}

float FurHeightAlpha(float layerT)
{
    return saturate(SampleFurBand(layerT).a);
}

bool IsFurFuzzDraw()
{
    return _FurFuzzMode > 0.5;
}

// True when this material wants the screen-space fuzz layer rather than the geometric
// one. The two are mutually exclusive: in screen-space mode the controller does not
// submit the extra geometric draw at all, so IsFurFuzzDraw is false throughout and every
// widening, lengthening and profile below stays out of the way.
bool IsFurScreenFuzz()
{
    return _FuzzMode > 0.5;
}

// The fuzz coat is longer than the coat it covers, which is what makes it reach past the
// silhouette instead of hiding inside it. Relative to the fur's own length, so it holds at
// any model scale.
float FurFuzzLengthGain()
{
    return IsFurFuzzDraw() ? 1.0 + max(_FuzzExtent, 0.0) : 1.0;
}

// And wider, which is where the blur comes from - but width alone is not a blur, and that
// was the first version's mistake. A hair drawn four times its own width at a quarter of
// its opacity is not blurred, it is a FAT TRANSLUCENT HAIR: it still has a hard edge, so it
// reads as the coat gone see-through rather than as haze. What makes something look blurred
// is the falloff, so the width here only sets how far the falloff reaches.
float FurFuzzWidthGain()
{
    return IsFurFuzzDraw() ? max(_FuzzSoftness, 1.0) : 1.0;
}

// ---------------------------------------------------------------------------
//  The shape of a blurred hair.
//
//  Two falloffs, and both are needed.
//
//  ACROSS the strand, opacity has to fall from the middle to the edge. A blur kernel is a
//  bell, not a slab; the squared cosine-ish shoulder below is the cheapest thing close
//  enough to one. Without it the fuzz layer has a hard boundary and the eye reads it as
//  transparency instead of softness, whatever its opacity.
//
//  ALONG it, opacity has to fade toward the tip - and this is what removes the spikes. The
//  fuzz strand is deliberately longer than the coat, so without a fade its end is a hard
//  edge sticking out past the silhouette. At a fraction of the hair count those ends do not
//  overlap enough to merge, so each one is individually visible: the coat sprouts spikes.
//  Faded, the same geometry dissolves into the haze it was supposed to be.
//
//  lateral: 0 at the strand's centre, 1 at its edge.
//  along:   0 at the root, 1 at the tip.
// ---------------------------------------------------------------------------
float FurFuzzProfile(float lateral, float along)
{
    float side = saturate(1.0 - lateral);
    float tip  = saturate(1.0 - along);

    // Squared on both axes: linear falloff still leaves a visible edge where it reaches
    // zero, because the eye finds the discontinuity in the SLOPE. Squaring lands it softly.
    return side * side * tip * tip;
}
// Collision is NOT a uniform here: it is resolved per vertex against real
// collider primitives - see FurShellCollision.hlsl for the arrays and for why a
// single force vector could not do the job.


// ---------------------------------------------------------------------------
//  Fur GEOMETRY - only the strand type uses any of this.
//
//  Both of those types draw two things per object: the SKIN (the source mesh,
//  once) and the FUR (a batch mesh of strand ribbons, instanced). One
//  shader serves both, and _FurGeometryMode is which of the two this draw is.
//  It is a per-draw uniform, so the branch on it is fully coherent - every lane
//  in the draw takes the same side and the other side costs nothing.
// ---------------------------------------------------------------------------

/// One strand root, planted on the source mesh by FurStrandDistribution.
///
/// Laid out as six float4-sized members rather than as the natural
/// float3/float3/float4/float2... sequence. Structured buffers are tightly packed
/// and the C# struct is too, so the natural layout WOULD match - but it matches by
/// arithmetic that has to be redone in the head every time either side is touched,
/// and a mismatch shows up as fur planted in a scatter of wrong places rather than
/// as an error. Every member starting on a 16-byte boundary removes the question.
struct FurRootData
{
    float4 positionAndSeedX;    // xyz: root position in mesh object space, w: random 0..1
    float4 normalAndSeedY;      // xyz: surface normal (object space),      w: random 0..1
    float4 tangentOS;           // xyz: surface tangent, w: handedness sign
    // xy: UV0 at the root - the strand grid and _BaseMap live here.
    // zw: the DETAIL UV at the root (see _FurDetailUV). Equal to xy on a mesh with no
    //     second UV set, so a ribbon coat never has to ask whether one was planted.
    float4 uvAndDetailUV;
    uint4  boneIndex;           // Skinning influences into _FurSkinMatrices
    float4 boneWeight;          // ...and their weights (0 for an unskinned mesh)
};

float    _FurGeometryMode;      // 0 = this draw is the skin, 1 = this draw is the fur
// 1 = this component owns the surface the fur grows out of and draws it; 0 = the object's
// own renderer is visible and draws it, so the base layer must not put a second copy on top.
float    _FurDrawSkin;
// 1 = a Fur Controller submitted this draw, so the instance index is a layer (or root batch)
// index. 0 = anything else - most often a MeshRenderer that carries the fur material itself,
// which Unity may batch with other renderers through GPU instancing. There the instance index
// only says where the object landed in that batch, and reading it as a layer clipped whole
// bodies as though they were fur. See GetFurShellIndex.
float    _FurInstancedDraw;
float    _FurInstanceCount;     // Roots actually being drawn (the LOD result)
float    _FurRootsPerBatch;     // Roots carried by one instance of the batch mesh
float    _FurSkinned;           // 1 = resolve roots through _FurSkinMatrices
float4x4 _FurObjectToWorld;     // Object-to-world for the fur draw, supplied explicitly
// Its inverse, supplied rather than derived. A ribbon draw carries no per-instance
// transform, so unity_WorldToObject is not the matrix this object is drawn with and
// the motion vector pass cannot use it to get back to object space.
float4x4 _FurWorldToObject;

/// One bone's skinning transform: bone.localToWorld * bindpose, which takes a point
/// from mesh object space straight to WORLD space.
///
/// Three explicit float4 ROWS rather than a float4x4. A float4x4 in a structured
/// buffer is read back under whichever matrix packing the compiler happens to be
/// using, while UnityEngine.Matrix4x4 always stores its floats column-major - so the
/// two agree or disagree depending on a compiler default that is invisible from
/// either side. When they disagree the matrix arrives TRANSPOSED, and a transposed
/// skinning matrix does not error: it scatters every hair to a plausible-looking
/// wrong place, which is a long afternoon to track down.
///
/// Rows and dot products cannot be misread. The fourth row of an affine transform is
/// always (0,0,0,1), so it is not stored.
struct FurSkinMatrix
{
    float4 row0;
    float4 row1;
    float4 row2;
};

#if defined(_FURTYPE_STRANDS)
StructuredBuffer<FurRootData> _FurRoots;
StructuredBuffer<FurSkinMatrix> _FurSkinMatrices;
#endif

// --- Textures (always outside the constant buffer) ---
// Per-object REGIONAL wetness, R channel, indexed by UV0. Written every frame by
// FurWetnessMap into a small RGHalf target (see FurWetnessSplat.shader), so only
// the part of the surface that actually touched water gets wet - and stays wet
// after leaving it. Defaults to "black" (dry) in the Properties block so the
// material previews and the no-controller fallback path do not read as soaked.
TEXTURE2D(_WetnessMap);     SAMPLER(sampler_WetnessMap);

// --- Per-object DETAIL MAPS, indexed by UV0 ---
//
// Baked once by FurAttributeMap (see FurAttributeBake.shader) from an authored
// texture into this object's own unwrap, at a resolution the object chooses.
// Unlike the wetness map these are not re-rendered per frame - they are authored
// data, so they change only when something asks them to.
//
// Each is a MULTIPLIER of the material value it modulates, never a replacement.
// That is what keeps a single material serving objects with and without maps: an
// unmapped object multiplies by one and looks exactly as it did, and a mapped one
// still reads its shape from the sliders an artist tuned.
//
// Defaults are "white" in the Properties block so the material preview and the
// no-controller fallback path resolve to a multiplier of 1 rather than to black,
// which for the length map would mean no fur at all.
TEXTURE2D(_FurLengthMap);    SAMPLER(sampler_FurLengthMap);
TEXTURE2D(_FurThicknessMap); SAMPLER(sampler_FurThicknessMap);
TEXTURE2D(_FurColorMap);     SAMPLER(sampler_FurColorMap);
TEXTURE2D(_FurCombMap);      SAMPLER(sampler_FurCombMap);
// _BaseMap, sampler_BaseMap, _BumpMap and sampler_BumpMap come from SurfaceInput.hlsl

// ---------------------------------------------------------------------------
//  Resolved wetness at a point on the surface.
//
//  _Wetness acts as a FLOOR rather than a scale: it is the "everything is at
//  least this wet" control (a global rain soak, a scripted fade) while the map
//  adds regional wetness on top. A scale would make the map useless whenever the
//  global value sat at zero, which is its normal resting value.
//
//  Sampled at LOD 0 explicitly: the map has no mips, and the fragment-side call
//  sites sit after a discard, where implicit derivatives are not reliable.
//
//  NOTE: this takes the resolved DETAIL UV (see _FurDetailUV), never the
//  _BaseMap-tiled one. The map is rasterised from the mesh's own unwrap, so the
//  material's tiling would misregister it.
// ---------------------------------------------------------------------------
float SampleFurWetness(float2 detailUV)
{
    // Uniform branch, so a whole draw takes one side of it and the texture fetch
    // disappears entirely when the wetness map is switched off. Worth a branch:
    // this is called from the VERTEX stage as well, once per vertex per shell, and
    // a vertex texture fetch is not cheap.
    if (_WetnessEnabled < 0.5)
        return saturate(_Wetness);

    float regional = SAMPLE_TEXTURE2D_LOD(_WetnessMap, sampler_WetnessMap, detailUV, 0).r;
    return saturate(max(_Wetness, regional));
}

// ---------------------------------------------------------------------------
//  Detail maps.
//
//  All three take the resolved DETAIL UV (see _FurDetailUV), not the _BaseMap-tiled
//  one, for the same reason the wetness map does: they are rasterised from the
//  mesh's own unwrap, so applying the material's tiling on top would misregister
//  them against the surface they describe.
//
//  Sampled at LOD 0 explicitly. The maps carry no mips, the length one is read
//  from the VERTEX stage where derivatives do not exist at all, and the fragment
//  call sites sit around a discard where they are not reliable.
//
//  Each opens with a uniform branch, so a whole draw takes one side of it and the
//  fetch disappears entirely when the map is off. Worth a branch even in the
//  fragment stage: the thickness map is read in four passes, and the length map
//  once per vertex per shell - a vertex texture fetch is not cheap.
// ---------------------------------------------------------------------------

// Multiplier on _FurLength. Mid-grey leaves the material's length alone, black shaves
// the region bare, white doubles it - see FUR_DETAIL_SCALE_MAX for why neutral sits in
// the middle rather than at white.
float SampleFurLengthScale(float2 detailUV)
{
    if (_FurLengthMapEnabled < 0.5)
        return 1.0;

    float m = saturate(SAMPLE_TEXTURE2D_LOD(_FurLengthMap, sampler_FurLengthMap, detailUV, 0).r);
    return m * FUR_DETAIL_SCALE_MAX;
}

// Multiplier on _Thickness - strand WIDTH, not strand count. Thinning a region this way
// makes the coat there read as finer and wispier at full height, which is a different
// look from shortening it; thickening it gives coarse guard hair. Mid-grey is neutral,
// exactly as for length.
float SampleFurThicknessScale(float2 detailUV)
{
    if (_FurThicknessMapEnabled < 0.5)
        return 1.0;

    float m = saturate(SAMPLE_TEXTURE2D_LOD(_FurThicknessMap, sampler_FurThicknessMap, detailUV, 0).r);
    return m * FUR_DETAIL_SCALE_MAX;
}

// Multiplier on albedo, applied ALONGSIDE the root/tip gradient rather than in
// place of it. The gradient describes the strand - dark at the skin, bleached at
// the tip - while this describes the region: markings, a white bib, a mask. They
// are different axes of the same coat and both have to survive.
// ---------------------------------------------------------------------------
//  The comb map: which way the hair LIES.
//
//  Everything else that bends a strand is a FORCE - gravity pulls down, wind blows
//  along, the body's own motion drags behind. Forces are global and they change; the
//  coat answers them and springs back. What none of them can express is the thing an
//  animal's coat mostly is: a lie of the hair that is simply THERE, different on the
//  cheek and the flank and the back of the neck, and the same tomorrow as today.
//
//  That is a painted property of the surface, so it is stored like one - a direction per
//  texel, encoded in RG exactly as a normal map encodes its xy, with (0.5, 0.5) meaning
//  no comb at all. The length of the decoded vector carries how HARD the region is
//  combed, so one map holds both the direction and the strength and a half-hearted stroke
//  reads as a half-hearted comb.
//
//  Returned in TANGENT space. World space would be simpler and wrong: the whole point is
//  that a comb running down an animal's back keeps running down its back when the animal
//  turns, and only a direction held relative to the surface does that.
// ---------------------------------------------------------------------------
float2 SampleFurComb(float2 detailUV)
{
    if (_FurCombMapEnabled < 0.5)
        return float2(0.0, 0.0);

    float2 encoded = SAMPLE_TEXTURE2D_LOD(_FurCombMap, sampler_FurCombMap, detailUV, 0).rg;
    return encoded * 2.0 - 1.0;
}

float3 SampleFurColorTint(float2 detailUV)
{
    if (_FurColorMapEnabled < 0.5)
        return float3(1.0, 1.0, 1.0);

    float3 tint = SAMPLE_TEXTURE2D_LOD(_FurColorMap, sampler_FurColorMap, detailUV, 0).rgb
                * _FurColorMapTint.rgb;

    // Blending toward 1 rather than scaling the tint keeps the neutral end
    // NEUTRAL: at strength 0 the map contributes exactly nothing, whatever it
    // contains, so it can be faded in and out at runtime without shifting the
    // coat's brightness on the way.
    return lerp(float3(1.0, 1.0, 1.0), tint, saturate(_FurColorMapTint.a));
}

#endif // FUR_SHELL_INPUT_INCLUDED
