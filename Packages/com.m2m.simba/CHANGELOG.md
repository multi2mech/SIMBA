# Changelog

## 1.2.0-test.2

- Added SHMSH005 support to the Unity Editor binary-header reader.
- Fixed the importer error: `Unsupported SIMBA magic 'SHMSH005'`.
- Preserved runtime compatibility with SHMSH003, SHMSH004 and SHMSH005.
- Kept one official ShellMesh Python converter.
- Kept public `Pause()` and `Resume()` methods on SIMBAPlayer,
  ShellMeshAnimator and LineMeshPlayer.


## 1.2.0-test.1

- Unified ShellMesh converter: one `shell_mesh_h5_to_fields.py`.
- Legacy array-layout and Time_* HDF5 inputs supported by the same converter.
- Removed duplicate `_v5` converter.
- Added public `Pause()` and `Resume()` runtime controls.
- Includes SHMSH005 streaming, offsets, cache, connectivity pool, delta/LZ4, Float16/Float32 and lazy fields.

# Changelog

## 1.2.0-experimental.1

- Added experimental SHMSH005 streaming format.
- Added frame flags and empty frames.
- Added frame offsets and three-frame cache.
- Added connectivity pool, delta encoding and LZ4 compression.
- Added selectable Float16/Float32 vertices.
- Added automatic UInt16/UInt32 connectivity delta width.
- Added lazy field loading for ShellMesh v5.
- Added native GraphicsBuffer / WebGL vertex-color automatic backend.

# Changelog

All notable changes to SIMBA are documented here.

## [1.0.1] - 2026-08-03

### Fixed

- Resolved the `PackageInfo` ambiguity on Unity versions exposing both Editor types.
- Made the Import Simulation scroll view exception-safe to prevent unbalanced IMGUI layout and GUIClip errors.
- Added explicit package-manager type aliases in Python tool path resolution.

## [1.0.0] - 2026-08-03

### Added

- Unified public `SIMBAPlayer` component and API.
- Runtime playback, field, colormap, range and appearance controls.
- Runtime events for loading, frame, field, colormap and playback completion.
- Custom `SIMBAPlayer` Inspector with Play Mode controls.
- `SIMBAUtilities` screenshot helper.
- Editor menu entries for default material, screenshots, documentation and About.
- About window and replaceable placeholder icons.
- Expanded API and release documentation.

## [0.4.0] - 2026-08-03

- Dynamic field dropdown in the FieldColorController Inspector.
- Complete HDF5, Python, API and binary-format documentation.
- Conda `environment.yml` and pip `requirements.txt`.

## [0.3.0] - 2026-08-03

- Direct HDF5 import from Unity.
- Persistent Python/Conda interpreter settings.

## [0.2.0] - 2026-08-03

- SIMBA Import Simulation Editor window.
- GeometryType in binary format.

## [0.1.0] - 2026-08-03

- Initial UPM package.
