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

    /// <summary>离开 <see cref="GameManager.StartContentSceneName"/> 时记录的玩家位置；下次进入该世界时由 <see cref="PlayerController"/> 恢复。</summary>
    public SaveVector3 WorldPlayerLastPosition;

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
