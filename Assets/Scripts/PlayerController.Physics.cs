using UnityEngine;

public partial class PlayerController
{
    private Vector2 baseVelocity;
    private Vector2 velocity;
    private bool jumpQueued;
    private bool jumping;
    private float jumpApexY;
    private float coyoteTimeLeft;
    private float fixedZ;
    
    private bool hasRetainedVelocity;
    private Vector2 retainedVelocityAxis;
    private float retainedVelocitySpeed;

    private void MoveByAndResolve(Vector3 delta)
    {
        if (delta == Vector3.zero)
        {
            return;
        }

        Vector3 position = transform.position + delta;
        position.z = fixedZ;
        MoveTo(position);
        SyncColliderTransforms();
        ResolveTerrainOverlaps(delta);
    }

    private void ResolveTerrainOverlaps(Vector3 movedDelta)
    {
        bool skipHorizontalVelocityStopFromOverlap =
            !contacts.grounded && contacts.leftBlocked && contacts.rightBlocked;

        for (int i = 0; i < MaxOverlapResolveIterations; i++)
        {
            Bounds bounds = GetBounds();
            if (bounds.size == Vector3.zero)
            {
                return;
            }

            if (!TryFindOverlapSeparation(bounds, movedDelta, out Vector3 correction))
            {
                return;
            }

            Vector3 position = transform.position + correction;
            position.z = fixedZ;
            MoveTo(position);
            SyncColliderTransforms();

            if (Mathf.Abs(correction.x) > 0f)
            {
                StopRetainedVelocityForCollision(movedDelta, true);
                if (Mathf.Abs(movedDelta.x) > OverlapResolveEpsilon && !skipHorizontalVelocityStopFromOverlap)
                {
                    baseVelocity.x = 0f;
                    velocity.x = 0f;
                }
            }

            if (Mathf.Abs(correction.y) > 0f)
            {
                StopRetainedVelocityForCollision(movedDelta, false);
                if (Mathf.Abs(movedDelta.y) > OverlapResolveEpsilon)
                {
                    baseVelocity.y = 0f;
                    velocity.y = 0f;
                    jumping = false;
                }
            }
        }
    }

    private Vector3 CalculateAabbSeparation(Bounds playerBounds, Bounds otherBounds, Vector3 movedDelta)
    {
        float moveLeft = otherBounds.min.x - playerBounds.max.x - OverlapResolveEpsilon;
        float moveRight = otherBounds.max.x - playerBounds.min.x + OverlapResolveEpsilon;
        float moveDown = otherBounds.min.y - playerBounds.max.y - OverlapResolveEpsilon;
        float moveUp = otherBounds.max.y - playerBounds.min.y + OverlapResolveEpsilon;

        float xCorrection = movedDelta.x > 0f
            ? moveLeft
            : movedDelta.x < 0f
                ? moveRight
                : (Mathf.Abs(moveLeft) < Mathf.Abs(moveRight) ? moveLeft : moveRight);
        float yCorrection = movedDelta.y > 0f
            ? moveDown
            : movedDelta.y < 0f
                ? moveUp
                : (Mathf.Abs(moveDown) < Mathf.Abs(moveUp) ? moveDown : moveUp);

        return Mathf.Abs(xCorrection) < Mathf.Abs(yCorrection)
            ? new Vector3(xCorrection, 0f, 0f)
            : new Vector3(0f, yCorrection, 0f);
    }

    private static bool OverlapsXY(Bounds a, Bounds b)
    {
        return a.min.x < b.max.x &&
            a.max.x > b.min.x &&
            a.min.y < b.max.y &&
            a.max.y > b.min.y;
    }

    public bool HandlePlayerImpact(SceneMovablePlayerImpactContext context)
    {
        return false;
    }

    public void ApplyExternalVelocity(Vector2 newVelocity)
    {
        BeginRetainedVelocity(newVelocity);
        jumpQueued = false;
        jumping = false;
        coyoteTimeLeft = 0f;
    }

    public void ClampMotion()
    {
        jumping = false;
        coyoteTimeLeft = 0f;

        ClampVelocityVectorWithCCD(baseVelocity, out Vector2 clampedBaseVelocity);
        baseVelocity = clampedBaseVelocity;

        ClampVelocityVectorWithCCD(velocity, out Vector2 clampedVelocity);
        velocity = clampedVelocity;

        if (hasRetainedVelocity && Mathf.Abs(retainedVelocityAxis.y) > 0f)
        {
            ClampRetainedVelocityWithCCD();
        }
    }

    public void ApplyExternalPositionDelta(Vector3 delta)
    {
        if (delta.sqrMagnitude <= Mathf.Epsilon * Mathf.Epsilon)
        {
            return;
        }

        Vector3 newPos = transform.position + delta;
        newPos.z = fixedZ;
        MoveTo(newPos);
        SyncColliderTransforms();
    }

    private void BeginRetainedVelocity(Vector2 newVelocity)
    {
        float xSpeed = Mathf.Abs(newVelocity.x);
        float ySpeed = Mathf.Abs(newVelocity.y);

        if (xSpeed <= retainedVelocityStopSpeed && ySpeed <= retainedVelocityStopSpeed)
        {
            ClearRetainedVelocity();
            return;
        }

        if (xSpeed >= ySpeed)
        {
            retainedVelocityAxis = newVelocity.x >= 0f ? Vector2.right : Vector2.left;
            retainedVelocitySpeed = xSpeed;
        }
        else
        {
            retainedVelocityAxis = newVelocity.y >= 0f ? Vector2.up : Vector2.down;
            retainedVelocitySpeed = ySpeed;
        }

        hasRetainedVelocity = retainedVelocitySpeed > retainedVelocityStopSpeed;
    }

    private Vector2 TickRetainedVelocity(float dt)
    {
        if (!hasRetainedVelocity || IsRetainedVelocityBlocked())
        {
            ClearRetainedVelocity();
            return Vector2.zero;
        }

        retainedVelocitySpeed = Mathf.Max(0f, retainedVelocitySpeed - retainedVelocityDrag * dt);
        if (retainedVelocitySpeed <= retainedVelocityStopSpeed)
        {
            ClearRetainedVelocity();
            return Vector2.zero;
        }

        return GetRetainedVelocity();
    }

    private Vector2 GetRetainedVelocity()
    {
        return hasRetainedVelocity ? retainedVelocityAxis * retainedVelocitySpeed : Vector2.zero;
    }

    private bool IsRetainedVelocityBlocked()
    {
        if (!hasRetainedVelocity)
        {
            return false;
        }

        if (retainedVelocityAxis.x > 0f)
        {
            return contacts.rightBlocked;
        }

        if (retainedVelocityAxis.x < 0f)
        {
            return contacts.leftBlocked;
        }

        if (retainedVelocityAxis.y > 0f)
        {
            return contacts.upBlocked;
        }

        if (retainedVelocityAxis.y < 0f)
        {
            return contacts.downBlocked;
        }

        return false;
    }

    private void StopRetainedVelocityForCollision(Vector3 movedDelta, bool horizontal)
    {
        if (!hasRetainedVelocity)
        {
            return;
        }

        bool retainedIsHorizontal = Mathf.Abs(retainedVelocityAxis.x) > 0f;
        if (retainedIsHorizontal != horizontal)
        {
            return;
        }

        float movedAxis = horizontal ? movedDelta.x : movedDelta.y;
        float retainedAxis = horizontal ? retainedVelocityAxis.x : retainedVelocityAxis.y;
        if (movedAxis * retainedAxis > 0f)
        {
            ClearRetainedVelocity();
        }
    }

    private void ClampRetainedVelocityWithCCD()
    {
        if (!hasRetainedVelocity)
        {
            return;
        }

        Vector2 retainedVel = GetRetainedVelocity();
        if (retainedVel == Vector2.zero)
        {
            ClearRetainedVelocity();
            return;
        }

        float intendedDistance = retainedVel.magnitude * Time.fixedDeltaTime;
        Vector2 direction = retainedVel.normalized;

        if (CastRetainedVelocity(direction, intendedDistance, out float hitDistance))
        {
            float safeDistance = Mathf.Max(0f, hitDistance - skinWidth);
            retainedVelocitySpeed = safeDistance / Time.fixedDeltaTime;

            if (retainedVelocitySpeed <= retainedVelocityStopSpeed)
            {
                ClearRetainedVelocity();
            }
        }
    }

    private void ClampVelocityVectorWithCCD(Vector2 velocity, out Vector2 clampedVelocity)
    {
        clampedVelocity = velocity;

        if (velocity == Vector2.zero)
        {
            return;
        }

        float intendedDistance = velocity.magnitude * Time.fixedDeltaTime;
        Vector2 direction = velocity.normalized;

        if (CastRetainedVelocity(direction, intendedDistance, out float hitDistance))
        {
            float safeDistance = Mathf.Max(0f, hitDistance - skinWidth);
            float safeSpeed = safeDistance / Time.fixedDeltaTime;
            clampedVelocity = direction * safeSpeed;
        }
    }

    private void ClearRetainedVelocity()
    {
        hasRetainedVelocity = false;
        retainedVelocityAxis = Vector2.zero;
        retainedVelocitySpeed = 0f;
    }

    private void SyncColliderTransforms()
    {
        Physics.SyncTransforms();
        Physics2D.SyncTransforms();
    }
}
