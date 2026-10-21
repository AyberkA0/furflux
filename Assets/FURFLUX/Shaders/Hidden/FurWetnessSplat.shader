// ---------------------------------------------------------------------------
//  Hidden/FURFLUX/Wetness Splat
//
//  Texture-space (UV-space) wetness accumulation for shell fur.
//
//  Pass 0 "Accumulate" rasterises the fur mesh with its UV0 layout used AS the
//  clip-space position, so every fragment corresponds to one texel of the
//  object's wetness map. The fragment still knows its real world position, so
//  it can test itself against the water volumes and write the result into the
//  texel that belongs to that piece of surface.
//
//  This is what makes regional wetness mesh-agnostic: nothing here assumes a
//  sphere, a capsule or any particular silhouette. Any mesh with a sane
//  (non-overlapping) UV0 unwrap works, skinned meshes included - the caller
//  feeds in the baked/deformed mesh, so the world positions follow the skeleton.
//
//  Pass 1 "Dilate" pads the UV islands by one texel. Rasterisation only fills
//  texels whose centre lands inside a triangle, so island borders would sample
//  as permanently dry and show up as hard seams across the fur. The pass copies
//  each *uncovered* texel from the average of its covered neighbours and leaves
//  covered texels untouched, so wetness never bleeds across the surface itself.
//
//  Precision note: the target is RGHalf, not R8. Drying integrates a small
//  per-frame delta (dryingRate * dt ~ 0.002), which an 8-bit target rounds
//  straight back to zero - fur would never dry at all.
//    R = wetness 0..1
//    G = coverage (1 = this texel belongs to the mesh)
// ---------------------------------------------------------------------------
Shader "Hidden/FURFLUX/Wetness Splat"
{
    // No Properties block on purpose: every input is supplied per draw through a
    // MaterialPropertyBlock from FurWetnessMap, and the material is created at
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

        // Must match FurWetnessMap.MaxSources on the C# side.
        #define FUR_MAX_WET_SOURCES 8

        // Which UV set this map is stored in. 0 = UV0, 1 = UV1 (mesh.uv2). Must match
        // _FurDetailUV in FurShellInput.hlsl - this map is written here and read there,
        // and a disagreement puts every wet patch somewhere else on the body.
        //
        // Wetness needs a non-overlapping unwrap for the same reason the painted maps do,
        // and rather more visibly: it is a SIMULATION, so a shared texel does not merely
        // copy an artist's stroke to the wrong place, it makes one soaked paw wet a dry
        // shoulder every frame it stays in the water.
        float _FurDetailUV;

        float2 FurBakeUV(float2 uv0, float2 uv1)
        {
            return _FurDetailUV > 0.5 ? uv1 : uv0;
        }

        TEXTURE2D(_PrevWetness);
        // Passed explicitly rather than relying on Unity's automatic
        // _PrevWetness_TexelSize, which is not a documented guarantee for textures
        // bound through a MaterialPropertyBlock. xy = size in texels.
        float4 _WetTargetSize;

        // Water volumes, resolved on the CPU into rigid transforms + primitive
        // dimensions. The transform is rigid (rotation + translation only), so
        // distances measured in that space are real metres and the feather
        // radius below is a metre value, not some normalised unit.
        float4x4 _WetSourceToLocal[FUR_MAX_WET_SOURCES];
        float4   _WetSourceShape [FUR_MAX_WET_SOURCES];   // box: half extents | sphere: (r,0,0) | capsule: (r, halfSegment, 0)
        float4   _WetSourceParams[FUR_MAX_WET_SOURCES];   // x = type, y = strength, z = feather (metres)
        float    _WetSourceCount;

        float _WettingRate;     // wetness gained per second at full contact
        float _DryingRate;      // wetness lost per second when fully clear of water
        float _DryingEnabled;   // 0 = wetness is permanent until something wets it further
        float _WetDeltaTime;

        // Signed distance to a water volume, in metres. Negative = inside.
        float WaterSourceSDF(float3 positionWS, int index)
        {
            float3 p = mul(_WetSourceToLocal[index], float4(positionWS, 1.0)).xyz;
            float3 s = _WetSourceShape[index].xyz;
            int    type = (int)_WetSourceParams[index].x;

            if (type == 1)                      // sphere
            {
                return length(p) - s.x;
            }
            else if (type == 2)                 // capsule (segment along local Y)
            {
                float3 q = p;
                q.y -= clamp(q.y, -s.y, s.y);
                return length(q) - s.x;
            }

            // type 0: box (also the fallback used for mesh/terrain colliders,
            // which arrive as their world AABB)
            float3 d = abs(p) - s;
            return length(max(d, 0.0)) + min(max(d.x, max(d.y, d.z)), 0.0);
        }

        // How wet this world position wants to be, 0..1, across every volume.
        float EvaluateWaterSources(float3 positionWS)
        {
            float target = 0.0;
            int count = min((int)_WetSourceCount, FUR_MAX_WET_SOURCES);

            for (int i = 0; i < count; i++)
            {
                float sdf     = WaterSourceSDF(positionWS, i);
                float feather = max(_WetSourceParams[i].z, 1e-4);

                // Fully wet inside the volume, ramping to dry `feather` metres
                // outside it - the splash/spray margin around the waterline.
                float coverage = saturate(1.0 - sdf / feather);

                target = max(target, coverage * _WetSourceParams[i].y);
            }

            return saturate(target);
        }
        ENDHLSL

        // -------------------------------------------------------------------
        //  Pass 0 - Accumulate
        // -------------------------------------------------------------------
        Pass
        {
            Name "Accumulate"

            HLSLPROGRAM
            #pragma vertex   WetnessAccumulateVertex
            #pragma fragment WetnessAccumulateFragment

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 texcoord   : TEXCOORD0;
                float2 texcoord1  : TEXCOORD1;   // mesh.uv2 - see _FurDetailUV
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Varyings WetnessAccumulateVertex(Attributes input)
            {
                Varyings output;

                // UV0 IS the clip-space position: the mesh is unwrapped flat
                // into the render target. z is placed inside the clip range and
                // depth is off, so no projection matrix is involved.
                float2 ndc = FurBakeUV(input.texcoord, input.texcoord1) * 2.0 - 1.0;
            #if UNITY_UV_STARTS_AT_TOP
                // Keep the write in the same orientation the fur shader will
                // later read with SAMPLE_TEXTURE2D(uv).
                ndc.y = -ndc.y;
            #endif

                output.positionCS = float4(ndc, 0.5, 1.0);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);

                return output;
            }

            float4 WetnessAccumulateFragment(Varyings input) : SV_Target
            {
                // Read the previous frame's value by texel index rather than by
                // UV. Source and destination are the same size and both are
                // addressed through SV_Position, so any platform-side vertical
                // flip cancels out and cannot desynchronise the feedback loop.
                int2 texel = int2(input.positionCS.xy);
                float prev = LOAD_TEXTURE2D(_PrevWetness, texel).r;

                float target = EvaluateWaterSources(input.positionWS);

                // Rising is driven by contact; drying is gated by (1 - target)
                // so a submerged patch never dries while it is still in water.
                float rise = target * _WettingRate * _WetDeltaTime;
                float fall = (_DryingEnabled > 0.5)
                    ? (1.0 - target) * _DryingRate * _WetDeltaTime
                    : 0.0;

                float wet = saturate(prev + rise - fall);

                return float4(wet, 1.0, 0.0, 0.0);
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
            #pragma vertex   WetnessDilateVertex
            #pragma fragment WetnessDilateFragment

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings WetnessDilateVertex(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                return output;
            }

            float4 WetnessDilateFragment(Varyings input) : SV_Target
            {
                int2 texel = int2(input.positionCS.xy);
                int2 maxTexel = int2(_WetTargetSize.xy) - 1;

                float2 center = LOAD_TEXTURE2D(_PrevWetness, texel).rg;

                // Covered texels are authoritative - passing them through
                // untouched is what stops this filter from smearing wetness
                // across the surface a texel per frame.
                if (center.g > 0.5)
                    return float4(center.r, 1.0, 0.0, 0.0);

                float wetSum = 0.0;
                float covSum = 0.0;

                [unroll]
                for (int dy = -1; dy <= 1; dy++)
                {
                    [unroll]
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int2 c = clamp(texel + int2(dx, dy), int2(0, 0), maxTexel);
                        float2 s = LOAD_TEXTURE2D(_PrevWetness, c).rg;
                        wetSum += s.r * s.g;
                        covSum += s.g;
                    }
                }

                if (covSum < 1e-4)
                    return float4(0.0, 0.0, 0.0, 0.0);

                // Marked as covered so the fur shader's bilinear filter has a
                // valid value to blend toward across the seam.
                return float4(wetSum / covSum, 1.0, 0.0, 0.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
