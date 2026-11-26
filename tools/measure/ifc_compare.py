#!/usr/bin/env python3
"""Pure-Python STEP/IFC parser and comparator for RvtIO reproducible QA.

Compares a candidate IFC export against a Revit reference IFC (and diffs two
RvtIO commit reports produced against the same Revit reference) across:
  1. Identifiers (GlobalId & Tag pairing, spaces, spatial roots)
  2. IFC classes (same class, cross-class mappings, missing/extra per class)
  3. Storeys (IfcBuildingStorey names, GlobalIds, normalized elevations, element containment)
  4. World bounding boxes & extents (SI metres, configurable tolerance)

No third-party dependencies are required.
"""

from __future__ import annotations

import collections
from collections.abc import Iterator
import math
import re
from pathlib import Path
from typing import Any

EXCLUDED_ELEMENT_TYPES = frozenset({
    "IFCOPENINGELEMENT",
    "IFCOPENINGSTANDARDCASE",
    "IFCVIRTUALELEMENT",
})

NON_PHYSICAL_PRODUCT_TYPES = frozenset({
    "IFCPROJECT",
    "IFCSITE",
    "IFCBUILDING",
    "IFCBUILDINGSTOREY",
    "IFCSPACE",
    "IFCSPATIALZONE",
    "IFCEXTERNALSPATIALELEMENT",
    "IFCGRID",
    "IFCANNOTATION",
    "IFCDISTRIBUTIONPORT",
    "IFCPORT",
    "IFCSYSTEM",
    "IFCZONE",
    "IFCGROUP",
    "IFCPROJECTIONELEMENT",
    "IFCSTRUCTURALITEM",
    "IFCSTRUCTURALMEMBER",
    "IFCSTRUCTURALCONNECTION",
})

SPATIAL_ROOT_TYPES = ("IFCPROJECT", "IFCSITE", "IFCBUILDING")

SI_PREFIX_FACTORS = {
    "EXA": 1e18,
    "PETA": 1e15,
    "TERA": 1e12,
    "GIGA": 1e9,
    "MEGA": 1e6,
    "KILO": 1e3,
    "HECTO": 1e2,
    "DECA": 1e1,
    "$": 1.0,
    None: 1.0,
    "DECI": 1e-1,
    "CENTI": 1e-2,
    "MILLI": 1e-3,
    "MICRO": 1e-6,
    "NANO": 1e-9,
    "PICO": 1e-12,
}

CONVERSION_TO_METRES = {
    "FOOT": 0.3048,
    "FEET": 0.3048,
    "INCH": 0.0254,
    "INCHES": 0.0254,
    "YARD": 0.9144,
    "MILE": 1609.344,
    "MILLIMETRE": 0.001,
    "MILLIMETER": 0.001,
    "CENTIMETRE": 0.01,
    "CENTIMETER": 0.01,
    "METRE": 1.0,
    "METER": 1.0,
}

IDENT = ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (0.0, 0.0, 0.0))

ENTITY_RE = re.compile(r"#(\d+)\s*=\s*([A-Z0-9_]+)\s*\((.*)\)\s*;", re.S)
SCHEMA_RE = re.compile(r"FILE_SCHEMA\s*\(\s*\(\s*'([^']+)'", re.I)
STEP_TOKEN_RE = re.compile(r"'(?:[^']|'')*'|(?P<comment>/\*.*?\*/)|;", re.S)


def step_statements(text: str) -> Iterator[tuple[int, int, str]]:
    """Yield each STEP statement without comments and its span in the source."""
    start = fragment = 0
    parts: list[str] = []
    for token in STEP_TOKEN_RE.finditer(text):
        if token.lastgroup == "comment":
            parts.extend((text[fragment:token.start()], " "))
            fragment = token.end()
        elif token.group() == ";":
            parts.append(text[fragment:token.end()])
            yield start, token.end(), "".join(parts).strip()
            start = fragment = token.end()
            parts.clear()


def tokenize_args(s: str) -> list[str]:
    """Split a STEP argument list at top-level commas."""
    out: list[str] = []
    depth = 0
    cur: list[str] = []
    instr = False
    i = 0
    n = len(s)
    while i < n:
        c = s[i]
        if instr:
            cur.append(c)
            if c == "'":
                if i + 1 < n and s[i + 1] == "'":
                    cur.append("'")
                    i += 2
                    continue
                instr = False
        elif c == "'":
            instr = True
            cur.append(c)
        elif c == "(":
            depth += 1
            cur.append(c)
        elif c == ")":
            depth -= 1
            cur.append(c)
        elif c == "," and depth == 0:
            out.append("".join(cur).strip())
            cur = []
        else:
            cur.append(c)
        i += 1
    out.append("".join(cur).strip())
    return out


def decode_step_string(tok: str) -> str | None:
    tok = tok.strip()
    if tok in ("$", "*", ""):
        return None
    if len(tok) >= 2 and tok[0] == "'" and tok[-1] == "'":
        inner = tok[1:-1].replace("''", "'")
        # Decode STEP \X2\...\X0\ UTF-16BE hex sequences when present
        def _repl_x2(m: re.Match[str]) -> str:
            hex_str = m.group(1)
            try:
                return bytes.fromhex(hex_str).decode("utf-16-be")
            except Exception:
                return m.group(0)
        inner = re.sub(r"\\X2\\([0-9A-Fa-f]+)\\X0\\", _repl_x2, inner)
        return inner
    return tok


def parse_enum(tok: str) -> str | None:
    tok = tok.strip()
    if tok.startswith(".") and tok.endswith(".") and len(tok) >= 2:
        return tok[1:-1]
    return None


def parse_ref(tok: str) -> int | None:
    tok = tok.strip()
    if tok.startswith("#") and tok[1:].isdigit():
        return int(tok[1:])
    return None


def parse_ref_list(tok: str) -> list[int]:
    tok = tok.strip()
    if not (tok.startswith("(") and tok.endswith(")")):
        return []
    out: list[int] = []
    for item in tokenize_args(tok[1:-1]):
        r = parse_ref(item)
        if r is not None:
            out.append(r)
    return out


def parse_num(tok: str) -> float | None:
    tok = tok.strip()
    if tok in ("$", "*", ""):
        return None
    # Strip STEP typed wrappers such as IFCREAL(1.5) or IFCLENGTHMEASURE(100.)
    if "(" in tok and tok.endswith(")"):
        tok = tok[tok.find("(") + 1 : -1].strip()
    try:
        return float(tok)
    except ValueError:
        return None


def parse_num_list(tok: str) -> list[float]:
    tok = tok.strip()
    if not (tok.startswith("(") and tok.endswith(")")):
        return []
    out: list[float] = []
    for item in tokenize_args(tok[1:-1]):
        v = parse_num(item)
        if v is not None:
            out.append(v)
    return out


def parse_nested_num_list(tok: str) -> list[list[float]]:
    tok = tok.strip()
    if not (tok.startswith("(") and tok.endswith(")")):
        return []
    return [parse_num_list(item) for item in tokenize_args(tok[1:-1])]


def parse_nested_int_list(tok: str) -> list[list[int]]:
    tok = tok.strip()
    if not (tok.startswith("(") and tok.endswith(")")):
        return []
    out: list[list[int]] = []
    for item in tokenize_args(tok[1:-1]):
        out.append([int(round(v)) for v in parse_num_list(item)])
    return out


def _vec_norm(v: tuple[float, float, float]) -> tuple[float, float, float]:
    length = math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])
    if length < 1e-12:
        return (1.0, 0.0, 0.0)
    return (v[0] / length, v[1] / length, v[2] / length)


def _vec_cross(a: tuple[float, float, float], b: tuple[float, float, float]) -> tuple[float, float, float]:
    return (
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    )


def _vec_dot(a: tuple[float, float, float], b: tuple[float, float, float]) -> float:
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def mat_mul(a: tuple, b: tuple) -> tuple:
    ax, ay, az, ao = a
    bx, by, bz, bo = b

    def tx(v: tuple[float, float, float]) -> tuple[float, float, float]:
        return (
            ax[0] * v[0] + ay[0] * v[1] + az[0] * v[2],
            ax[1] * v[0] + ay[1] * v[1] + az[1] * v[2],
            ax[2] * v[0] + ay[2] * v[1] + az[2] * v[2],
        )

    bt = tx(bo)
    return (tx(bx), tx(by), tx(bz), (ao[0] + bt[0], ao[1] + bt[1], ao[2] + bt[2]))


def apply_mat(m: tuple, p: tuple[float, float, float]) -> tuple[float, float, float]:
    x, y, z, o = m
    return (
        o[0] + x[0] * p[0] + y[0] * p[1] + z[0] * p[2],
        o[1] + x[1] * p[0] + y[1] * p[1] + z[1] * p[2],
        o[2] + x[2] * p[0] + y[2] * p[1] + z[2] * p[2],
    )


def union_aabb(a: list[float] | None, b: list[float] | None) -> list[float] | None:
    if a is None:
        return None if b is None else list(b)
    if b is None:
        return list(a)
    return [
        min(a[0], b[0]), min(a[1], b[1]), min(a[2], b[2]),
        max(a[3], b[3]), max(a[4], b[4]), max(a[5], b[5]),
    ]


def aabb_from_points(pts: list[tuple[float, float, float]], transform: tuple) -> list[float] | None:
    if not pts:
        return None
    pts = [apply_mat(transform, p) for p in pts]
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    zs = [p[2] for p in pts]
    return [min(xs), min(ys), min(zs), max(xs), max(ys), max(zs)]


def transform_aabb(aabb: list[float], m: tuple) -> list[float]:
    corners = [
        (x, y, z)
        for x in (aabb[0], aabb[3])
        for y in (aabb[1], aabb[4])
        for z in (aabb[2], aabb[5])
    ]
    res = aabb_from_points(corners, m)
    assert res is not None
    return res


def scale_aabb(aabb: list[float], scale: float) -> list[float]:
    return [round(v * scale, 6) for v in aabb]


class StepModel:
    """In-memory STEP Part-21 model for fast IFC structure and geometry inspection."""

    def __init__(self, path: Path):
        self.path = path
        self.text = path.read_text(encoding="utf-8-sig", errors="replace")
        self.schema = "UNKNOWN"
        self.ent: dict[int, tuple[str, str]] = {}
        self.record_spans: dict[int, tuple[int, int]] = {}
        self.by_type_map: dict[str, list[int]] = collections.defaultdict(list)
        in_data = False
        for start, end, statement in step_statements(self.text):
            if not in_data:
                schema_match = SCHEMA_RE.match(statement)
                if schema_match:
                    self.schema = schema_match.group(1)
                in_data = statement == "DATA;"
                continue
            if statement == "ENDSEC;":
                self.data_end = start
                break
            m = ENTITY_RE.fullmatch(statement)
            if m is None:
                raise ValueError(f"Invalid STEP entity at character {start}: {path}")
            eid = int(m.group(1))
            etype = m.group(2).upper()
            self.ent[eid] = (etype, m.group(3))
            self.record_spans[eid] = (start, end)
            self.by_type_map[etype].append(eid)
        else:
            raise ValueError(f"Invalid STEP file (missing DATA;/ENDSEC;): {path}")
        self._args_cache: dict[int, list[str]] = {}
        self._point_cache: dict[int, tuple[float, float, float]] = {}
        self._dir_cache: dict[int, tuple[float, float, float]] = {}
        self._placement_cache: dict[int, tuple] = {}
        self._geom_cache: dict[tuple[int, tuple], tuple[list[float] | None, bool]] = {}
        self.length_scale_to_metres = self._resolve_length_unit_scale()
        self.warnings: list[str] = []

    def type_of(self, eid: int | None) -> str | None:
        if eid is None:
            return None
        row = self.ent.get(eid)
        return row[0] if row else None

    def args(self, eid: int) -> list[str]:
        cached = self._args_cache.get(eid)
        if cached is not None:
            return cached
        parsed = tokenize_args(self.ent[eid][1])
        self._args_cache[eid] = parsed
        return parsed

    def by_type(self, etype: str) -> list[int]:
        return self.by_type_map.get(etype.upper(), [])

    def _resolve_length_unit_scale(self) -> float:
        for ua_id in self.by_type("IFCUNITASSIGNMENT"):
            ua_args = self.args(ua_id)
            if not ua_args:
                continue
            for uref in parse_ref_list(ua_args[0]):
                utype = self.type_of(uref)
                if utype == "IFCSIUNIT":
                    uargs = self.args(uref)
                    if len(uargs) >= 4 and parse_enum(uargs[1]) == "LENGTHUNIT":
                        prefix = parse_enum(uargs[2])
                        name = parse_enum(uargs[3])
                        if name == "METRE":
                            return SI_PREFIX_FACTORS.get(prefix, 1.0)
                elif utype == "IFCCONVERSIONBASEDUNIT":
                    uargs = self.args(uref)
                    if len(uargs) >= 4 and parse_enum(uargs[1]) == "LENGTHUNIT":
                        uname = (decode_step_string(uargs[2]) or "").upper()
                        mwu_ref = parse_ref(uargs[3])
                        if mwu_ref and self.type_of(mwu_ref) == "IFCMEASUREWITHUNIT":
                            m_args = self.args(mwu_ref)
                            val = parse_num(m_args[0]) if m_args else None
                            base_ref = parse_ref(m_args[1]) if len(m_args) > 1 else None
                            if val is not None and base_ref and self.type_of(base_ref) == "IFCSIUNIT":
                                b_args = self.args(base_ref)
                                b_prefix = parse_enum(b_args[2]) if len(b_args) > 2 else None
                                return val * SI_PREFIX_FACTORS.get(b_prefix, 1.0)
                        if uname in CONVERSION_TO_METRES:
                            return CONVERSION_TO_METRES[uname]
        return 1.0

    def cartesian_point(self, eid: int | None) -> tuple[float, float, float]:
        if eid is None:
            return (0.0, 0.0, 0.0)
        cached = self._point_cache.get(eid)
        if cached is not None:
            return cached
        coords = parse_num_list(self.args(eid)[0])
        x = coords[0] if len(coords) > 0 else 0.0
        y = coords[1] if len(coords) > 1 else 0.0
        z = coords[2] if len(coords) > 2 else 0.0
        pt = (x, y, z)
        self._point_cache[eid] = pt
        return pt

    def direction(self, eid: int | None, default: tuple[float, float, float]) -> tuple[float, float, float]:
        if eid is None:
            return default
        cached = self._dir_cache.get(eid)
        if cached is not None:
            return cached
        coords = parse_num_list(self.args(eid)[0])
        d = _vec_norm((
            coords[0] if len(coords) > 0 else default[0],
            coords[1] if len(coords) > 1 else default[1],
            coords[2] if len(coords) > 2 else default[2],
        ))
        self._dir_cache[eid] = d
        return d

    def axis2placement3d(self, eid: int | None) -> tuple:
        if eid is None:
            return IDENT
        et = self.type_of(eid)
        if et == "IFCAXIS2PLACEMENT2D":
            a = self.args(eid)
            loc = self.cartesian_point(parse_ref(a[0])) if len(a) > 0 else (0.0, 0.0, 0.0)
            refdir = self.direction(parse_ref(a[1]) if len(a) > 1 else None, (1.0, 0.0, 0.0))
            x = _vec_norm((refdir[0], refdir[1], 0.0))
            z = (0.0, 0.0, 1.0)
            y = _vec_norm(_vec_cross(z, x))
            return (x, y, z, (loc[0], loc[1], 0.0))
        a = self.args(eid)
        loc = self.cartesian_point(parse_ref(a[0])) if len(a) > 0 else (0.0, 0.0, 0.0)
        z = self.direction(parse_ref(a[1]) if len(a) > 1 else None, (0.0, 0.0, 1.0))
        ref_x = self.direction(parse_ref(a[2]) if len(a) > 2 else None, (1.0, 0.0, 0.0))
        # Orthogonalize ref_x against z (IFC convention)
        dot = _vec_dot(ref_x, z)
        px = (ref_x[0] - dot * z[0], ref_x[1] - dot * z[1], ref_x[2] - dot * z[2])
        if math.sqrt(_vec_dot(px, px)) < 1e-9:
            fallback = (1.0, 0.0, 0.0) if abs(z[0]) < 0.9 else (0.0, 1.0, 0.0)
            d2 = _vec_dot(fallback, z)
            px = (fallback[0] - d2 * z[0], fallback[1] - d2 * z[1], fallback[2] - d2 * z[2])
        x = _vec_norm(px)
        y = _vec_norm(_vec_cross(z, x))
        return (x, y, z, loc)

    def local_placement(self, eid: int | None) -> tuple:
        if eid is None:
            return IDENT
        cached = self._placement_cache.get(eid)
        if cached is not None:
            return cached
        et = self.type_of(eid)
        if et != "IFCLOCALPLACEMENT":
            return IDENT
        a = self.args(eid)
        parent = self.local_placement(parse_ref(a[0])) if len(a) > 0 else IDENT
        rel = self.axis2placement3d(parse_ref(a[1])) if len(a) > 1 else IDENT
        mat = mat_mul(parent, rel)
        self._placement_cache[eid] = mat
        return mat

    def transform_operator_3d(self, eid: int | None) -> tuple:
        if eid is None:
            return IDENT
        et = self.type_of(eid)
        if et not in ("IFCCARTESIANTRANSFORMATIONOPERATOR3D", "IFCCARTESIANTRANSFORMATIONOPERATOR3DNONUNIFORM"):
            return IDENT
        a = self.args(eid)
        ax1 = self.direction(parse_ref(a[0]) if len(a) > 0 else None, (1.0, 0.0, 0.0))
        ax2 = self.direction(parse_ref(a[1]) if len(a) > 1 else None, (0.0, 1.0, 0.0))
        origin = self.cartesian_point(parse_ref(a[2]) if len(a) > 2 else None)
        s = parse_num(a[3]) if len(a) > 3 else None
        scale1 = 1.0 if s is None else s
        ax3 = self.direction(parse_ref(a[4]) if len(a) > 4 else None, _vec_cross(ax1, ax2))
        # Gram-Schmidt orthogonalization matching IFC spec
        u1 = _vec_norm(ax1)
        d12 = _vec_dot(ax2, u1)
        u2_raw = (ax2[0] - d12 * u1[0], ax2[1] - d12 * u1[1], ax2[2] - d12 * u1[2])
        u2 = _vec_norm(u2_raw) if math.sqrt(_vec_dot(u2_raw, u2_raw)) > 1e-9 else _vec_norm(_vec_cross(ax3, u1))
        d13 = _vec_dot(ax3, u1)
        d23 = _vec_dot(ax3, u2)
        u3_raw = (
            ax3[0] - d13 * u1[0] - d23 * u2[0],
            ax3[1] - d13 * u1[1] - d23 * u2[1],
            ax3[2] - d13 * u1[2] - d23 * u2[2],
        )
        u3 = _vec_norm(u3_raw) if math.sqrt(_vec_dot(u3_raw, u3_raw)) > 1e-9 else _vec_norm(_vec_cross(u1, u2))
        if et == "IFCCARTESIANTRANSFORMATIONOPERATOR3DNONUNIFORM" and len(a) >= 7:
            s2 = parse_num(a[5])
            s3 = parse_num(a[6])
            scale2 = scale1 if s2 is None else s2
            scale3 = scale1 if s3 is None else s3
        else:
            scale2 = scale1
            scale3 = scale1
        return (
            (u1[0] * scale1, u1[1] * scale1, u1[2] * scale1),
            (u2[0] * scale2, u2[1] * scale2, u2[2] * scale2),
            (u3[0] * scale3, u3[1] * scale3, u3[2] * scale3),
            origin,
        )

    def curve_2d_points(self, eid: int) -> tuple[list[tuple[float, float]], bool]:
        et = self.type_of(eid)
        if et == "IFCPOLYLINE":
            pts: list[tuple[float, float]] = []
            for p_ref in parse_ref_list(self.args(eid)[0]):
                p = self.cartesian_point(p_ref)
                pts.append((p[0], p[1]))
            return pts, True
        if et == "IFCINDEXEDPOLYCURVE":
            a = self.args(eid)
            pl_ref = parse_ref(a[0])
            if pl_ref is None:
                return [], False
            pts_raw = parse_nested_num_list(self.args(pl_ref)[0])
            raw_2d = [(row[0], row[1]) for row in pts_raw if len(row) >= 2]
            if len(a) > 1 and a[1].startswith("("):
                # Segments ((IFCLINEINDEX((...))), (IFCARCINDEX((...))))
                seg_pts: list[tuple[float, float]] = []
                for m in re.finditer(r"IFC(?:LINE|ARC)INDEX\s*\(\s*\(([^)]+)\)\s*\)", a[1]):
                    for idx_tok in m.group(1).split(","):
                        idx = int(idx_tok.strip()) - 1
                        if 0 <= idx < len(raw_2d):
                            seg_pts.append(raw_2d[idx])
                return seg_pts or raw_2d, bool(seg_pts) and not re.search(r"IFCARCINDEX\b", a[1])
            return raw_2d, True
        if et == "IFCTRIMMEDCURVE":
            a = self.args(eid)
            basis_ref = parse_ref(a[0])
            if basis_ref is None:
                return [], False
            bt = self.type_of(basis_ref)
            # Fast extraction for line/circle trimmed curves and point trims
            pts_trim: list[tuple[float, float]] = []
            for trim_arg in (a[1] if len(a) > 1 else "", a[2] if len(a) > 2 else ""):
                for r_m in re.finditer(r"#(\d+)", trim_arg):
                    p = self.cartesian_point(int(r_m.group(1)))
                    pts_trim.append((p[0], p[1]))
            if bt == "IFCCIRCLE":
                c_args = self.args(basis_ref)
                pos = self.axis2placement3d(parse_ref(c_args[0])) if c_args else IDENT
                radius = parse_num(c_args[1]) or 0.0 if len(c_args) > 1 else 0.0
                # Sample circle points transformed into 2D
                sampled: list[tuple[float, float]] = []
                for k in range(16):
                    ang = 2.0 * math.pi * k / 16.0
                    wp = apply_mat(pos, (radius * math.cos(ang), radius * math.sin(ang), 0.0))
                    sampled.append((wp[0], wp[1]))
                return sampled, False
            if pts_trim:
                return pts_trim, bt == "IFCLINE" and len(pts_trim) == 2
            if bt == "IFCLINE":
                l_args = self.args(basis_ref)
                p = self.cartesian_point(parse_ref(l_args[0])) if l_args else (0.0, 0.0, 0.0)
                return [(p[0], p[1])], False
            return [], False
        if et == "IFCCOMPOSITECURVE":
            a = self.args(eid)
            all_pts: list[tuple[float, float]] = []
            exact = True
            for seg_ref in parse_ref_list(a[0]):
                s_args = self.args(seg_ref)
                parent_curve = parse_ref(s_args[2]) if len(s_args) > 2 else None
                if self.type_of(seg_ref) != "IFCCOMPOSITECURVESEGMENT" or parent_curve is None:
                    exact = False
                    continue
                points, segment_exact = self.curve_2d_points(parent_curve)
                all_pts.extend(points)
                exact = exact and bool(points) and segment_exact
            return all_pts, exact
        return [], False

    def profile_2d_points(self, eid: int) -> tuple[list[tuple[float, float]], bool]:
        et = self.type_of(eid)
        a = self.args(eid)
        if et in ("IFCRECTANGLEPROFILEDEF", "IFCRECTANGLEHOLLOWPROFILEDEF"):
            pos = self.axis2placement3d(parse_ref(a[2])) if len(a) > 2 else IDENT
            xdim = parse_num(a[3]) or 0.0 if len(a) > 3 else 0.0
            ydim = parse_num(a[4]) or 0.0 if len(a) > 4 else 0.0
            hx, hy = xdim * 0.5, ydim * 0.5
            pts: list[tuple[float, float]] = []
            for lx, ly in [(-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy)]:
                wp = apply_mat(pos, (lx, ly, 0.0))
                pts.append((wp[0], wp[1]))
            return pts, True
        if et in ("IFCCIRCLEPROFILEDEF", "IFCCIRCLEHOLLOWPROFILEDEF"):
            pos = self.axis2placement3d(parse_ref(a[2])) if len(a) > 2 else IDENT
            radius = parse_num(a[3]) or 0.0 if len(a) > 3 else 0.0
            pts = []
            for lx, ly in [(-radius, -radius), (radius, -radius), (radius, radius), (-radius, radius)]:
                wp = apply_mat(pos, (lx, ly, 0.0))
                pts.append((wp[0], wp[1]))
            return pts, False
        if et == "IFCELLIPSEPROFILEDEF":
            pos = self.axis2placement3d(parse_ref(a[2])) if len(a) > 2 else IDENT
            rx = parse_num(a[3]) or 0.0 if len(a) > 3 else 0.0
            ry = parse_num(a[4]) or 0.0 if len(a) > 4 else 0.0
            return [(wp[0], wp[1]) for lx, ly in [(-rx, -ry), (rx, -ry), (rx, ry), (-rx, ry)] for wp in [apply_mat(pos, (lx, ly, 0.0))]], False
        if et in ("IFCARBITRARYCLOSEDPROFILEDEF", "IFCARBITRARYPROFILEDEFWITHVOIDS"):
            curve_ref = parse_ref(a[2]) if len(a) > 2 else None
            return self.curve_2d_points(curve_ref) if curve_ref else ([], False)
        if et == "IFCCOMPOSITEPROFILEDEF":
            sub_refs = parse_ref_list(a[2]) if len(a) > 2 else []
            pts_all: list[tuple[float, float]] = []
            exact = True
            for sr in sub_refs:
                points, sub_exact = self.profile_2d_points(sr)
                pts_all.extend(points)
                exact = exact and bool(points) and sub_exact
            return pts_all, exact
        if et in ("IFCISHAPEPROFILEDEF", "IFCLSHAPEPROFILEDEF", "IFCTSHAPEPROFILEDEF", "IFCUSHAPEPROFILEDEF", "IFCZSHAPEPROFILEDEF"):
            pos = self.axis2placement3d(parse_ref(a[2])) if len(a) > 2 else IDENT
            w = parse_num(a[3]) or 0.0 if len(a) > 3 else 0.0
            h = parse_num(a[4]) or 0.0 if len(a) > 4 else 0.0
            hx, hy = w * 0.5, h * 0.5
            return [(wp[0], wp[1]) for lx, ly in [(-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy)] for wp in [apply_mat(pos, (lx, ly, 0.0))]], False
        return [], False

    def item_aabb(self, eid: int, transform: tuple) -> tuple[list[float] | None, bool]:
        """Bound the transformed geometry of an item in file length units."""
        key = (eid, transform)
        cached = self._geom_cache.get(key)
        if cached is not None:
            return cached
        et = self.type_of(eid)
        if et is None:
            return None, False
        a = self.args(eid)
        res: list[float] | None = None
        exact = True

        if et == "IFCMAPPEDITEM":
            rmap_ref = parse_ref(a[0]) if len(a) > 0 else None
            op_ref = parse_ref(a[1]) if len(a) > 1 else None
            if rmap_ref and self.type_of(rmap_ref) == "IFCREPRESENTATIONMAP":
                rm_args = self.args(rmap_ref)
                origin_mat = self.axis2placement3d(parse_ref(rm_args[0]) if rm_args else None)
                rep_ref = parse_ref(rm_args[1]) if len(rm_args) > 1 else None
                if rep_ref:
                    op_mat = self.transform_operator_3d(op_ref)
                    total = mat_mul(transform, mat_mul(op_mat, _invert_rigid(origin_mat)))
                    res, exact = self.shape_representation_aabb(rep_ref, total)
        elif et == "IFCEXTRUDEDAREASOLID":
            prof_ref = parse_ref(a[0]) if len(a) > 0 else None
            pos_mat = mat_mul(transform, self.axis2placement3d(parse_ref(a[1]) if len(a) > 1 else None))
            edir = self.direction(parse_ref(a[2]) if len(a) > 2 else None, (0.0, 0.0, 1.0))
            depth = parse_num(a[3]) if len(a) > 3 else None
            if prof_ref and depth is not None:
                disp = (edir[0] * depth, edir[1] * depth, edir[2] * depth)
                profile_type = self.type_of(prof_ref)
                if profile_type in ("IFCCIRCLEPROFILEDEF", "IFCCIRCLEHOLLOWPROFILEDEF", "IFCELLIPSEPROFILEDEF"):
                    profile = self.args(prof_ref)
                    rx = parse_num(profile[3]) if len(profile) > 3 else None
                    ry = rx
                    if profile_type == "IFCELLIPSEPROFILEDEF":
                        ry = parse_num(profile[4]) if len(profile) > 4 else None
                    if rx is not None and ry is not None:
                        profile_mat = self.axis2placement3d(parse_ref(profile[2]))
                        px, py, _, start = mat_mul(pos_mat, profile_mat)
                        end = apply_mat(pos_mat, tuple(profile_mat[3][i] + disp[i] for i in range(3)))
                        radii = [math.hypot(rx * px[i], ry * py[i]) for i in range(3)]
                        res = [min(start[i], end[i]) - radii[i] for i in range(3)]
                        res.extend(max(start[i], end[i]) + radii[i] for i in range(3))
                else:
                    pts2, exact = self.profile_2d_points(prof_ref)
                    pts3 = [(x, y, 0.0) for x, y in pts2]
                    pts3.extend((x + disp[0], y + disp[1], disp[2]) for x, y in pts2)
                    res = aabb_from_points(pts3, pos_mat)
        elif et == "IFCREVOLVEDAREASOLID":
            prof_ref = parse_ref(a[0]) if len(a) > 0 else None
            pos_mat = mat_mul(transform, self.axis2placement3d(parse_ref(a[1]) if len(a) > 1 else None))
            axis_ref = parse_ref(a[2]) if len(a) > 2 else None
            angle = parse_num(a[3]) or 0.0 if len(a) > 3 else 0.0
            if prof_ref:
                pts2, _ = self.profile_2d_points(prof_ref)
                if pts2 and axis_ref:
                    ax_args = self.args(axis_ref)
                    ax_loc = self.cartesian_point(parse_ref(ax_args[0])) if ax_args else (0.0, 0.0, 0.0)
                    ax_dir = self.direction(parse_ref(ax_args[1]) if len(ax_args) > 1 else None, (0.0, 1.0, 0.0))
                    pts3 = []
                    steps = 8
                    for i in range(steps + 1):
                        theta = angle * i / steps
                        for x, y in pts2:
                            rp = _rotate_around_axis((x, y, 0.0), ax_loc, ax_dir, theta)
                            pts3.append(rp)
                    res = aabb_from_points(pts3, pos_mat)
            exact = False
        elif et == "IFCSWEPTDISKSOLID":
            curve_ref = parse_ref(a[0]) if len(a) > 0 else None
            radius = parse_num(a[1]) or 0.0 if len(a) > 1 else 0.0
            if curve_ref:
                pts3 = self._curve_3d_points(curve_ref)
                box = aabb_from_points(pts3, transform)
                if box:
                    radii = [radius * math.hypot(*(v[i] for v in transform[:3])) for i in range(3)]
                    res = [box[i] - radii[i] for i in range(3)] + [box[i + 3] + radii[i] for i in range(3)]
            exact = False
        elif et == "IFCFACETEDBREP":
            shell_ref = parse_ref(a[0]) if len(a) > 0 else None
            if shell_ref:
                res, exact = self._face_set_aabb(shell_ref, transform)
        elif et in ("IFCFACEBASEDSURFACEMODEL", "IFCSHELLBASEDSURFACEMODEL"):
            for sref in parse_ref_list(a[0]):
                sub, sub_exact = self._face_set_aabb(sref, transform)
                res = union_aabb(res, sub)
                exact = exact and sub_exact
        elif et == "IFCCONNECTEDFACESET":
            res, exact = self._face_set_aabb(eid, transform)
        elif et in ("IFCPOLYGONALFACESET", "IFCTRIANGULATEDFACESET"):
            coords_ref = parse_ref(a[0]) if len(a) > 0 else None
            if coords_ref:
                pts3 = [(r[0], r[1], r[2]) for r in parse_nested_num_list(self.args(coords_ref)[0]) if len(r) >= 3]
                res = aabb_from_points(pts3, transform)
        elif et == "IFCBOUNDINGBOX":
            corner = self.cartesian_point(parse_ref(a[0]) if len(a) > 0 else None)
            xdim = parse_num(a[1]) or 0.0 if len(a) > 1 else 0.0
            ydim = parse_num(a[2]) or 0.0 if len(a) > 2 else 0.0
            zdim = parse_num(a[3]) or 0.0 if len(a) > 3 else 0.0
            box = [corner[0], corner[1], corner[2], corner[0] + xdim, corner[1] + ydim, corner[2] + zdim]
            res = transform_aabb(box, transform)
        elif et in ("IFCBOOLEANCLIPPINGRESULT", "IFCBOOLEANRESULT"):
            op1 = parse_ref(a[1]) if len(a) > 1 else None
            if op1:
                res, _ = self.item_aabb(op1, transform)
            exact = False  # clipping can shrink the solid
        elif et == "IFCGEOMETRICSET":
            for iref in parse_ref_list(a[0]):
                sb, se = self.item_aabb(iref, transform)
                res = union_aabb(res, sb)
                exact = exact and se
        else:
            exact = False

        result = (res, exact and res is not None)
        self._geom_cache[key] = result
        return result

    def _curve_3d_points(self, eid: int) -> list[tuple[float, float, float]]:
        et = self.type_of(eid)
        if et == "IFCPOLYLINE":
            return [self.cartesian_point(r) for r in parse_ref_list(self.args(eid)[0])]
        return []

    def _face_set_aabb(self, eid: int, transform: tuple) -> tuple[list[float] | None, bool]:
        if self.type_of(eid) not in ("IFCCLOSEDSHELL", "IFCOPENSHELL", "IFCCONNECTEDFACESET"):
            return None, False
        a = self.args(eid)
        pts: list[tuple[float, float, float]] = []
        exact = True
        for fref in parse_ref_list(a[0]):
            face_type = self.type_of(fref)
            exact = exact and face_type == "IFCFACE"
            if face_type in ("IFCFACE", "IFCFACESURFACE"):
                f_args = self.args(fref)
                for bref in parse_ref_list(f_args[0]):
                    b_args = self.args(bref)
                    loop_ref = parse_ref(b_args[0]) if b_args else None
                    if loop_ref and self.type_of(loop_ref) == "IFCPOLYLOOP":
                        for pref in parse_ref_list(self.args(loop_ref)[0]):
                            pts.append(self.cartesian_point(pref))
                    else:
                        exact = False
        return aabb_from_points(pts, transform), exact and bool(pts)

    def shape_representation_aabb(self, rep_id: int, transform: tuple) -> tuple[list[float] | None, bool]:
        if self.type_of(rep_id) != "IFCSHAPEREPRESENTATION":
            return None, False
        a = self.args(rep_id)
        item_refs = parse_ref_list(a[3]) if len(a) > 3 else []
        box: list[float] | None = None
        exact = True
        for iref in item_refs:
            ib, ie = self.item_aabb(iref, transform)
            box = union_aabb(box, ib)
            exact = exact and ie
        return box, exact and box is not None

    def product_world_aabb_metres(self, eid: int) -> tuple[list[float] | None, bool]:
        """Compute world AABB in SI metres for an IfcProduct (Body prioritized)."""
        a = self.args(eid)
        placement_ref = parse_ref(a[5]) if len(a) > 5 else None
        rep_ref = parse_ref(a[6]) if len(a) > 6 else None
        if rep_ref is None:
            return None, True
        if self.type_of(rep_ref) != "IFCPRODUCTDEFINITIONSHAPE":
            return None, False
        reps = parse_ref_list(self.args(rep_ref)[2]) if len(self.args(rep_ref)) > 2 else []
        body_reps: list[int] = []
        fallback_reps: list[int] = []
        exact = True
        for rref in reps:
            if self.type_of(rref) != "IFCSHAPEREPRESENTATION":
                exact = False
                continue
            rargs = self.args(rref)
            rep_id = (decode_step_string(rargs[1]) or "").lower() if len(rargs) > 1 else ""
            if rep_id == "body":
                body_reps.append(rref)
            elif rep_id != "axis":
                fallback_reps.append(rref)
        chosen = body_reps or fallback_reps
        if not chosen:
            return None, exact and bool(reps)
        world_native: list[float] | None = None
        placement_mat = self.local_placement(placement_ref)
        for rref in chosen:
            rb, re_exact = self.shape_representation_aabb(rref, placement_mat)
            world_native = union_aabb(world_native, rb)
            exact = exact and re_exact
        if world_native is None:
            return None, False
        return scale_aabb(world_native, self.length_scale_to_metres), exact

    def extract_structure(self) -> dict[str, Any]:
        """Extract comparable spatial structure, elements, classes, storeys, and world AABBs."""
        scale = self.length_scale_to_metres

        # Aggregation & containment maps
        parent_aggregate: dict[int, int] = {}
        for rel_id in self.by_type("IFCRELAGGREGATES"):
            ra = self.args(rel_id)
            whole = parse_ref(ra[4]) if len(ra) > 4 else None
            if whole is not None:
                for part in parse_ref_list(ra[5] if len(ra) > 5 else ""):
                    parent_aggregate[part] = whole

        container_of: dict[int, int] = {}
        for rel_id in self.by_type("IFCRELCONTAINEDINSPATIALSTRUCTURE"):
            ra = self.args(rel_id)
            container = parse_ref(ra[5] if len(ra) > 5 else "")
            if container is not None:
                for elem in parse_ref_list(ra[4] if len(ra) > 4 else ""):
                    container_of[elem] = container

        storeys_by_id: dict[int, dict[str, Any]] = {}
        for sid in self.by_type("IFCBUILDINGSTOREY"):
            sa = self.args(sid)
            gid = decode_step_string(sa[0]) or ""
            name = decode_step_string(sa[2]) or ""
            elev_raw = parse_num(sa[9]) if len(sa) > 9 else None
            elev_m = round(elev_raw * scale, 6) if elev_raw is not None else None
            storeys_by_id[sid] = {
                "entityId": sid,
                "globalId": gid,
                "name": name,
                "elevationMeters": elev_m,
            }

        def resolve_storey(eid: int) -> str | None:
            cur: int | None = eid
            seen = 0
            while cur is not None and seen < 16:
                if cur in storeys_by_id:
                    return storeys_by_id[cur]["name"] or storeys_by_id[cur]["globalId"]
                nxt = container_of.get(cur)
                if nxt is None:
                    nxt = parent_aggregate.get(cur)
                cur = nxt
                seen += 1
            return None

        spatial_roots: dict[str, list[str]] = {}
        for root_type in SPATIAL_ROOT_TYPES:
            spatial_roots[root_type] = [
                decode_step_string(self.args(rid)[0]) or ""
                for rid in self.by_type(root_type)
            ]

        spaces: dict[str, dict[str, Any]] = {}
        for sp_id in self.by_type("IFCSPACE"):
            sa = self.args(sp_id)
            gid = decode_step_string(sa[0]) or ""
            box, exact = self.product_world_aabb_metres(sp_id)
            spaces[gid] = {
                "globalId": gid,
                "name": decode_step_string(sa[2]) or "",
                "longName": decode_step_string(sa[8]) if len(sa) > 8 else None,
                "storey": resolve_storey(sp_id),
                "worldBboxMeters": box,
                "boxExact": exact,
            }

        elements: dict[str, dict[str, Any]] = {}
        untagged_elements = 0
        duplicate_tags: list[str] = []
        openings_count = len(self.by_type("IFCOPENINGELEMENT")) + len(self.by_type("IFCOPENINGSTANDARDCASE"))
        model_box: list[float] | None = None

        # Any entity whose argument pattern is a physical IfcProduct not in spatial/excluded sets
        for eid, (etype, _) in self.ent.items():
            if not etype.startswith("IFC"):
                continue
            if etype in EXCLUDED_ELEMENT_TYPES or etype in NON_PHYSICAL_PRODUCT_TYPES:
                continue
            if etype.startswith(("IFCREL", "IFCTYPE", "IFCPROPERTY", "IFCSIUNIT", "IFCCARTESIAN", "IFCDIRECTION", "IFCAXIS", "IFCSHAPE", "IFCREPRESENTATION", "IFCMAPPED", "IFCEXTRUDED", "IFCFACE", "IFCPOLY", "IFCMATERIAL", "IFCQUANTITY", "IFCELEMENTQUANTITY", "IFCSTYLED", "IFCSURFACE", "IFCPRE", "IFCCLASS", "IFCCONVERSION", "IFCMEASURE", "IFCDERIVED", "IFCDIMENSIONAL", "IFCORGANIZATION", "IFCPERSON", "IFCAPPLICATION", "IFCOWNER", "IFCPOSTAL", "IFCUNIT", "IFCGEOMETRIC", "IFCGROUP")):
                continue
            # All physical IfcElement subtypes inherit IfcProduct: 8th attribute (index 7) is Tag,
            # while index 5 is ObjectPlacement (#ref or $) and index 6 is Representation (#ref or $).
            a = self.args(eid)
            if len(a) < 8:
                continue
            if etype.endswith("TYPE") or etype.endswith("STYLE") or etype.endswith("PROPERTIES"):
                continue
            p_tok, r_tok = a[5].strip(), a[6].strip()
            if not ((p_tok == "$" or p_tok.startswith("#")) and (r_tok == "$" or r_tok.startswith("#"))):
                continue
            if not (p_tok.startswith("#") or r_tok.startswith("#") or eid in container_of or eid in parent_aggregate):
                continue

            gid = decode_step_string(a[0]) or ""
            tag = decode_step_string(a[7])
            if not tag:
                untagged_elements += 1
                continue
            if tag in elements:
                duplicate_tags.append(tag)
                continue
            box, exact = self.product_world_aabb_metres(eid)
            if box is not None:
                model_box = union_aabb(model_box, box)
            elements[tag] = {
                "entityId": eid,
                "tag": tag,
                "globalId": gid,
                "class": etype,
                "name": decode_step_string(a[2]) or "",
                "objectType": decode_step_string(a[4]) or "",
                "storey": resolve_storey(eid),
                "worldBboxMeters": box,
                "boxExact": exact,
            }

        return {
            "schema": self.schema,
            "lengthScaleToMetres": scale,
            "spatialRoots": spatial_roots,
            "storeys": list(storeys_by_id.values()),
            "spaces": spaces,
            "elements": elements,
            "untaggedElements": untagged_elements,
            "duplicateTags": sorted(set(duplicate_tags)),
            "openingsCount": openings_count,
            "modelWorldBboxMeters": _box_summary(model_box),
        }


def _invert_rigid(m: tuple) -> tuple:
    x, y, z, o = m
    ix = (x[0], y[0], z[0])
    iy = (x[1], y[1], z[1])
    iz = (x[2], y[2], z[2])
    io = (
        -(ix[0] * o[0] + iy[0] * o[1] + iz[0] * o[2]),
        -(ix[1] * o[0] + iy[1] * o[1] + iz[1] * o[2]),
        -(ix[2] * o[0] + iy[2] * o[1] + iz[2] * o[2]),
    )
    return (ix, iy, iz, io)


def _rotate_around_axis(
    p: tuple[float, float, float],
    origin: tuple[float, float, float],
    axis: tuple[float, float, float],
    theta: float,
) -> tuple[float, float, float]:
    vx, vy, vz = p[0] - origin[0], p[1] - origin[1], p[2] - origin[2]
    kx, ky, kz = _vec_norm(axis)
    c = math.cos(theta)
    s = math.sin(theta)
    dot = kx * vx + ky * vy + kz * vz
    cx, cy, cz = _vec_cross((kx, ky, kz), (vx, vy, vz))
    rx = vx * c + cx * s + kx * dot * (1.0 - c)
    ry = vy * c + cy * s + ky * dot * (1.0 - c)
    rz = vz * c + cz * s + kz * dot * (1.0 - c)
    return (origin[0] + rx, origin[1] + ry, origin[2] + rz)


def _box_summary(box: list[float] | None) -> dict[str, Any] | None:
    if box is None:
        return None
    return {
        "min": [round(box[0], 6), round(box[1], 6), round(box[2], 6)],
        "max": [round(box[3], 6), round(box[4], 6), round(box[5], 6)],
        "size": [round(box[3] - box[0], 6), round(box[4] - box[1], 6), round(box[5] - box[2], 6)],
        "center": [round((box[0] + box[3]) * 0.5, 6), round((box[1] + box[4]) * 0.5, 6), round((box[2] + box[5]) * 0.5, 6)],
    }


def _tag_sort_key(tag: str) -> tuple[int, int, str]:
    return (0, int(tag), tag) if tag.isdigit() else (1, 0, tag)


def compare_ifc_against_reference(
    candidate_path: Path,
    reference_path: Path,
    bbox_tol_m: float = 0.01,
    storey_elev_tol_m: float = 0.005,
) -> dict[str, Any]:
    """Compare one exported IFC against Revit's reference IFC."""
    cand = StepModel(candidate_path).extract_structure()
    ref = StepModel(reference_path).extract_structure()

    ref_elems = ref["elements"]
    cand_elems = cand["elements"]
    ref_tags = set(ref_elems)
    cand_tags = set(cand_elems)

    matched_tags = sorted(ref_tags & cand_tags, key=_tag_sort_key)
    missing_tags = sorted(ref_tags - cand_tags, key=_tag_sort_key)
    added_tags = sorted(cand_tags - ref_tags, key=_tag_sort_key)

    exact_gid = 0
    diff_gid: list[dict[str, Any]] = []
    exact_class = 0
    cross_class_counts: dict[str, list[str]] = collections.defaultdict(list)
    by_ref_class: dict[str, dict[str, int]] = collections.defaultdict(
        lambda: {"referenceCount": 0, "sameClass": 0, "differentClass": 0, "missing": 0}
    )

    for tag in ref_tags:
        by_ref_class[ref_elems[tag]["class"]]["referenceCount"] += 1
    for tag in missing_tags:
        by_ref_class[ref_elems[tag]["class"]]["missing"] += 1

    same_storey = 0
    diff_storey: list[dict[str, Any]] = []
    only_ref_storey = 0
    only_cand_storey = 0
    neither_storey = 0

    within_box = 0
    exceeding_box = 0
    missing_cand_box = 0
    missing_ref_box = 0
    missing_both_boxes = 0
    approximate_pairs = 0
    unverified_box_tags: list[str] = []
    shifted_elements: list[dict[str, Any]] = []
    max_center_shift = 0.0
    max_extent_delta = 0.0

    for tag in matched_tags:
        re_el = ref_elems[tag]
        ce_el = cand_elems[tag]

        # GlobalId
        if re_el["globalId"] == ce_el["globalId"]:
            exact_gid += 1
        else:
            diff_gid.append({
                "tag": tag,
                "class": re_el["class"],
                "referenceGlobalId": re_el["globalId"],
                "candidateGlobalId": ce_el["globalId"],
            })

        # IFC class
        rc, cc = re_el["class"], ce_el["class"]
        if rc == cc:
            exact_class += 1
            by_ref_class[rc]["sameClass"] += 1
        else:
            by_ref_class[rc]["differentClass"] += 1
            cross_class_counts[f"{rc} -> {cc}"].append(tag)

        # Storey assignment
        rs, cs = re_el["storey"], ce_el["storey"]
        if rs is not None and cs is not None:
            if rs == cs:
                same_storey += 1
            else:
                diff_storey.append({"tag": tag, "class": rc, "referenceStorey": rs, "candidateStorey": cs})
        elif rs is not None:
            only_ref_storey += 1
            diff_storey.append({"tag": tag, "class": rc, "referenceStorey": rs, "candidateStorey": None})
        elif cs is not None:
            only_cand_storey += 1
            diff_storey.append({"tag": tag, "class": rc, "referenceStorey": None, "candidateStorey": cs})
        else:
            neither_storey += 1

        # World AABB
        rb, cb = re_el["worldBboxMeters"], ce_el["worldBboxMeters"]
        if rb is None:
            missing_ref_box += 1
            if cb is None:
                missing_cand_box += 1
                missing_both_boxes += 1
                if not (re_el["boxExact"] and ce_el["boxExact"]):
                    unverified_box_tags.append(tag)
            continue
        if cb is None:
            missing_cand_box += 1
            shifted_elements.append({
                "tag": tag,
                "class": rc,
                "reason": "missing-candidate-box",
                "referenceBox": _box_summary(rb),
                "candidateBox": None,
            })
            continue

        if not (re_el["boxExact"] and ce_el["boxExact"]):
            approximate_pairs += 1
        rcenter = [(rb[0] + rb[3]) * 0.5, (rb[1] + rb[4]) * 0.5, (rb[2] + rb[5]) * 0.5]
        ccenter = [(cb[0] + cb[3]) * 0.5, (cb[1] + cb[4]) * 0.5, (cb[2] + cb[5]) * 0.5]
        center_shift = math.sqrt(sum((ccenter[i] - rcenter[i]) ** 2 for i in range(3)))
        rsize = [rb[3] - rb[0], rb[4] - rb[1], rb[5] - rb[2]]
        csize = [cb[3] - cb[0], cb[4] - cb[1], cb[5] - cb[2]]
        extent_delta = max(abs(csize[i] - rsize[i]) for i in range(3))
        max_corner_delta = max(abs(cb[i] - rb[i]) for i in range(6))

        if center_shift > max_center_shift:
            max_center_shift = center_shift
        if extent_delta > max_extent_delta:
            max_extent_delta = extent_delta

        if max_corner_delta <= bbox_tol_m:
            within_box += 1
        else:
            exceeding_box += 1
            shifted_elements.append({
                "tag": tag,
                "class": rc,
                "reason": "exceeds-tolerance",
                "centerShiftMeters": round(center_shift, 6),
                "extentDeltaMeters": round(extent_delta, 6),
                "maxCornerDeltaMeters": round(max_corner_delta, 6),
                "referenceBox": _box_summary(rb),
                "candidateBox": _box_summary(cb),
            })

    added_by_class: dict[str, list[str]] = collections.defaultdict(list)
    for tag in added_tags:
        added_by_class[cand_elems[tag]["class"]].append(tag)

    # Spaces (by GlobalId)
    ref_spaces = set(ref["spaces"])
    cand_spaces = set(cand["spaces"])
    matched_spaces = sorted(ref_spaces & cand_spaces)
    missing_spaces = sorted(ref_spaces - cand_spaces)
    added_spaces = sorted(cand_spaces - ref_spaces)

    # Storeys (paired by Name)
    ref_storeys = {s["name"]: s for s in ref["storeys"]}
    cand_storeys = {s["name"]: s for s in cand["storeys"]}
    matched_storey_names = sorted(set(ref_storeys) & set(cand_storeys))
    storey_rows = []
    storey_elev_ok = True
    for sname in matched_storey_names:
        rs = ref_storeys[sname]
        cs = cand_storeys[sname]
        relev = rs["elevationMeters"]
        celev = cs["elevationMeters"]
        edelta = round(abs(celev - relev), 6) if (relev is not None and celev is not None) else None
        within = edelta is not None and edelta <= storey_elev_tol_m
        if not within:
            storey_elev_ok = False
        storey_rows.append({
            "name": sname,
            "referenceGlobalId": rs["globalId"],
            "candidateGlobalId": cs["globalId"],
            "globalIdEqual": rs["globalId"] == cs["globalId"],
            "referenceElevationMeters": relev,
            "candidateElevationMeters": celev,
            "elevationDeltaMeters": edelta,
            "withinTolerance": within,
        })

    # Spatial roots
    spatial_roots_summary = {}
    for rt in SPATIAL_ROOT_TYPES:
        rids = set(ref["spatialRoots"].get(rt, []))
        cids = set(cand["spatialRoots"].get(rt, []))
        spatial_roots_summary[rt] = {
            "referenceCount": len(rids),
            "candidateCount": len(cids),
            "matchedGlobalIds": len(rids & cids),
        }

    # Model-level world AABB comparison
    ref_mbox = ref["modelWorldBboxMeters"]
    cand_mbox = cand["modelWorldBboxMeters"]
    mbox_delta = None
    mbox_within = False
    if ref_mbox and cand_mbox:
        deltas = [abs(cand_mbox["min"][i] - ref_mbox["min"][i]) for i in range(3)] + [
            abs(cand_mbox["max"][i] - ref_mbox["max"][i]) for i in range(3)
        ]
        mbox_delta = round(max(deltas), 6)
        mbox_within = mbox_delta <= bbox_tol_m

    return {
        "schema": "rvtio-ifc-compare/1",
        "candidateFile": candidate_path.name,
        "referenceFile": reference_path.name,
        "candidateSchema": cand["schema"],
        "referenceSchema": ref["schema"],
        "comparableGroups": {
            "elements": "IfcElement minus {IfcOpeningElement, IfcVirtualElement}, paired by Tag",
            "spaces": "IfcSpace, paired by GlobalId",
            "storeys": "IfcBuildingStorey, paired by Name",
            "spatialRoots": "IfcProject, IfcSite, IfcBuilding, matched by GlobalId",
        },
        "tolerances": {
            "worldBboxMeters": bbox_tol_m,
            "storeyElevationMeters": storey_elev_tol_m,
        },
        "identifiers": {
            "referenceTaggedCount": len(ref_tags),
            "candidateTaggedCount": len(cand_tags),
            "matchedCount": len(matched_tags),
            "exactGlobalIdCount": exact_gid,
            "differentGlobalIdCount": len(diff_gid),
            "differentGlobalIds": diff_gid,
            "missingCount": len(missing_tags),
            "missingIds": [
                {"tag": t, "class": ref_elems[t]["class"], "name": ref_elems[t]["name"], "globalId": ref_elems[t]["globalId"]}
                for t in missing_tags
            ],
            "addedCount": len(added_tags),
            "addedIds": [
                {"tag": t, "class": cand_elems[t]["class"], "name": cand_elems[t]["name"], "globalId": cand_elems[t]["globalId"]}
                for t in added_tags
            ],
            "untaggedReference": ref["untaggedElements"],
            "untaggedCandidate": cand["untaggedElements"],
            "duplicateTagsReference": ref["duplicateTags"],
            "duplicateTagsCandidate": cand["duplicateTags"],
            "referenceOpeningsCount": ref["openingsCount"],
            "candidateOpeningsCount": cand["openingsCount"],
            "spaces": {
                "referenceCount": len(ref_spaces),
                "candidateCount": len(cand_spaces),
                "matchedGlobalIds": len(matched_spaces),
                "missingGlobalIds": missing_spaces,
                "addedGlobalIds": added_spaces,
            },
            "spatialRoots": spatial_roots_summary,
        },
        "classes": {
            "exactClassMatchCount": exact_class,
            "differentClassMatchCount": sum(len(v) for v in cross_class_counts.values()),
            "byReferenceClass": dict(sorted(by_ref_class.items())),
            "crossClassMappings": [
                {"mapping": k, "count": len(v), "tags": v}
                for k, v in sorted(cross_class_counts.items())
            ],
            "addedByClass": [
                {"class": k, "count": len(v), "tags": v}
                for k, v in sorted(added_by_class.items())
            ],
        },
        "storeys": {
            "referenceCount": len(ref["storeys"]),
            "candidateCount": len(cand["storeys"]),
            "matchedStoreys": storey_rows,
            "missingStoreys": sorted(set(ref_storeys) - set(cand_storeys)),
            "addedStoreys": sorted(set(cand_storeys) - set(ref_storeys)),
            "allElevationsWithinTolerance": storey_elev_ok,
            "elementAssignments": {
                "sameStorey": same_storey,
                "differentStoreyCount": len(diff_storey),
                "onlyReferenceHasStorey": only_ref_storey,
                "onlyCandidateHasStorey": only_cand_storey,
                "neitherHasStorey": neither_storey,
                "differences": diff_storey,
            },
        },
        "worldBbox": {
            "toleranceMeters": bbox_tol_m,
            "referenceModelBox": ref_mbox,
            "candidateModelBox": cand_mbox,
            "modelBoxMaxCornerDeltaMeters": mbox_delta,
            "modelBoxWithinTolerance": mbox_within,
            "pairedWithBothBoxes": within_box + exceeding_box,
            "withinToleranceCount": within_box,
            "exceedingToleranceCount": exceeding_box,
            "missingCandidateBoxCount": missing_cand_box,
            "missingReferenceBoxCount": missing_ref_box,
            "missingBothBoxesCount": missing_both_boxes,
            "unverifiedBoxTags": unverified_box_tags,
            "approximatePairCount": approximate_pairs,
            "maxCenterShiftMeters": round(max_center_shift, 6),
            "maxExtentDeltaMeters": round(max_extent_delta, 6),
            "shiftedElements": shifted_elements,
        },
    }


def diff_commit_reports(base: dict[str, Any], head: dict[str, Any]) -> dict[str, Any]:
    """Compare two RvtIO commit comparison reports scored against the same Revit reference."""
    base_missing = {x["tag"] for x in base["identifiers"]["missingIds"]}
    head_missing = {x["tag"] for x in head["identifiers"]["missingIds"]}
    base_added = {x["tag"] for x in base["identifiers"]["addedIds"]}
    head_added = {x["tag"] for x in head["identifiers"]["addedIds"]}
    base_shifted = {x["tag"] for x in base["worldBbox"]["shiftedElements"]}
    head_shifted = {x["tag"] for x in head["worldBbox"]["shiftedElements"]}
    base_wrong_class = {tag for m in base["classes"]["crossClassMappings"] for tag in m["tags"]}
    head_wrong_class = {tag for m in head["classes"]["crossClassMappings"] for tag in m["tags"]}
    base_wrong_storey = {x["tag"] for x in base["storeys"]["elementAssignments"]["differences"]}
    head_wrong_storey = {
        x["tag"] for x in head["storeys"]["elementAssignments"]["differences"] if x["referenceStorey"] is not None
    }

    return {
        "matchedCountDelta": head["identifiers"]["matchedCount"] - base["identifiers"]["matchedCount"],
        "exactGlobalIdDelta": head["identifiers"]["exactGlobalIdCount"] - base["identifiers"]["exactGlobalIdCount"],
        "exactClassMatchDelta": head["classes"]["exactClassMatchCount"] - base["classes"]["exactClassMatchCount"],
        "sameStoreyDelta": head["storeys"]["elementAssignments"]["sameStorey"] - base["storeys"]["elementAssignments"]["sameStorey"],
        "bboxWithinToleranceDelta": head["worldBbox"]["withinToleranceCount"] - base["worldBbox"]["withinToleranceCount"],
        "newlyRecoveredTags": sorted(base_missing - head_missing, key=_tag_sort_key),
        "newlyMissingTags": sorted(head_missing - base_missing, key=_tag_sort_key),
        "newlyAddedTags": sorted(head_added - base_added, key=_tag_sort_key),
        "removedExtraTags": sorted(base_added - head_added, key=_tag_sort_key),
        "regressedClassTags": sorted(head_wrong_class - base_wrong_class - base_missing, key=_tag_sort_key),
        "regressedStoreyTags": sorted(head_wrong_storey - base_wrong_storey - base_missing, key=_tag_sort_key),
        "newlyAlignedBboxTags": sorted(base_shifted - head_shifted, key=_tag_sort_key),
        "regressedBboxTags": sorted(head_shifted - base_shifted, key=_tag_sort_key),
    }
