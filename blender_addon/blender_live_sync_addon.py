bl_info = {
    "name": "Unity Bridge",
    "author": "Codex",
    "version": (0, 5, 0),
    "blender": (4, 0, 0),
    "location": "Top Bar + View3D > Sidebar > MML Sync",
    "description": "One-click connect/disconnect Blender and Unity live sync.",
    "category": "Import-Export",
}

import http.client
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from typing import Any

import numpy as _np

import bpy
from bpy.props import BoolProperty, FloatProperty, PointerProperty, StringProperty
from bpy.types import Operator, Panel, PropertyGroup
from mathutils import Matrix

_dirty = False
_timer_registered = False
_publish_timer_registered = False
_topbar_registered = False
_view3d_header_registered = False
_is_syncing = False
_toggle_guard = False
_last_status_poll_at = 0.0
_pending_snapshot: dict[str, Any] | None = None
_last_publish_retry_at = 0.0
_last_publish_sent_at = 0.0

# Persistent HTTP connection to avoid TIME_WAIT port exhaustion
_http_conn: http.client.HTTPConnection | None = None
_http_conn_host: str = ""



def _get_http_conn(host: str, port: int, timeout: float = 3.0) -> http.client.HTTPConnection:
    global _http_conn, _http_conn_host
    key = f"{host}:{port}"
    if _http_conn is None or _http_conn_host != key:
        if _http_conn is not None:
            try:
                _http_conn.close()
            except Exception:
                pass
        _http_conn = http.client.HTTPConnection(host, port, timeout=timeout)
        _http_conn_host = key
    return _http_conn


def _http_request(method: str, host: str, port: int, path: str,
                  body: bytes | None = None, timeout: float = 3.0) -> tuple[int, str]:
    """Reusable persistent HTTP request. Returns (status_code, body_text)."""
    global _http_conn
    headers = {"Connection": "keep-alive"}
    if body is not None:
        headers["Content-Type"] = "application/json"
        headers["Content-Length"] = str(len(body))
    for attempt in range(2):
        try:
            conn = _get_http_conn(host, port, timeout)
            conn.request(method, path, body=body, headers=headers)
            resp = conn.getresponse()
            status = resp.status
            text = resp.read().decode("utf-8", errors="replace")
            return status, text
        except Exception:
            # Connection broken — close and retry once with a fresh connection
            if _http_conn is not None:
                try:
                    _http_conn.close()
                except Exception:
                    pass
                _http_conn = None
            if attempt == 1:
                raise
    return -1, ""

# Blender -> Unity axis conversion:
# x_u = x_b
# y_u = z_b
# z_u = y_b
_C_MATRIX = Matrix(
    (
        (1.0, 0.0, 0.0, 0.0),
        (0.0, 0.0, 1.0, 0.0),
        (0.0, 1.0, 0.0, 0.0),
        (0.0, 0.0, 0.0, 1.0),
    )
)
_C_MATRIX_INV = _C_MATRIX.inverted()


def _show_popup(title: str, message: str, icon: str = "INFO") -> None:
    if bpy.app.background:
        print(f"[Blender-Unity-Bridge] {title}: {message}")
        return

    wm = bpy.context.window_manager
    if wm is None or len(wm.windows) == 0:
        print(f"[Blender-Unity-Bridge] {title}: {message}")
        return

    def draw(self, _context):
        self.layout.label(text=message)

    wm.popup_menu(draw, title=title, icon=icon)


def _sanitize_file_name(name: str) -> str:
    invalid = '<>:"/\\|?*'
    safe = "".join("_" if c in invalid else c for c in name)
    safe = safe.strip().strip(".")
    return safe or "LiveModel"


def _map_xyz(x: float, y: float, z: float) -> tuple[float, float, float]:
    return (float(x), float(z), float(y))


def _normal_xyz(value: Any) -> tuple[float, float, float]:
    if hasattr(value, "x"):
        return (float(value.x), float(value.y), float(value.z))
    return (float(value[0]), float(value[1]), float(value[2]))


def _parse_url(url: str) -> tuple[str, int, str]:
    """Parse http://host:port/path into (host, port, path)."""
    url = url.removeprefix("http://")
    if "/" in url:
        hostport, path = url.split("/", 1)
        path = "/" + path
    else:
        hostport, path = url, "/"
    if ":" in hostport:
        host, port_str = hostport.rsplit(":", 1)
        port = int(port_str)
    else:
        host, port = hostport, 80
    return host, port, path


def _post_json(url: str, payload: dict[str, Any], timeout: float = 3.0) -> tuple[bool, dict[str, Any] | None, str]:
    try:
        host, port, path = _parse_url(url)
        t0 = time.monotonic()
        body = json.dumps(payload).encode("utf-8")
        t1 = time.monotonic()
        status, text = _http_request("POST", host, port, path, body=body, timeout=timeout)
        t2 = time.monotonic()
        print(f"[BUB Perf]   json_encode={t1-t0:.3f}s  http_post={t2-t1:.3f}s  payload={len(body)//1024}KB")
        if status < 0:
            return False, None, "connection failed"
        data = json.loads(text) if text else {}
        return True, data, text
    except Exception as exc:
        return False, None, str(exc)


def _get_json(url: str, timeout: float = 3.0) -> tuple[bool, dict[str, Any] | None, str]:
    try:
        host, port, path = _parse_url(url)
        status, text = _http_request("GET", host, port, path, timeout=timeout)
        if status < 0:
            return False, None, "connection failed"
        data = json.loads(text) if text else {}
        return True, data, text
    except Exception as exc:
        return False, None, str(exc)


def _health_check(settings: "BUBSyncSettings", timeout: float = 1.5) -> bool:
    endpoint = settings.server_url.rstrip("/") + "/health"
    ok, data, _ = _get_json(endpoint, timeout=timeout)
    return bool(ok and data and data.get("status") == "ok")


def _try_start_bridge(settings: "BUBSyncSettings") -> tuple[bool, str]:
    # Auto-locate the start script relative to this addon file
    script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "start_blender_unity_bridge.ps1")
    if not os.path.isfile(script):
        return False, f"Bridge start script not found: {script}"

    bridge_dir = os.path.dirname(script)
    creationflags = 0
    if os.name == "nt":
        creationflags = subprocess.CREATE_NEW_PROCESS_GROUP | subprocess.DETACHED_PROCESS | subprocess.CREATE_NO_WINDOW

    # Auto-detect Python — prefer system python, fall back to Blender's bundled python
    python_candidates: list[str] = []
    if sys.executable not in python_candidates:
        python_candidates.append(sys.executable)

    which_python = shutil.which("python")
    which_py = shutil.which("py")
    for candidate in (which_python, which_py):
        if candidate and candidate not in python_candidates:
            python_candidates.append(candidate)

    for pyexe in python_candidates:
        cmd = [pyexe, "-m", "uvicorn", "blender_unity_bridge_server:app", "--host", "127.0.0.1", "--port", "8000"]
        try:
            proc = subprocess.Popen(
                cmd,
                cwd=bridge_dir,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=creationflags,
            )
        except Exception:
            continue

        for _ in range(20):
            time.sleep(0.25)
            if _health_check(settings, timeout=1.0):
                return True, f"Bridge server started via {pyexe}."

        try:
            proc.terminate()
        except Exception:
            pass

    try:
        if os.name == "nt":
            subprocess.Popen(
                ["powershell", "-ExecutionPolicy", "Bypass", "-File", script],
                cwd=bridge_dir,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=creationflags,
            )
        else:
            subprocess.Popen([script], cwd=bridge_dir, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except Exception as exc:
        return False, f"Bridge start failed: {exc}"

    for _ in range(24):
        time.sleep(0.25)
        if _health_check(settings, timeout=1.0):
            return True, "Bridge server started via script."
    return False, "Bridge start timed out."


def _set_connection_enabled(settings: "BUBSyncSettings", value: bool) -> None:
    global _toggle_guard
    _toggle_guard = True
    settings.connection_enabled = value
    _toggle_guard = False


def _connect(settings: "BUBSyncSettings") -> tuple[bool, str]:
    global _http_conn
    # Reset connection to force a fresh TCP handshake on reconnect
    if _http_conn is not None:
        try:
            _http_conn.close()
        except Exception:
            pass
        _http_conn = None

    if not _health_check(settings):
        if settings.auto_start_bridge:
            ok_start, start_msg = _try_start_bridge(settings)
            if not ok_start:
                settings.remote_connected = False
                return False, start_msg
            # Wait up to 6s for the bridge to be ready
            deadline = time.monotonic() + 6.0
            ready = False
            while time.monotonic() < deadline:
                if _health_check(settings, timeout=1.0):
                    ready = True
                    break
                time.sleep(0.4)
            if not ready:
                settings.remote_connected = False
                return False, "Bridge started but not responding within 6s."
        else:
            settings.remote_connected = False
            return False, "Bridge server offline."

    endpoint = settings.server_url.rstrip("/") + "/sync/connection/connect"
    payload = {
        "source": "blender",
        "source_blend": bpy.data.filepath or "",
        "note": "toggle connect",
    }
    ok, data, error_text = _post_json(endpoint, payload)
    if not ok or data is None:
        settings.remote_connected = False
        return False, f"Connect failed: {error_text}"
    if not data.get("ok"):
        settings.remote_connected = False
        return False, f"Connect rejected: {data}"

    settings.remote_connected = bool(data.get("connected", True))
    return True, "Unity connection established."


def _disconnect(settings: "BUBSyncSettings") -> tuple[bool, str]:
    endpoint = settings.server_url.rstrip("/") + "/sync/connection/disconnect"
    payload = {
        "source": "blender",
        "source_blend": bpy.data.filepath or "",
        "note": "toggle disconnect",
    }
    ok, data, error_text = _post_json(endpoint, payload)
    settings.remote_connected = False
    if not ok or data is None:
        return False, f"Disconnected locally (remote call failed: {error_text})"
    if not data.get("ok"):
        return False, f"Disconnected locally (remote rejected: {data})"
    return True, "Unity connection closed."


def _refresh_connection_status(settings: "BUBSyncSettings", force: bool = False) -> tuple[bool, str]:
    global _last_status_poll_at
    now = time.monotonic()
    if not force and now - _last_status_poll_at < settings.status_poll_seconds:
        return True, "skip"
    _last_status_poll_at = now

    endpoint = settings.server_url.rstrip("/") + "/sync/connection/status"
    ok, data, error_text = _get_json(endpoint)
    if not ok or data is None:
        settings.remote_connected = False
        return False, f"Status check failed: {error_text}"

    settings.remote_connected = bool(data.get("connected", False))
    return True, "connected" if settings.remote_connected else "disconnected"


def _matrix_to_unity_transform(matrix_world: Matrix) -> tuple[list[float], list[float], list[float]]:
    mapped = _C_MATRIX @ matrix_world @ _C_MATRIX_INV
    loc, rot, scale = mapped.decompose()
    position = [float(loc.x), float(loc.y), float(loc.z)]
    rotation = [float(rot.x), float(rot.y), float(rot.z), float(rot.w)]
    scaling = [float(scale.x), float(scale.y), float(scale.z)]
    return position, rotation, scaling


def _serialize_node_graph(mat: "bpy.types.Material") -> dict[str, Any] | None:
    """Serialize a material node graph to a JSON-compatible dict for Unity HLSL translation."""
    if not mat or not mat.use_nodes or not mat.node_tree:
        return None

    tree = mat.node_tree
    # Build ASCII-safe identifiers (node names may contain unicode/spaces)
    name_to_safe: dict[str, str] = {}
    for i, node in enumerate(tree.nodes):
        name_to_safe[node.name] = f"N{i}_{node.type}"

    nodes_list: list[dict[str, Any]] = []
    for node in tree.nodes:
        safe = name_to_safe[node.name]
        nd: dict[str, Any] = {"name": safe, "type": node.type}

        # Inputs with default values
        inputs: list[dict[str, Any]] = []
        for inp in node.inputs:
            id_: dict[str, Any] = {"name": inp.name, "type": inp.type}
            try:
                if inp.type == "VALUE":
                    id_["dv"] = float(inp.default_value)
                elif inp.type in ("RGBA", "VECTOR"):
                    id_["dc"] = [float(v) for v in inp.default_value]
                elif inp.type in ("INT", "BOOLEAN"):
                    id_["dv"] = float(inp.default_value)
            except Exception:
                pass
            inputs.append(id_)
        nd["inputs"] = inputs

        # Node-type-specific properties (stored in reused flat fields)
        if node.type == "MATH":
            nd["op"] = node.operation
            nd["use_clamp"] = node.use_clamp
        elif node.type in ("MIX_RGB", "MIX"):
            nd["blend"] = getattr(node, "blend_type", "MIX")
            nd["use_clamp"] = getattr(node, "use_clamp", False)
            nd["op"] = getattr(node, "data_type", "RGBA")
        elif node.type == "VALTORGB":
            cr = node.color_ramp
            nd["cr_interp"] = cr.interpolation
            positions: list[float] = []
            colors: list[float] = []
            for el in cr.elements:
                positions.append(float(el.position))
                colors.extend(float(c) for c in el.color)
            nd["cr_pos"] = positions
            nd["cr_col"] = colors
        elif node.type == "TEX_NOISE":
            nd["op"] = node.noise_dimensions
        elif node.type == "TEX_WAVE":
            nd["op"] = node.wave_type
            nd["blend"] = node.bands_direction
            nd["cr_interp"] = node.wave_profile
        elif node.type == "TEX_GRADIENT":
            nd["op"] = node.gradient_type
        elif node.type == "TEX_IMAGE":
            if node.image:
                nd["img_name"] = node.image.name
                nd["img_path"] = bpy.path.abspath(node.image.filepath) if node.image.filepath else ""
            nd["op"] = node.interpolation
            nd["blend"] = node.extension
        elif node.type == "MAPPING":
            nd["op"] = node.vector_type
        elif node.type == "CLAMP":
            nd["op"] = node.clamp_type
        elif node.type == "MAP_RANGE":
            nd["op"] = node.interpolation_type
            nd["use_clamp"] = node.clamp
        elif node.type == "BSDF_PRINCIPLED":
            nd["op"] = node.distribution

        nodes_list.append(nd)

    links_list: list[dict[str, Any]] = []
    for link in tree.links:
        links_list.append({
            "fn": name_to_safe[link.from_node.name],
            "fs": link.from_socket.name,
            "tn": name_to_safe[link.to_node.name],
            "ts": link.to_socket.name,
        })

    output_node_safe = ""
    for node in tree.nodes:
        if node.type == "OUTPUT_MATERIAL":
            output_node_safe = name_to_safe[node.name]
            break

    return {"nodes": nodes_list, "links": links_list, "output_node": output_node_safe}


def _build_object_snapshot(obj: bpy.types.Object, depsgraph: bpy.types.Depsgraph, include_material: bool = False) -> dict[str, Any] | None:
    if obj.type != "MESH":
        return None

    if obj.mode == "EDIT":
        try:
            obj.update_from_editmode()
        except Exception:
            pass

    eval_obj = obj.evaluated_get(depsgraph)
    mesh = None
    try:
        mesh = eval_obj.to_mesh(preserve_all_data_layers=True, depsgraph=depsgraph)
    except TypeError:
        mesh = eval_obj.to_mesh()
    except Exception:
        mesh = None

    if mesh is None:
        return None

    try:
        obj_name = obj.name
        position, rotation, scale = _matrix_to_unity_transform(obj.matrix_world)

        mesh.calc_loop_triangles()

        n_loops   = len(mesh.loops)
        n_tris    = len(mesh.loop_triangles)
        n_verts   = len(mesh.vertices)
        n_corners = n_tris * 3

        # ── Positions (always available) ──
        vco = _np.empty(n_verts * 3, dtype=_np.float32)
        mesh.vertices.foreach_get("co", vco)
        vco = vco.reshape(-1, 3)

        # ── Triangle → loop index (3 loops per tri) ──
        tri_loop_idx = _np.empty(n_corners, dtype=_np.int32)
        mesh.loop_triangles.foreach_get("loops", tri_loop_idx)

        # ── Vertex index per loop ──
        loop_vert_idx = _np.empty(n_loops, dtype=_np.int32)
        mesh.loops.foreach_get("vertex_index", loop_vert_idx)

        # ── Normals: try Blender 4.1+ corner_normals first, then legacy ──
        try:
            # Blender 4.1+: corner_normals is a per-loop read-only attribute
            nrm_raw = _np.empty(n_loops * 3, dtype=_np.float32)
            mesh.corner_normals.foreach_get("vector", nrm_raw)
            loop_nrm = nrm_raw.reshape(-1, 3)
        except Exception:
            try:
                # Legacy Blender (<4.1): calc_normals_split then loops.foreach_get
                mesh.calc_normals_split()
                nrm_raw = _np.empty(n_loops * 3, dtype=_np.float32)
                mesh.loops.foreach_get("normal", nrm_raw)
                loop_nrm = nrm_raw.reshape(-1, 3)
            except Exception:
                # Ultimate fallback: use flat face normals per corner
                loop_nrm = _np.zeros((n_loops, 3), dtype=_np.float32)

        # ── UVs ──
        if mesh.uv_layers.active:
            uv_raw = _np.empty(n_loops * 2, dtype=_np.float32)
            mesh.uv_layers.active.data.foreach_get("uv", uv_raw)
            uv_raw = uv_raw.reshape(-1, 2)
        else:
            uv_raw = _np.zeros((n_loops, 2), dtype=_np.float32)

        # ── Gather per-corner data ──
        corner_vert = loop_vert_idx[tri_loop_idx]
        pos_b = vco[corner_vert]
        nrm_b = loop_nrm[tri_loop_idx]
        uv_c  = uv_raw[tri_loop_idx]

        # Axis remap: Unity x=Bx, y=Bz, z=By
        pos_u = pos_b[:, [0, 2, 1]]
        nrm_u = nrm_b[:, [0, 2, 1]]

        # Winding reversal: swap corner 1↔2 per tri
        seq = _np.arange(n_corners, dtype=_np.int32).reshape(-1, 3)
        seq[:, [1, 2]] = seq[:, [2, 1]]

        vertices  = pos_u.flatten().tolist()
        normals   = nrm_u.flatten().tolist()
        uvs       = uv_c.flatten().tolist()
        triangles = seq.flatten().tolist()

        snap: dict[str, Any] = {
            "object_name": obj_name,
            "objectName":  obj_name,
            "position":    position,
            "rotation":    rotation,
            "scale":       scale,
            "vertices":    vertices,
            "triangles":   triangles,
            "normals":     normals,
            "uv":          uvs,
        }

        if include_material:
            node_graph: dict[str, Any] | None = None
            try:
                if obj.material_slots and obj.material_slots[0].material:
                    node_graph = _serialize_node_graph(obj.material_slots[0].material)
            except Exception as _ng_err:
                print(f"[Blender-Unity-Bridge] Node graph serialization failed: {_ng_err}")

            material_data: dict[str, Any] = {}
            try:
                if obj.material_slots and obj.material_slots[0].material:
                    mat = obj.material_slots[0].material
                    material_data["material_name"] = mat.name
                    if mat.use_nodes:
                        for node in mat.node_tree.nodes:
                            if node.type == "BSDF_PRINCIPLED":
                                bc = node.inputs["Base Color"].default_value
                                material_data["base_color"] = [float(bc[0]), float(bc[1]), float(bc[2]), float(bc[3])]
                                material_data["metallic"] = float(node.inputs["Metallic"].default_value)
                                material_data["roughness"] = float(node.inputs["Roughness"].default_value)
                                ec_input = node.inputs.get("Emission Color") or node.inputs.get("Emission")
                                if ec_input is not None:
                                    ev = ec_input.default_value
                                    try:
                                        material_data["emission"] = [float(ev[0]), float(ev[1]), float(ev[2])]
                                    except Exception:
                                        material_data["emission"] = [0.0, 0.0, 0.0]
                                else:
                                    material_data["emission"] = [0.0, 0.0, 0.0]
                                es_input = node.inputs.get("Emission Strength")
                                material_data["emission_strength"] = float(es_input.default_value) if es_input else 0.0
                                break
            except Exception as _mat_err:
                print(f"[Blender-Unity-Bridge] Material extraction failed: {_mat_err}")
                material_data = {}

            snap["material"] = material_data
            if node_graph is not None:
                snap["node_graph"] = node_graph

        return snap
    finally:
        eval_obj.to_mesh_clear()


def _build_snapshot(settings: "BUBSyncSettings", include_material: bool = False) -> tuple[bool, dict[str, Any] | str]:
    depsgraph = bpy.context.evaluated_depsgraph_get()
    objects: list[dict[str, Any]] = []

    for obj in bpy.data.objects:
        if settings.use_selection_only and not obj.select_get():
            continue
        if obj.type != "MESH":
            continue

        object_snapshot = _build_object_snapshot(obj, depsgraph, include_material=include_material)
        if object_snapshot is not None:
            objects.append(object_snapshot)

    snapshot = {
        "objects": objects,
        "object_count": len(objects),
        "coordinate_mapping": {
            "rule": "x_u=x_b, y_u=z_b, z_u=y_b",
            "x": "x",
            "y": "z",
            "z": "y",
            "units": "1_blender_unit_equals_1_unity_meter",
        },
    }
    return True, snapshot


def _publish_sync(settings: "BUBSyncSettings", snapshot: dict[str, Any]) -> tuple[bool, str]:
    asset_name = _sanitize_file_name((settings.asset_name or "LiveModel").strip() or "LiveModel")
    payload = {
        "asset_name": asset_name,
        "source_blend": bpy.data.filepath or "",
        "snapshot": snapshot,
        "metadata": {
            "object_count": len(snapshot.get("objects", [])),
            "selection_only": settings.use_selection_only,
            "transport": "direct_snapshot",
            "coordinate_mapping": {
                "rule": "x_u=x_b, y_u=z_b, z_u=y_b",
                "x": "x",
                "y": "z",
                "z": "y",
            },
        },
    }
    endpoint = settings.server_url.rstrip("/") + "/sync/publish_snapshot"
    ok, data, error_text = _post_json(endpoint, payload)
    if not ok or data is None:
        return False, f"Publish failed: {error_text}"
    if not data.get("ok"):
        return False, f"Publish rejected: {data.get('error', data)}"

    object_count = data.get("object_count")
    if object_count is None:
        object_count = len(snapshot.get("objects", []))
    return True, f"Snapshot published ({object_count} objects)."


def _queue_publish_retry(snapshot: dict[str, Any]) -> None:
    global _pending_snapshot
    _pending_snapshot = json.loads(json.dumps(snapshot))


def _clear_publish_retry() -> None:
    global _pending_snapshot
    _pending_snapshot = None


def _retry_pending_publish(settings: "BUBSyncSettings") -> tuple[bool, str]:
    global _last_publish_retry_at
    if _pending_snapshot is None:
        return True, "no_pending"
    if not settings.connection_enabled or not settings.remote_connected:
        return False, "pending publish waiting for active connection"

    now = time.monotonic()
    if now - _last_publish_retry_at < settings.publish_retry_seconds:
        return True, "retry_wait"
    _last_publish_retry_at = now

    pending_snapshot = _pending_snapshot
    ok, msg = _publish_sync(settings, pending_snapshot)
    if ok:
        _clear_publish_retry()
        return True, "pending snapshot published"
    return False, f"pending publish failed: {msg}"


def _sync_once(settings: "BUBSyncSettings", include_material: bool = False) -> tuple[bool, str]:
    global _is_syncing
    if _is_syncing:
        return False, "Sync skipped: publish in progress."
    if not settings.connection_enabled:
        return False, "Unity Connection is off."
    if not settings.remote_connected:
        return False, "Unity Connection is not active."

    _is_syncing = True
    try:
        t0 = time.monotonic()
        ok, snapshot_or_error = _build_snapshot(settings, include_material=include_material)
        t1 = time.monotonic()
        if not ok:
            return False, str(snapshot_or_error)

        snapshot = snapshot_or_error
        ok, publish_result = _publish_sync(settings, snapshot)
        t2 = time.monotonic()
        print(f"[BUB Perf] build={t1-t0:.3f}s  publish={t2-t1:.3f}s  total={t2-t0:.3f}s")
        if not ok:
            _queue_publish_retry(snapshot)
            return False, publish_result

        _clear_publish_retry()
        return True, publish_result
    finally:
        _is_syncing = False


def _max_publish_interval(settings: "BUBSyncSettings") -> float:
    return max(1.0 / 30.0, float(settings.debounce_seconds))


def _event_publish_tick() -> float | None:
    global _dirty, _publish_timer_registered, _last_publish_sent_at
    scene = bpy.context.scene
    if not scene or not hasattr(scene, "bub_sync_settings"):
        _publish_timer_registered = False
        return None

    settings = scene.bub_sync_settings
    if not settings.auto_sync or not settings.connection_enabled:
        _publish_timer_registered = False
        return None

    if not settings.remote_connected or not _dirty:
        _publish_timer_registered = False
        return None

    interval = _max_publish_interval(settings)
    elapsed = time.monotonic() - _last_publish_sent_at
    if elapsed < interval:
        return max(0.001, interval - elapsed)

    _dirty = False
    try:
        ok, msg = _sync_once(settings)
        settings.last_status = msg
        if ok:
            _last_publish_sent_at = time.monotonic()
            print(f"[Blender-Unity-Bridge] {msg}")
        else:
            print(f"[Blender-Unity-Bridge] ERROR: {msg}")
    except Exception as _tick_err:
        print(f"[Blender-Unity-Bridge] EXCEPTION in publish tick: {_tick_err}")
        import traceback
        traceback.print_exc()
    finally:
        _publish_timer_registered = False
    return None


def _schedule_publish_from_event() -> None:
    global _publish_timer_registered
    if _publish_timer_registered:
        return
    _publish_timer_registered = True
    bpy.app.timers.register(_event_publish_tick, first_interval=0.0, persistent=False)


def _mark_dirty() -> None:
    global _dirty
    _dirty = True


def _on_connection_toggle(settings: "BUBSyncSettings", _context: bpy.types.Context) -> None:
    if _toggle_guard:
        return

    if settings.connection_enabled:
        ok, msg = _connect(settings)
        settings.last_status = msg
        if not ok:
            _set_connection_enabled(settings, False)
            _show_popup("Unity Connection", msg, "ERROR")
            return
        _mark_dirty()
        _schedule_publish_from_event()
        return

    ok, msg = _disconnect(settings)
    settings.last_status = msg
    if not ok:
        _show_popup("Unity Connection", msg, "INFO")


def _depsgraph_update(scene: bpy.types.Scene, depsgraph: bpy.types.Depsgraph) -> None:
    if not hasattr(scene, "bub_sync_settings"):
        return
    settings = scene.bub_sync_settings
    if not settings.auto_sync or not settings.connection_enabled:
        return

    for update in depsgraph.updates:
        id_data = update.id
        if isinstance(id_data, (bpy.types.Object, bpy.types.Mesh, bpy.types.Collection)):
            _mark_dirty()
            _schedule_publish_from_event()
            break


def _draw_topbar(self, context: bpy.types.Context) -> None:
    scene = context.scene
    if not scene or not hasattr(scene, "bub_sync_settings"):
        return

    settings = scene.bub_sync_settings
    row = self.layout.row(align=True)
    row.separator()
    row.prop(settings, "connection_enabled", text="Unity Connection", toggle=True)
    status_icon = "CHECKMARK" if settings.remote_connected else "LOCKED"
    row.label(text="Connected" if settings.remote_connected else "Disconnected", icon=status_icon)


def _draw_view3d_header(self, context: bpy.types.Context) -> None:
    scene = context.scene
    if not scene or not hasattr(scene, "bub_sync_settings"):
        return

    settings = scene.bub_sync_settings
    row = self.layout.row(align=True)
    row.separator()
    row.prop(settings, "connection_enabled", text="Unity Connection", toggle=True)


def _sync_timer() -> float:
    scene = bpy.context.scene
    if not scene:
        return 0.1
    if not hasattr(scene, "bub_sync_settings"):
        return 0.1

    settings = scene.bub_sync_settings
    if settings.connection_enabled:
        ok, msg = _refresh_connection_status(settings, force=False)
        if not ok:
            settings.last_status = msg

    if settings.connection_enabled and _pending_snapshot is not None:
        _, msg_retry = _retry_pending_publish(settings)
        if msg_retry not in ("retry_wait", "no_pending"):
            settings.last_status = msg_retry

    if settings.auto_sync and settings.connection_enabled and settings.remote_connected and _dirty:
        _schedule_publish_from_event()
    return 0.1


def _ensure_timer_registered() -> None:
    global _timer_registered
    if _timer_registered:
        return
    bpy.app.timers.register(_sync_timer, first_interval=0.1, persistent=True)
    _timer_registered = True


def _auto_connect_deferred() -> None:
    """Try to connect automatically; silently skip if bridge is not running."""
    scene = bpy.context.scene
    if not scene or not hasattr(scene, "bub_sync_settings"):
        bpy.app.timers.register(_auto_connect_deferred, first_interval=1.0)
        return
    settings = scene.bub_sync_settings
    ok, msg = _connect(settings)
    settings.last_status = msg
    if ok:
        _set_connection_enabled(settings, True)
        _mark_dirty()
        _schedule_publish_from_event()
        print(f"[Blender-Unity-Bridge] Auto-connected on startup: {msg}")
    else:
        print(f"[Blender-Unity-Bridge] Auto-connect skipped (bridge not running): {msg}")


@bpy.app.handlers.persistent
def _load_post_handler(_filepath: str) -> None:
    """Auto-connect after Blender loads a file or starts up."""
    bpy.app.timers.register(_auto_connect_deferred, first_interval=1.5)


class BUBSyncSettings(PropertyGroup):
    server_url: StringProperty(  # type: ignore
        name="Server URL",
        default="http://127.0.0.1:8000",
    )
    auto_start_bridge: BoolProperty(  # type: ignore
        name="Auto Start Bridge",
        default=True,
    )
    bridge_start_script: StringProperty(  # type: ignore
        name="Bridge Start Script",
        subtype="FILE_PATH",
        default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "start_blender_unity_bridge.ps1"),
    )
    bridge_python_exe: StringProperty(  # type: ignore
        name="Bridge Python",
        subtype="FILE_PATH",
        default="",
    )
    asset_name: StringProperty(  # type: ignore
        name="Asset Name",
        default="LiveModel",
    )
    use_selection_only: BoolProperty(  # type: ignore
        name="Selection Only",
        default=False,
    )
    auto_sync: BoolProperty(  # type: ignore
        name="Auto Sync",
        default=True,
    )
    debounce_seconds: FloatProperty(  # type: ignore
        name="Max Push Interval (s)",
        min=0.01,
        max=1.0,
        default=1.0 / 30.0,
    )
    status_poll_seconds: FloatProperty(  # type: ignore
        name="Status Poll (s)",
        min=0.1,
        max=10.0,
        default=0.25,
    )
    publish_retry_seconds: FloatProperty(  # type: ignore
        name="Publish Retry (s)",
        min=0.2,
        max=10.0,
        default=1.0,
    )
    connection_enabled: BoolProperty(  # type: ignore
        name="Unity Connection",
        default=False,
        update=_on_connection_toggle,
    )
    remote_connected: BoolProperty(  # type: ignore
        name="Remote Connected",
        default=False,
    )
    last_status: StringProperty(  # type: ignore
        name="Last Status",
        default="Not connected.",
    )


class BUBSYNC_OT_sync_now(Operator):
    bl_idname = "bubsync.sync_now"
    bl_label = "Sync Material"
    bl_description = "Push geometry + material/node-graph to Unity (one-shot, not auto)"

    def execute(self, context):
        settings = context.scene.bub_sync_settings
        ok, msg = _sync_once(settings, include_material=True)
        settings.last_status = msg
        if ok:
            self.report({"INFO"}, msg)
            return {"FINISHED"}
        self.report({"ERROR"}, msg)
        return {"CANCELLED"}


class BUBSYNC_OT_reconnect(Operator):
    bl_idname = "bubsync.reconnect"
    bl_label = "Reload"
    bl_description = "Fully reload the addon module and auto-reconnect"

    def execute(self, context):
        import importlib
        import sys

        mod_name = __name__

        def _deferred_reload():
            mod = sys.modules.get(mod_name)
            if mod is None:
                print(f"[Blender-Unity-Bridge] Reload failed: module '{mod_name}' not found.")
                return None
            try:
                mod.unregister()
                importlib.reload(mod)
                mod.register()
                print("[Blender-Unity-Bridge] Addon reloaded successfully.")
            except Exception as e:
                print(f"[Blender-Unity-Bridge] Reload error: {e}")
            return None  # run once

        bpy.app.timers.register(_deferred_reload, first_interval=0.1)
        self.report({"INFO"}, "Reloading addon…")
        return {"FINISHED"}


class BUBSYNC_PT_panel(Panel):
    bl_label = "Unity Connection"
    bl_idname = "BUBSYNC_PT_panel"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Unity Bridge"

    def draw(self, context):
        layout = self.layout
        settings = context.scene.bub_sync_settings

        layout.prop(settings, "connection_enabled")
        layout.label(text=f"Remote: {'Connected' if settings.remote_connected else 'Disconnected'}")
        layout.prop(settings, "server_url")
        layout.prop(settings, "use_selection_only")
        layout.prop(settings, "auto_sync")
        layout.operator("bubsync.reconnect", icon="FILE_REFRESH")

        adv = layout.box()
        adv.label(text="Advanced")
        adv.prop(settings, "debounce_seconds")
        adv.prop(settings, "publish_retry_seconds")

        layout.separator()
        layout.label(text="Real-time: geometry only (no material)")
        layout.operator("bubsync.sync_now", icon="MATERIAL", text="Sync Material (Manual)")
        layout.label(text=f"Status: {settings.last_status}")


classes = (
    BUBSyncSettings,
    BUBSYNC_OT_sync_now,
    BUBSYNC_OT_reconnect,
    BUBSYNC_PT_panel,
)


def register():
    global _topbar_registered, _view3d_header_registered
    for cls in classes:
        bpy.utils.register_class(cls)
    bpy.types.Scene.bub_sync_settings = PointerProperty(type=BUBSyncSettings)

    if _depsgraph_update not in bpy.app.handlers.depsgraph_update_post:
        bpy.app.handlers.depsgraph_update_post.append(_depsgraph_update)

    if hasattr(bpy.types, "TOPBAR_HT_upper_bar"):
        funcs = bpy.types.TOPBAR_HT_upper_bar._dyn_ui_initialize()
        while _draw_topbar in funcs:
            bpy.types.TOPBAR_HT_upper_bar.remove(_draw_topbar)
            funcs = bpy.types.TOPBAR_HT_upper_bar._dyn_ui_initialize()
        _topbar_registered = False
    if hasattr(bpy.types, "VIEW3D_HT_header"):
        funcs = bpy.types.VIEW3D_HT_header._dyn_ui_initialize()
        while _draw_view3d_header in funcs:
            bpy.types.VIEW3D_HT_header.remove(_draw_view3d_header)
            funcs = bpy.types.VIEW3D_HT_header._dyn_ui_initialize()
        _view3d_header_registered = False

    _ensure_timer_registered()
    print("[Blender-Unity-Bridge] Add-on registered.")


def unregister():
    global _topbar_registered, _view3d_header_registered
    if _depsgraph_update in bpy.app.handlers.depsgraph_update_post:
        bpy.app.handlers.depsgraph_update_post.remove(_depsgraph_update)


    if _topbar_registered and hasattr(bpy.types, "TOPBAR_HT_upper_bar"):
        bpy.types.TOPBAR_HT_upper_bar.remove(_draw_topbar)
        _topbar_registered = False
    if _view3d_header_registered and hasattr(bpy.types, "VIEW3D_HT_header"):
        bpy.types.VIEW3D_HT_header.remove(_draw_view3d_header)
        _view3d_header_registered = False

    del bpy.types.Scene.bub_sync_settings
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)
    print("[Blender-Unity-Bridge] Add-on unregistered.")


if __name__ == "__main__":
    register()
