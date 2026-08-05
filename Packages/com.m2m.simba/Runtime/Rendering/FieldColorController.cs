using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace M2M.SIMBA
{
    [DisallowMultipleComponent]
    public sealed class FieldColorController : MonoBehaviour
    {
        public enum RangeMode
        {
            Global,
            PerFrame,
            Manual
        }

        [Header("Field selection")]
        [SerializeField, Min(0)]
        private int selectedFieldIndex;

        [SerializeField]
        private string preferredFieldName = "Stress";

        [Header("Colormap")]
        public SIMBAColorMap colorMapPreset = SIMBAColorMap.Turbo;

        [Tooltip("Used only when Color Map Preset is Custom.")]
        public Texture2D customColorMap;

        public RangeMode rangeMode = RangeMode.Global;
        public float manualMin;
        public float manualMax = 1f;
        public bool interpolateField = true;

        [Header("Appearance")]
        [Range(0f, 1f)] public float metallic;
        [Range(0f, 1f)] public float smoothness = 0.35f;

        public int SelectedFieldIndex => selectedFieldIndex;
        public SIMBAColorMap ColorMapPreset => colorMapPreset;
        public RangeMode CurrentRangeMode => rangeMode;
        public float ManualMinimum => manualMin;
        public float ManualMaximum => manualMax;
        public int ConfiguredFieldIndex => selectedFieldIndex;
        public string PreferredFieldName => preferredFieldName;

        public string SelectedFieldName =>
            source != null &&
            source.IsLoaded &&
            source.FieldCount > 0
                ? source.GetField(selectedFieldIndex).Name
                : string.Empty;

        public string[] AvailableFieldNames
        {
            get
            {
                if (source == null || !source.IsLoaded)
                    return Array.Empty<string>();

                string[] names = new string[source.FieldCount];
                for (int i = 0; i < names.Length; i++)
                    names[i] = source.GetField(i).Name;
                return names;
            }
        }

        public event Action<int, string> FieldChanged;
        public event Action<SIMBAColorMap> ColorMapChanged;
        public event Action<RangeMode, float, float> RangeChanged;

        private static readonly int FieldBufferId =
            Shader.PropertyToID("_FieldBuffer");
        private static readonly int ColorMapId =
            Shader.PropertyToID("_ColorMap");
        private static readonly int FieldMinId =
            Shader.PropertyToID("_FieldMin");
        private static readonly int FieldMaxId =
            Shader.PropertyToID("_FieldMax");
        private static readonly int MetallicId =
            Shader.PropertyToID("_Metallic");
        private static readonly int SmoothnessId =
            Shader.PropertyToID("_Smoothness");

        private const string VertexColorKeyword =
            "SIMBA_VERTEX_COLORS";

        private IFieldAnimationSource source;
        private Renderer targetRenderer;
        private Material runtimeMaterial;
        private Mesh targetMesh;

        private GraphicsBuffer fieldBuffer;
        private int bufferCount;

        private Color32[] encodedVertexColors =
            Array.Empty<Color32>();

        private float[] interpolatedValues =
            Array.Empty<float>();

        private bool backendReady;
        private bool useVertexColorBackend;

        private void Awake()
        {
            source = FindSource();

            if (source == null)
            {
                throw new MissingComponentException(
                    "Serve un componente IFieldAnimationSource " +
                    "sullo stesso GameObject.");
            }

            source.DataLoaded += HandleLoaded;
            source.FrameChanged += HandleFrameChanged;
        }

        private void Start()
        {
            if (source.IsLoaded)
                HandleLoaded();
        }

        private void OnDestroy()
        {
            if (source != null)
            {
                source.DataLoaded -= HandleLoaded;
                source.FrameChanged -= HandleFrameChanged;
            }

            fieldBuffer?.Dispose();

            if (runtimeMaterial != null)
                Destroy(runtimeMaterial);
        }

        private IFieldAnimationSource FindSource()
        {
            foreach (MonoBehaviour behaviour in
                     GetComponents<MonoBehaviour>())
            {
                if (behaviour is IFieldAnimationSource candidate)
                    return candidate;
            }

            return null;
        }

        private void HandleLoaded()
        {
            if (!isActiveAndEnabled)
                return;

            if (source.FieldCount <= 0)
                throw new InvalidOperationException(
                    "Il file non contiene campi.");

            int preferred =
                string.IsNullOrWhiteSpace(preferredFieldName)
                    ? -1
                    : source.FindField(preferredFieldName);

            if (preferred >= 0)
                selectedFieldIndex = preferred;

            selectedFieldIndex = Mathf.Clamp(
                selectedFieldIndex,
                0,
                source.FieldCount - 1);

            targetRenderer = source.TargetRenderer;

            if (targetRenderer == null ||
                targetRenderer.sharedMaterial == null)
            {
                throw new MissingReferenceException(
                    "Assegna un materiale SIMBA/FieldGradientURP.");
            }

            MeshFilter filter =
                targetRenderer.GetComponent<MeshFilter>();

            targetMesh = filter != null
                ? filter.sharedMesh
                : (targetRenderer as SkinnedMeshRenderer)?.sharedMesh;

            useVertexColorBackend =
                Application.platform == RuntimePlatform.WebGLPlayer ||
                SystemInfo.graphicsDeviceType ==
                    GraphicsDeviceType.OpenGLES3 ||
                !SystemInfo.supportsComputeShaders;

            if (useVertexColorBackend && targetMesh == null)
            {
                throw new MissingComponentException(
                    "Il backend vertex-color richiede " +
                    "MeshFilter o SkinnedMeshRenderer.");
            }

            CreateRuntimeMaterial();
            backendReady = true;

            UpdateField(
                source.CurrentFrame,
                source.NextFrame,
                source.FrameInterpolation);

            FieldChanged?.Invoke(
                selectedFieldIndex,
                SelectedFieldName);
        }

        private void CreateRuntimeMaterial()
        {
            if (runtimeMaterial != null)
                Destroy(runtimeMaterial);

            runtimeMaterial = new Material(
                targetRenderer.sharedMaterial)
            {
                name =
                    targetRenderer.sharedMaterial.name +
                    " (Runtime)"
            };

            targetRenderer.sharedMaterial = runtimeMaterial;

            if (useVertexColorBackend)
                runtimeMaterial.EnableKeyword(VertexColorKeyword);
            else
                runtimeMaterial.DisableKeyword(VertexColorKeyword);
        }

        private void EnsureBackendCapacity(int count)
        {
            if (count <= 0)
            {
                interpolatedValues = Array.Empty<float>();
                encodedVertexColors = Array.Empty<Color32>();
                fieldBuffer?.Dispose();
                fieldBuffer = null;
                bufferCount = 0;
                return;
            }

            if (interpolatedValues.Length != count)
                interpolatedValues = new float[count];

            if (useVertexColorBackend)
            {
                if (encodedVertexColors.Length != count)
                    encodedVertexColors = new Color32[count];
                return;
            }

            if (fieldBuffer == null || bufferCount != count)
            {
                fieldBuffer?.Dispose();

                fieldBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    count,
                    sizeof(float));

                bufferCount = count;

                runtimeMaterial.SetBuffer(
                    FieldBufferId,
                    fieldBuffer);
            }
        }

        public bool SetField(string name)
        {
            if (source == null || !source.IsLoaded)
            {
                preferredFieldName = name;
                return false;
            }

            int index = source.FindField(name);
            if (index < 0)
                return false;

            SetField(index);
            return true;
        }

        public void SetField(int index)
        {
            if (source == null || !source.IsLoaded)
            {
                selectedFieldIndex = Mathf.Max(0, index);
                return;
            }

            selectedFieldIndex = Mathf.Clamp(
                index,
                0,
                source.FieldCount - 1);

            preferredFieldName =
                source.GetField(selectedFieldIndex).Name;

            RefreshCurrentFrame();

            FieldChanged?.Invoke(
                selectedFieldIndex,
                SelectedFieldName);
        }

        public void SetColorMap(SIMBAColorMap preset)
        {
            colorMapPreset = preset;
            ApplyMaterialProperties();
            ColorMapChanged?.Invoke(preset);
        }

        public void SetColorMap(Texture2D texture)
        {
            customColorMap = texture;
            colorMapPreset = SIMBAColorMap.Custom;
            ApplyMaterialProperties();
            ColorMapChanged?.Invoke(colorMapPreset);
        }

        public void UseGlobalRange()
        {
            rangeMode = RangeMode.Global;
            RefreshCurrentFrame();
            RangeChanged?.Invoke(
                rangeMode,
                manualMin,
                manualMax);
        }

        public void UsePerFrameRange()
        {
            rangeMode = RangeMode.PerFrame;
            RefreshCurrentFrame();
            RangeChanged?.Invoke(
                rangeMode,
                manualMin,
                manualMax);
        }

        public void SetManualRange(float minimum, float maximum)
        {
            if (maximum < minimum)
                (minimum, maximum) = (maximum, minimum);

            manualMin = minimum;
            manualMax = maximum;
            rangeMode = RangeMode.Manual;

            RefreshCurrentFrame();

            RangeChanged?.Invoke(
                rangeMode,
                minimum,
                maximum);
        }

        public void SetMetallic(float value)
        {
            metallic = Mathf.Clamp01(value);
            ApplyMaterialProperties();
        }

        public void SetSmoothness(float value)
        {
            smoothness = Mathf.Clamp01(value);
            ApplyMaterialProperties();
        }

        public void RefreshCurrentFrame()
        {
            if (source != null &&
                source.IsLoaded &&
                backendReady)
            {
                UpdateField(
                    source.CurrentFrame,
                    source.NextFrame,
                    source.FrameInterpolation);
            }
        }

        public void ConfigureInitialField(string name)
        {
            preferredFieldName = name ?? string.Empty;

            if (source != null && source.IsLoaded)
                SetField(preferredFieldName);
        }

        private void HandleFrameChanged(
            int frame,
            int next,
            float interpolation)
        {
            if (backendReady)
                UpdateField(frame, next, interpolation);
        }

        private void UpdateField(
            int frame,
            int nextFrame,
            float interpolation)
        {
            if (runtimeMaterial == null ||
                source == null ||
                !source.IsLoaded)
            {
                return;
            }

            AnimatedField field =
                source.GetField(selectedFieldIndex);

            float[] currentValues =
                source.GetFieldValues(
                    selectedFieldIndex,
                    frame);

            float[] nextValues =
                source.GetFieldValues(
                    selectedFieldIndex,
                    nextFrame);

            float t =
                interpolateField &&
                nextFrame != frame &&
                currentValues.Length > 0 &&
                nextValues.Length == currentValues.Length
                    ? interpolation
                    : 0f;

            EnsureBackendCapacity(currentValues.Length);

            if (currentValues.Length == 0)
            {
                ApplyMaterialProperties();
                return;
            }

            float[] values = currentValues;

            if (t != 0f)
            {
                for (int i = 0;
                     i < currentValues.Length;
                     i++)
                {
                    interpolatedValues[i] =
                        Mathf.LerpUnclamped(
                            currentValues[i],
                            nextValues[i],
                            t);
                }

                values = interpolatedValues;
            }

            ResolveRange(
                field,
                frame,
                nextFrame,
                t,
                out float minimum,
                out float maximum);

            if (useVertexColorBackend)
            {
                if (targetMesh == null)
                    return;

                if (targetMesh.vertexCount != values.Length)
                {
                    throw new InvalidOperationException(
                        $"SIMBA vertex-color backend: " +
                        $"valori={values.Length}, " +
                        $"vertici={targetMesh.vertexCount}.");
                }

                float inverseSpan =
                    1f / Mathf.Max(
                        maximum - minimum,
                        1e-20f);

                for (int i = 0; i < values.Length; i++)
                {
                    byte encoded =
                        (byte)Mathf.RoundToInt(
                            Mathf.Clamp01(
                                (values[i] - minimum) *
                                inverseSpan) *
                            255f);

                    encodedVertexColors[i] =
                        new Color32(
                            encoded,
                            0,
                            0,
                            255);
                }

                targetMesh.colors32 =
                    encodedVertexColors;
            }
            else
            {
                fieldBuffer.SetData(values);

                runtimeMaterial.SetBuffer(
                    FieldBufferId,
                    fieldBuffer);

                runtimeMaterial.SetFloat(
                    FieldMinId,
                    minimum);

                runtimeMaterial.SetFloat(
                    FieldMaxId,
                    maximum);
            }

            ApplyMaterialProperties();
        }

        private void ResolveRange(
            AnimatedField field,
            int frame,
            int next,
            float interpolation,
            out float minimum,
            out float maximum)
        {
            if (rangeMode == RangeMode.Manual)
            {
                minimum = manualMin;
                maximum = manualMax;
            }
            else if (rangeMode == RangeMode.PerFrame)
            {
                float frameMinimum = field.FrameMin[frame];
                float frameMaximum = field.FrameMax[frame];
                float nextMinimum = field.FrameMin[next];
                float nextMaximum = field.FrameMax[next];

                if (!float.IsFinite(frameMinimum))
                    frameMinimum = field.GlobalMin;
                if (!float.IsFinite(frameMaximum))
                    frameMaximum = field.GlobalMax;
                if (!float.IsFinite(nextMinimum))
                    nextMinimum = frameMinimum;
                if (!float.IsFinite(nextMaximum))
                    nextMaximum = frameMaximum;

                minimum = Mathf.Lerp(
                    frameMinimum,
                    nextMinimum,
                    interpolation);

                maximum = Mathf.Lerp(
                    frameMaximum,
                    nextMaximum,
                    interpolation);
            }
            else
            {
                minimum = field.GlobalMin;
                maximum = field.GlobalMax;
            }

            if (maximum < minimum)
                (minimum, maximum) = (maximum, minimum);

            if (Mathf.Abs(maximum - minimum) < 1e-20f)
                maximum = minimum + 1e-20f;
        }

        private void ApplyMaterialProperties()
        {
            if (runtimeMaterial == null)
                return;

            Texture2D map =
                colorMapPreset == SIMBAColorMap.Custom
                    ? customColorMap
                    : SIMBAColorMaps.Load(colorMapPreset);

            if (map != null)
            {
                map.wrapMode = TextureWrapMode.Clamp;
                map.filterMode = FilterMode.Bilinear;

                runtimeMaterial.SetTexture(
                    ColorMapId,
                    map);
            }

            runtimeMaterial.SetFloat(
                MetallicId,
                metallic);

            runtimeMaterial.SetFloat(
                SmoothnessId,
                smoothness);
        }
    }
}
