// ---------------------------------------------------------------------------
//  Fur screen-space fuzz.
//
//  The other fuzz layer draws the coat a second time - the same hairs, longer and much
//  wider - and softens THOSE. This one never touches geometry. It takes the pixels the
//  coat already drew, blurs them, and lays the result back down everywhere the coat
//  itself is not. So what spreads is the 2D projection of the real hairs, carrying their
//  real colours and their real lighting, rather than a second set of hairs standing in
//  for a blur of them.
//
//  Two things fall out of that, and they are the two the geometric layer buys with a
//  whole extra coat:
//
//    * A halo. The blur reaches past the silhouette into the background, which is the
//      soft edge no alpha-clipped coat can have on its own.
//    * Fullness. The gaps BETWEEN hairs are not marked - the coat's stencil mark is
//      written after the strand clip, so it follows the hairs and not the triangles they
//      were cut from - so the haze lands in the gaps and the coat reads denser without a
//      hair being added.
//
//  Three passes: mark-limited extract, separable blur at half resolution, composite.
//  The stencil is what carries the coat's identity between them - see FurShellLit's
//  ForwardLit pass for where the mark is written.
// ---------------------------------------------------------------------------
Shader "Hidden/FURFLUX/Screen Fuzz"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        ZWrite Off
        ZTest Always
        Cull Off

        HLSLINCLUDE
        #pragma target 4.5
        #pragma exclude_renderers gles3 glcore

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        // x = blur radius in SOURCE pixels, y = fuzz amount, z/w spare.
        float4 _FurFuzzParams;

        // One tap's step, in the source texture's UV. The blur is separable, so this is
        // horizontal on one pass and vertical on the next and the shader itself does not
        // care which.
        float2 _FurFuzzBlurStep;

        #define FUR_FUZZ_TAPS 7   // per side; 15 samples across the kernel

        // A Gaussian wide enough that its own tail is what fades, rather than the kernel
        // running out of taps. sigma = radius / 2 puts the last tap at two sigma, where
        // the weight has fallen to about 1.8% - far enough that the truncation does not
        // leave a visible ring at the edge of the halo.
        float FurFuzzWeight(float tap)
        {
            float sigma = max(FUR_FUZZ_TAPS, 1.0) * 0.5;
            float x = tap / sigma;
            return exp(-0.5 * x * x);
        }
        ENDHLSL

        // -------------------------------------------------------------------
        //  1. Extract - the coat's own pixels, and nothing else.
        //
        //  The stencil does the selecting, so this pass writes only where the coat
        //  actually landed and the target's clear value survives everywhere else. That
        //  clear is what makes the blur behave: the surrounding zero is transparent
        //  BLACK in premultiplied terms, so spreading into it dilutes coverage without
        //  dragging any colour along, which is exactly what a hair's edge should do.
        //
        //  Premultiplied on purpose. Blurring straight alpha and straight colour
        //  separately lets a nearly-transparent pixel's colour count as much as an
        //  opaque one's, and the halo picks up whatever happened to be in the empty
        //  texels around the coat.
        // -------------------------------------------------------------------
        Pass
        {
            Name "FurFuzzExtract"

            Stencil
            {
                Ref      [_FuzzStencilRef]
                ReadMask [_FuzzStencilReadMask]
                Comp     Equal
            }

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 color = SAMPLE_TEXTURE2D_X_LOD(
                    _BlitTexture, sampler_LinearClamp, input.texcoord, 0).rgb;

                // Alpha 1 because the mark means full coverage: the fragment survived the
                // strand clip, so this pixel IS hair. Everything the blur reads outside
                // the mark is the cleared zero.
                return float4(color, 1.0);
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  2. Blur - one axis, run twice.
        //
        //  No stencil here. The whole point is to spread PAST the mark, so the pass that
        //  reads the extract has to be free to write outside it.
        // -------------------------------------------------------------------
        Pass
        {
            Name "FurFuzzBlur"

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float4 sum    = 0.0;
                float  weight = 0.0;

                [unroll]
                for (int i = -FUR_FUZZ_TAPS; i <= FUR_FUZZ_TAPS; i++)
                {
                    float w = FurFuzzWeight(abs(i));
                    float2 uv = input.texcoord + _FurFuzzBlurStep * i;

                    // Clamped rather than left to the sampler: at the screen edge an
                    // unclamped tap wraps the halo around to the far side, and a coat
                    // walking off the edge of frame drags a bright band with it.
                    uv = clamp(uv, 0.0, 1.0);

                    sum    += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0) * w;
                    weight += w;
                }

                return sum / max(weight, 1e-5);
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------
        //  3. Composite - the haze, everywhere the coat is not.
        //
        //  The stencil is inverted here, and that inversion is the whole trick. Inside
        //  the mark the sharp coat stays exactly as it was drawn: not dimmed, not
        //  softened, not blended with a blurred copy of itself. Outside it - the
        //  background past the silhouette, and the skin showing through between hairs -
        //  the blur is all there is, so that is where it goes.
        // -------------------------------------------------------------------
        Pass
        {
            Name "FurFuzzComposite"

            // PREMULTIPLIED source, which is what the blur produced and what it should
            // stay. Backing out to straight alpha only to have the blend multiply it
            // in again is a division by a coverage that is legitimately near zero all
            // around the edge of the halo - the exact place the result is visible.
            Blend One OneMinusSrcAlpha

            Stencil
            {
                Ref      [_FuzzStencilRef]
                ReadMask [_FuzzStencilReadMask]
                Comp     NotEqual
            }

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float4 blurred = SAMPLE_TEXTURE2D_X_LOD(
                    _BlitTexture, sampler_LinearClamp, input.texcoord, 0);

                // Colour and coverage scale together, which is the one operation
                // premultiplied alpha is closed under: half the strength is half the
                // haze, not a paler haze at the same opacity. Where the coat had no
                // influence the blur left zero here, and zero scaled is still zero -
                // so the composite adds nothing rather than adding black.
                return blurred * saturate(_FurFuzzParams.y);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
