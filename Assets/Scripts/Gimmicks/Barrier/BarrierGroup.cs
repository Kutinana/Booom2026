using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class BarrierGroup : MonoBehaviour
{
    public List<Barrier> Barriers;
    void Start()
    {
        if (Barriers == null || Barriers.Count == 0)
        {
            Barriers = new List<Barrier>(GetComponentsInChildren<Barrier>()); 
        }
        if (Barriers.Count == 0)
        {
            Debug.LogWarning($"BarrierGroup '{name}' has no barriers assigned or found in children.");
        }
    }

    public void UpdateBarriersState(bool isOpen)
    {
        foreach (var barrier in Barriers)
        {
            barrier.UpdateBarrierState(isOpen);
        }
    }
}
