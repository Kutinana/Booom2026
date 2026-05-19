using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public partial class PlayerController
{
    private int m_CurrentWorldIndex = -1;
    private static bool s_PreventSaveWorldPositionThisReload;

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

        Save save = new Save().DeSerialize<Save>();
        if (save.WorldPlayerLastPositions == null)
        {
            return false;
        }

        if (m_CurrentWorldIndex == -1)
        {
            var world = FindWorldData(sceneName);
            if (world != null) m_CurrentWorldIndex = world.Index;
        }

        if (m_CurrentWorldIndex == -1 || !save.WorldPlayerLastPositions.TryGetValue(m_CurrentWorldIndex, out SaveVector3 sv))
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

    public static void ClearSavedWorldPositionAndPreventSaveThisReload(string sceneName)
    {
        s_PreventSaveWorldPositionThisReload = true;

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
