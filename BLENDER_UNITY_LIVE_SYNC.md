# Unity Connection (One-Click Mode)

## Changed Components
- Bridge server: `D:\Code-2\MML_Bridge\mml_bridge_server.py`
- Blender add-on: `D:\Code-2\MML_Bridge\blender_live_sync_addon.py`
- Unity editor sync script: `D:\UnityHub\My project MML\Assets\Scripts\MMLBridge\Editor\MmlBlenderLiveSyncEditor.cs`

## One-Click Behavior
1. Click `Unity Connection` in Blender top bar:
   - If bridge server is offline, add-on auto starts it.
   - Then it calls `/sync/connection/connect`.
2. Click the same button again:
   - Calls `/sync/connection/disconnect`.
3. Only when connected, Blender publishes sync events and Unity imports updates.
4. World-space mapping is enforced:
   - Blender FBX export: `axis_forward=-Z`, `axis_up=Y`, `global_scale=1`
   - Unity importer: `useFileScale=false`, `globalScale=1`, `bakeAxisConversion=true`

## Blender Setup
1. Install add-on file:
   - `D:\Code-2\MML_Bridge\blender_live_sync_addon.py`
2. Enable add-on: `Unity Connection`
3. Optional settings in side panel (`3D View > Sidebar > MML Sync`):
   - `Server URL`: `http://127.0.0.1:8000`
   - `Auto Start Bridge`: enabled
   - `Bridge Start Script`: `D:\Code-2\MML_Bridge\start_mml_bridge.ps1`
   - `Bridge Python`: `D:\Python\python.exe`
   - `Debounce (s)`: `0.25` (default, near realtime)

## Unity Setup
- Open project: `D:\UnityHub\My project MML`
- Keep `MML Bridge > Live Sync` enabled.
- Recommended poll: `0.25s` for near realtime.
- Unity polls:
  - `/sync/connection/status`
  - `/sync/latest`

## API (Current)
- `POST /sync/connection/connect`
- `POST /sync/connection/disconnect`
- `GET /sync/connection/status`
- `POST /sync/publish`
- `GET /sync/latest`
