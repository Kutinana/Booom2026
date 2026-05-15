using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "GameConfig", menuName = "GameConfig")]
public class GameConfig : ScriptableObject
{
    /// <summary>运行时由 <see cref="GameManager"/>（或其它唯一持有者）在 Awake 中通过 <see cref="SetRuntimeCurrent"/> 赋值。</summary>
    public static GameConfig Current { get; private set; }

    public static void SetRuntimeCurrent(GameConfig _instance)
    {
        Current = _instance;
    }

    public static void ClearRuntimeCurrentIf(GameConfig _instance)
    {
        if (Current == _instance)
        {
            Current = null;
        }
    }

    public List<WorldData> Worlds;
    public List<LevelData> Levels;
}