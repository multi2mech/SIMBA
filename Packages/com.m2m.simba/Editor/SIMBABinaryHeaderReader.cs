#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;

namespace M2M.SIMBA.Editor
{
    /// <summary>
    /// Reads SIMBA binary metadata without loading animation payloads.
    ///
    /// Supported:
    /// - SHMSH003 / LNMSH003
    /// - SHMSH004 / LNMSH004
    /// - SHMSH005
    /// </summary>
    public static class SIMBABinaryHeaderReader
    {
        private static readonly byte[] ShellV3Magic =
            Encoding.ASCII.GetBytes("SHMSH003");

        private static readonly byte[] ShellV4Magic =
            Encoding.ASCII.GetBytes("SHMSH004");

        private static readonly byte[] ShellV5Magic =
            Encoding.ASCII.GetBytes("SHMSH005");

        private static readonly byte[] LineV3Magic =
            Encoding.ASCII.GetBytes("LNMSH003");

        private static readonly byte[] LineV4Magic =
            Encoding.ASCII.GetBytes("LNMSH004");

        public static SIMBABinaryHeader Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException(
                    "A binary path is required.",
                    nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "SIMBA binary file not found.",
                    path);
            }

            using FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            using BinaryReader reader = new BinaryReader(
                stream,
                Encoding.UTF8,
                false);

            byte[] magic = reader.ReadBytes(8);

            if (magic.Length != 8)
            {
                throw new EndOfStreamException(
                    "The file is too short to contain a SIMBA header.");
            }

            if (Matches(magic, ShellV5Magic))
                return ReadShellV5(reader, path);

            bool shell;
            bool explicitTopology;

            if (Matches(magic, ShellV3Magic))
            {
                shell = true;
                explicitTopology = false;
            }
            else if (Matches(magic, ShellV4Magic))
            {
                shell = true;
                explicitTopology = true;
            }
            else if (Matches(magic, LineV3Magic))
            {
                shell = false;
                explicitTopology = false;
            }
            else if (Matches(magic, LineV4Magic))
            {
                shell = false;
                explicitTopology = true;
            }
            else
            {
                throw new InvalidDataException(
                    $"Unsupported SIMBA magic " +
                    $"'{Encoding.ASCII.GetString(magic)}'.");
            }

            return ReadLegacy(
                reader,
                path,
                shell,
                explicitTopology);
        }

        private static SIMBABinaryHeader ReadShellV5(
            BinaryReader reader,
            string path)
        {
            SIMBABinaryHeader header = new SIMBABinaryHeader
            {
                SourcePath = Path.GetFullPath(path),
                Version = reader.ReadInt32(),
                GeometryType =
                    (GeometryType)reader.ReadInt32(),
                TopologyMode =
                    SIMBATopologyMode.Dynamic
            };

            if (header.Version != 5)
            {
                throw new InvalidDataException(
                    $"SHMSH005 contains unsupported " +
                    $"version {header.Version}.");
            }

            // Feature flags.
            reader.ReadUInt32();

            header.FrameCount = reader.ReadInt32();
            header.FramesPerSecond = reader.ReadSingle();
            header.FrameStep = reader.ReadInt32();

            // VertexFormat, IndexFormat and reserved alignment.
            reader.ReadByte();
            reader.ReadByte();
            reader.ReadUInt16();

            // Maximum values across all frames.
            header.ValueCount = reader.ReadInt32();
            header.ElementCount = reader.ReadInt32();

            int fieldCount = reader.ReadInt32();

            ValidateV5Header(header, fieldCount);

            for (int i = 0; i < fieldCount; i++)
            {
                header.FieldNames.Add(ReadString(reader));
                header.FieldUnits.Add(ReadString(reader));

                // Global minimum and maximum.
                reader.ReadSingle();
                reader.ReadSingle();

                // Per-frame minimum and maximum arrays.
                SkipBytes(
                    reader,
                    checked(header.FrameCount * sizeof(float)));

                SkipBytes(
                    reader,
                    checked(header.FrameCount * sizeof(float)));
            }

            return header;
        }

        private static SIMBABinaryHeader ReadLegacy(
            BinaryReader reader,
            string path,
            bool shell,
            bool explicitTopology)
        {
            SIMBABinaryHeader header = new SIMBABinaryHeader
            {
                SourcePath = Path.GetFullPath(path),
                Version = reader.ReadInt32(),
                GeometryType =
                    (GeometryType)reader.ReadInt32()
            };

            if (explicitTopology)
            {
                int rawMode = reader.ReadInt32();

                if (!Enum.IsDefined(
                    typeof(SIMBATopologyMode),
                    rawMode))
                {
                    throw new InvalidDataException(
                        $"Invalid topology mode {rawMode}.");
                }

                header.TopologyMode =
                    (SIMBATopologyMode)rawMode;
            }
            else
            {
                header.TopologyMode =
                    SIMBATopologyMode.Static;
            }

            header.FrameCount = reader.ReadInt32();
            header.ValueCount = reader.ReadInt32();
            header.ElementCount = reader.ReadInt32();
            header.FramesPerSecond = reader.ReadSingle();
            header.FrameStep =
                explicitTopology
                    ? reader.ReadInt32()
                    : 1;

            int fieldCount = reader.ReadInt32();

            ValidateLegacyHeader(
                header,
                fieldCount,
                shell);

            for (int i = 0; i < fieldCount; i++)
            {
                header.FieldNames.Add(ReadString(reader));
                header.FieldUnits.Add(ReadString(reader));
                reader.ReadSingle();
                reader.ReadSingle();
            }

            return header;
        }

        private static void ValidateV5Header(
            SIMBABinaryHeader header,
            int fieldCount)
        {
            if (header.GeometryType != GeometryType.ShellMesh)
            {
                throw new InvalidDataException(
                    "SHMSH005 must contain ShellMesh geometry.");
            }

            if (header.FrameCount <= 0)
            {
                throw new InvalidDataException(
                    "Frame count must be positive.");
            }

            // A v5 file may legitimately contain only empty frames.
            if (header.ValueCount < 0)
            {
                throw new InvalidDataException(
                    "Maximum vertex count cannot be negative.");
            }

            if (header.ElementCount < 0)
            {
                throw new InvalidDataException(
                    "Maximum element count cannot be negative.");
            }

            ValidateTimingAndFields(header, fieldCount);
        }

        private static void ValidateLegacyHeader(
            SIMBABinaryHeader header,
            int fieldCount,
            bool shellMagic)
        {
            if (header.Version < 3 ||
                header.Version > 4)
            {
                throw new InvalidDataException(
                    $"Unsupported SIMBA version " +
                    $"{header.Version}.");
            }

            if (header.FrameCount <= 0)
            {
                throw new InvalidDataException(
                    "Frame count must be positive.");
            }

            if (header.ValueCount <= 0)
            {
                throw new InvalidDataException(
                    "Value count must be positive.");
            }

            if (header.ElementCount < 0)
            {
                throw new InvalidDataException(
                    "Element count cannot be negative.");
            }

            ValidateTimingAndFields(header, fieldCount);

            GeometryType expected =
                shellMagic
                    ? GeometryType.ShellMesh
                    : GeometryType.LineMesh;

            if (header.GeometryType != expected)
            {
                throw new InvalidDataException(
                    $"Magic identifies {expected}, but " +
                    $"the header contains " +
                    $"{header.GeometryType}.");
            }
        }

        private static void ValidateTimingAndFields(
            SIMBABinaryHeader header,
            int fieldCount)
        {
            if (!(header.FramesPerSecond > 0f) ||
                float.IsNaN(header.FramesPerSecond) ||
                float.IsInfinity(header.FramesPerSecond))
            {
                throw new InvalidDataException(
                    "Frames per second must be finite " +
                    "and positive.");
            }

            if (header.FrameStep <= 0)
            {
                throw new InvalidDataException(
                    "Frame step must be positive.");
            }

            if (fieldCount < 0 || fieldCount > 4096)
            {
                throw new InvalidDataException(
                    $"Invalid field count {fieldCount}.");
            }
        }

        private static string ReadString(
            BinaryReader reader)
        {
            int length = reader.ReadInt32();

            if (length < 0 || length > 1024 * 1024)
            {
                throw new InvalidDataException(
                    $"Invalid UTF-8 string length {length}.");
            }

            byte[] bytes = reader.ReadBytes(length);

            if (bytes.Length != length)
                throw new EndOfStreamException();

            return Encoding.UTF8.GetString(bytes);
        }

        private static void SkipBytes(
            BinaryReader reader,
            int count)
        {
            long target = checked(
                reader.BaseStream.Position + count);

            if (target > reader.BaseStream.Length)
                throw new EndOfStreamException();

            reader.BaseStream.Seek(
                count,
                SeekOrigin.Current);
        }

        private static bool Matches(
            byte[] left,
            byte[] right)
        {
            if (left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                    return false;
            }

            return true;
        }
    }
}
#endif
