from __future__ import annotations

import argparse
import hashlib
import re
import struct
from pathlib import Path

import h5py
import lz4.block
import numpy as np

from field_export_common import (
    FieldBlock,
    radius_field,
    sanitize_field,
    sanitize_scalar_frame,
    write_string,
    field_stats,
)

MAGIC = b"SHMSH005"
VERSION = 5
GEOMETRY_TYPE = 0

FEATURE_EMPTY_FRAMES = 1 << 0
FEATURE_FRAME_OFFSETS = 1 << 1
FEATURE_CONNECTIVITY_POOL = 1 << 2
FEATURE_LZ4_CONNECTIVITY = 1 << 3
FEATURE_DELTA_CONNECTIVITY = 1 << 4
FEATURE_LAZY_FIELDS = 1 << 5

FRAME_EMPTY = 1 << 0

VERTEX_CANDIDATES = ("Nodes", "Vertices", "Coordinates", "Positions")
TOPOLOGY_CANDIDATES = ("Connectivity", "Triangles", "Elements", "Faces")


def datasets(h5):
    result = {}
    h5.visititems(
        lambda name, obj: result.__setitem__(name, obj)
        if isinstance(obj, h5py.Dataset) else None
    )
    return result


def find_any_dataset(ds, names):
    for candidate in names:
        for name, obj in ds.items():
            if name.split("/")[-1].lower() == candidate.lower():
                return obj
    return None


def normalize_vertices_array(value):
    array = np.asarray(value)
    if array.ndim == 2:
        if array.shape[-1] != 3 and array.shape[0] == 3:
            array = array.T
        array = array[None]
    elif array.ndim == 3 and array.shape[-1] != 3:
        if array.shape[1] == 3:
            array = np.transpose(array, (2, 0, 1))
        elif array.shape[0] == 3:
            array = np.transpose(array, (2, 1, 0))
    if array.ndim != 3 or array.shape[-1] != 3:
        raise ValueError(f"Unsupported vertices shape {array.shape}")
    return np.asarray(array, dtype=np.float32)


def load_hdf5(h5, requested):
    keys = sorted(
        [key for key, value in h5.items()
         if isinstance(value, h5py.Group) and key.startswith("Time_")],
        key=time_number,
    )
    if keys:
        vertices = []
        triangles = []
        raw_fields = {name: [] for name in requested}
        for key in keys:
            group = h5[key]
            node_ds = find_dataset(group, VERTEX_CANDIDATES)
            triangle_ds = find_dataset(group, TOPOLOGY_CANDIDATES)
            if node_ds is None or triangle_ds is None:
                raise KeyError(f"{key}: Nodes/Connectivity missing")
            frame_vertices = normalize_nodes(node_ds[...])
            frame_triangles = normalize_triangles(triangle_ds[...], len(frame_vertices))
            vertices.append(frame_vertices)
            triangles.append(frame_triangles)
            for name in list(raw_fields):
                dataset = find_dataset(group, (name,))
                if dataset is None:
                    raw_fields.pop(name, None)
                    print(f"WARNING: field {name} not found in every frame", flush=True)
                else:
                    raw_fields[name].append(
                        sanitize_scalar_frame(dataset[...], len(frame_vertices), name)
                    )
        fields = [FieldBlock(name, "", values) for name, values in raw_fields.items()]
        return vertices, triangles, fields

    ds = datasets(h5)
    vertex_ds = find_any_dataset(ds, VERTEX_CANDIDATES)
    topology_ds = find_any_dataset(ds, TOPOLOGY_CANDIDATES)
    if vertex_ds is None or topology_ds is None:
        raise KeyError("Legacy HDF5: vertices/connectivity dataset not found")
    vertex_array = normalize_vertices_array(vertex_ds[...])
    vertices = [np.ascontiguousarray(frame, dtype=np.float32) for frame in vertex_array]
    raw_connectivity = np.asarray(topology_ds[...])
    if raw_connectivity.ndim == 2:
        one = normalize_triangles(raw_connectivity, len(vertices[0]))
        triangles = [one.copy() for _ in vertices]
    elif raw_connectivity.ndim == 3:
        if raw_connectivity.shape[0] != len(vertices):
            raise ValueError("Legacy HDF5 connectivity/frame count mismatch")
        triangles = [normalize_triangles(raw_connectivity[i], len(vertices[i]))
                     for i in range(len(vertices))]
    else:
        raise ValueError(f"Unsupported legacy connectivity shape {raw_connectivity.shape}")
    fields = []
    for name in requested:
        dataset = find_any_dataset(ds, (name,))
        if dataset is None:
            print(f"WARNING: field {name} not found", flush=True)
            continue
        if len({len(frame) for frame in vertices}) != 1:
            raise ValueError(f"{name}: variable vertex counts require Time_* groups")
        values = sanitize_field(dataset[...], len(vertices), len(vertices[0]), name)
        fields.append(FieldBlock(name, "", values))
    return vertices, triangles, fields


def time_number(key: str) -> int:
    match = re.findall(r"\d+", key)
    return int(match[0]) if match else 0


def find_dataset(group, names):
    for candidate in names:
        for key, value in group.items():
            if isinstance(value, h5py.Dataset) and key.lower() == candidate.lower():
                return value
    return None


def normalize_nodes(value):
    array = np.asarray(value, dtype=np.float32)
    if array.ndim != 2:
        raise ValueError(f"Nodes shape {array.shape}, expected (n,3)")
    if array.shape[1] != 3 and array.shape[0] == 3:
        array = array.T
    if array.shape[1] != 3:
        raise ValueError(f"Nodes shape {array.shape}, expected (n,3)")
    return np.ascontiguousarray(array, dtype=np.float32)


def normalize_triangles(value, vertex_count):
    array = np.asarray(value, dtype=np.int64)
    if array.size == 0:
        return np.empty((0, 3), dtype=np.int32)
    if array.ndim != 2:
        raise ValueError(f"Connectivity shape {array.shape}, expected (n,3)")
    if array.shape[1] != 3 and array.shape[0] == 3:
        array = array.T
    if array.shape[1] != 3:
        raise ValueError(f"Connectivity shape {array.shape}, expected (n,3)")
    if array.min() == 1:
        array = array - 1
    if array.min() < 0 or array.max() >= vertex_count:
        raise ValueError("Triangle connectivity out of range")
    return np.ascontiguousarray(array, dtype=np.int32)


def zigzag_encode(values):
    values = np.asarray(values, dtype=np.int64)
    return ((values << 1) ^ (values >> 63)).astype(np.uint64)

def connectivity_fits_uint16(connectivity):
    flat = np.asarray(connectivity, dtype=np.int64).reshape(-1)

    if flat.size == 0:
        return True

    deltas = np.empty_like(flat)
    deltas[0] = flat[0]
    deltas[1:] = flat[1:] - flat[:-1]

    encoded = zigzag_encode(deltas)

    return (
        encoded.size == 0
        or encoded.max() <= np.iinfo(np.uint16).max
    )
def encode_connectivity(connectivity, index_format):
    flat = np.asarray(connectivity, dtype=np.int64).reshape(-1)
    if flat.size == 0:
        raw = b""
    else:
        deltas = np.empty_like(flat)
        deltas[0] = flat[0]
        deltas[1:] = flat[1:] - flat[:-1]
        encoded = zigzag_encode(deltas)

        if index_format == 0:
            if encoded.size and encoded.max() > np.iinfo(np.uint16).max:
                raise ValueError(
                    "Internal error: uint16 selected for connectivity "
                    "that requires uint32."
                )

            raw = encoded.astype("<u2", copy=False).tobytes()
        else:
            if encoded.size and encoded.max() > np.iinfo(np.uint32).max:
                raise ValueError("Delta connectivity exceeds uint32.")
            raw = encoded.astype("<u4", copy=False).tobytes()

    compressed = lz4.block.compress(raw, store_size=False)
    return len(flat), len(raw), compressed


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--fields", nargs="*", default=[])
    parser.add_argument("--fps", type=float, default=30.0)
    parser.add_argument("--frame-step", type=int, default=1)
    parser.add_argument("--scale", type=float, default=1.0)
    parser.add_argument("--no-swap-yz", action="store_true")
    parser.add_argument("--add-radius", action="store_true")
    parser.add_argument(
        "--vertex-format",
        choices=("float16", "float32"),
        default="float32",
    )
    parser.add_argument(
        "--index-format",
        choices=("auto", "uint16", "uint32"),
        default="auto",
    )
    args = parser.parse_args()

    requested = [
        name for name in args.fields
        if name.lower() != "radius"
    ]

    with h5py.File(args.input, "r") as h5:
        vertices, triangles, fields = load_hdf5(h5, requested)

    step = max(1, args.frame_step)
    selected = list(range(0, len(vertices), step))
    if selected[-1] != len(vertices) - 1:
        selected.append(len(vertices) - 1)

    vertices = [vertices[i] for i in selected]
    triangles = [triangles[i] for i in selected]

    converted = []
    converted_triangles = []
    for points, connectivity in zip(vertices, triangles):
        p = (
            points[:, [0, 2, 1]]
            if not args.no_swap_yz
            else points.copy()
        )
        t = (
            connectivity[:, [0, 2, 1]]
            if not args.no_swap_yz
            else connectivity.copy()
        )
        converted.append(
            np.ascontiguousarray(
                p * np.float32(args.scale),
                dtype=np.float32,
            )
        )
        converted_triangles.append(
            np.ascontiguousarray(t, dtype=np.int32)
        )

    fields = [
        FieldBlock(
            field.name,
            field.units,
            [field.frames()[i] for i in selected],
        )
        for field in fields
    ]

    if args.add_radius or any(name.lower() == "radius" for name in args.fields) or not fields:
        radius = radius_field(vertices)
        radius = [
            np.ascontiguousarray(
                value * np.float32(args.scale),
                dtype=np.float32,
            )
            for value in radius
        ]
        fields.append(FieldBlock("Radius", "m", radius))

    vertex_format = 1 if args.vertex_format == "float16" else 0

    max_vertex_count = max(len(frame) for frame in converted)
    max_triangle_count = max(
        len(frame) for frame in converted_triangles
    )

    if args.index_format == "uint32":
        index_format = 1

    elif args.index_format == "uint16":
        all_fit_uint16 = all(
            connectivity_fits_uint16(connectivity)
            for connectivity in converted_triangles
        )

        if all_fit_uint16:
            index_format = 0
        else:
            print(
                "WARNING: requested uint16, but delta connectivity "
                "does not fit. Falling back to uint32.",
                flush=True,
            )
            index_format = 1

    else:
        all_fit_uint16 = (
            max_vertex_count <= 65535
            and all(
                connectivity_fits_uint16(connectivity)
                for connectivity in converted_triangles
            )
        )

        index_format = 0 if all_fit_uint16 else 1

        if index_format == 1:
            print(
                "Connectivity requires uint32 "
                "(vertex count or delta range exceeds uint16).",
                flush=True,
            )

    # Deduplicate connectivity by exact byte identity.
    pool = []
    pool_lookup = {}
    frame_connectivity_ids = []

    for connectivity in converted_triangles:
        key = hashlib.sha256(
            np.asarray(connectivity, dtype="<i4").tobytes()
        ).digest()

        existing = pool_lookup.get(key)
        if existing is not None and np.array_equal(
            pool[existing],
            connectivity,
        ):
            frame_connectivity_ids.append(existing)
            continue

        pool_id = len(pool)
        pool_lookup[key] = pool_id
        pool.append(connectivity)
        frame_connectivity_ids.append(pool_id)

    encoded_pool = [
        encode_connectivity(connectivity, index_format)
        for connectivity in pool
    ]

    stats = [field_stats(field) for field in fields]

    features = (
        FEATURE_EMPTY_FRAMES
        | FEATURE_FRAME_OFFSETS
        | FEATURE_CONNECTIVITY_POOL
        | FEATURE_LZ4_CONNECTIVITY
        | FEATURE_DELTA_CONNECTIVITY
        | FEATURE_LAZY_FIELDS
    )

    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)

    with output.open("wb") as file:
        file.write(MAGIC)
        file.write(
            struct.pack(
                "<iiIifiBBHiii",
                VERSION,
                GEOMETRY_TYPE,
                features,
                len(converted),
                args.fps,
                step,
                vertex_format,
                index_format,
                0,
                max_vertex_count,
                max_triangle_count,
                len(fields),
            )
        )

        for field, stat in zip(fields, stats):
            global_min, global_max, frame_min, frame_max = stat
            write_string(file, field.name)
            write_string(file, field.units)
            file.write(
                struct.pack("<ff", global_min, global_max)
            )
            np.asarray(frame_min, dtype="<f4").tofile(file)
            np.asarray(frame_max, dtype="<f4").tofile(file)

        file.write(struct.pack("<i", len(encoded_pool)))
        for index_count, decoded_bytes, compressed in encoded_pool:
            file.write(
                struct.pack(
                    "<iii",
                    index_count,
                    decoded_bytes,
                    len(compressed),
                )
            )
            file.write(compressed)

        offsets_position = file.tell()
        file.write(b"\x00" * (8 * len(converted)))
        frame_offsets = []

        field_frames = [field.frames() for field in fields]

        for frame_index, points in enumerate(converted):
            frame_offsets.append(file.tell())
            connectivity = converted_triangles[frame_index]
            empty = len(points) == 0 or len(connectivity) == 0
            flags = FRAME_EMPTY if empty else 0

            file.write(
                struct.pack(
                    "<Bii",
                    flags,
                    len(points),
                    frame_connectivity_ids[frame_index],
                )
            )

            if empty:
                continue

            if vertex_format == 0:
                np.asarray(points, dtype="<f4").tofile(file)
            else:
                np.asarray(points, dtype="<f2").tofile(file)

            for values in field_frames:
                np.asarray(
                    values[frame_index],
                    dtype="<f4",
                ).tofile(file)

        end_position = file.tell()
        file.seek(offsets_position)
        np.asarray(frame_offsets, dtype="<i8").tofile(file)
        file.seek(end_position)

    print(f"Created {output}", flush=True)
    print(
        f"Frames={len(converted)}, "
        f"connectivity pool={len(pool)}, "
        f"vertex format={args.vertex_format}, "
        f"index format={'uint16' if index_format == 0 else 'uint32'}",
        flush=True,
    )


if __name__ == "__main__":
    main()
