using System;

namespace M2M.SIMBA
{
    [Flags]
    public enum SIMBAFileFeatures : uint
    {
        None = 0,
        EmptyFrames = 1u << 0,
        FrameOffsets = 1u << 1,
        ConnectivityPool = 1u << 2,
        LZ4Connectivity = 1u << 3,
        DeltaConnectivity = 1u << 4,
        LazyFields = 1u << 5
    }

    public enum SIMBAVertexFormat : byte
    {
        Float32 = 0,
        Float16 = 1
    }

    public enum SIMBAIndexFormat : byte
    {
        UInt16 = 0,
        UInt32 = 1
    }

    [Flags]
    public enum SIMBAFrameFlags : byte
    {
        None = 0,
        Empty = 1 << 0
    }
}
