using System.Collections;
using System.Collections.Generic;
using Kuchinashi.DataSystem;
using UnityEngine;

public class Save : ReadableAndWriteableData
{
    public override string Path => "save";

    public List<int> FinishedLevels;
}
