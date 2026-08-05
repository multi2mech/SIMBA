from __future__ import annotations

import struct
from dataclasses import dataclass
from enum import IntEnum
from typing import BinaryIO, Iterable, Sequence

import numpy as np


class TopologyMode(IntEnum):
    STATIC = 0
    DYNAMIC = 1


@dataclass
class FieldBlock:
    name: str
    units: str
    values: Sequence[np.ndarray] | np.ndarray

    def frames(self) -> list[np.ndarray]:
        if isinstance(self.values, np.ndarray) and self.values.ndim == 2:
            return [np.ascontiguousarray(row, dtype=np.float32) for row in self.values]
        return [np.ascontiguousarray(np.asarray(row), dtype=np.float32).reshape(-1) for row in self.values]


def radius_field(points: np.ndarray | Sequence[np.ndarray]) -> list[np.ndarray] | np.ndarray:
    """Distance from the original Z axis: sqrt(x^2 + y^2)."""
    if isinstance(points, np.ndarray) and points.ndim == 3:
        return np.sqrt(points[..., 0] ** 2 + points[..., 1] ** 2).astype(np.float32)
    return [
        np.sqrt(np.asarray(frame)[:, 0] ** 2 + np.asarray(frame)[:, 1] ** 2).astype(np.float32)
        for frame in points
    ]


def write_string(f: BinaryIO, text: str) -> None:
    raw = text.encode('utf-8')
    f.write(struct.pack('<i', len(raw)))
    f.write(raw)


def sanitize_scalar_frame(values: np.ndarray, value_count: int, name: str) -> np.ndarray:
    a = np.asarray(values)
    a = np.squeeze(a)
    if a.ndim != 1 or a.size != value_count:
        raise ValueError(f'{name}: expected {value_count} scalar values, found shape {a.shape}')
    a = np.asarray(a, dtype=np.float32)
    if not np.isfinite(a).all():
        finite = a[np.isfinite(a)]
        replacement = float(np.mean(finite)) if finite.size else 0.0
        a = np.nan_to_num(a, nan=replacement, posinf=replacement, neginf=replacement)
    return np.ascontiguousarray(a)


def sanitize_field(values: np.ndarray, frame_count: int, value_count: int, name: str) -> np.ndarray:
    a = np.asarray(values)
    a = np.squeeze(a)
    if a.ndim == 1:
        if a.size != value_count:
            raise ValueError(f'{name}: expected {value_count} values, found {a.size}')
        a = np.repeat(a[None, :], frame_count, axis=0)
    elif a.ndim == 2:
        if a.shape == (value_count, frame_count):
            a = a.T
        elif a.shape != (frame_count, value_count):
            raise ValueError(f'{name}: shape {a.shape}, expected ({frame_count},{value_count})')
    else:
        raise ValueError(f'{name}: non-scalar field with shape {a.shape}')
    rows = [sanitize_scalar_frame(row, value_count, name) for row in a]
    return np.ascontiguousarray(np.stack(rows), dtype=np.float32)


def field_stats(field: FieldBlock):
    frames = field.frames()
    if not frames:
        raise ValueError(f'{field.name}: no frames')
    frame_min = np.asarray([float(v.min()) for v in frames], dtype=np.float32)
    frame_max = np.asarray([float(v.max()) for v in frames], dtype=np.float32)
    return float(frame_min.min()), float(frame_max.max()), frame_min, frame_max


def write_field_headers(f: BinaryIO, fields: Sequence[FieldBlock]):
    stats = []
    for field in fields:
        gmin, gmax, fmin, fmax = field_stats(field)
        stats.append((fmin, fmax))
        write_string(f, field.name)
        write_string(f, field.units)
        f.write(struct.pack('<ff', gmin, gmax))
    return stats


def write_field_ranges(f: BinaryIO, stats) -> None:
    for frame_min, frame_max in stats:
        np.asarray(frame_min, dtype='<f4').tofile(f)
        np.asarray(frame_max, dtype='<f4').tofile(f)


def topology_is_static(connectivity: Sequence[np.ndarray]) -> bool:
    if not connectivity:
        return True
    first = np.asarray(connectivity[0])
    return all(np.array_equal(first, np.asarray(frame)) for frame in connectivity[1:])


def choose_topology_mode(requested: str, connectivity: Sequence[np.ndarray]) -> TopologyMode:
    requested = requested.lower()
    detected_static = topology_is_static(connectivity)
    if requested == 'auto':
        return TopologyMode.STATIC if detected_static else TopologyMode.DYNAMIC
    if requested == 'static':
        if not detected_static:
            raise ValueError('Static topology requested, but connectivity changes between frames.')
        return TopologyMode.STATIC
    if requested == 'dynamic':
        return TopologyMode.DYNAMIC
    raise ValueError(f'Unknown topology mode: {requested}')
