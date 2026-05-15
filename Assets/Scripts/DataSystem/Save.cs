using System.Collections;
using System.Collections.Generic;
using Kuchinashi.DataSystem;
using UnityEngine;

public class Save : ReadableAndWriteableData
{
    public override string Path => System.IO.Path.Combine(Application.persistentDataPath, "save.json");

    public List<int> FinishedLevels;
}
