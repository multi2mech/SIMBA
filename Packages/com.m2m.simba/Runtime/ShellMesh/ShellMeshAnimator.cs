using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace M2M.SIMBA
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ShellMeshLoader))]
    public sealed class ShellMeshAnimator :
        MonoBehaviour,
        IFieldAnimationSource
    {
        public bool playOnLoad = true;
        public bool loop = true;
        public bool interpolateFrames = true;
        public bool recalculateNormalsEveryFrame = true;
        public bool recalculateBoundsEveryFrame = true;
        [Min(0f)] public float speed = 1f;

        public bool IsPlaying { get; private set; }
        public int CurrentFrame { get; private set; }
        public int NextFrame { get; private set; }
        public float FrameInterpolation { get; private set; }
        public float CurrentTime { get; private set; }
        public bool IsLoaded => loader != null && loader.IsLoaded;
        public int FrameCount => IsLoaded ? loader.Data.FrameCount : 0;
        public int ValueCount =>
            IsLoaded ? loader.GetFrame(CurrentFrame).Vertices.Length : 0;
        public int FieldCount =>
            IsLoaded ? loader.Data.Fields.Length : 0;
        public Renderer TargetRenderer =>
            GetComponent<MeshRenderer>();

        public event Action DataLoaded;
        public event Action<int, int, float> FrameChanged;

        private ShellMeshLoader loader;
        private Vector3[] interpolationBuffer =
            Array.Empty<Vector3>();

        private void Awake()
        {
            loader = GetComponent<ShellMeshLoader>();
            loader.Loaded += OnLoaded;
        }

        private void OnDestroy()
        {
            if (loader != null)
                loader.Loaded -= OnLoaded;
        }

        private void Start()
        {
            if (loader.IsLoaded)
                OnLoaded();
        }

        private void OnLoaded()
        {
            CurrentTime = 0f;
            IsPlaying = playOnLoad;
            Apply();
            DataLoaded?.Invoke();
        }

        private void Update()
        {
            if (!IsLoaded || !IsPlaying)
                return;

            CurrentTime += Time.deltaTime * speed;
            float duration =
                FrameCount / loader.Data.FramesPerSecond;

            if (loop)
            {
                CurrentTime =
                    Mathf.Repeat(CurrentTime, duration);
            }
            else if (CurrentTime >= duration)
            {
                CurrentTime = Mathf.Max(
                    0f,
                    duration -
                    1f / loader.Data.FramesPerSecond);
                IsPlaying = false;
            }

            Apply();
        }

        public void Play() => IsPlaying = true;
        public void Pause() => IsPlaying = false;
        public void Resume() => IsPlaying = true;

        public void Stop()
        {
            IsPlaying = false;
            CurrentTime = 0f;
            Apply();
        }

        public void SetFrame(int frame)
        {
            if (!IsLoaded)
                return;

            CurrentTime =
                Mathf.Clamp(frame, 0, FrameCount - 1) /
                loader.Data.FramesPerSecond;
            Apply();
        }

        public void SetNormalizedTime(float value)
        {
            if (!IsLoaded)
                return;

            float duration =
                FrameCount / loader.Data.FramesPerSecond;

            CurrentTime =
                Mathf.Clamp01(value) *
                Mathf.Max(
                    0f,
                    duration -
                    1f / loader.Data.FramesPerSecond);
            Apply();
        }

        private void Apply()
        {
            float exact =
                CurrentTime *
                loader.Data.FramesPerSecond;

            CurrentFrame = loop
                ? Mod(Mathf.FloorToInt(exact), FrameCount)
                : Mathf.Clamp(
                    Mathf.FloorToInt(exact),
                    0,
                    FrameCount - 1);

            NextFrame = loop
                ? (CurrentFrame + 1) % FrameCount
                : Mathf.Min(
                    CurrentFrame + 1,
                    FrameCount - 1);

            loader.PrefetchAround(CurrentFrame);

            ShellMeshFrame current =
                loader.GetFrame(CurrentFrame);

            ShellMeshFrame next =
                loader.GetFrame(NextFrame);

            bool canInterpolate =
                interpolateFrames &&
                !loader.Data.HasDynamicTopology &&
                current.Vertices.Length > 0 &&
                current.Vertices.Length ==
                    next.Vertices.Length;

            FrameInterpolation = canInterpolate
                ? exact - Mathf.Floor(exact)
                : 0f;

            if (canInterpolate)
                ApplyInterpolated(current, next);
            else
                loader.ApplyFrameToMesh(current);

            FrameChanged?.Invoke(
                CurrentFrame,
                NextFrame,
                FrameInterpolation);
        }

        private void ApplyInterpolated(
            ShellMeshFrame current,
            ShellMeshFrame next)
        {
            if (interpolationBuffer.Length !=
                current.Vertices.Length)
            {
                interpolationBuffer =
                    new Vector3[current.Vertices.Length];
            }

            for (int i = 0;
                 i < interpolationBuffer.Length;
                 i++)
            {
                interpolationBuffer[i] =
                    Vector3.LerpUnclamped(
                        current.Vertices[i],
                        next.Vertices[i],
                        FrameInterpolation);
            }

            Mesh mesh = loader.RuntimeMesh;
            mesh.vertices = interpolationBuffer;

            if (recalculateBoundsEveryFrame)
                mesh.RecalculateBounds();

            if (recalculateNormalsEveryFrame)
                mesh.RecalculateNormals();
        }

        public AnimatedField GetField(int index) =>
            loader.Data.Fields[index];

        public float[] GetFieldValues(
            int fieldIndex,
            int frame) =>
            loader.GetFrame(frame).FieldValues[fieldIndex];

        public int FindField(string name)
        {
            for (int i = 0; i < FieldCount; i++)
            {
                if (string.Equals(
                    GetField(i).Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private static int Mod(int x, int m)
        {
            int result = x % m;
            return result < 0 ? result + m : result;
        }
    }
}
