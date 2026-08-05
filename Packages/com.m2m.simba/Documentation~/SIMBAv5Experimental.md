# SIMBA v1.2 experimental / SHMSH005

This package contains an experimental ShellMesh v5 path intended for
large Blender2SIMBA datasets.

Implemented:

- `SHMSH005` feature flags;
- official empty-frame support;
- frame offset table and random-access streaming;
- a configurable 2–8 frame LRU cache;
- connectivity deduplication pool;
- delta-encoded connectivity;
- raw LZ4 block compression for connectivity;
- automatic or explicit UInt16/UInt32 delta storage;
- selectable Float16/Float32 vertices;
- lazy field values: only cached frames are held in RAM;
- GraphicsBuffer on native Unity builds and vertex-color fallback on WebGL;
- compatibility reader for legacy SHMSH003/SHMSH004.

The v5 Python converter is:

`Python~/shell_mesh_h5_to_fields.py`

Example:

```bash
python shell_mesh_h5_to_fields.py \
  --input animation.h5 \
  --output animation_v5.bin \
  --vertex-format float16 \
  --index-format auto
```

Install dependencies:

```bash
pip install -r Python~/requirements.txt
```

## Important test-build limitations

- The new streaming format is implemented for ShellMesh first.
- LineMesh remains on its existing v3/v4 eager-loading path.
- On WebGL, UnityWebRequest still downloads the complete binary before
  creating a MemoryStream; browser streaming/range requests are not part
  of this test build.
- Float16 is intended for visualization. Use Float32 when scientific
  coordinate fidelity is required.
- This is an experimental package and should be tested on representative
  files before replacing a production package.
