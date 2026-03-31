# MML Bridge — Blender ↔ Unity Live Sync

> Stream Blender mesh geometry into Unity Editor in real time, with automatic URP HLSL shader generation from Blender's node graph.

![Version](https://img.shields.io/badge/version-0.5.0-blue)
![Blender](https://img.shields.io/badge/Blender-4.0%2B-orange)
![Unity](https://img.shields.io/badge/Unity-2022.3%2B%20URP-brightgreen)
![Python](https://img.shields.io/badge/Python-3.10%2B-yellow)
![License](https://img.shields.io/badge/license-MIT-lightgrey)

---

## What Is This?

**MML Bridge** is a three-component system that creates a real-time live link between **Blender** and **Unity Editor**:

```
┌─────────────────────┐   HTTP/WebSocket   ┌──────────────────────┐   WebSocket   ┌────────────────────────┐
│  Blender 4.0+       │ ─────────────────► │  Bridge Server       │ ────────────► │  Unity 2022.3+ (URP)   │
│  (Addon)            │                    │  (FastAPI + SQLite)  │               │  (Editor Plugin)       │
│                     │                    │  localhost:8000      │               │                        │
│  • Geometry export  │                    │  • Snapshot store    │               │  • Mesh instantiation  │
│  • Node graph       │                    │  • WebSocket relay   │               │  • HLSL shader gen     │
│    serialization    │                    │  • Connection mgmt   │               │  • Texture import      │
│  • Auto-sync on     │                    └──────────────────────┘               │  • PBR material setup  │
│    depsgraph update │                                                            └────────────────────────┘
└─────────────────────┘
```

**Real-time flow**: move/edit anything in Blender → geometry updates appear in Unity within milliseconds.
**Material flow**: press "Sync Material" → Blender's node graph is serialized → Unity generates a URP HLSL shader automatically.

---

## Features

### Geometry Sync (Real-time)
- Streams vertex positions, normals, UVs and triangles on every depsgraph update
- Automatic coordinate system conversion: Blender (Z-up, right-hand) → Unity (Y-up, left-hand)
- Handles mesh deformations, modifiers, shape keys (via evaluated mesh)
- Correct winding order (compensates for the mirror reflection in axis remapping)
- Supports meshes with > 65 535 vertices (UInt32 index format auto-selected)

### Material Sync (Manual, one-shot)
- Press **Sync Material** in the Blender panel → full node graph pushed to Unity
- Auto-generates a URP-compatible HLSL `.shader` file from the Blender node graph
- Shader cached by source hash → re-compilation only happens when the graph changes
- Textures referenced by Image Texture nodes are automatically copied from Blender's paths into `Assets/Textures/MML_Live/` and imported

### Supported Blender Shader Nodes (40+)

| Category | Nodes |
|---|---|
| **Input** | `Texture Coordinate`, `Value`, `RGB` |
| **Math** | `Math` (25 operations: Add, Subtract, Multiply, Divide, Power, Log, Sqrt, Abs, Round, Floor, Ceil, Modulo, Sine, Cosine, Tangent, Arctan2, Min, Max, Snap, Wrap, Ping-Pong, …) |
| **Color** | `Mix RGB / Mix`, `Color Ramp`, `Hue/Saturation/Value`, `Brightness/Contrast`, `Gamma`, `Invert`, `RGB to BW`, `RGB Curves` (approximated) |
| **Texture** | `Image Texture`, `Noise Texture` (FBM), `Wave Texture`, `Gradient Texture` |
| **Vector** | `Mapping`, `Vector Math`, `Combine XYZ`, `Separate XYZ`, `Combine RGB`, `Separate RGB`, `Normal Map` (OpenGL→DirectX Y-flip) |
| **Converter** | `Clamp`, `Map Range`, `Fresnel`, `Layer Weight` |
| **Utility** | `Reroute` |
| **BSDF** | `Principled BSDF` (Albedo, Metallic, Roughness, Emission, Alpha, Normal) |

### Connection Management
- One-click connect / disconnect toggle in the Blender sidebar
- Bridge server auto-starts if offline (configurable Python path)
- Persistent HTTP connection reuse (avoids TCP TIME_WAIT port exhaustion)
- WebSocket for near-zero-latency mesh delivery to Unity Editor
- Automatic retry on failed publishes

---

## Requirements

| Component | Minimum version |
|---|---|
| Blender | 4.0 |
| Python | 3.10 |
| fastapi | 0.111 |
| uvicorn | 0.30 |
| Unity | 2022.3 LTS |
| Unity URP | 14.0 |

---

## Installation

### Step 1 — Bridge Server (Python)

The bridge server acts as a relay between Blender and Unity. It runs locally on port 8000.

```bash
# Clone this repository
git clone https://github.com/y-tang0320253/mml-bridge.git
cd mml-bridge/bridge_server

# Install Python dependencies
pip install -r requirements.txt

# Start the server (Windows)
powershell -File start_mml_bridge.ps1

# Or start manually
python -m uvicorn mml_bridge_server:app --host 127.0.0.1 --port 8000
```

Verify the server is running: open [http://127.0.0.1:8000/health](http://127.0.0.1:8000/health) — you should see `{"status":"ok"}`.

---

### Step 2 — Blender Addon

1. In Blender, go to **Edit → Preferences → Add-ons → Install…**
2. Select `blender_addon/blender_live_sync_addon.py`
3. Enable the addon **"Unity Connection"**

Configure once in **View3D → N Panel → MML Sync**:

| Setting | Default | Description |
|---|---|---|
| Bridge Start Script | *(path)* | Path to `start_mml_bridge.ps1` |
| Bridge Python | `python` | Python executable with fastapi installed |
| Server URL | `http://127.0.0.1:8000` | Bridge server address |
| Auto Start Bridge | ✓ | Launch server automatically when connecting |

---

### Step 3 — Unity Package

#### Option A — Unity Package Manager (Git URL) *(recommended)*

1. Open **Window → Package Manager**
2. Click **+** → **Add package from git URL…**
3. Enter:
   ```
   https://github.com/y-tang0320253/mml-bridge.git?path=unity_package
   ```

#### Option B — Manual copy

Copy the three `.cs` files directly into your Unity project:

```
Assets/
└── MMLBridge/
    ├── Editor/
    │   ├── MmlBlenderLiveSyncEditor.cs
    │   └── MmlShaderGenerator.cs
    └── Runtime/
        └── MmlBridgeClient.cs
```

> **URP is required.** The generated shaders use `UniversalFragmentPBR`. Make sure your project has URP 14+ installed (`com.unity.render-pipelines.universal`).

---

## Usage

### Quick Start

1. Open Blender with your scene
2. In the **N Panel → MML Sync** tab, toggle **Unity Connection** ON
   - The bridge server launches automatically (if configured)
   - Status shows **"Connected"**
3. Open your Unity project — a `MML_LiveSync_Root` object appears in the scene
4. Move or edit any mesh object in Blender → watch it update in Unity's Scene view in real time

### Sync Modes

| Mode | Trigger | What's sent | Cost |
|---|---|---|---|
| **Auto Sync** (real-time) | Any depsgraph update | Vertices, normals, UVs, triangles, transform | ~1–50 ms |
| **Sync Material** (manual) | Button press | Full data + node graph + textures | ~500 ms–2 s (shader compile) |

> Materials and shaders are **not** sent automatically to avoid the expensive HLSL recompilation on every frame. Press **Sync Material (Manual)** once after you finish editing the material.

### Sync Material Button

In **View3D → N Panel → MML Sync**:

```
[Reconnect]
────────────────────────────
Real-time: geometry only (no material)
[🎨 Sync Material (Manual)]
Status: Snapshot published (1 objects).
```

Click **Sync Material (Manual)** whenever you:
- Add or change an Image Texture node
- Modify the Principled BSDF inputs (metallic, roughness, etc.)
- Add a Normal Map node
- Change any shader node connections

### Generated Assets in Unity

| Asset type | Location |
|---|---|
| Generated HLSL shaders | `Assets/Shaders/MML_Generated/` |
| Auto-imported textures | `Assets/Textures/MML_Live/` |
| Materials | `Assets/Materials/MML_LiveSync_*.mat` |
| Live mesh GameObjects | Scene → `MML_LiveSync_Root/MML_LiveSync_*` |

---

## Configuration Reference

### Blender Addon Settings

| Property | Type | Default | Description |
|---|---|---|---|
| `connection_enabled` | bool | false | Master connect/disconnect toggle |
| `server_url` | string | `http://127.0.0.1:8000` | Bridge server address |
| `auto_start_bridge` | bool | true | Launch server automatically |
| `bridge_start_script` | path | — | PowerShell startup script path |
| `bridge_python_exe` | path | `python` | Python executable |
| `asset_name` | string | `LiveModel` | Identifier used in Unity |
| `use_selection_only` | bool | false | Only sync selected objects |
| `auto_sync` | bool | true | Enable real-time geometry sync |
| `debounce_seconds` | float | 0.033 | Minimum interval between publishes (33 ms ≈ 30 fps) |
| `status_poll_seconds` | float | 0.25 | How often the addon checks connection status |
| `publish_retry_seconds` | float | 1.0 | Retry interval for failed publishes |

### Unity Editor Settings (EditorPrefs)

| Key | Description |
|---|---|
| `MML.LiveSync.Enabled` | Whether live sync is active |
| `MML.LiveSync.ServerUrl` | Bridge server URL |
| `MML.LiveSync.PollSeconds` | Status poll interval |
| `MML.LiveSync.LastSnapshotEventId` | Resume point for missed events |

Access via **MML Bridge → Live Sync** menu in Unity.

---

## Architecture Deep-Dive

### Coordinate System Mapping

Blender uses a **right-handed, Z-up** coordinate system. Unity uses a **left-handed, Y-up** system. The mapping applied is:

```
Unity X = Blender X
Unity Y = Blender Z
Unity Z = Blender Y
```

This is a det = −1 reflection (mirrors one axis), which reverses triangle winding order. The addon automatically reverses `[0, 1, 2]` → `[0, 2, 1]` to compensate.

Rotations are handled via a change-of-basis matrix:

```python
_C_MATRIX = Matrix(
    (1, 0, 0, 0),
    (0, 0, 1, 0),
    (0, 1, 0, 0),
    (0, 0, 0, 1),
)
mapped = _C_MATRIX @ matrix_world @ _C_MATRIX_INV
```

### Normal Map Handling

Blender stores normal maps in **OpenGL convention** (Y pointing up in tangent space), while Unity DirectX convention has **Y pointing down**. The HLSL generator automatically inserts:

```hlsl
unpacked.y = -unpacked.y;  // OpenGL → DirectX
```

Textures are imported with `textureType = 0` (Default, not Normal Map) and `sRGBTexture = false` (linear), so our manual `rgb * 2 - 1` unpack works correctly without Unity re-packing into DXT5nm.

### Shader Generation Pipeline

```
Blender Node Graph (JSON)
    │
    ▼
MmlShaderGenerator.cs
    ├─ Topological sort (DFS post-order)
    ├─ Per-node HLSL code emission
    │   ├─ TEX_IMAGE  → SAMPLE_TEXTURE2D(...)
    │   ├─ MATH       → saturate(a + b)
    │   ├─ VALTORGB   → piecewise linear lerp
    │   ├─ TEX_NOISE  → mml_fnoise(p, detail, roughness)
    │   ├─ NORMAL_MAP → normalize(mul(unpacked, tbn))
    │   └─ ... (40+ node types)
    ├─ Socket connection resolution (backward traversal)
    └─ Full URP HLSL shader source
        │
        ▼
    File.WriteAllText → AssetDatabase.ImportAsset
    (skipped if source hash unchanged)
```

### Bridge Server API

| Method | Endpoint | Description |
|---|---|---|
| GET | `/health` | Server health check |
| POST | `/sync/connection/connect` | Register Blender connection |
| POST | `/sync/connection/disconnect` | Unregister connection |
| GET | `/sync/connection/status` | Current connection state |
| POST | `/sync/publish_snapshot` | Push mesh snapshot from Blender |
| GET | `/sync/latest_snapshot` | Get latest snapshot (Unity polling fallback) |
| WS | `/ws/sync_snapshot` | Real-time snapshot stream to Unity |

---

## Troubleshooting

### Blender shows "Bridge server offline"
- Make sure Python with fastapi/uvicorn is installed: `pip install fastapi uvicorn[standard]`
- Check the **Bridge Python** path in settings points to the correct Python executable
- Start the server manually: `python -m uvicorn mml_bridge_server:app --host 127.0.0.1 --port 8000`
- Check [http://127.0.0.1:8000/health](http://127.0.0.1:8000/health) in a browser

### Unity scene doesn't update
- Ensure **MML Bridge → Live Sync → Enable** is checked in Unity
- Check Unity Console for `[MML Live Sync]` log messages
- Verify the bridge server is running (Blender status shows "Connected")
- Try pressing **Reconnect** in the Blender panel

### Material appears flat / magenta in Unity
- Press **Sync Material (Manual)** — materials are not sent automatically
- Check that URP is installed in your Unity project (`com.unity.render-pipelines.universal`)
- Check Unity Console for shader compilation errors: look for `[MML] Shader recompiled`
- If the shader is magenta, check `Assets/Shaders/MML_Generated/` for syntax errors

### Normal map has no effect
- Press **Sync Material (Manual)** to re-push the node graph
- Ensure your texture is in `Assets/Textures/MML_Live/` and imported with `sRGB = false`
- Check that the texture meta file has `textureType: 0` (not `1` = Normal Map)

### Performance: updates are slow (> 500 ms)
- High-poly meshes take longer to serialize. Try enabling **Selection Only** to sync only the active object
- Geometry serialization runs on Blender's main thread — complex modifiers (Subdivision, Boolean) add cost
- The shader recompile (`ForceSynchronousImport`) only happens when the node graph changes; subsequent syncs are fast

### Port 8000 is already in use
Change the port in both places:
1. `start_mml_bridge.ps1`: `--port 8001`
2. Blender settings: Server URL → `http://127.0.0.1:8001`

---

## Project Structure

```
mml-bridge/
├── README.md
├── LICENSE
├── .gitignore
│
├── blender_addon/
│   └── blender_live_sync_addon.py    # Single-file Blender add-on (install this)
│
├── bridge_server/
│   ├── mml_bridge_server.py          # FastAPI bridge server
│   ├── requirements.txt              # Python dependencies
│   └── start_mml_bridge.ps1          # Windows one-click launcher
│
└── unity_package/
    ├── package.json                  # UPM package manifest
    ├── Editor/
    │   ├── MmlBlenderLiveSyncEditor.cs   # Editor sync engine + mesh builder
    │   ├── MmlShaderGenerator.cs         # HLSL shader code generator
    │   └── MmlBridge.Editor.asmdef
    └── Runtime/
        ├── MmlBridgeClient.cs            # Runtime telemetry client
        └── MmlBridge.Runtime.asmdef
```

---

## Contributing

Pull requests are welcome. For major changes, please open an issue first to discuss what you would like to change.

### Adding a new Blender node type

1. **Blender side** (`blender_live_sync_addon.py`): Add the node's properties to `_serialize_node_graph()` if it has node-specific data (like Color Ramp positions)
2. **Unity side** (`MmlShaderGenerator.cs`): Add a `case "YOUR_NODE_TYPE":` in `EmitNode()` that returns HLSL code

The HLSL code receives resolved inputs as `float3` or `float` variables named `v_{nodeName}_{socketName}`.

---

## Changelog

### v0.5.0
- Material sync decoupled from real-time geometry sync (manual **Sync Material** button)
- Removed `transform_only` detection — cleaner architecture, no Blender double-clear bug
- Shader source hash cache: avoids recompilation when node graph is unchanged
- Auto texture import from Blender absolute paths
- Normal map OpenGL→DirectX Y-flip correction
- Persistent HTTP connection to avoid port exhaustion
- WebSocket relay for sub-frame latency in Unity Editor

### v0.4.x
- Initial node graph serialization and HLSL generation
- 40+ Blender node types supported

---

## License

MIT — see [LICENSE](LICENSE)
