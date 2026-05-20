using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class BarrierGroup : MonoBehaviour
{
    public List<Barrier> Barriers = new List<Barrier>();
    [Min(0f)]
    public float ToggleInterval = 0.1f;

    private Coroutine updateBarriersCoroutine;

    private void Awake()
    {

        Barriers.Clear();

        Barrier[] foundBarriers = GetComponentsInChildren<Barrier>(true);

        Barriers.AddRange(foundBarriers);
    }
    void Start()
    {
        if (Barriers == null || Barriers.Count == 0)
        {
#if UNITY_EDITOR
            Debug.LogWarning($"BarrierGroup '{name}' has no barriers assigned.");
#endif
        }
    }

    public void UpdateBarriersState(bool isOpen)
    {
        if (updateBarriersCoroutine != null)
        {
            StopCoroutine(updateBarriersCoroutine);
        }
        if (this.isActiveAndEnabled)
        {
            updateBarriersCoroutine = StartCoroutine(UpdateBarriersStateSequentially(isOpen));
        }
    }

    private IEnumerator UpdateBarriersStateSequentially(bool isOpen)
    {
        if (Barriers == null || Barriers.Count == 0)
        {
            updateBarriersCoroutine = null;
            yield break;
        }

        foreach (var barrier in Barriers)
        {
            if (barrier == null)
            {
                continue;
            }

            while (!barrier.UpdateBarrierState(isOpen))
            {
                yield return null;
            }

            if (ToggleInterval > 0f)
            {
                yield return new WaitForSeconds(ToggleInterval);
            }
        }

        updateBarriersCoroutine = null;
    }

    private void OnDisable()
    {
        if (updateBarriersCoroutine != null)
        {
            StopCoroutine(updateBarriersCoroutine);
            updateBarriersCoroutine = null;
        }
    }

    private void OnValidate()
    {
        if (ToggleInterval < 0f)
        {
            ToggleInterval = 0f;
        }
    }
}
