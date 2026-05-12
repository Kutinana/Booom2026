using UnityEngine;

public partial class PlayerController
{
    private const string PlatformTag = "Platform";

    [Header("Platform")]
    [SerializeField, Min(0f)] private float platformDropDuration = 0.25f;
    [SerializeField, Range(0f, 1f)] private float platformTopNormalThreshold = 0.5f;
    [SerializeField, Min(0f)] private float platformLandingTolerance = 0.03f;

    private bool platformDropQueued;
    private float platformDropUntil;
    private GameObject ignoredPlatform;
    private GameObject downPlatform;

    private void HandlePlatformInput()
    {
        if (Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.DownArrow))
        {
            platformDropQueued = true;
        }
    }

    private void ProcessPlatformDropRequest()
    {
        if (!platformDropQueued)
        {
            return;
        }

        platformDropQueued = false;
        if (!contacts.grounded)
        {
            return;
        }

        if (TryPushDownWorldBox())
        {
            return;
        }

        if (downPlatform == null)
        {
            return;
        }

        ignoredPlatform = downPlatform;
        platformDropUntil = Time.time + platformDropDuration;
        contacts.downBlocked = false;
        contacts.grounded = false;
        contacts.downTag = null;
        downPlatform = null;
        downBox = null;
    }

    private bool TryPushDownWorldBox()
    {
        WorldBox worldBox = downBox as WorldBox;
        if (worldBox == null)
        {
            return false;
        }

        worldBox.TryPush(BoxPushDirection.Down, gameObject);
        return true;
    }

    private GameObject GetPlatformObject(Collider collider)
    {
        return collider != null ? GetPlatformObject(collider.transform) : null;
    }

    private GameObject GetPlatformObject(Collider2D collider)
    {
        return collider != null ? GetPlatformObject(collider.transform) : null;
    }

    private GameObject GetPlatformObject(Transform start)
    {
        Transform current = start;
        while (current != null)
        {
            if (current.CompareTag(PlatformTag))
            {
                return current.gameObject;
            }

            current = current.parent;
        }

        return null;
    }

    private bool ShouldCollideWithPlatform(GameObject platform, Vector3 normal, Vector3 direction)
    {
        if (platform == null)
        {
            return true;
        }

        if (direction.y >= 0f)
        {
            return false;
        }

        if (platform == ignoredPlatform && Time.time < platformDropUntil)
        {
            return false;
        }

        return normal.y >= platformTopNormalThreshold;
    }

    private bool ShouldCollideWithPlatform(GameObject platform, float platformTopY, Bounds playerBounds, Vector3 normal, Vector3 direction)
    {
        if (!ShouldCollideWithPlatform(platform, normal, direction))
        {
            return false;
        }

        if (platform == null)
        {
            return true;
        }

        float tolerance = Mathf.Max(platformLandingTolerance, skinWidth * 2f);
        return playerBounds.min.y >= platformTopY - tolerance;
    }
}
