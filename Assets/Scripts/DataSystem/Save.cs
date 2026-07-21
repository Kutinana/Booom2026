using System.Collections.Generic;
using Kuchinashi.DataSystem;
using Newtonsoft.Json;
using UnityEngine;

public class Save : ReadableAndWriteableData
{
    [JsonIgnore] public override string Path => System.IO.Path.Combine(Application.persistentDataPath, "save");

    public List<int> FinishedLevels;

    /// <summary>已完成关卡在 World 中对应 <see cref="LevelBoxController"/> 的世界坐标（键为 <see cref="LevelData.Index"/>）。</summary>
    public Dictionary<int, SaveVector3> CompletedLevelBoxWorldPositions;

    /// <summary>离开 <see cref="GameManager.IsWorldHubScene"/> 时记录的玩家位置（键为 World Index）；下次进入该世界时由 <see cref="PlayerController"/> 恢复。</summary>
    public Dictionary<int, SaveVector3> WorldPlayerLastPositions;

    /// <summary>玩家在 World 中最后一次激活的 checkpoint 位置（键为 World Index）。</summary>
    public Dictionary<int, SaveVector3> WorldCheckpointLastPositions;

    /// <summary>checkpoint 位置被阻挡时用于向上偏移的 adjustY（键为 World Index）。</summary>
    public Dictionary<int, float> WorldCheckpointAdjustY;

    /// <summary>每个 World 最后激活的 checkpoint 唯一标识（键为 World Index）。</summary>
    public Dictionary<int, string> WorldCheckpointLastIds;

    /// <summary>记录进入关卡前的上一个 Hub 场景名（用于决定返回 StartScene 还是 WorldScene）。</summary>
    public string LastHubScene;

    public bool HasCGPlayed;
    public bool HasThanksPlayed;
}

[System.Serializable]
public class SaveVector3
{
    public float x;
    public float y;
    public float z;
}
