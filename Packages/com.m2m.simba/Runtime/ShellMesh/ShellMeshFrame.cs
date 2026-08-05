using System;
using UnityEngine;

namespace M2M.SIMBA
{
    public sealed class ShellMeshFrame
    {
        public int Index;
        public SIMBAFrameFlags Flags;
        public Vector3[] Vertices = Array.Empty<Vector3>();
        public int[] Triangles = Array.Empty<int>();
        public float[][] FieldValues = Array.Empty<float[]>();
        public bool IsEmpty => (Flags & SIMBAFrameFlags.Empty) != 0 ||
                               Vertices.Length == 0 ||
                               Triangles.Length == 0;
    }
}
