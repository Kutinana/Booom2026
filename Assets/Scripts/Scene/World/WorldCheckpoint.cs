using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Place on a trigger object in World Hub scenes.
/// When the player passes through, this checkpoint becomes the latest respawn point for R-reload.
/// If the checkpoint position is blocked on restore, player will be moved up by adjustY repeatedly.
/// </summary>
[DisallowMultipleComponent]
public class WorldCheckpoint : MonoBehaviour
{
    [SerializeField] private Transform respawnPoint;
    [SerializeField, Min(0f)] private float adjustY = 1f;
    [SerializeField] private bool oneShot = false;
    [SerializeField] private string checkpointId;
    [SerializeField] private GameObject activeParticleRoot;

    [Header("Checkpoint Activate UI")]
    [SerializeField] private GameObject checkpointRegisteredUIPrefab;
    [SerializeField] private Transform checkpointRegisteredUIAnchor;
    [SerializeField] private Vector3 checkpointRegisteredUIOffset = new Vector3(0f, 1.5f, 0f);
    [SerializeField, Min(0.01f)] private float checkpointRegisteredUILifetime = 1.5f;

    [Header("Flag Visual")]
    [SerializeField] private Transform flagPole;
    [SerializeField] private Transform flag;
    [SerializeField, Min(0.01f)] private float flagLevelLerpSpeed = 8f;
    [SerializeField, Min(0f)] private float poleScaleYPerLevel = 1f;
    [SerializeField, Min(0f)] private float flagPosYPerLevel = 1f;

    [Header("Flag Block Probe")]
    [SerializeField] private Collider2D currentCellProbe2D;
    [SerializeField] private Collider2D belowCellProbe2D;
    [SerializeField] private Collider currentCellProbe3D;
    [SerializeField] private Collider belowCellProbe3D;
    [SerializeField] private LayerMask blockMask = ~0;
    [SerializeField, Min(0.01f)] private float levelWorldStepY = 1f;
    [SerializeField, Min(1)] private int maxRaiseLevelsPerTick = 16;
    [SerializeField, Min(0)] private int clearFramesBeforeLower = 2;

    private bool activated;
    private bool isCheckpointActive;

    private Vector3 basePoleLocalScale;
    private Vector3 baseFlagLocalPosition;
    private bool hasCachedBaseVisualState;

    private int targetVisualLevel;
    private float currentVisualLevel;
    private int clearFrameStreak;

    private readonly Collider2D[] overlapHits2D = new Collider2D[16];
    private readonly Collider[] overlapHits3D = new Collider[16];

    private static readonly List<WorldCheckpoint> s_Instances = new List<WorldCheckpoint>(16);

    private Vector3 CheckpointPosition => respawnPoint != null ? respawnPoint.position : transform.position;
    private string RuntimeCheckpointId => string.IsNullOrWhiteSpace(checkpointId) ? BuildHierarchyPath(transform) : checkpointId.Trim();

    private void Awake()
    {
        InitializeFlagReferences();
        CacheBaseVisualState();
        ApplyVisualByLevel(currentVisualLevel);
    }

    private void OnEnable()
    {
        if (!s_Instances.Contains(this))
        {
            s_Instances.Add(this);
        }

        SyncVisualFromSave();
    }

    private void Start()
    {
        SyncVisualFromSave();
    }

    private void OnDisable()
    {
        s_Instances.Remove(this);
    }

    private void LateUpdate()
    {
        if (isCheckpointActive)
        {
            UpdateTargetLevelByBlocking();
        }

        float previous = currentVisualLevel;
        currentVisualLevel = Mathf.MoveTowards(currentVisualLevel, targetVisualLevel,
            flagLevelLerpSpeed * Time.deltaTime);

        if (!Mathf.Approximately(previous, currentVisualLevel))
        {
            ApplyVisualByLevel(currentVisualLevel);
        }
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        TryActivate(other != null ? other.GetComponentInParent<PlayerController>() : null);
    }

    private void OnTriggerEnter(Collider other)
    {
        TryActivate(other != null ? other.GetComponentInParent<PlayerController>() : null);
    }

    private void TryActivate(PlayerController player)
    {
        if (player == null)
        {
            return;
        }

        if (oneShot && activated)
        {
            return;
        }

        if (!GameManager.IsWorldHubScene(player.gameObject.scene.name))
        {
            return;
        }

        bool isNewCheckpoint = !IsCurrentCheckpointAlreadySavedAsLatest();

        player.SaveWorldCheckpoint(CheckpointPosition, adjustY, RuntimeCheckpointId);
        RefreshVisualsForScene(gameObject.scene.name, RuntimeCheckpointId);
        if (isNewCheckpoint)
        {
            SpawnCheckpointRegisteredUI();
        }

        activated = true;
    }

    private bool IsCurrentCheckpointAlreadySavedAsLatest()
    {
        if (!TryGetWorldIndex(gameObject.scene.name, out int worldIndex))
        {
            return false;
        }

        Save save = new Save().DeSerialize<Save>();
        if (save.WorldCheckpointLastIds == null ||
            !save.WorldCheckpointLastIds.TryGetValue(worldIndex, out string lastId) ||
            string.IsNullOrEmpty(lastId))
        {
            return false;
        }

        return string.Equals(lastId, RuntimeCheckpointId, System.StringComparison.Ordinal);
    }

    private void SpawnCheckpointRegisteredUI()
    {
        if (checkpointRegisteredUIPrefab == null)
        {
            return;
        }

        Transform anchor = checkpointRegisteredUIAnchor != null ? checkpointRegisteredUIAnchor : transform;
        Vector3 spawnPosition = anchor.position + checkpointRegisteredUIOffset;
        GameObject uiFx = Instantiate(checkpointRegisteredUIPrefab, spawnPosition, Quaternion.identity);
        if (uiFx != null)
        {
            Destroy(uiFx, checkpointRegisteredUILifetime);
        }
    }

    private void SyncVisualFromSave()
    {
        if (!TryGetWorldIndex(gameObject.scene.name, out int worldIndex))
        {
            SetActiveVisual(false);
            return;
        }

        Save save = new Save().DeSerialize<Save>();
        bool isActive = save.WorldCheckpointLastIds != null &&
            save.WorldCheckpointLastIds.TryGetValue(worldIndex, out string lastId) &&
            !string.IsNullOrEmpty(lastId) &&
            string.Equals(lastId, RuntimeCheckpointId, System.StringComparison.Ordinal);

        SetActiveVisual(isActive);
    }

    private static void RefreshVisualsForScene(string sceneName, string activeId)
    {
        for (int i = 0; i < s_Instances.Count; i++)
        {
            WorldCheckpoint checkpoint = s_Instances[i];
            if (checkpoint == null || !checkpoint.isActiveAndEnabled)
            {
                continue;
            }

            if (checkpoint.gameObject.scene.name != sceneName)
            {
                continue;
            }

            bool active = string.Equals(checkpoint.RuntimeCheckpointId, activeId, System.StringComparison.Ordinal);
            checkpoint.SetActiveVisual(active);
        }
    }

    private void SetActiveVisual(bool active)
    {
        isCheckpointActive = active;
        targetVisualLevel = active ? 1 : 0;
        clearFrameStreak = 0;

        GameObject root = ResolveParticleRoot();
        if (root == null)
        {
            return;
        }

        if (!root.activeSelf)
        {
            root.SetActive(true);
        }

        ParticleSystem[] ps = root.GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < ps.Length; i++)
        {
            if (ps[i] == null)
            {
                continue;
            }

            if (active)
            {
                ps[i].Play(true);
            }
            else
            {
                ps[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }
    }

    private GameObject ResolveParticleRoot()
    {
        if (activeParticleRoot != null)
        {
            return activeParticleRoot;
        }

        ParticleSystem ps = GetComponentInChildren<ParticleSystem>(true);
        return ps != null ? ps.gameObject : null;
    }

    private void InitializeFlagReferences()
    {
        if (flag != null)
        {
            if (currentCellProbe2D == null)
            {
                currentCellProbe2D = flag.GetComponentInChildren<Collider2D>(true);
            }

            if (currentCellProbe3D == null)
            {
                currentCellProbe3D = flag.GetComponentInChildren<Collider>(true);
            }
        }
    }

    private void CacheBaseVisualState()
    {
        if (hasCachedBaseVisualState)
        {
            return;
        }

        if (flagPole != null)
        {
            basePoleLocalScale = flagPole.localScale;
        }

        if (flag != null)
        {
            baseFlagLocalPosition = flag.localPosition;
        }

        hasCachedBaseVisualState = true;
    }

    private void UpdateTargetLevelByBlocking()
    {
        if (targetVisualLevel < 1)
        {
            targetVisualLevel = 1;
        }

        int raised = 0;
        while (raised < maxRaiseLevelsPerTick && IsCurrentCellBlockedAtLevel(targetVisualLevel))
        {
            targetVisualLevel++;
            raised++;
        }

        if (raised > 0)
        {
            clearFrameStreak = 0;
            return;
        }

        if (targetVisualLevel <= 1)
        {
            clearFrameStreak = 0;
            return;
        }

        if (!IsBelowCellEmptyAtLevel(targetVisualLevel))
        {
            clearFrameStreak = 0;
            return;
        }

        clearFrameStreak++;
        if (clearFrameStreak <= clearFramesBeforeLower)
        {
            return;
        }

        targetVisualLevel--;
        clearFrameStreak = 0;
    }

    private bool IsCurrentCellBlockedAtLevel(int level)
    {
        float offsetY = GetLevelOffsetDeltaY(level);
        bool blocked2D = IsProbeBlocked2D(currentCellProbe2D, offsetY);
        if (blocked2D)
        {
            return true;
        }

        return IsProbeBlocked3D(currentCellProbe3D, offsetY);
    }

    private bool IsBelowCellEmptyAtLevel(int level)
    {
        float offsetY = GetLevelOffsetDeltaY(level);
        bool blocked2D = IsProbeBlocked2D(belowCellProbe2D, offsetY);
        if (blocked2D)
        {
            return false;
        }

        return !IsProbeBlocked3D(belowCellProbe3D, offsetY);
    }

    private float GetLevelOffsetDeltaY(int level)
    {
        float targetOffset = Mathf.Max(0, level - 1) * levelWorldStepY;
        float currentOffset = Mathf.Max(0f, currentVisualLevel - 1f) * levelWorldStepY;
        return targetOffset - currentOffset;
    }

    private bool IsProbeBlocked2D(Collider2D probeCollider, float offsetDeltaY)
    {
        if (probeCollider == null)
        {
            return false;
        }

        Physics2D.SyncTransforms();

        Bounds b = probeCollider.bounds;
        Vector2 size = b.size;
        if (size.x <= 0f || size.y <= 0f)
        {
            return false;
        }

        Vector2 center = (Vector2)b.center + Vector2.up * offsetDeltaY;
        float angle = probeCollider.transform.eulerAngles.z;
        int hitCount = Physics2D.OverlapBoxNonAlloc(center, size, angle, overlapHits2D, blockMask);
        for (int i = 0; i < hitCount; i++)
        {
            Collider2D hit = overlapHits2D[i];
            if (hit == null || hit.isTrigger)
            {
                continue;
            }

            if (hit.transform.IsChildOf(transform))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private bool IsProbeBlocked3D(Collider probeCollider, float offsetDeltaY)
    {
        if (probeCollider == null)
        {
            return false;
        }

        Physics.SyncTransforms();

        Bounds b = probeCollider.bounds;
        Vector3 size = b.size;
        if (size.x <= 0f || size.y <= 0f || size.z <= 0f)
        {
            return false;
        }

        Vector3 center = b.center + Vector3.up * offsetDeltaY;
        Vector3 halfExtents = size * 0.5f;
        Quaternion rotation = probeCollider.transform.rotation;
        int hitCount = Physics.OverlapBoxNonAlloc(center, halfExtents, overlapHits3D, rotation, blockMask,
            QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = overlapHits3D[i];
            if (hit == null)
            {
                continue;
            }

            if (hit.transform.IsChildOf(transform))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private void ApplyVisualByLevel(float level)
    {
        CacheBaseVisualState();

        if (flagPole != null)
        {
            Vector3 scale = basePoleLocalScale;
            scale.y += level * poleScaleYPerLevel;
            flagPole.localScale = scale;
        }

        if (flag != null)
        {
            Vector3 localPos = baseFlagLocalPosition;
            localPos.y += level * flagPosYPerLevel;
            flag.localPosition = localPos;
        }
    }

    private static bool TryGetWorldIndex(string sceneName, out int worldIndex)
    {
        worldIndex = -1;
        if (string.IsNullOrEmpty(sceneName) || GameConfig.Current == null || GameConfig.Current.Worlds == null)
        {
            return false;
        }

        for (int i = 0; i < GameConfig.Current.Worlds.Count; i++)
        {
            WorldData w = GameConfig.Current.Worlds[i];
            if (w == null)
            {
                continue;
            }

            if (string.Equals(w.Name, sceneName, System.StringComparison.OrdinalIgnoreCase))
            {
                worldIndex = w.Index;
                return true;
            }

            if (!string.IsNullOrEmpty(w.ScenePath))
            {
                string nameFromPath = System.IO.Path.GetFileNameWithoutExtension(w.ScenePath);
                if (string.Equals(nameFromPath, sceneName, System.StringComparison.OrdinalIgnoreCase))
                {
                    worldIndex = w.Index;
                    return true;
                }
            }
        }

        return false;
    }

    private static string BuildHierarchyPath(Transform t)
    {
        if (t == null)
        {
            return string.Empty;
        }

        string path = t.name;
        Transform parent = t.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }

        return path;
    }
}
