using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public partial class PlayerController
{
    private const int MaxCheckpointSpawnAdjustSteps = 64;

    private int m_CurrentWorldIndex = -1;
    private static bool s_PreventSaveWorldPositionThisReload;
    private static bool s_UseCheckpointOnNextWorldRestore;

    private bool HasWorldRestoreTarget(Save save, int worldIndex)
    {
        if (save == null || worldIndex < 0)
        {
            return false;
        }

        if (save.WorldCheckpointLastPositions != null && save.WorldCheckpointLastPositions.ContainsKey(worldIndex))
        {
            return true;
        }

        return save.WorldPlayerLastPositions != null && save.WorldPlayerLastPositions.ContainsKey(worldIndex);
    }

    private WorldData FindWorldData(string sceneName)
    {
        if (GameConfig.Current == null || GameConfig.Current.Worlds == null) return null;

        foreach (var w in GameConfig.Current.Worlds)
        {
            if (w == null) continue;

            if (string.Equals(w.Name, sceneName, StringComparison.OrdinalIgnoreCase)) return w;

            if (!string.IsNullOrEmpty(w.ScenePath))
            {
                string nameFromPath = System.IO.Path.GetFileNameWithoutExtension(w.ScenePath);
                if (string.Equals(nameFromPath, sceneName, StringComparison.OrdinalIgnoreCase)) return w;
            }
        }

        return null;
    }

    private IEnumerator RestoreWorldPlayerPositionDeferred()
    {
        yield return null;

        if (grid == null)
        {
            grid = FindSceneGrid();
        }

        TryRestoreWorldPlayerSavedPosition();
    }

    private bool TryRestoreWorldPlayerSavedPosition()
    {
        string sceneName = gameObject.scene.name;
        if (!GameManager.IsWorldHubScene(sceneName))
        {
            return false;
        }

        // Consume the one-shot restore mode so checkpoint only affects the intended reload.
        bool useCheckpointThisRestore = s_UseCheckpointOnNextWorldRestore;
        s_UseCheckpointOnNextWorldRestore = false;

        Save save = new Save().DeSerialize<Save>();

        if (m_CurrentWorldIndex == -1)
        {
            var world = FindWorldData(sceneName);
            if (world != null) m_CurrentWorldIndex = world.Index;
        }

        if (useCheckpointThisRestore && TryRestoreWorldPlayerCheckpointPosition(save))
        {
            return true;
        }

        if (m_CurrentWorldIndex == -1 ||
            save.WorldPlayerLastPositions == null ||
            !save.WorldPlayerLastPositions.TryGetValue(m_CurrentWorldIndex, out SaveVector3 sv))
        {
            return false;
        }

        Vector3 raw = new Vector3(sv.x, sv.y, sv.z);
        fixedZ = raw.z;

        if (grid == null)
        {
            MoveTo(raw);
            return true;
        }

        Vector3Int cell = grid.WorldToCell(raw);
        Vector3 snapped = grid.CellToWorld(cell) + Vector3.Scale(grid.cellSize, cellOffset);
        snapped.z = fixedZ;
        MoveTo(snapped);
        return true;
    }

    private bool TryRestoreWorldPlayerCheckpointPosition(Save save)
    {
        if (m_CurrentWorldIndex == -1 || save.WorldCheckpointLastPositions == null)
        {
            return false;
        }

        if (!save.WorldCheckpointLastPositions.TryGetValue(m_CurrentWorldIndex, out SaveVector3 checkpoint))
        {
            return false;
        }

        float adjustY = 0f;
        if (save.WorldCheckpointAdjustY != null && save.WorldCheckpointAdjustY.TryGetValue(m_CurrentWorldIndex, out float savedAdjustY))
        {
            adjustY = Mathf.Max(0f, savedAdjustY);
        }

        Vector3 target = new Vector3(checkpoint.x, checkpoint.y, checkpoint.z);
        fixedZ = target.z;

        if (adjustY > 0f)
        {
            int step = 0;
            while (IsWorldSpawnBlocked(target) && step < MaxCheckpointSpawnAdjustSteps)
            {
                target.y += adjustY;
                step++;
            }
        }

        MoveTo(target);
        return true;
    }

    private bool IsWorldSpawnBlocked(Vector3 targetPosition)
    {
        Physics.SyncTransforms();
        Physics2D.SyncTransforms();

        if (m_Collider2D != null)
        {
            Bounds selfBounds2D = m_Collider2D.bounds;
            Vector2 centerOffset = (Vector2)(selfBounds2D.center - transform.position);
            Vector2 queryCenter = (Vector2)targetPosition + centerOffset;
            Vector2 querySize = (Vector2)selfBounds2D.size;
            int hitCount2D = Physics2D.OverlapBoxNonAlloc(queryCenter, querySize, 0f, overlapHits2D, collisionMask);

            for (int i = 0; i < hitCount2D; i++)
            {
                Collider2D hit = overlapHits2D[i];
                if (hit == null || hit == m_Collider2D || hit.isTrigger)
                {
                    continue;
                }

                if (hit.transform.IsChildOf(transform))
                {
                    continue;
                }

                return true;
            }
        }

        if (m_Collider3D != null)
        {
            Bounds selfBounds3D = m_Collider3D.bounds;
            Vector3 centerOffset = selfBounds3D.center - transform.position;
            Vector3 queryCenter = targetPosition + centerOffset;
            Vector3 queryHalfExtents = selfBounds3D.extents;
            int hitCount3D = Physics.OverlapBoxNonAlloc(
                queryCenter,
                queryHalfExtents,
                overlapHits3D,
                Quaternion.identity,
                collisionMask,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hitCount3D; i++)
            {
                Collider hit = overlapHits3D[i];
                if (hit == null || hit == m_Collider3D)
                {
                    continue;
                }

                if (hit.transform.IsChildOf(transform))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    public void SaveWorldCheckpoint(Vector3 checkpointPosition, float adjustY, string checkpointId)
    {
        string sceneName = gameObject.scene.name;
        if (!GameManager.IsWorldHubScene(sceneName))
        {
            return;
        }

        if (m_CurrentWorldIndex == -1)
        {
            var world = FindWorldData(sceneName);
            if (world != null)
            {
                m_CurrentWorldIndex = world.Index;
            }
        }

        if (m_CurrentWorldIndex == -1)
        {
            return;
        }

        Save save = new Save().DeSerialize<Save>();
        save.WorldCheckpointLastPositions ??= new Dictionary<int, SaveVector3>();
        save.WorldCheckpointAdjustY ??= new Dictionary<int, float>();
        save.WorldCheckpointLastIds ??= new Dictionary<int, string>();

        save.WorldCheckpointLastPositions[m_CurrentWorldIndex] = new SaveVector3
        {
            x = checkpointPosition.x,
            y = checkpointPosition.y,
            z = checkpointPosition.z
        };
        save.WorldCheckpointAdjustY[m_CurrentWorldIndex] = Mathf.Max(0f, adjustY);
        save.WorldCheckpointLastIds[m_CurrentWorldIndex] = checkpointId ?? string.Empty;
        save.Serialize();
    }

    public void SaveWorldCheckpoint(Vector3 checkpointPosition, float adjustY)
    {
        SaveWorldCheckpoint(checkpointPosition, adjustY, string.Empty);
    }

    public static void ClearSavedWorldPositionAndPreventSaveThisReload(string sceneName)
    {
        s_PreventSaveWorldPositionThisReload = true;
        s_UseCheckpointOnNextWorldRestore = true;

        if (GameConfig.Current == null || GameConfig.Current.Worlds == null) return;
        
        // 由于是静态方法，无法直接访问 m_CurrentWorldIndex，仍需实时查找（按 R 时通常 GameConfig.Current 是有效的）
        WorldData world = null;
        foreach (var w in GameConfig.Current.Worlds)
        {
            if (w == null) continue;
            if (string.Equals(w.Name, sceneName, StringComparison.OrdinalIgnoreCase)) { world = w; break; }
            if (!string.IsNullOrEmpty(w.ScenePath))
            {
                string nameFromPath = System.IO.Path.GetFileNameWithoutExtension(w.ScenePath);
                if (string.Equals(nameFromPath, sceneName, StringComparison.OrdinalIgnoreCase)) { world = w; break; }
            }
        }
        
        if (world == null) return;

        Save save = new Save().DeSerialize<Save>();
        if (save.WorldPlayerLastPositions != null && save.WorldPlayerLastPositions.Remove(world.Index))
        {
            save.Serialize();
        }
    }

    private void PersistWorldPlayerPositionIfLeavingWorld()
    {
        if (s_PreventSaveWorldPositionThisReload)
        {
            s_PreventSaveWorldPositionThisReload = false;
            return;
        }

        // 场景卸载时 OnDestroy 里 Scene.isLoaded 往往已是 false，不能用它作为条件，否则会永远不存盘。
        string sceneName = gameObject.scene.name;
        if (string.IsNullOrEmpty(sceneName) || !GameManager.IsWorldHubScene(sceneName))
        {
            return;
        }

        if (ServiceBase.TryGet(out PlayerService playerService) &&
            playerService.Player == this &&
            playerService.IsDying)
        {
            return;
        }

        // 优先使用缓存的 index，避免 OnApplicationQuit 时 GameConfig.Current 已销毁导致查找失败
        int targetIndex = m_CurrentWorldIndex;
        if (targetIndex == -1)
        {
            var world = FindWorldData(sceneName);
            if (world != null) targetIndex = world.Index;
        }

        if (targetIndex == -1) return;

        Save save = new Save().DeSerialize<Save>();
        if (save.WorldPlayerLastPositions == null) save.WorldPlayerLastPositions = new Dictionary<int, SaveVector3>();

        Vector3 p = transform.position;
        save.WorldPlayerLastPositions[targetIndex] = new SaveVector3 { x = p.x, y = p.y, z = p.z };
        save.Serialize();
    }
}
