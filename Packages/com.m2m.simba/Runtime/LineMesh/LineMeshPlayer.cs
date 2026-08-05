using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace M2M.SIMBA
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class LineMeshPlayer : MonoBehaviour, IFieldAnimationSource
    {
        private static readonly byte[] MagicV3 =
    Encoding.ASCII.GetBytes("LNMSH003");

private static readonly byte[] MagicV4 =
    Encoding.ASCII.GetBytes("LNMSH004");
        [Header("Data")]
        public string fileName = "line_mesh_fields.bin";
        public bool loadOnStart = true;

        [Header("Playback")]
        public bool playOnLoad = true;
        public bool loop = true;
        public bool interpolateFrames = true;
        [Min(0f)] public float speed = 1f;

        [Header("Tube")]
        [Min(1e-7f)] public float tubeRadius = 0.00015f;
        [Range(3, 16)] public int tubeSides = 6;

        [Header("Updates")]
        public bool recalculateNormals = true;
        public bool recalculateBounds = true;

        public bool IsLoaded { get; private set; }
        public bool IsPlaying { get; private set; }
        public int CurrentFrame { get; private set; }
        public int NextFrame { get; private set; }
        public float FrameInterpolation { get; private set; }
        public float CurrentTime => time;
        public int FrameCount => IsLoaded ? data.FrameCount : 0;
        public int ValueCount => meshVertexCount;
        public int FieldCount => IsLoaded ? data.Fields.Length : 0;
        public Renderer TargetRenderer => GetComponent<MeshRenderer>();
        public float FramesPerSecond =>
            IsLoaded ? data.SourceFps / Mathf.Max(1, data.FrameStep) : 0f;

        public event Action DataLoaded;
        public event Action<int, int, float> FrameChanged;

        private LineMeshData data;
        private Mesh mesh;
        private Vector3[] vertices = Array.Empty<Vector3>();
        private int[] triangles = Array.Empty<int>();
        private int meshVertexCount;
        private float time;
        private AnimatedField[] expandedFields = Array.Empty<AnimatedField>();

        private IEnumerator Start()
        {
            if (loadOnStart)
                yield return Load();
        }

        private void OnDestroy()
        {
            if (mesh != null)
                Destroy(mesh);
        }

        public IEnumerator Load()
        {
            string path = Path.Combine(
                Application.streamingAssetsPath,
                fileName);

            byte[] bytes;

            if (path.Contains("://"))
            {
                using UnityWebRequest request =
                    UnityWebRequest.Get(path);

                yield return request.SendWebRequest();

                if (request.result !=
                    UnityWebRequest.Result.Success)
                {
                    throw new IOException(request.error);
                }

                bytes = request.downloadHandler.data;
            }
            else
            {
                bytes = File.ReadAllBytes(path);
            }

            data = Parse(bytes);
            BuildExpandedFields();
            BuildMeshForFrame(0, false);

            IsLoaded = true;
            IsPlaying = playOnLoad;
            time = 0f;

            Apply();
            DataLoaded?.Invoke();
        }

        private void Update()
        {
            if (!IsLoaded || !IsPlaying)
                return;

            time += Time.deltaTime * speed;

            float duration = GetDuration();
            if (duration <= 0f)
                return;

            if (loop)
            {
                time = Mathf.Repeat(time, duration);
            }
            else if (time >= duration)
            {
                time = duration;
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
            time = 0f;
            Apply();
        }

        public void SetFrame(int frame)
        {
            if (!IsLoaded)
                return;

            frame = Mathf.Clamp(frame, 0, data.FrameCount - 1);
            time = data.SourceFrameIndices[frame] / data.SourceFps;
            Apply();
        }

        public void SetNormalizedTime(float normalizedTime)
        {
            if (!IsLoaded)
                return;

            time = Mathf.Clamp01(normalizedTime) * GetDuration();
            Apply();
        }

        private float GetDuration()
        {
            if (!IsLoaded || data.FrameCount <= 1)
                return IsLoaded ? 1f / data.SourceFps : 0f;

            return data.SourceFrameIndices[data.FrameCount - 1] /
                   data.SourceFps;
        }

        private static LineMeshData Parse(byte[] bytes)
        {
            using BinaryReader reader = new BinaryReader(
                new MemoryStream(bytes, false),
                Encoding.UTF8);

            byte[] magic = reader.ReadBytes(8);

            if (Equal(magic, MagicV3))
                return ParseV3(reader);

            if (Equal(magic, MagicV4))
                return ParseV4(reader);

            throw new InvalidDataException(
                "Magic SIMBA LineMesh non valido.");
        }

        private static LineMeshData ParseV3(BinaryReader reader)
        {
            int version = reader.ReadInt32();
            if (version != 3)
                throw new InvalidDataException(
                    $"Versione LineMesh {version} non supportata.");

            ValidateGeometry(reader);

            LineMeshData result = new LineMeshData
            {
                Version = version,
                TopologyMode = ShellTopologyMode.Static,
                FrameCount = reader.ReadInt32(),
                NodeCount = reader.ReadInt32(),
                EdgeCount = reader.ReadInt32(),
                SourceFps = reader.ReadSingle(),
                FrameStep = reader.ReadInt32()
            };

            int fieldCount = reader.ReadInt32();
            ValidateHeader(result, fieldCount);
            ReadFieldHeaders(reader, result, fieldCount);

            result.SourceFrameIndices =
                ReadInts(reader, result.FrameCount);

            result.Edges =
                ReadEdges(reader, result.EdgeCount);

            ReadFieldRanges(reader, result);

            result.Nodes = new Vector3[result.FrameCount][];

            for (int frame = 0;
                 frame < result.FrameCount;
                 frame++)
            {
                result.Nodes[frame] =
                    ReadVertices(reader, result.NodeCount);

                ReadFieldValues(
                    reader,
                    result,
                    frame,
                    result.NodeCount);
            }

            WarnUnreadBytes(reader);
            return result;
        }

        private static LineMeshData ParseV4(BinaryReader reader)
        {
            int version = reader.ReadInt32();
            if (version != 4)
                throw new InvalidDataException(
                    $"Versione LineMesh {version} non supportata.");

            ValidateGeometry(reader);

            ShellTopologyMode topologyMode =
                (ShellTopologyMode)reader.ReadInt32();

            if (topologyMode != ShellTopologyMode.Static &&
                topologyMode != ShellTopologyMode.Dynamic)
            {
                throw new InvalidDataException(
                    "TopologyMode LineMesh non valido.");
            }

            LineMeshData result = new LineMeshData
            {
                Version = version,
                TopologyMode = topologyMode,
                FrameCount = reader.ReadInt32(),
                NodeCount = reader.ReadInt32(),
                EdgeCount = reader.ReadInt32(),
                SourceFps = reader.ReadSingle(),
                FrameStep = reader.ReadInt32()
            };

            int fieldCount = reader.ReadInt32();
            ValidateHeader(result, fieldCount);
            ReadFieldHeaders(reader, result, fieldCount);

            result.SourceFrameIndices =
                ReadInts(reader, result.FrameCount);

            ReadFieldRanges(reader, result);
            result.Nodes = new Vector3[result.FrameCount][];

            if (!result.HasDynamicTopology)
            {
                result.Edges =
                    ReadEdges(reader, result.EdgeCount);

                for (int frame = 0;
                     frame < result.FrameCount;
                     frame++)
                {
                    result.Nodes[frame] =
                        ReadVertices(reader, result.NodeCount);

                    ReadFieldValues(
                        reader,
                        result,
                        frame,
                        result.NodeCount);
                }
            }
            else
            {
                result.FrameEdges =
                    new Vector2Int[result.FrameCount][];

                for (int frame = 0;
                     frame < result.FrameCount;
                     frame++)
                {
                    int nodeCount = reader.ReadInt32();
                    int edgeCount = reader.ReadInt32();

                    if (nodeCount <= 0 || edgeCount <= 0)
                    {
                        throw new InvalidDataException(
                            $"Frame LineMesh {frame}: " +
                            "conteggi non validi.");
                    }

                    result.Nodes[frame] =
                        ReadVertices(reader, nodeCount);

                    result.FrameEdges[frame] =
                        ReadEdges(reader, edgeCount);

                    ValidateEdges(
                        result.FrameEdges[frame],
                        nodeCount,
                        frame);

                    ReadFieldValues(
                        reader,
                        result,
                        frame,
                        nodeCount);
                }
            }

            WarnUnreadBytes(reader);
            return result;
        }

        private static void ValidateGeometry(BinaryReader reader)
        {
            GeometryType geometryType =
                (GeometryType)reader.ReadInt32();

            if (geometryType != GeometryType.LineMesh)
            {
                throw new InvalidDataException(
                    $"Il file contiene {geometryType}, non LineMesh.");
            }
        }

        private static void ValidateHeader(
            LineMeshData result,
            int fieldCount)
        {
            if (result.FrameCount <= 0 ||
                result.NodeCount <= 0 ||
                result.EdgeCount <= 0 ||
                result.SourceFps <= 0f ||
                result.FrameStep <= 0 ||
                fieldCount <= 0)
            {
                throw new InvalidDataException(
                    "Header LineMesh non valido.");
            }
        }

        private static void ReadFieldHeaders(
            BinaryReader reader,
            LineMeshData result,
            int fieldCount)
        {
            result.Fields = new AnimatedField[fieldCount];

            for (int index = 0;
                 index < fieldCount;
                 index++)
            {
                result.Fields[index] = new AnimatedField
                {
                    Name = ReadString(reader),
                    Units = ReadString(reader),
                    GlobalMin = reader.ReadSingle(),
                    GlobalMax = reader.ReadSingle(),
                    FrameMin = new float[result.FrameCount],
                    FrameMax = new float[result.FrameCount],
                    Values = new float[result.FrameCount][]
                };
            }
        }

        private static void ReadFieldRanges(
            BinaryReader reader,
            LineMeshData result)
        {
            foreach (AnimatedField field in result.Fields)
            {
                for (int frame = 0;
                     frame < result.FrameCount;
                     frame++)
                {
                    field.FrameMin[frame] = reader.ReadSingle();
                }

                for (int frame = 0;
                     frame < result.FrameCount;
                     frame++)
                {
                    field.FrameMax[frame] = reader.ReadSingle();
                }
            }
        }

        private static void ReadFieldValues(
            BinaryReader reader,
            LineMeshData result,
            int frame,
            int valueCount)
        {
            foreach (AnimatedField field in result.Fields)
            {
                float[] values = new float[valueCount];

                for (int index = 0;
                     index < valueCount;
                     index++)
                {
                    values[index] = reader.ReadSingle();
                }

                field.Values[frame] = values;
            }
        }

        private static Vector3[] ReadVertices(
            BinaryReader reader,
            int count)
        {
            Vector3[] values = new Vector3[count];

            for (int index = 0; index < count; index++)
            {
                values[index] = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle());
            }

            return values;
        }

        private static Vector2Int[] ReadEdges(
            BinaryReader reader,
            int count)
        {
            Vector2Int[] values = new Vector2Int[count];

            for (int index = 0; index < count; index++)
            {
                values[index] = new Vector2Int(
                    reader.ReadInt32(),
                    reader.ReadInt32());
            }

            return values;
        }

        private static int[] ReadInts(
            BinaryReader reader,
            int count)
        {
            int[] values = new int[count];

            for (int index = 0; index < count; index++)
                values[index] = reader.ReadInt32();

            return values;
        }

        private static string ReadString(BinaryReader reader)
        {
            int byteCount = reader.ReadInt32();

            if (byteCount < 0 || byteCount > 1024 * 1024)
            {
                throw new InvalidDataException(
                    "Lunghezza stringa LineMesh non valida.");
            }

            byte[] bytes = reader.ReadBytes(byteCount);

            if (bytes.Length != byteCount)
                throw new EndOfStreamException();

            return Encoding.UTF8.GetString(bytes);
        }

        private static void ValidateEdges(
            Vector2Int[] edges,
            int nodeCount,
            int frame = -1)
        {
            for (int index = 0; index < edges.Length; index++)
            {
                Vector2Int edge = edges[index];

                if (edge.x < 0 || edge.x >= nodeCount ||
                    edge.y < 0 || edge.y >= nodeCount)
                {
                    string prefix = frame >= 0
                        ? $"Frame {frame}, "
                        : string.Empty;

                    throw new InvalidDataException(
                        $"{prefix}edge {index} fuori intervallo.");
                }
            }
        }

        private static void WarnUnreadBytes(BinaryReader reader)
        {
            long remaining =
                reader.BaseStream.Length -
                reader.BaseStream.Position;

            if (remaining != 0)
            {
                Debug.LogWarning(
                    $"[SIMBA] {remaining} byte non letti " +
                    "nel file LineMesh.");
            }
        }

        private void BuildExpandedFields()
        {
            expandedFields = new AnimatedField[data.Fields.Length];

            for (int fieldIndex = 0;
                 fieldIndex < data.Fields.Length;
                 fieldIndex++)
            {
                AnimatedField nodeField = data.Fields[fieldIndex];
                AnimatedField expanded = new AnimatedField
                {
                    Name = nodeField.Name,
                    Units = nodeField.Units,
                    GlobalMin = nodeField.GlobalMin,
                    GlobalMax = nodeField.GlobalMax,
                    FrameMin = nodeField.FrameMin,
                    FrameMax = nodeField.FrameMax,
                    Values = new float[data.FrameCount][]
                };

                for (int frame = 0;
                     frame < data.FrameCount;
                     frame++)
                {
                    Vector2Int[] frameEdges = data.GetEdges(frame);
                    int frameVertexCount =
                        frameEdges.Length * tubeSides * 2;

                    float[] values = new float[frameVertexCount];
                    int perEdge = tubeSides * 2;

                    for (int edgeIndex = 0;
                         edgeIndex < frameEdges.Length;
                         edgeIndex++)
                    {
                        Vector2Int edge = frameEdges[edgeIndex];
                        int start = edgeIndex * perEdge;

                        for (int side = 0;
                             side < tubeSides;
                             side++)
                        {
                            values[start + side] =
                                nodeField.Values[frame][edge.x];

                            values[start + tubeSides + side] =
                                nodeField.Values[frame][edge.y];
                        }
                    }

                    expanded.Values[frame] = values;
                }

                expandedFields[fieldIndex] = expanded;
            }
        }

        private void BuildMeshForFrame(
            int frame,
            bool clearExisting)
        {
            Vector2Int[] frameEdges = data.GetEdges(frame);
            int requiredVertexCount =
                frameEdges.Length * tubeSides * 2;

            int requiredTriangleIndexCount =
                frameEdges.Length * tubeSides * 6;

            if (vertices.Length != requiredVertexCount)
                vertices = new Vector3[requiredVertexCount];

            if (triangles.Length != requiredTriangleIndexCount)
            {
                triangles = BuildTubeTriangles(frameEdges.Length);
            }

            meshVertexCount = requiredVertexCount;

            if (mesh == null)
            {
                mesh = new Mesh
                {
                    name = "line_mesh_runtime"
                };

                mesh.MarkDynamic();
                GetComponent<MeshFilter>().sharedMesh = mesh;
            }
            else if (clearExisting)
            {
                mesh.Clear(false);
            }

            mesh.indexFormat =
                requiredVertexCount > 65535
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16;

            mesh.vertices = vertices;
            mesh.triangles = triangles;
        }

        private int[] BuildTubeTriangles(int edgeCount)
        {
            int[] result = new int[edgeCount * tubeSides * 6];
            int perEdge = tubeSides * 2;

            for (int edgeIndex = 0;
                 edgeIndex < edgeCount;
                 edgeIndex++)
            {
                int vertexStart = edgeIndex * perEdge;

                for (int side = 0;
                     side < tubeSides;
                     side++)
                {
                    int a = vertexStart + side;
                    int b = vertexStart +
                            (side + 1) % tubeSides;
                    int c = vertexStart + tubeSides + side;
                    int d = vertexStart + tubeSides +
                            (side + 1) % tubeSides;

                    int triangleStart =
                        (edgeIndex * tubeSides + side) * 6;

                    result[triangleStart] = a;
                    result[triangleStart + 1] = c;
                    result[triangleStart + 2] = b;
                    result[triangleStart + 3] = b;
                    result[triangleStart + 4] = c;
                    result[triangleStart + 5] = d;
                }
            }

            return result;
        }

        private void Apply()
        {
            ResolveFrames();

            bool canInterpolate =
                !data.HasDynamicTopology &&
                interpolateFrames &&
                NextFrame != CurrentFrame;

            FrameInterpolation = canInterpolate
                ? ResolveInterpolation()
                : 0f;

            if (data.HasDynamicTopology)
                BuildMeshForFrame(CurrentFrame, true);

            Vector3[] currentNodes = data.Nodes[CurrentFrame];
            Vector3[] nextNodes = data.Nodes[NextFrame];
            Vector2Int[] frameEdges = data.GetEdges(CurrentFrame);

            int perEdge = tubeSides * 2;

            for (int edgeIndex = 0;
                 edgeIndex < frameEdges.Length;
                 edgeIndex++)
            {
                Vector2Int edge = frameEdges[edgeIndex];

                Vector3 point0 = canInterpolate
                    ? Vector3.LerpUnclamped(
                        currentNodes[edge.x],
                        nextNodes[edge.x],
                        FrameInterpolation)
                    : currentNodes[edge.x];

                Vector3 point1 = canInterpolate
                    ? Vector3.LerpUnclamped(
                        currentNodes[edge.y],
                        nextNodes[edge.y],
                        FrameInterpolation)
                    : currentNodes[edge.y];

                WriteTubeEdge(
                    edgeIndex * perEdge,
                    point0,
                    point1);
            }

            mesh.vertices = vertices;

            if (recalculateNormals)
                mesh.RecalculateNormals();

            if (recalculateBounds)
                mesh.RecalculateBounds();

            FrameChanged?.Invoke(
                CurrentFrame,
                NextFrame,
                FrameInterpolation);
        }

        private void ResolveFrames()
        {
            float sourceFrame = time * data.SourceFps;
            int upper = 0;

            while (upper < data.FrameCount &&
                   data.SourceFrameIndices[upper] < sourceFrame)
            {
                upper++;
            }

            NextFrame = Mathf.Clamp(
                upper,
                0,
                data.FrameCount - 1);

            CurrentFrame = Mathf.Max(0, NextFrame - 1);

            if (NextFrame == 0)
                CurrentFrame = 0;
        }

        private float ResolveInterpolation()
        {
            float sourceFrame = time * data.SourceFps;
            float currentSourceFrame =
                data.SourceFrameIndices[CurrentFrame];

            float nextSourceFrame =
                data.SourceFrameIndices[NextFrame];

            return nextSourceFrame > currentSourceFrame
                ? Mathf.InverseLerp(
                    currentSourceFrame,
                    nextSourceFrame,
                    sourceFrame)
                : 0f;
        }

        private void WriteTubeEdge(
            int start,
            Vector3 point0,
            Vector3 point1)
        {
            Vector3 delta = point1 - point0;
            float lengthSquared = delta.sqrMagnitude;

            Vector3 axis = lengthSquared > 1e-20f
                ? delta / Mathf.Sqrt(lengthSquared)
                : Vector3.forward;

            Vector3 referenceAxis =
                Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.95f
                    ? Vector3.right
                    : Vector3.up;

            Vector3 u = Vector3.Cross(
                axis,
                referenceAxis).normalized;

            Vector3 v = Vector3.Cross(axis, u).normalized;

            for (int side = 0;
                 side < tubeSides;
                 side++)
            {
                float angle =
                    2f * Mathf.PI * side / tubeSides;

                Vector3 offset = tubeRadius *
                    (Mathf.Cos(angle) * u +
                     Mathf.Sin(angle) * v);

                vertices[start + side] = point0 + offset;
                vertices[start + tubeSides + side] =
                    point1 + offset;
            }
        }

        public AnimatedField GetField(int index) =>
            expandedFields[index];

        public float[] GetFieldValues(int fieldIndex, int frame) =>
            expandedFields[fieldIndex].Values[frame];

        public int FindField(string name)
        {
            for (int index = 0;
                 index < FieldCount;
                 index++)
            {
                if (string.Equals(
                    data.Fields[index].Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;

            for (int index = 0;
                 index < a.Length;
                 index++)
            {
                if (a[index] != b[index])
                    return false;
            }

            return true;
        }
    }
}
