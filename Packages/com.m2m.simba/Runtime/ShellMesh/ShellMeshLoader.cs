using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace M2M.SIMBA
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class ShellMeshLoader : MonoBehaviour
    {
        private static readonly byte[] MagicV3 = Encoding.ASCII.GetBytes("SHMSH003");
        private static readonly byte[] MagicV4 = Encoding.ASCII.GetBytes("SHMSH004");
        private static readonly byte[] MagicV5 = Encoding.ASCII.GetBytes("SHMSH005");

        public string fileName = "shell_mesh_fields.bin";
        public bool loadOnStart = true;
        public bool recalculateNormals = true;
        public bool markDynamic = true;

        [Header("Streaming v5")]
        [Range(2, 8)] public int cachedFrameCount = 3;

        public bool IsLoaded { get; private set; }
        public ShellMeshData Data { get; private set; }
        public Mesh RuntimeMesh { get; private set; }
        public event Action Loaded;

        private Stream stream;
        private BinaryReader reader;
        private readonly Dictionary<int, ShellMeshFrame> frameCache =
            new Dictionary<int, ShellMeshFrame>();
        private readonly LinkedList<int> cacheOrder = new LinkedList<int>();
        private ConnectivityEntry[] connectivityPool = Array.Empty<ConnectivityEntry>();
        private readonly Dictionary<int, int[]> connectivityCache =
            new Dictionary<int, int[]>();

        private struct ConnectivityEntry
        {
            public int IndexCount;
            public int DecodedByteCount;
            public int CompressedByteCount;
            public long PayloadOffset;
        }

        private IEnumerator Start()
        {
            if (loadOnStart)
                yield return Load();
        }

        private void OnDestroy()
        {
            CloseStream();
            if (RuntimeMesh != null)
                Destroy(RuntimeMesh);
        }

        public IEnumerator Load()
        {
            CloseStream();
            frameCache.Clear();
            cacheOrder.Clear();
            connectivityCache.Clear();
            IsLoaded = false;

            string path = Path.Combine(
                Application.streamingAssetsPath,
                fileName);

            if (path.Contains("://"))
            {
                using UnityWebRequest request = UnityWebRequest.Get(path);
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                    throw new IOException(request.error);

                stream = new MemoryStream(
                    request.downloadHandler.data,
                    false);
            }
            else
            {
                stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.RandomAccess);
            }

            reader = new BinaryReader(stream, Encoding.UTF8, true);
            byte[] magic = reader.ReadBytes(8);

            if (Equal(magic, MagicV5))
            {
                Data = ParseV5Header(reader);
            }
            else if (Equal(magic, MagicV3) || Equal(magic, MagicV4))
            {
                // Legacy formats are still loaded eagerly.
                stream.Position = 0;
                Data = ParseLegacy(reader);
                CloseStream();
            }
            else
            {
                throw new InvalidDataException(
                    "Magic SIMBA ShellMesh non valido.");
            }

            BuildMesh();
            IsLoaded = true;
            Loaded?.Invoke();
        }

        public ShellMeshFrame GetFrame(int frame)
        {
            if (!IsLoaded && Data == null)
                throw new InvalidOperationException("SIMBA data not loaded.");

            frame = Mathf.Clamp(frame, 0, Data.FrameCount - 1);

            if (!Data.IsStreaming)
            {
                float[][] fields = new float[Data.Fields.Length][];
                for (int i = 0; i < fields.Length; i++)
                    fields[i] = Data.Fields[i].Values[frame];

                return new ShellMeshFrame
                {
                    Index = frame,
                    Flags = Data.Vertices[frame].Length == 0
                        ? SIMBAFrameFlags.Empty
                        : SIMBAFrameFlags.None,
                    Vertices = Data.Vertices[frame],
                    Triangles = Data.GetTriangles(frame),
                    FieldValues = fields
                };
            }

            if (frameCache.TryGetValue(frame, out ShellMeshFrame cached))
            {
                Touch(frame);
                return cached;
            }

            ShellMeshFrame loaded = ReadV5Frame(frame);
            frameCache[frame] = loaded;
            cacheOrder.AddLast(frame);
            TrimFrameCache();
            return loaded;
        }

        public void PrefetchAround(int frame)
        {
            if (!Data.IsStreaming)
                return;

            GetFrame(frame);
            if (frame > 0) GetFrame(frame - 1);
            if (frame + 1 < Data.FrameCount) GetFrame(frame + 1);
        }

        private ShellMeshData ParseV5Header(BinaryReader r)
        {
            int version = r.ReadInt32();
            if (version != 5)
                throw new InvalidDataException($"Versione {version} non supportata.");

            ValidateGeometry(r);

            ShellMeshData data = new ShellMeshData
            {
                Version = version,
                TopologyMode = ShellTopologyMode.Dynamic,
                Features = (SIMBAFileFeatures)r.ReadUInt32(),
                FrameCount = r.ReadInt32(),
                FramesPerSecond = r.ReadSingle(),
                FrameStep = r.ReadInt32(),
                VertexFormat = (SIMBAVertexFormat)r.ReadByte(),
                IndexFormat = (SIMBAIndexFormat)r.ReadByte()
            };

            r.ReadUInt16(); // reserved/alignment
            data.VertexCount = r.ReadInt32();   // maximum
            data.TriangleCount = r.ReadInt32(); // maximum

            int fieldCount = r.ReadInt32();
            ValidateV5Header(data, fieldCount);

            data.Fields = new AnimatedField[fieldCount];
            for (int i = 0; i < fieldCount; i++)
            {
                data.Fields[i] = new AnimatedField
                {
                    Name = ReadString(r),
                    Units = ReadString(r),
                    GlobalMin = r.ReadSingle(),
                    GlobalMax = r.ReadSingle(),
                    FrameMin = ReadFloats(r, data.FrameCount),
                    FrameMax = ReadFloats(r, data.FrameCount),
                    Values = Array.Empty<float[]>()
                };
            }

            int poolCount = r.ReadInt32();
            if (poolCount < 0)
                throw new InvalidDataException("Connectivity pool non valido.");

            connectivityPool = new ConnectivityEntry[poolCount];
            for (int i = 0; i < poolCount; i++)
            {
                ConnectivityEntry entry = new ConnectivityEntry
                {
                    IndexCount = r.ReadInt32(),
                    DecodedByteCount = r.ReadInt32(),
                    CompressedByteCount = r.ReadInt32(),
                    PayloadOffset = r.BaseStream.Position
                };

                if (entry.IndexCount < 0 ||
                    entry.DecodedByteCount < 0 ||
                    entry.CompressedByteCount < 0)
                {
                    throw new InvalidDataException(
                        $"Connectivity pool entry {i} non valida.");
                }

                connectivityPool[i] = entry;
                r.BaseStream.Seek(
                    entry.CompressedByteCount,
                    SeekOrigin.Current);
            }

            data.FrameOffsets = new long[data.FrameCount];
            for (int i = 0; i < data.FrameCount; i++)
                data.FrameOffsets[i] = r.ReadInt64();

            return data;
        }

        private ShellMeshFrame ReadV5Frame(int frame)
        {
            lock (reader)
            {
                reader.BaseStream.Seek(
                    Data.FrameOffsets[frame],
                    SeekOrigin.Begin);

                SIMBAFrameFlags flags =
                    (SIMBAFrameFlags)reader.ReadByte();

                int vertexCount = reader.ReadInt32();
                int connectivityId = reader.ReadInt32();

                if (vertexCount < 0)
                    throw new InvalidDataException(
                        $"Frame {frame}: vertex count non valido.");

                if ((flags & SIMBAFrameFlags.Empty) != 0)
                {
                    float[][] emptyFields =
                        new float[Data.Fields.Length][];

                    for (int i = 0; i < emptyFields.Length; i++)
                        emptyFields[i] = Array.Empty<float>();

                    return new ShellMeshFrame
                    {
                        Index = frame,
                        Flags = flags,
                        Vertices = Array.Empty<Vector3>(),
                        Triangles = Array.Empty<int>(),
                        FieldValues = emptyFields
                    };
                }

                if (connectivityId < 0 ||
                    connectivityId >= connectivityPool.Length)
                {
                    throw new InvalidDataException(
                        $"Frame {frame}: connectivity ID non valido.");
                }

                Vector3[] vertices =
                    ReadV5Vertices(reader, vertexCount);

                int[] triangles =
                    GetConnectivity(connectivityId);

                float[][] fields =
                    new float[Data.Fields.Length][];

                for (int field = 0;
                     field < fields.Length;
                     field++)
                {
                    fields[field] =
                        ReadFloats(reader, vertexCount);
                }

                return new ShellMeshFrame
                {
                    Index = frame,
                    Flags = flags,
                    Vertices = vertices,
                    Triangles = triangles,
                    FieldValues = fields
                };
            }
        }

        private Vector3[] ReadV5Vertices(
            BinaryReader r,
            int count)
        {
            Vector3[] vertices = new Vector3[count];

            if (Data.VertexFormat == SIMBAVertexFormat.Float32)
            {
                for (int i = 0; i < count; i++)
                {
                    vertices[i] = new Vector3(
                        r.ReadSingle(),
                        r.ReadSingle(),
                        r.ReadSingle());
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    vertices[i] = new Vector3(
                        SIMBAHalf.ToSingle(r.ReadUInt16()),
                        SIMBAHalf.ToSingle(r.ReadUInt16()),
                        SIMBAHalf.ToSingle(r.ReadUInt16()));
                }
            }

            return vertices;
        }

        private int[] GetConnectivity(int id)
        {
            if (connectivityCache.TryGetValue(id, out int[] cached))
                return cached;

            ConnectivityEntry entry = connectivityPool[id];

            lock (reader)
            {
                reader.BaseStream.Seek(
                    entry.PayloadOffset,
                    SeekOrigin.Begin);

                byte[] compressed =
                    reader.ReadBytes(entry.CompressedByteCount);

                if (compressed.Length != entry.CompressedByteCount)
                    throw new EndOfStreamException();

                byte[] decoded = SIMBALZ4.Decode(
                    compressed,
                    entry.DecodedByteCount);

                int[] values = DecodeDeltaConnectivity(
                    decoded,
                    entry.IndexCount,
                    Data.IndexFormat);

                connectivityCache[id] = values;
                return values;
            }
        }

        private static int[] DecodeDeltaConnectivity(
            byte[] bytes,
            int count,
            SIMBAIndexFormat format)
        {
            int width = format == SIMBAIndexFormat.UInt16 ? 2 : 4;

            if (bytes.Length != count * width)
                throw new InvalidDataException(
                    "Connectivity decoded size non valida.");

            int[] values = new int[count];
            int previous = 0;

            for (int i = 0; i < count; i++)
            {
                int delta;

                if (format == SIMBAIndexFormat.UInt16)
                {
                    ushort encoded = BitConverter.ToUInt16(bytes, i * 2);
                    delta = (encoded >> 1) ^ -(encoded & 1);
                }
                else
                {
                    uint encoded = BitConverter.ToUInt32(bytes, i * 4);
                    delta = unchecked((int)((encoded >> 1) ^
                        (uint)-(int)(encoded & 1)));
                }

                previous += delta;
                values[i] = previous;
            }

            return values;
        }

        private void BuildMesh()
        {
            RuntimeMesh = new Mesh
            {
                name = "shell_mesh_runtime"
            };

            if (markDynamic)
                RuntimeMesh.MarkDynamic();

            ShellMeshFrame first = GetFrame(0);
            ApplyFrameToMesh(first);
            GetComponent<MeshFilter>().sharedMesh = RuntimeMesh;
        }

        public void ApplyFrameToMesh(ShellMeshFrame frame)
        {
            RuntimeMesh.Clear(false);
            RuntimeMesh.indexFormat =
                frame.Vertices.Length > 65535
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16;
            RuntimeMesh.vertices = frame.Vertices;
            RuntimeMesh.triangles = frame.Triangles;

            if (frame.Vertices.Length > 0)
            {
                RuntimeMesh.RecalculateBounds();
                if (recalculateNormals)
                    RuntimeMesh.RecalculateNormals();
            }
        }

        private void Touch(int frame)
        {
            LinkedListNode<int> node =
                cacheOrder.Find(frame);

            if (node == null)
                return;

            cacheOrder.Remove(node);
            cacheOrder.AddLast(node);
        }

        private void TrimFrameCache()
        {
            int capacity = Mathf.Max(2, cachedFrameCount);

            while (frameCache.Count > capacity)
            {
                int oldest = cacheOrder.First.Value;
                cacheOrder.RemoveFirst();
                frameCache.Remove(oldest);
            }
        }

        private void CloseStream()
        {
            reader?.Dispose();
            reader = null;
            stream?.Dispose();
            stream = null;
        }

        // ---------- Legacy v3/v4 reader ----------

        private static ShellMeshData ParseLegacy(BinaryReader r)
        {
            byte[] magic = r.ReadBytes(8);
            if (Equal(magic, MagicV3)) return ParseV3(r);
            if (Equal(magic, MagicV4)) return ParseV4(r);
            throw new InvalidDataException("Magic legacy ShellMesh non valido.");
        }

        private static ShellMeshData ParseV3(BinaryReader r)
        {
            int version = r.ReadInt32();
            ValidateGeometry(r);
            ShellMeshData d = NewLegacyData(
                version,
                ShellTopologyMode.Static,
                r,
                false);

            int fieldCount = r.ReadInt32();
            ReadLegacyFieldHeaders(r, d, fieldCount);
            d.Triangles = ReadInts(r, d.TriangleCount * 3);
            ReadLegacyFieldRanges(r, d);

            d.Vertices = new Vector3[d.FrameCount][];
            for (int frame = 0; frame < d.FrameCount; frame++)
            {
                d.Vertices[frame] =
                    ReadVertices(r, d.VertexCount);
                ReadLegacyFieldValues(
                    r,
                    d,
                    frame,
                    d.VertexCount);
            }

            return d;
        }

        private static ShellMeshData ParseV4(BinaryReader r)
        {
            int version = r.ReadInt32();
            ValidateGeometry(r);

            ShellTopologyMode mode =
                (ShellTopologyMode)r.ReadInt32();

            ShellMeshData d = NewLegacyData(
                version,
                mode,
                r,
                true);

            int fieldCount = r.ReadInt32();
            ReadLegacyFieldHeaders(r, d, fieldCount);
            ReadLegacyFieldRanges(r, d);
            d.Vertices = new Vector3[d.FrameCount][];

            if (!d.HasDynamicTopology)
            {
                d.Triangles =
                    ReadInts(r, d.TriangleCount * 3);

                for (int frame = 0;
                     frame < d.FrameCount;
                     frame++)
                {
                    d.Vertices[frame] =
                        ReadVertices(r, d.VertexCount);
                    ReadLegacyFieldValues(
                        r,
                        d,
                        frame,
                        d.VertexCount);
                }
            }
            else
            {
                d.FrameTriangles =
                    new int[d.FrameCount][];

                for (int frame = 0;
                     frame < d.FrameCount;
                     frame++)
                {
                    int nv = r.ReadInt32();
                    int nt = r.ReadInt32();

                    if (nv < 0 || nt < 0)
                        throw new InvalidDataException(
                            $"Frame {frame}: conteggi non validi.");

                    d.Vertices[frame] =
                        ReadVertices(r, nv);
                    d.FrameTriangles[frame] =
                        ReadInts(r, nt * 3);
                    ReadLegacyFieldValues(r, d, frame, nv);
                }
            }

            return d;
        }

        private static ShellMeshData NewLegacyData(
            int version,
            ShellTopologyMode mode,
            BinaryReader r,
            bool hasFrameStep)
        {
            return new ShellMeshData
            {
                Version = version,
                TopologyMode = mode,
                FrameCount = r.ReadInt32(),
                VertexCount = r.ReadInt32(),
                TriangleCount = r.ReadInt32(),
                FramesPerSecond = r.ReadSingle(),
                FrameStep = hasFrameStep ? r.ReadInt32() : 1
            };
        }

        private static void ValidateGeometry(BinaryReader r)
        {
            GeometryType type =
                (GeometryType)r.ReadInt32();

            if (type != GeometryType.ShellMesh)
                throw new InvalidDataException(
                    $"Il file contiene {type}, non ShellMesh.");
        }

        private static void ValidateV5Header(
            ShellMeshData d,
            int fieldCount)
        {
            if (d.FrameCount <= 0 ||
                d.FramesPerSecond <= 0f ||
                d.FrameStep <= 0 ||
                d.VertexCount < 0 ||
                d.TriangleCount < 0 ||
                fieldCount <= 0)
            {
                throw new InvalidDataException(
                    "Header ShellMesh v5 non valido.");
            }
        }

        private static void ReadLegacyFieldHeaders(
            BinaryReader r,
            ShellMeshData d,
            int count)
        {
            d.Fields = new AnimatedField[count];

            for (int i = 0; i < count; i++)
            {
                d.Fields[i] = new AnimatedField
                {
                    Name = ReadString(r),
                    Units = ReadString(r),
                    GlobalMin = r.ReadSingle(),
                    GlobalMax = r.ReadSingle(),
                    FrameMin = new float[d.FrameCount],
                    FrameMax = new float[d.FrameCount],
                    Values = new float[d.FrameCount][]
                };
            }
        }

        private static void ReadLegacyFieldRanges(
            BinaryReader r,
            ShellMeshData d)
        {
            foreach (AnimatedField field in d.Fields)
            {
                field.FrameMin =
                    ReadFloats(r, d.FrameCount);
                field.FrameMax =
                    ReadFloats(r, d.FrameCount);
            }
        }

        private static void ReadLegacyFieldValues(
            BinaryReader r,
            ShellMeshData d,
            int frame,
            int count)
        {
            foreach (AnimatedField field in d.Fields)
                field.Values[frame] = ReadFloats(r, count);
        }

        private static Vector3[] ReadVertices(
            BinaryReader r,
            int count)
        {
            Vector3[] values = new Vector3[count];

            for (int i = 0; i < count; i++)
            {
                values[i] = new Vector3(
                    r.ReadSingle(),
                    r.ReadSingle(),
                    r.ReadSingle());
            }

            return values;
        }

        private static int[] ReadInts(
            BinaryReader r,
            int count)
        {
            int[] values = new int[count];
            for (int i = 0; i < count; i++)
                values[i] = r.ReadInt32();
            return values;
        }

        private static float[] ReadFloats(
            BinaryReader r,
            int count)
        {
            float[] values = new float[count];
            for (int i = 0; i < count; i++)
                values[i] = r.ReadSingle();
            return values;
        }

        private static string ReadString(BinaryReader r)
        {
            int length = r.ReadInt32();

            if (length < 0 || length > 1024 * 1024)
                throw new InvalidDataException(
                    "Stringa SIMBA non valida.");

            byte[] bytes = r.ReadBytes(length);
            if (bytes.Length != length)
                throw new EndOfStreamException();

            return Encoding.UTF8.GetString(bytes);
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }

            return true;
        }
    }
}
