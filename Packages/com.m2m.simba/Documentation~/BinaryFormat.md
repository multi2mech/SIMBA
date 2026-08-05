# SIMBA binary format v3

All numbers are little-endian. Strings are encoded as a signed 32-bit byte length followed by UTF-8 bytes.

The runtime uses two magic values:

- `SHMSH003` for ShellMesh;
- `LINEM003` for LineMesh.

## Common header

```text
char[8] magic
int32   version = 3
int32   geometryType       # 0 ShellMesh, 1 LineMesh
int32   frameCount
int32   valueCount         # vertices for ShellMesh, nodes for LineMesh
int32   elementCount       # triangles or edges
float32 fps/sourceFps
```

LineMesh adds:

```text
int32 frameStep
```

Then:

```text
int32 fieldCount
for each field:
    string  name
    string  units
    float32 globalMin
    float32 globalMax
```

## ShellMesh body

```text
int32 triangles[triangleCount * 3]
for each field:
    float32 frameMin[frameCount]
    float32 frameMax[frameCount]
for each frame:
    float32 vertices[vertexCount * 3]
    for each field:
        float32 values[vertexCount]
```

## LineMesh body

```text
int32 sourceFrameIndices[frameCount]
int32 edges[edgeCount * 2]
for each field:
    float32 frameMin[frameCount]
    float32 frameMax[frameCount]
for each frame:
    float32 nodes[nodeCount * 3]
    for each field:
        float32 values[nodeCount]
```

## Compatibility

The reader validates magic, version and `GeometryType`. Regenerate older binaries with the converter version distributed with the installed package.

## Design constraints

- static topology;
- constant node/vertex count;
- nodal scalar fields;
- complete frames stored sequentially;
- no compression in v3.

Future versions can introduce chunking, compression and streaming while preserving versioned readers.
