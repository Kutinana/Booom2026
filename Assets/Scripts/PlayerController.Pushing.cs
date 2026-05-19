using QFramework;
using UnityEngine;

public partial class PlayerController
{
    private StandardBox activePushBox;
    private BoxPushDirection activePushDirection;
    private bool activePushCanPush;

    private bool hasAirborneInit;
    private StandardBox airborneInitBox;
    private BoxPushDirection airborneInitDirection;

    private float pushStallTime;
    private bool pushStalled;
    private StandardBox stalledBox;
    private BoxPushDirection stalledDirection;

    private bool worldBoxExitHadInnerBlock;
    private bool worldBoxExitPendingPressureLogicalUnblock;
    private bool worldBoxPressureLatchHadPlayerOnAnyPlate;
    private bool worldBoxPrevFrameBoxOnPlate;

    private void HandleBoxPush(float dt, ref Vector3 delta)
    {
        if (ServiceBase.TryGet(out PhysicalBoxService releaseLookup) && releaseLookup.IsPusherInRelease(gameObject))
        {
            delta.x = 0f;
            baseVelocity.x = 0f;
            velocity.x = 0f;
            return;
        }

        StandardBox box = null;
        BoxPushDirection direction = default;

        if (moveInput.x > 0.01f && contacts.rightBlocked && rightBox != null)
        {
            box = rightBox;
            direction = BoxPushDirection.Right;
        }
        else if (moveInput.x < -0.01f && contacts.leftBlocked && leftBox != null)
        {
            box = leftBox;
            direction = BoxPushDirection.Left;
        }

        if (box == null)
        {
            EndPushSession();
            ResetAirborneInitState();
            ResetStallState();
            return;
        }

        if (pushStalled && stalledBox == box && stalledDirection == direction)
        {
            return;
        }

        if (!contacts.grounded)
        {
            EndPushSession();

            if (!hasAirborneInit || airborneInitBox != box || airborneInitDirection != direction)
            {
                box.InitializePush(direction, gameObject);
                hasAirborneInit = true;
                airborneInitBox = box;
                airborneInitDirection = direction;
            }

            return;
        }

        ResetAirborneInitState();

        if (activePushBox != box || activePushDirection != direction)
        {
            EndPushSession();
            ResetStallState();
            bool canPush = box.InitializePush(direction, gameObject).CanPush;
            if (!canPush)
            {
                return;
            }

            activePushBox = box;
            activePushDirection = direction;
            activePushCanPush = true;
            if (box is WorldBox worldBoxForSession)
            {
                InitializeWorldBoxPressurePlateSessionTracking(worldBoxForSession);
            }
        }

        if (!activePushCanPush)
        {
            return;
        }

        if (!ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            return;
        }

        if (box is WorldBox worldBoxForExit && activePushBox == box && activePushDirection == direction)
        {
            bool rawInnerBlocked = worldBoxForExit.QueryInnerExitBlockedForActivePush(direction);
            bool innerBlocked = rawInnerBlocked;
            if (worldBoxExitPendingPressureLogicalUnblock)
            {
                if (worldBoxExitHadInnerBlock)
                {
                    innerBlocked = false;
                }

                worldBoxExitPendingPressureLogicalUnblock = false;
            }

            if (debugWorldBoxInnerExitEdge)
            {
                Debug.Log(
                    $"[Player] WorldBox exit edge: rawInnerBlocked={rawInnerBlocked} innerBlocked={innerBlocked} hadInnerBlock={worldBoxExitHadInnerBlock} box={box.name} dir={direction}",
                    this);
            }

            if (innerBlocked)
            {
                worldBoxExitHadInnerBlock = true;
            }
            else if (worldBoxExitHadInnerBlock)
            {
                worldBoxExitHadInnerBlock = false;
                EndPushSession(cancelLinearPushImmediate: true);
                ResetStallState();
                worldBoxForExit.TryPassThroughInnerFromActivePush(direction);
                physicalBoxService.QueueGridAlignmentRelease(worldBoxForExit);
                delta.x = 0f;
                baseVelocity.x = 0f;
                velocity.x = 0f;
                return;
            }
        }

        float pushSpeed = physicalBoxService.LinearPushSpeed;
        if (pushSpeed <= 0f)
        {
            pushSpeed = moveSpeed * Mathf.Max(0.05f, pushSpeedMultiplier);
        }

        float dirSign = direction == BoxPushDirection.Right ? 1f : -1f;
        float maxStep = pushSpeed * dt * dirSign;

        if (dirSign > 0f)
        {
            delta.x = Mathf.Clamp(delta.x, 0f, maxStep);
        }
        else
        {
            delta.x = Mathf.Clamp(delta.x, maxStep, 0f);
        }

        float clamped = physicalBoxService.TryAdvanceLinearPush(box, direction, delta.x, gameObject);

        if (box is WorldBox worldBoxPressure && activePushBox == box && activePushDirection == direction)
        {
            if (TryHandleWorldBoxPressurePlatePushInterrupts(worldBoxPressure, direction, physicalBoxService))
            {
                delta.x = 0f;
                baseVelocity.x = 0f;
                velocity.x = 0f;
                return;
            }
        }

        if (Mathf.Abs(clamped) < pushStallProgressThreshold)
        {
            pushStallTime += dt;
            if (pushStallTime >= pushStallTimeout)
            {
                EndPushSession();
                pushStalled = true;
                stalledBox = box;
                stalledDirection = direction;
                pushStallTime = 0f;
                delta.x = 0f;
                baseVelocity.x = 0f;
                velocity.x = 0f;
                return;
            }
        }
        else
        {
            pushStallTime = 0f;
        }

        delta.x = clamped;
        baseVelocity.x = clamped / Mathf.Max(dt, Mathf.Epsilon);
        velocity.x = baseVelocity.x;
    }

    private void InitializeWorldBoxPressurePlateSessionTracking(WorldBox worldBox)
    {
        worldBoxPressureLatchHadPlayerOnAnyPlate = false;
        worldBoxPrevFrameBoxOnPlate = false;
        if (!ServiceBase.TryGet(out PressurePlateService pressurePlateService))
        {
            return;
        }

        worldBoxPrevFrameBoxOnPlate = pressurePlateService.QueryWorldBoxGridSnapStablePressingAnyRegisteredPlateXY(worldBox);
        worldBoxPressureLatchHadPlayerOnAnyPlate = pressurePlateService.QueryBoundsOverlapAnyRegisteredPlateXY(GetBounds());
    }

    private bool TryHandleWorldBoxPressurePlatePushInterrupts(
        WorldBox worldBox,
        BoxPushDirection direction,
        PhysicalBoxService physicalBoxService)
    {
        if (!ServiceBase.TryGet(out PressurePlateService pressurePlateService))
        {
            return false;
        }

        Bounds playerBounds = GetBounds();
        bool playerOnPlate = pressurePlateService.QueryBoundsOverlapAnyRegisteredPlateXY(playerBounds);
        if (playerOnPlate)
        {
            worldBoxPressureLatchHadPlayerOnAnyPlate = true;
        }

        if (worldBoxPressureLatchHadPlayerOnAnyPlate && !playerOnPlate)
        {
            ApplyWorldBoxPressurePlateInterrupt(worldBox, direction, physicalBoxService, pressurePlateService, snapBoxXOnPlateAfterCancel: false);
            return true;
        }

        bool boxStrictlyOnPlate = pressurePlateService.QueryWorldBoxGridSnapStablePressingAnyRegisteredPlateXY(worldBox);
        if (!worldBoxPrevFrameBoxOnPlate && boxStrictlyOnPlate)
        {
            ApplyWorldBoxPressurePlateInterrupt(worldBox, direction, physicalBoxService, pressurePlateService, snapBoxXOnPlateAfterCancel: true);
            return true;
        }

        worldBoxPrevFrameBoxOnPlate = boxStrictlyOnPlate;
        return false;
    }

    private void ApplyWorldBoxPressurePlateInterrupt(
        WorldBox worldBox,
        BoxPushDirection direction,
        PhysicalBoxService physicalBoxService,
        PressurePlateService pressurePlateService,
        bool snapBoxXOnPlateAfterCancel)
    {
        EndPushSession(cancelLinearPushImmediate: true);
        ResetStallState();
        if (snapBoxXOnPlateAfterCancel)
        {
            pressurePlateService.SnapBoxXToNearestHorizontalGridIfOverlappingAnyPlate(worldBox);
        }

        worldBox.TryTeleportPusherToOuterEntranceForPushInterrupt(direction);
        physicalBoxService.QueueGridAlignmentRelease(worldBox);
    }

    private void ResetStallState()
    {
        pushStallTime = 0f;
        pushStalled = false;
        stalledBox = null;
    }

    private void EndPushSession(bool cancelLinearPushImmediate = false)
    {
        if (activePushBox != null && ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            if (cancelLinearPushImmediate)
            {
                physicalBoxService.CancelLinearPush(activePushBox);
            }
            else
            {
                physicalBoxService.ReleaseLinearPush(activePushBox);
            }
        }

        activePushBox = null;
        activePushCanPush = false;
        worldBoxExitHadInnerBlock = false;
        worldBoxExitPendingPressureLogicalUnblock = false;
        worldBoxPressureLatchHadPlayerOnAnyPlate = false;
        worldBoxPrevFrameBoxOnPlate = false;
    }

    private void ResetAirborneInitState()
    {
        hasAirborneInit = false;
        airborneInitBox = null;
    }

    private void HandleWorldBoxUpPush(float verticalDelta)
    {
        if (verticalDelta <= 0f || !contacts.upBlocked)
        {
            return;
        }

        WorldBox worldBox = upBox as WorldBox;
        if (worldBox == null)
        {
            return;
        }

        worldBox.TryPush(BoxPushDirection.Up, gameObject);
    }

    private void OnPressurePlateState(PressurePlateStateEvent e)
    {
        if (!e.Pressed && e.WasPressed)
        {
            worldBoxExitPendingPressureLogicalUnblock = true;
        }
    }
}
