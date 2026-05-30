using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

public class IntStateController : MonoBehaviour
{
    [Serializable]
    public class ValueEventPair
    {
        [Header("当 CurrentValue == TargetValue 时")]
        public int TargetValue;

        public UnityEvent OnValueReached;

        [Header("当 CurrentValue 不再等于 TargetValue 时")]
        public UnityEvent OnValueExit;

        [HideInInspector]
        public bool WasMatchedLastFrame;
    }

    [Header("当前数值")]
    public int CurrentValue;

    [Header("显示文本")]
    public TMP_Text ValueText;

    [Header("特殊数值事件")]
    public List<ValueEventPair> ValueEvents = new();

    private void Start()
    {
        RefreshText();
        CheckValueEvents(true);
    }

    // 增加数值
    public void AddValue(int amount)
    {
        SetValue(CurrentValue + amount);
    }

    // 减少数值
    public void SubtractValue(int amount)
    {
        SetValue(CurrentValue - amount);
    }

    // 直接设置数值
    public void SetValue(int newValue)
    {
        CurrentValue = newValue;

        RefreshText();
        CheckValueEvents(false);
    }

    // 更新TMP文本
    private void RefreshText()
    {
        if (ValueText != null)
        {
            ValueText.text = CurrentValue.ToString();
        }
    }

    // 检查事件触发
    private void CheckValueEvents(bool forceInitialize)
    {
        foreach (var valueEvent in ValueEvents)
        {
            bool isMatched = CurrentValue == valueEvent.TargetValue;

            if (forceInitialize)
            {
                valueEvent.WasMatchedLastFrame = isMatched;
                continue;
            }

            // 进入目标值
            if (isMatched && !valueEvent.WasMatchedLastFrame)
            {
                valueEvent.OnValueReached?.Invoke();
            }

            // 离开目标值
            if (!isMatched && valueEvent.WasMatchedLastFrame)
            {
                valueEvent.OnValueExit?.Invoke();
            }

            valueEvent.WasMatchedLastFrame = isMatched;
        }
    }
}