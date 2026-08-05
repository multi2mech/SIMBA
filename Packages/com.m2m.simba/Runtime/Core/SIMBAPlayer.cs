using System;
using System.Collections;
using UnityEngine;

namespace M2M.SIMBA
{
    /// <summary>
    /// Unified public API for controlling either a ShellMesh or LineMesh visualization.
    /// Add this component to the same GameObject created by the SIMBA importer.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SIMBAPlayer : MonoBehaviour
    {
        public bool IsLoaded => source != null && source.IsLoaded;
        public bool IsPlaying => shellAnimator != null ? shellAnimator.IsPlaying : linePlayer != null && linePlayer.IsPlaying;
        public GeometryType Geometry => linePlayer != null ? GeometryType.LineMesh : GeometryType.ShellMesh;
        public int FrameCount => source?.FrameCount ?? 0;
        public int CurrentFrame => source?.CurrentFrame ?? 0;
        public int NextFrame => source?.NextFrame ?? 0;
        public float FrameInterpolation => source?.FrameInterpolation ?? 0f;
        public float FramesPerSecond => shellAnimator != null && shellLoader != null && shellLoader.IsLoaded
            ? shellLoader.Data.FramesPerSecond
            : linePlayer != null && linePlayer.IsLoaded ? linePlayer.FramesPerSecond : 0f;
        public float Duration => FramesPerSecond > 0f && FrameCount > 0 ? FrameCount / FramesPerSecond : 0f;
        public float CurrentTime => shellAnimator != null ? shellAnimator.CurrentTime : linePlayer != null ? linePlayer.CurrentTime : 0f;
        public string CurrentField => colors != null ? colors.SelectedFieldName : string.Empty;
        public int CurrentFieldIndex => colors != null ? colors.SelectedFieldIndex : -1;
        public SIMBAColorMap CurrentColorMap => colors != null ? colors.ColorMapPreset : SIMBAColorMap.Turbo;
        public FieldColorController.RangeMode CurrentRangeMode => colors != null ? colors.CurrentRangeMode : FieldColorController.RangeMode.Global;
        public string[] AvailableFields => colors != null ? colors.AvailableFieldNames : Array.Empty<string>();

        public event Action Loaded;
        public event Action<int> FrameChanged;
        public event Action<int, string> FieldChanged;
        public event Action<SIMBAColorMap> ColorMapChanged;
        public event Action PlaybackFinished;

        private IFieldAnimationSource source;
        private ShellMeshLoader shellLoader;
        private ShellMeshAnimator shellAnimator;
        private LineMeshPlayer linePlayer;
        private FieldColorController colors;
        private bool finishSent;

        private void Awake()
        {
            ResolveComponents();
            Subscribe();
        }

        private void Start()
        {
            if (IsLoaded) HandleLoaded();
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        private void Update()
        {
            if (!IsLoaded || IsLooping || IsPlaying)
            {
                finishSent = false;
                return;
            }

            if (!finishSent && CurrentFrame >= Mathf.Max(0, FrameCount - 1))
            {
                finishSent = true;
                PlaybackFinished?.Invoke();
            }
        }

        private void ResolveComponents()
        {
            shellLoader = GetComponent<ShellMeshLoader>();
            shellAnimator = GetComponent<ShellMeshAnimator>();
            linePlayer = GetComponent<LineMeshPlayer>();
            colors = GetComponent<FieldColorController>();
            if (shellAnimator != null) source = shellAnimator;
            else if (linePlayer != null) source = linePlayer;

            if (source == null)
                Debug.LogError("[SIMBA] SIMBAPlayer requires ShellMeshAnimator or LineMeshPlayer on the same GameObject.", this);
        }

        private void Subscribe()
        {
            if (source != null)
            {
                source.DataLoaded += HandleLoaded;
                source.FrameChanged += HandleFrameChanged;
            }
            if (colors != null)
            {
                colors.FieldChanged += HandleFieldChanged;
                colors.ColorMapChanged += HandleColorMapChanged;
            }
        }

        private void Unsubscribe()
        {
            if (source != null)
            {
                source.DataLoaded -= HandleLoaded;
                source.FrameChanged -= HandleFrameChanged;
            }
            if (colors != null)
            {
                colors.FieldChanged -= HandleFieldChanged;
                colors.ColorMapChanged -= HandleColorMapChanged;
            }
        }

        private void HandleLoaded() => Loaded?.Invoke();
        private void HandleFrameChanged(int frame, int next, float interpolation) => FrameChanged?.Invoke(frame);
        private void HandleFieldChanged(int index, string name) => FieldChanged?.Invoke(index, name);
        private void HandleColorMapChanged(SIMBAColorMap map) => ColorMapChanged?.Invoke(map);

        public void Play()
        {
            shellAnimator?.Play();
            linePlayer?.Play();
        }

        public void Pause()
        {
            shellAnimator?.Pause();
            linePlayer?.Pause();
        }

        /// <summary>Riprende l'animazione dal tempo corrente.</summary>
        public void Resume()
        {
            shellAnimator?.Resume();
            linePlayer?.Resume();
        }

        public void Stop()
        {
            shellAnimator?.Stop();
            linePlayer?.Stop();
        }

        public void Restart()
        {
            Stop();
            Play();
        }

        public void SetFrame(int frame)
        {
            shellAnimator?.SetFrame(frame);
            linePlayer?.SetFrame(frame);
        }

        public void SetNormalizedTime(float normalizedTime)
        {
            normalizedTime = Mathf.Clamp01(normalizedTime);
            shellAnimator?.SetNormalizedTime(normalizedTime);
            linePlayer?.SetNormalizedTime(normalizedTime);
        }

        public void SetSpeed(float speed)
        {
            speed = Mathf.Max(0f, speed);
            if (shellAnimator != null) shellAnimator.speed = speed;
            if (linePlayer != null) linePlayer.speed = speed;
        }

        public void SetLoop(bool loop)
        {
            if (shellAnimator != null) shellAnimator.loop = loop;
            if (linePlayer != null) linePlayer.loop = loop;
        }

        public bool IsLooping => shellAnimator != null ? shellAnimator.loop : linePlayer != null && linePlayer.loop;

        public bool SetField(string fieldName) => colors != null && colors.SetField(fieldName);
        public void SetField(int fieldIndex) => colors?.SetField(fieldIndex);
        public void SetColorMap(SIMBAColorMap colorMap) => colors?.SetColorMap(colorMap);
        public void SetColorMap(Texture2D customTexture) => colors?.SetColorMap(customTexture);
        public void UseGlobalRange() => colors?.UseGlobalRange();
        public void UsePerFrameRange() => colors?.UsePerFrameRange();
        public void SetManualRange(float minimum, float maximum) => colors?.SetManualRange(minimum, maximum);
        public void SetMetallic(float value) => colors?.SetMetallic(value);
        public void SetSmoothness(float value) => colors?.SetSmoothness(value);

        /// <summary>Reloads the currently configured binary file.</summary>
        public void Reload()
        {
            if (shellLoader != null) StartCoroutine(shellLoader.Load());
            else if (linePlayer != null) StartCoroutine(linePlayer.Load());
        }
    }
}
