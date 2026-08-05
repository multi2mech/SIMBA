using System;
using UnityEngine;

namespace M2M.SIMBA
{
    public interface IFieldAnimationSource
    {
        bool IsLoaded { get; }
        int FrameCount { get; }
        int ValueCount { get; }
        int FieldCount { get; }
        int CurrentFrame { get; }
        int NextFrame { get; }
        float FrameInterpolation { get; }
        Renderer TargetRenderer { get; }
        event Action DataLoaded;
        event Action<int, int, float> FrameChanged;
        AnimatedField GetField(int index);
        float[] GetFieldValues(int fieldIndex, int frame);
        int FindField(string fieldName);
    }
}
