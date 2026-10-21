#ifndef FUR_SHELL_COLLISION_INCLUDED
#define FUR_SHELL_COLLISION_INCLUDED

// ---------------------------------------------------------------------------
//  Fur collision - per vertex, against real collider geometry.
//
//  What this replaces and why
//  --------------------------
//  The previous model reduced every nearby collider to ONE world-space force
//  vector on the CPU, then bent the whole object's fur along it, masked by
//  dot(normal, -force) to guess which side was in contact. Three things were
//  wrong with that, and none of them were tuning problems:
//
//   1. Summing colliders cancels them. A wall on the left and a wall on the
//      right produced a net force of roughly zero, so fur squeezed between two
//      surfaces reacted to neither.
//   2. It never actually prevented penetration. The push was an artistic bend
//      by an arbitrary amount, so strands still passed through the collider.
//   3. Distance was measured from the object's BOUNDS CENTRE against a radius
//      taken from the AABB's largest extent. For anything that is not roughly
//      a ball - a squirrel, a tail, a limb - that radius is wrong everywhere on
//      the surface at once, so contact fired at the wrong distance.
//
//  Instead, each collider is sent to the GPU as a primitive (see
//  FurColliderPacker.cs) and every strand tip is resolved against all of them
//  independently. Distance is measured from the tip to the actual surface, so
//  there is no body radius to get wrong, opposing colliders cannot cancel, and
//  a strand that would end up inside a collider is moved out of it.
// ---------------------------------------------------------------------------

// Must match FurController.MaxFurColliders on the C# side.
#define FUR_MAX_COLLIDERS 8

float4x4 _FurColliderToLocal[FUR_MAX_COLLIDERS];
float4   _FurColliderShape  [FUR_MAX_COLLIDERS];  // box: half extents | sphere: (r,0,0) | capsule: (r, halfSegment, 0)
float4   _FurColliderParams [FUR_MAX_COLLIDERS];  // x = type (0 box, 1 sphere, 2 capsule), y = margin in meters, z = 1 if thin enough for fur to tunnel through
float    _FurColliderCount;
float    _CollisionStrength;                      // 0 = ignore contacts, 1 = fully resolve them
float    _CollisionSpread;                        // 0 = strands compress in place, 1 = they comb aside at full length

// ---------------------------------------------------------------------------
//  Which way a pressed strand slides along the contact surface.
//
//  This has to be SPATIALLY SMOOTH. The obvious idea - hash the vertex to pick a
//  random direction, on the theory that real fur splays every which way - shreds
//  the shell: neighbouring vertices of the same surface get sent opposite ways
//  and the sheet tears into spikes and flying plates. Fur splays because each
//  strand is its own primitive; a shell is a continuous sheet and cannot be
//  taken apart that way.
//
//  Projecting the strand's own growth direction onto the contact plane is smooth
//  by construction, and it produces the right splay for free: over a curved body
//  the growth direction fans outward, so a pressed patch opens away from its
//  centre. At the exact centre the projection vanishes - correctly, because that
//  strand has no side to fall toward.
// ---------------------------------------------------------------------------
float3 FurContactSlideDir(float3 freeDirWS, float3 surfaceNormalWS)
{
    return freeDirWS - surfaceNormalWS * dot(freeDirWS, surfaceNormalWS);
}

// ---------------------------------------------------------------------------
//  Signed distance only, without the surface normal. Used by the strand-crossing
//  march below, which needs a few distance probes per collider and would
//  otherwise pay for a normal at every one of them.
// ---------------------------------------------------------------------------
float FurColliderDistance(float3 positionWS, int index)
{
    float3 p    = mul(_FurColliderToLocal[index], float4(positionWS, 1.0)).xyz;
    float3 s    = _FurColliderShape[index].xyz;
    int    type = (int)_FurColliderParams[index].x;

    if (type == 1)          // sphere
        return length(p) - s.x;

    if (type == 2)          // capsule, segment along local Y
    {
        float3 q = p;
        q.y -= clamp(q.y, -s.y, s.y);
        return length(q) - s.x;
    }

    float3 e = abs(p) - s;  // box
    return length(max(e, 0.0)) + min(max(e.x, max(e.y, e.z)), 0.0);
}

// ---------------------------------------------------------------------------
//  Does the strand SEGMENT pass through this collider?
//
//  Testing only the tip - which is all the resolve loop used to do - silently
//  fails on anything thinner than the fur is long. A strand standing on a thin
//  floor pokes straight through it and comes out the far side, where the tip is
//  legitimately OUTSIDE the collider, so nothing ever registered a contact and
//  the buried part of the strand stayed dead straight.
//
//  Sphere tracing answers this for any shape without a per-primitive intersection
//  routine: a signed distance field is 1-Lipschitz along a line, so advancing by
//  its own value can never step over a surface. Marching from the root means a
//  thin plate is found on the second probe regardless of how thin it is.
//
//  Running out of steps can only ever MISS a contact, never invent one, and the
//  caller only reaches this code when the collider is already within the strand's
//  reach.
// ---------------------------------------------------------------------------
bool FurStrandCrossesCollider(float3 rootWS, float3 dirWS, float strandLength, int index, float margin)
{
    float t = 0.0;

    [unroll]
    for (int step = 0; step < 5; step++)
    {
        float distance = FurColliderDistance(rootWS + dirWS * t, index);
        if (distance < margin)
            return true;

        // Step by the FULL distance, not distance - margin. Subtracting the
        // margin makes the step collapse to nothing exactly where the march is
        // closing in on the surface, so it stalls a hair short and reports no
        // crossing. The full distance is still safe - it is the radius of a
        // sphere known to be empty - and it converges in two or three probes.
        t += max(distance, 1e-4);
        if (t > strandLength)
            break;
    }

    return false;
}

// ---------------------------------------------------------------------------
//  Signed distance from a world position to one collider, in meters, plus the
//  outward surface normal at that point. Negative distance = inside.
// ---------------------------------------------------------------------------
float FurColliderSDF(float3 positionWS, int index, out float3 normalWS)
{
    float4x4 toLocal = _FurColliderToLocal[index];
    float3   p       = mul(toLocal, float4(positionWS, 1.0)).xyz;
    float3   s       = _FurColliderShape[index].xyz;
    int      type    = (int)_FurColliderParams[index].x;

    float3 normalLocal;
    float  distance;

    if (type == 1)          // sphere
    {
        float len = length(p);
        distance    = len - s.x;
        normalLocal = len > 1e-6 ? p / len : float3(0.0, 1.0, 0.0);
    }
    else if (type == 2)     // capsule, segment along local Y
    {
        float3 q = p;
        q.y -= clamp(q.y, -s.y, s.y);
        float len = length(q);
        distance    = len - s.x;
        normalLocal = len > 1e-6 ? q / len : float3(0.0, 1.0, 0.0);
    }
    else                    // box
    {
        float3 e       = abs(p) - s;
        float3 outside = max(e, 0.0);
        float  outLen  = length(outside);

        distance = outLen + min(max(e.x, max(e.y, e.z)), 0.0);

        if (outLen > 1e-6)
        {
            normalLocal = outside * sign(p) / outLen;
        }
        else
        {
            // Fully inside: leave through the nearest face. Picking the axis of
            // LEAST penetration is what stops a strand rooted just inside a
            // surface from being flung out the far side of the box.
            float3 depth = -e;                    // distance to each face, all >= 0 in here
            float  least = min(depth.x, min(depth.y, depth.z));
            normalLocal  = sign(p) * float3(depth.x <= least ? 1.0 : 0.0,
                                            depth.y <= least ? 1.0 : 0.0,
                                            depth.z <= least ? 1.0 : 0.0);
            normalLocal  = SafeNormalize(normalLocal);
        }
    }

    // toLocal is rigid, so its 3x3 part is R^T. Multiplying the normal as a ROW
    // vector applies the transpose, which is R - exactly the rotation back into
    // world space. No inverse-transpose needed, and no scale to undo.
    normalWS = SafeNormalize(mul(normalLocal, (float3x3)toLocal));

    return distance;
}

// ---------------------------------------------------------------------------
//  Moves a strand tip out of every collider it intersects.
//
//  `strandLength` is this shell's own distance from the root (_FurLength * layerT),
//  which is also what makes the base shell immune: at layerT = 0 the length is 0
//  and the tip collapses back onto the skin, so collision can never tear a hole
//  in the surface itself.
// ---------------------------------------------------------------------------
float3 ResolveFurCollision(float3 rootWS, float3 tipWS, float strandLength, float3 freeDirWS)
{
    int count = min((int)_FurColliderCount, FUR_MAX_COLLIDERS);
    if (count <= 0 || strandLength <= 1e-6 || _CollisionStrength <= 0.0)
        return tipWS;

    float3 p = tipWS;

    for (int i = 0; i < count; i++)
    {
        float margin = _FurColliderParams[i].y;

        // A signed distance field is 1-Lipschitz, so a root further than the
        // strand can reach is proof no part of this strand touches the collider.
        // One probe rejects everything out of range - which is nearly every
        // vertex, nearly every frame - before any of the work below.
        //
        // Distance-only on purpose: the surface normal costs roughly as much
        // again as the distance, and the overwhelmingly common outcome here is
        // the reject, which never needs it.
        float rootDistance = FurColliderDistance(rootWS, i);
        if (rootDistance >= strandLength + margin)
            continue;

        // The margin is what makes contact SOFT: fur starts leaning away while
        // the collider is still short of it, instead of snapping the instant the
        // surfaces touch. That gradual lean is why the old system needed a
        // temporal smoothing filter, and why this one does not - the response is
        // continuous in space, so it is automatically continuous in time.
        float3 tipNormalWS;
        float  tipDistance = FurColliderSDF(p, i, tipNormalWS);

        float  push             = margin - tipDistance;
        float3 surfaceNormalWS  = tipNormalWS;

        // --- Thin colliders ---
        // Two separate ways the tip probe alone gets a thin collider wrong, both
        // of which appear as soon as the fur is longer than the collider is thick:
        //
        //  * The tip punches clean through a thin floor and ends up BELOW it,
        //    genuinely outside the collider, so nothing registers a contact and
        //    the buried part of the strand stays dead straight.
        //  * The tip stops INSIDE the slab, and the nearest way out of a thin box
        //    is frequently the far face - so the strand gets pushed further down,
        //    out through the underside.
        //
        // The root settles both. A strand grows out of the body, so the side its
        // root is on is the side the whole strand belongs on. Where the strand
        // actually crosses the collider, constrain the tip against the tangent
        // plane at the root's own contact point and push along the ROOT's normal,
        // which by construction points back toward the body rather than through
        // the obstacle.
        //
        // This is evaluated even when the tip probe already reports a contact, and
        // the deeper of the two wins: the second failure above happens precisely
        // when the tip probe is confident AND pointing the wrong way, so deferring
        // to it there would keep the bug.
        //
        // Gated on a CPU-side flag: tunnelling is only possible through a collider
        // thinner than the fur is long, and the CPU already knows every collider's
        // smallest dimension. A wall or a solid floor can skip the march outright,
        // which is what keeps the common case at one distance probe per collider.
        if (_FurColliderParams[i].z > 0.5 && rootDistance > 0.0 &&
            FurStrandCrossesCollider(rootWS, freeDirWS, strandLength, i, margin))
        {
            float3 rootNormalWS;
            FurColliderSDF(rootWS, i, rootNormalWS);   // normal is only needed on this rare path

            float3 rootContactWS = rootWS - rootNormalWS * rootDistance;
            float  heightAbove   = dot(p - rootContactWS, rootNormalWS);
            float  rootPush      = margin - heightAbove;

            if (rootPush > push)
            {
                push            = rootPush;
                surfaceNormalWS = rootNormalWS;
            }
        }

        push *= _CollisionStrength;
        if (push <= 0.0)
            continue;

        p += surfaceNormalWS * push;

        // --- Comb the strand aside ---
        //
        // Pushing out of the surface alone only ever SHORTENS the strand, so a
        // pressed patch reads as fur that got cut: it thins out and the surface
        // shows through. Real fur barely compresses - the strands keep their
        // length and lie over sideways, which empties the middle of the contact
        // and PILES UP around its rim. Strand count is conserved, it just
        // redistributes.
        //
        // A rigid strand of this length whose tip has been driven `push` closer
        // to its root must move exactly this far sideways to stay that long, so
        // the amount is geometry rather than a tuning value: at full crush the
        // lateral offset equals the strand length and the strand lies flat.
        float remaining = max(strandLength - push, 0.0);
        float lateral   = sqrt(max(strandLength * strandLength - remaining * remaining, 0.0))
                        * _CollisionSpread;
        if (lateral <= 1e-6)
            continue;

        float3 tangent = FurContactSlideDir(freeDirWS, surfaceNormalWS);
        float  tangentLength = length(tangent);
        if (tangentLength > 1e-4)
            p += tangent * (lateral / tangentLength);
    }

    // Bending must not STRETCH the strand - but it must be allowed to shorten
    // the chord from root to tip, because that is what bending physically does.
    //
    // Clamping to at most strandLength rather than re-seating to exactly it
    // matters more than it looks. Forcing the exact length silently undoes the
    // whole correction whenever the push is collinear with the strand: a strand
    // standing straight up under a flat surface gets pushed straight down, the
    // root-to-tip DIRECTION is unchanged, and re-normalising puts the tip back
    // where it started. Head-on contacts - a paw on the ground, a back against a
    // ceiling, exactly the cases fur collision exists for - did nothing at all.
    //
    // With the sideways comb above doing the work, this clamp is mostly inactive
    // at high Spread (the strand already ends up close to its own length) and
    // becomes the buckling fallback as Spread drops toward zero.
    float3 direction = p - rootWS;
    float  length2   = dot(direction, direction);
    if (length2 > strandLength * strandLength)
        p = rootWS + direction * (strandLength * rsqrt(length2));

    return p;
}

#endif // FUR_SHELL_COLLISION_INCLUDED
