using System;
using UnityEngine;

namespace M2M.SIMBA
{
    public sealed class ShellMeshData
    {
        public int Version;
        public ShellTopologyMode TopologyMode = ShellTopologyMode.Static;
        public SIMBAFileFeatures Features;
        public SIMBAVertexFormat VertexFormat = SIMBAVertexFormat.Float32;
        public SIMBAIndexFormat IndexFormat = SIMBAIndexFormat.UInt32;
        public int FrameCount;
        public int VertexCount;
        public int TriangleCount;
        public float FramesPerSecond;
        public int FrameStep = 1;
        public int[] SourceFrameIndices = Array.Empty<int>();
        public long[] FrameOffsets = Array.Empty<long>();
        public int[] Triangles = Array.Empty<int>();
        public int[][] FrameTriangles = Array.Empty<int[]>();
        public Vector3[][] Vertices = Array.Empty<Vector3[]>();
        public AnimatedField[] Fields = Array.Empty<AnimatedField>();

        public bool IsStreaming => Version >= 5;
        public bool HasDynamicTopology =>
            TopologyMode == ShellTopologyMode.Dynamic ||
            (Features & SIMBAFileFeatures.ConnectivityPool) != 0;

        public int GetVertexCount(int frame) =>
            IsStreaming ? 0 : Vertices[frame].Length;

        public int[] GetTriangles(int frame) =>
            HasDynamicTopology ? FrameTriangles[frame] : Triangles;

        public int GetTriangleCount(int frame) =>
            IsStreaming ? 0 : GetTriangles(frame).Length / 3;
    }
}
