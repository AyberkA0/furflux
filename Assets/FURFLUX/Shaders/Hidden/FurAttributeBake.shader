// ---------------------------------------------------------------------------
//  Hidden/FURFLUX/Attribute Bake
//
//  Resamples an authored detail texture into a per-object map that lives in the
//  mesh's OWN UV0 layout, at a resolution the FurController chooses.
//
//  Why a bake at all, when the fur shader could sample the authored texture
//  directly
//  ---------------------------------------------------------------------------
//  Three reasons, and none of them is filtering:
//
//   1. RESOLUTION becomes a per-object decision rather than a property of the
//      asset. A squirrel that fills the screen and one that never gets closer
//      than twenty metres can share the same authored length map and still pay
//      different memory for it.
//   2. The result is a RENDER TEXTURE, so gameplay can write into it - shave a
//      patch, scorch it, dye it - and the fur picks the change up on the next
//      frame without touching the source asset.
//   3. UV SEAMS get padded exactly once (pass 1), instead of the fur shader
//      having to cope with an unpadded authored texture in four passes.
//
//  Pass 0 "Bake" rasterises the mesh with its UV0 used AS the clip-space
//  position, so one fragment lands on each texel of the object's surface - the
//  same trick FurWetnessSplat.shader uses. The difference is what the fragment
//  does with itself: wetness tests a WORLD position against water volumes and so
//  must run every frame, while this only resamples a texture and therefore runs
//  once, when something it depends on actually changes.
//
//  Pass 1 "Dilate" pads the UV islands by one texel. Rasterisation only fills
//  texels whose centre lands inside a triangle, so island borders would read as
//  zero - fur of zero length in a hairline around every seam. Uncovered texels
//  take the average of their covered neighbours; covered texels pass through
//  untouched, so nothing bleeds across the surface itself.
//
//    RGB = the resampled value (R alone for the scalar maps)
//    A   = coverage (1 = this texel belongs to the mesh)
// ---------------------------------------------------------------------------
Shader "Hidden/FURFLUX/Attribute Bake"
{
    // No Properties block on purpose: every input is supplied per bake through a
    // MaterialPropertyBlock from FurAttributeMap, and the material is created at
    // runtime, so there is nothing for an inspector to author.
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        HLSLINCLUDE
        #pragma target 4.5
        #pragma exclude_renderers gles3 glcore

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_FurAttributeSource);   SAMPLER(sampler_FurAttributeSource);
        TEXTURE2D(_FurAttributePrev);

        // Passed explicitly rather than relying on Unity's automatic
        // _FurAttributePrev_TexelSize, which is not a documented guarantee for
        // textures bound through a MaterialPropertyBlock. xy = size in texels.
        float4 _FurAttributeTargetSize;

        // What an unsourced map bakes to. NOT simply white: for the two scalar maps
        // the value meaning "leave the material alone" is mid-grey, because the
        // encoding has to leave room above neutral for a map that LENGTHENS fur as
        // well as one that shortens it (see FUR_DETAIL_SCALE_MAX). Supplied by the
        // caller rather than assumed here, because it differs per channel.
        float4 _FurAttributeNeutral;
        float  _FurAttributeHasSource;
        // Scale and offset the source texture is applied with, exactly as a material's
        // _ST would. A texture tiled 3x3 on the model must be resampled tiled 3x3 here, or
        // the baked map is the same picture at a different frequency - and fur coloured
        // from it drifts out of phase with the mesh it is supposed to match.
        float4 _FurAttributeSourceST;

        // --- Brush (pass 2) ---
        // The brush is a SPHERE IN WORLD SPACE, not a circle in UV space, and that
        // is the whole reason the paint pass rasterises the mesh instead of just
        // stamping a quad into the texture. UV density is never uniform: a stamp of
        // fixed UV radius is a fat blob on a tightly packed island and a pinprick on
        // a loose one, and it stops dead at every seam. Measuring the distance from
        // each texel's own world position makes the stroke the same size everywhere
        // on the model and lets it cross seams without a join.
        float4 _FurBrushCenter;   // xyz world position, w radius in metres
        float4 _FurBrushDir;      // xyz brush ray direction, w = 1 in Solid mode
        float4 _FurBrushValue;    // rgb target value the stroke pushes toward
        float4 _FurBrushParams;   // x = strength (flow), y = hardness 0..1, z = has occlusion

        // 1 = this stroke is a COMB. _FurBrushValue.xyz is then a world-space DIRECTION
        // rather than a colour, and each texel encodes it into its own tangent frame - see
        // the fragment below for why that conversion cannot be done on the CPU.
        float _FurBrushComb;

        // Depth of the model as seen from the brush, captured once when a stroke starts
        // (pass 3). Solid mode projects each texel into this and rejects any that
        // something else on the model is standing in front of - which is the difference
        // between "the surface I can see" and "any surface facing roughly my way".
        TEXTURE2D(_FurBrushDepth);  SAMPLER(sampler_FurBrushDepth);
        float4x4 _FurBrushViewProj;
        float4   _FurBrushDepthSize;   // xy = size in texels, zw = 1/size

        // Which UV set to unwrap into. 0 = UV0, 1 = UV1 (mesh.uv2).
        //
        // This map stores one value per piece of SURFACE, so every piece of surface has
        // to own a texel. UV0 frequently does not give it one - mirrored and overlapped
        // islands are a texturing economy, and a perfectly reasonable one - and where it
        // does not, two unrelated places share a texel and painting either changes both.
        // The lightmap set is guaranteed non-overlapping, which is exactly the property
        // this needs. Must match _FurDetailUV in FurShellInput.hlsl: the map is written
        // here and read there, so a disagreement puts the paint somewhere else entirely.
        float _FurDetailUV;

        float2 FurBakeUV(float2 uv0, float2 uv1)
        {
            return _FurDetailUV > 0.5 ? uv1 : uv0;
        }
        ENDHLSL

        // -------------------------------------------------------------------
        //  Pass 0 - Bake
        // -------------------------------------------------------------------
        Pass
        {
            Name "Bake"

            HLSLPROGRAM
            #pragma vertex   FurAttributeBakeVertex
            #pragma fragment FurAttributeBakeFragment

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 texcoord   : TEXCOORD0;
                float2 texcoord1  : TEXCOORD1;   // mesh.uv2 - see _FurDetailUV
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings FurAttributeBakeVertex(Attributes input)
            {
                Varyings output;

                // UV0 IS the clip-space position: the mesh is unwrapped flat into
                // the render target. z sits inside the clip range and depth is off,
                // so no projection matrix is involved.
                // The layout this map is stored in, which is the one the fur shader will
                // read it back with. Note the SOURCE is still sampled by UV0 below: the
                // authored texture was painted against the material's unwrap, and moving
                // the storage does not move what the artist drew.
                float2 layoutUV = FurBakeUV(input.texcoord, input.texcoord1);

                float2 ndc = layoutUV * 2.0 - 1.0;
            #if UNITY_UV_STARTS_AT_TOP
                // Keep the write in the same orientation the fur shader will later
                // read with SAMPLE_TEXTURE2D(detailUV).
                ndc.y = -ndc.y;
            #endif

                output.positionCS = float4(ndc, 0.5, 1.0);
                output.uv = input.texcoord;

                return output;
            }

            float4 FurAttributeBakeFragment(Varyings input) : SV_Target
            {
                // An unsourced map is a legitimate state - it is where a painted map
                // starts - so it bakes the channel's neutral value rather than being
                // treated as an error or left at whatever the surface held.
                if (_FurAttributeHasSource < 0.5)
                    return float4(_FurAttributeNeutral.rgb, 1.0);

                // LOD 0 explicitly. The rasterisation here is a flat unwrap, so the
                // screen-space derivatives describe the UV LAYOUT rather than how
                // large this surface is on screen - letting the hardware pick a mip
                // from them would blur detail in tightly packed islands and keep it
                // in loose ones, for no reason connected to the final image.
                float2 sourceUV = input.uv * _FurAttributeSourceST.xy + _FurAttributeSourceST.zw;
                float4 s = SAMPLE_TEXTURE2D_LOD(_FurAttributeSource, sampler_FurAttributeSource,
                                                sourceUV, 0);

                return float4(s.rgb, 1.0);
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  Pass 1 - Dilate (UV island padding)
        // -------------------------------------------------------------------
        Pass
        {
            Name "Dilate"

            HLSLPROGRAM
            #pragma vertex   FurAttributeDilateVertex
            #pragma fragment FurAttributeDilateFragment

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings FurAttributeDilateVertex(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                return output;
            }

            float4 FurAttributeDilateFragment(Varyings input) : SV_Target
            {
                int2 texel = int2(input.positionCS.xy);
                int2 maxTexel = int2(_FurAttributeTargetSize.xy) - 1;

                float4 center = LOAD_TEXTURE2D(_FurAttributePrev, texel);

                // Coverage is THREE-valued, not a flag, and the middle value is what
                // lets this pass run more than once. 1 means the texel is really on
                // the mesh and is authoritative; 0.5 means a previous dilate invented
                // it from its neighbours; 0 means empty.
                //
                // Marking invented texels 1 - the obvious thing - would freeze the
                // padding at whatever the first bake produced, so a later brush stroke
                // would update the island interiors and leave a stale ring of old
                // colour around every seam. Keeping them at 0.5 means every dilate
                // recomputes them from whatever the interior says now.
                if (center.a > 0.75)
                    return float4(center.rgb, 1.0);

                float3 sum = 0.0;
                float  covSum = 0.0;

                [unroll]
                for (int dy = -1; dy <= 1; dy++)
                {
                    [unroll]
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int2 c = clamp(texel + int2(dx, dy), int2(0, 0), maxTexel);
                        float4 s = LOAD_TEXTURE2D(_FurAttributePrev, c);

                        // Only REAL texels seed the padding. Letting a previously
                        // invented one contribute would spread the map another texel
                        // outward on every dilate, so a few brush strokes would slowly
                        // bleed each island across the whole atlas.
                        float w = s.a > 0.75 ? 1.0 : 0.0;
                        sum    += s.rgb * w;
                        covSum += w;
                    }
                }

                if (covSum < 1e-4)
                    return float4(0.0, 0.0, 0.0, 0.0);

                // 0.5: valid enough for the fur shader's bilinear filter to blend
                // toward across the seam, but still marked as invented so the next
                // dilate recomputes it. See the note above.
                return float4(sum / covSum, 0.5);
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  Pass 2 - Paint (one brush stamp)
        // -------------------------------------------------------------------
        Pass
        {
            Name "Paint"

            // RGB lerps toward the brush value by the weight the fragment computes;
            // ALPHA is left exactly as it was. Alpha carries coverage, and a brush
            // stroke changes what the surface looks like, never which texels belong
            // to the mesh.
            //
            // Blending rather than read-modify-write on purpose: a stroke is dozens
            // of stamps a second, and reading the previous value would mean copying
            // the whole map to a scratch surface before every one of them.
            Blend SrcAlpha OneMinusSrcAlpha, Zero One

            HLSLPROGRAM
            #pragma vertex   FurPaintVertex
            #pragma fragment FurPaintFragment

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;     // the frame a comb stroke is stored in
                float2 texcoord   : TEXCOORD0;
                float2 texcoord1  : TEXCOORD1;   // mesh.uv2 - see _FurDetailUV
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float4 tangentWS  : TEXCOORD2;   // xyz tangent, w handedness
            };

            Varyings FurPaintVertex(Attributes input)
            {
                Varyings output;

                // Same unwrap-to-clip trick as the bake: the chosen UV set places the
                // fragment in the texture, while the world position and normal below say
                // where that piece of surface actually is, so the brush can test it.
                //
                // The stroke is decided entirely in world space, so which set is used here
                // changes only WHERE THE ANSWER IS STORED - never which surface the brush
                // marks. That separation is what makes moving to a non-overlapping set fix
                // paint appearing in two places without altering a single stroke.
                float2 ndc = FurBakeUV(input.texcoord, input.texcoord1) * 2.0 - 1.0;
            #if UNITY_UV_STARTS_AT_TOP
                ndc.y = -ndc.y;
            #endif

                output.positionCS = float4(ndc, 0.5, 1.0);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.normalWS   = TransformObjectToWorldNormal(input.normalOS);
                output.tangentWS  = float4(TransformObjectToWorldDir(input.tangentOS.xyz),
                                           input.tangentOS.w * GetOddNegativeScale());

                return output;
            }

            // ---------------------------------------------------------------------
            //  A world-space comb direction, written down in this texel's own frame.
            //
            //  This is the one thing about combing that cannot be resolved on the CPU. The
            //  artist drags in world space and means one thing by it - "lay the hair this
            //  way" - but there is no single frame to record that in: the tangent frame is
            //  different at every texel, and a comb has to be stored relative to the
            //  surface or it stops meaning anything the moment the animal turns around.
            //  So the conversion happens here, per texel, while the stroke lands.
            //
            //  The component along the NORMAL is dropped rather than kept. A comb lays hair
            //  DOWN across the surface; the part of the artist's drag pointing into or out
            //  of the model is them moving the mouse over a curved shape, not an intention,
            //  and honouring it would lengthen the fur on one side of a limb and shorten it
            //  on the other.
            //
            //  Encoded around a half, exactly as a normal map encodes its xy, so that the
            //  ordinary alpha blend below does the right thing when strokes overlap: two
            //  combs average, and combing back the way you came cancels to uncombed rather
            //  than to some third direction.
            // ---------------------------------------------------------------------
            float3 EncodeComb(float3 directionWS, float3 normalWS, float4 tangentWS)
            {
                float3 n = SafeNormalize(normalWS);
                float3 t = tangentWS.xyz - n * dot(n, tangentWS.xyz);

                if (dot(t, t) < 1e-10)
                    return float3(0.5, 0.5, 0.0);

                t = SafeNormalize(t);
                float3 b = tangentWS.w * cross(n, t);

                float3 flat = directionWS - n * dot(n, directionWS);

                // Clamped rather than normalised: length IS the strength, so a light
                // stroke has to stay a light comb. The clamp only stops an over-long
                // direction from encoding outside the texture's range.
                float2 combTS = float2(dot(flat, t), dot(flat, b));
                float len = length(combTS);
                if (len > 1.0)
                    combTS /= len;

                return float3(combTS * 0.5 + 0.5, 0.0);
            }

            float4 FurPaintFragment(Varyings input) : SV_Target
            {
                float radius = max(_FurBrushCenter.w, 1e-4);
                float d = length(input.positionWS - _FurBrushCenter.xyz) / radius;

                // Hardness reshapes the falloff rather than scaling it: at 0 the
                // weight ramps across the whole radius, at 1 it is flat to the very
                // edge. Both keep the same footprint, which is what the on-screen
                // brush circle is promising.
                float w = saturate((1.0 - d) / max(1.0 - _FurBrushParams.y, 1e-3));
                w = w * w * (3.0 - 2.0 * w);            // smoothstep

                // Wireframe mode stops here: the sphere is the whole test, so back faces
                // and occluded surfaces are painted exactly like visible ones.
                if (_FurBrushDir.w > 0.5)
                {
                    // --- Solid, part one: face the brush ---
                    float facing = dot(SafeNormalize(input.normalWS), -_FurBrushDir.xyz);

                    if (_FurBrushParams.z > 0.5)
                    {
                        // With a depth capture available, DEPTH is the authority on what
                        // is visible and this test is only here to kill surfaces pointing
                        // frankly backwards. Keeping the hard cut at 90 degrees was wrong:
                        // the walls of a tight crevice - between fingers, inside an ear -
                        // sit within a degree or two of perpendicular, so half of them
                        // fell on the far side of the cut and the brush refused to paint
                        // exactly the places that are hardest to reach by hand.
                        w *= smoothstep(-0.35, -0.05, facing);
                    }
                    else
                    {
                        // No capture: facing is the only information there is, so it has
                        // to be the strict test.
                        w *= step(0.0, facing);
                    }

                    // --- Solid, part two: be the nearest such surface ---
                    // Facing alone is not enough. The far leg of an animal faces the
                    // brush just as squarely as the near one, so a wide stroke marked
                    // both and the mode only looked correct on a convex shape.
                    if (_FurBrushParams.z > 0.5)
                    {
                        float4 clip = mul(_FurBrushViewProj, float4(input.positionWS, 1.0));
                        float3 ndc = clip.xyz / max(abs(clip.w), 1e-6) * sign(clip.w);
                        float2 depthUV = ndc.xy * 0.5 + 0.5;
                    #if UNITY_UV_STARTS_AT_TOP
                        depthUV.y = 1.0 - depthUV.y;
                    #endif

                        // Four taps, keeping the MOST PERMISSIVE - the one describing the
                        // farthest surface. The capture rasterises triangles at screen
                        // resolution while this rasterises texels in UV space, so a texel
                        // lands between depth samples and a single tap snaps to whichever
                        // neighbour happens to be nearest. At a silhouette that neighbour
                        // is often the background, and a single tap therefore rejected
                        // large parts of the very surface being painted.
                        float2 halfTexel = _FurBrushDepthSize.zw * 0.5;
                        float d0 = SAMPLE_TEXTURE2D_LOD(_FurBrushDepth, sampler_FurBrushDepth, depthUV + float2(-halfTexel.x, -halfTexel.y), 0).r;
                        float d1 = SAMPLE_TEXTURE2D_LOD(_FurBrushDepth, sampler_FurBrushDepth, depthUV + float2( halfTexel.x, -halfTexel.y), 0).r;
                        float d2 = SAMPLE_TEXTURE2D_LOD(_FurBrushDepth, sampler_FurBrushDepth, depthUV + float2(-halfTexel.x,  halfTexel.y), 0).r;
                        float d3 = SAMPLE_TEXTURE2D_LOD(_FurBrushDepth, sampler_FurBrushDepth, depthUV + float2( halfTexel.x,  halfTexel.y), 0).r;

                        // A tolerance is required, not optional: the two passes rasterise
                        // the same surface differently, so an exact compare rejects it.
                        // Scaled by the depth value because reversed Z is wildly
                        // non-uniform - a constant that is generous up close is nothing
                        // at all further away.
                    #if UNITY_REVERSED_Z
                        float stored = min(min(d0, d1), min(d2, d3));
                        float bias = max(1e-4, abs(ndc.z) * 2e-3);
                        w *= step(stored - bias, ndc.z);
                    #else
                        float stored = max(max(d0, d1), max(d2, d3));
                        float bias = max(1e-4, abs(ndc.z) * 2e-3);
                        w *= step(ndc.z, stored + bias);
                    #endif

                        // Off screen in the capture means there is nothing to compare
                        // against, so the depth test abstains rather than guessing.
                        float onScreen = step(0.0, depthUV.x) * step(depthUV.x, 1.0)
                                       * step(0.0, depthUV.y) * step(depthUV.y, 1.0);
                        w = lerp(0.0, w, onScreen);
                    }
                }

                // A comb writes a direction, everything else writes the value it was
                // given. Same coverage, same falloff, same occlusion test - only what
                // lands in RGB differs.
                float3 written = _FurBrushComb > 0.5
                    ? EncodeComb(_FurBrushValue.xyz, input.normalWS, input.tangentWS)
                    : _FurBrushValue.rgb;

                return float4(written, w * saturate(_FurBrushParams.x));
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  Pass 3 - Occlusion capture
        // -------------------------------------------------------------------
        //  Renders the model normally - world to clip, depth on - from the brush's
        //  viewpoint, writing each fragment's own depth. Solid mode reads it back in
        //  pass 2 to tell a visible surface from one hidden behind the body.
        //
        //  Captured once when a stroke starts rather than per stamp: the view cannot
        //  change while the cursor is held down, so re-rendering the model for every
        //  stamp would be paying for an answer that has not moved.
        // -------------------------------------------------------------------
        Pass
        {
            Name "Occlusion"

            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex   FurOcclusionVertex
            #pragma fragment FurOcclusionFragment

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings FurOcclusionVertex(Attributes input)
            {
                Varyings output;

                // The real projection this time, not the unwrap trick - this pass is
                // about where the surface is on SCREEN, which is the whole point.
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = mul(_FurBrushViewProj, float4(positionWS, 1.0));

                return output;
            }

            float4 FurOcclusionFragment(Varyings input) : SV_Target
            {
                // Stored as colour rather than read back out of a depth-format surface:
                // sampling depth formats varies by platform, and this pass has no reason
                // to care about any of that.
                return float4(input.positionCS.z, 0.0, 0.0, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
