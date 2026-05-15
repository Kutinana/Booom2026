using System.Collections;
using System.Collections.Generic;
using Kuchinashi.DataSystem;
using Newtonsoft.Json;
using UnityEngine;

public class Save : ReadableAndWriteableData
{
    [JsonIgnore] public override string Path => System.IO.Path.Combine(Application.persistentDataPath, "save");

    public List<int> FinishedLevels;
}
