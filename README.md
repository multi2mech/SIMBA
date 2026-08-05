<div align="center">

# SIMBA

### **SIM**ulation **B**uffered **A**nimation

*A Unity framework for animated scientific simulations.*

[![Unity](https://img.shields.io/badge/Unity-6+-black.svg)]()
[![License](https://img.shields.io/badge/license-MIT-blue.svg)]()
[![Version](https://img.shields.io/badge/version-v1.1.0-success.svg)]()

</div>

<p align="center">
  <img src="Logo/logo.png" width="600">
</p>


---

SIMBA is an open-source Unity framework for interactive visualization of animated scientific simulations.

Unlike traditional mesh animation formats, SIMBA is specifically designed for simulation data, supporting dynamic topology, animated scalar fields, efficient runtime playback and large datasets.



## Features

- Shell mesh animations
- Dynamic topology support
- Animated scalar fields
- Multiple synchronized objects
- Efficient binary runtime format
- LZ4-compressed connectivity
- Connectivity pooling
- Delta-encoded connectivity
- Float16 / Float32 vertex storage
- Automatic UInt16 / UInt32 indices
- GPU-based color mapping
- URP compatible
- Optimized for VR and XR



## Pipeline

```text
Simulation
      │
      ▼
Blender2SIMBA
      │
      ▼
Intermediate HDF5
      │
      ▼
SIMBA Converter
      │
      ▼
SHMSH005
      │
      ▼
Unity Runtime
```

## How to use 

📖 Documentation

https://multi2mech.github.io/SIMBA/

🎮 Web demo

Interactive demo [https://multi2mech.github.io/SIMBA-web/](https://multi2mech.github.io/SIMBA-web/)


### Install 

#### 1. Install in Unity

Use Package Manager:

```
https://github.com/multi2mech/SIMBA.git?path=/Packages/com.m2m.simba
```

This installs only the Unity package.

#### 2. Connect Python executable

[XXXX]


The repository also contains:

- Demo project
- Examples
- DataGeneration scripts
- Documentation

Academic citation and Zenodo DOI will be added with the first public release.



## How does it work?


### Binary format

SIMBA uses optimized binary formats for runtime playback.

Current formats:

| Geometry | Extension |
|-----------|-----------|
| Shell Mesh | SHMSH005 |
| Line Mesh | LNMSH003 |



### Runtime Components

SIMBA provides:

- ShellMeshLoader
- ShellMeshAnimator
- FieldColorController
- SIMBAPlayer
- LineMeshPlayer

allowing interactive playback directly inside Unity.


## Blender Export

SIMBA is designed to work together with the official Blender addon:

**Blender2SIMBA**

https://github.com/mastroalex/Blender2SIMBA

---

# Roadmap

- Volume meshes
- Streaming field loading
- Lazy decompression
- GPU interpolation
- Native WebGPU support

---

# Citation

If you use SIMBA in academic work, please cite:

```
SIMBA — Simulation Buffered Animation
A Unity framework for animated scientific simulations.
```

