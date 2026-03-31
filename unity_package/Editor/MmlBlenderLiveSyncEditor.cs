using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

[Serializable]
public class MmlConnectionStatusResponse
{
    public bool ok;
    public bool connected;
    public string source;
    public string source_blend;
    public string note;
    public string updated_at;
}

[Serializable]
public class MmlSnapshotLatestResponse
{
    public bool ok;
    public MmlSnapshotEvent latest_event;
    public MmlSnapshotEvent latestEvent;
}

[Serializable]
public class MmlSnapshotEvent
{
    public int id;
    public int eventId;
    public string asset_name;
    public string assetName;
    public string source_blend;
    public string sourceBlend;
    public string exported_at;
    public string exportedAt;
    public string payload_json;
    public string payloadJson;
    public string metadata_json;
    public string metadataJson;
    public string created_at;
    public string createdAt;
    public MmlSnapshotPayload snapshot;
}

[Serializable]
public class MmlSnapshotPayload
{
    public MmlSnapshotObject[] objects;
}

[Serializable]
public class MmlMaterialData
{
    public string material_name;
    public float[] base_color;
    public float metallic;
    public float roughness;
    public float[] emission;
    public float emission_strength;
}

// ── Node Graph Data Classes ──────────────────────────────────────────────────

[Serializable]
public class MmlNodeInputData
{
    public string name;
    public string type;   // VALUE | RGBA | VECTOR | SHADER
    public float  dv;     // default float (VALUE)
    public float[] dc;    // default color/vector [r,g,b,a] or [x,y,z]
}

[Serializable]
public class MmlNodeData
{
    public string name;        // ASCII-safe id, e.g. "N0_BSDF_PRINCIPLED"
    public string type;        // Blender node type
    public string op;          // operation / mode / noise_dimensions / gradient_type / etc.
    public string blend;       // blend_type / bands_direction / extension
    public string cr_interp;   // color ramp interpolation / wave profile
    public bool   use_clamp;
    public float[] cr_pos;     // color ramp stop positions
    public float[] cr_col;     // color ramp stop colors (4 floats per stop, RGBA)
    public string img_name;    // image texture: image name
    public string img_path;    // image texture: absolute path on Blender machine
    public MmlNodeInputData[] inputs;
}

[Serializable]
public class MmlNodeLinkData
{
    public string fn;  // from_node safe name
    public string fs;  // from_socket name
    public string tn;  // to_node safe name
    public string ts;  // to_socket name
}

[Serializable]
public class MmlNodeGraphData
{
    public MmlNodeData[]     nodes;
    public MmlNodeLinkData[] links;
    public string            output_node;
}

// ────────────────────────────────────────────────────────────────────────────

[Serializable]
public class MmlSnapshotObject
{
    public string object_name;
    public string objectName;
    public float[] position;
    public float[] rotation;
    public float[] scale;
    public float[] vertices;
    public int[] triangles;
    public float[] normals;
    public float[] uv;
    public MmlMaterialData  material;
    public MmlNodeGraphData node_graph;
}

[InitializeOnLoad]
public static class MmlBlenderLiveSyncEditor
{
    private static readonly Regex EventIdRegex = new Regex("\\\"id\\\"\\s*:\\s*(?<v>\\d+)", RegexOptions.Compiled);
    private static readonly Regex EventIdCamelRegex = new Regex("\\\"eventId\\\"\\s*:\\s*(?<v>\\d+)", RegexOptions.Compiled);
    private static readonly Regex PayloadJsonRegex = new Regex("\\\"payload_json\\\"\\s*:\\s*\\\"(?<v>(?:\\\\\\.|[^\\\"\\\\])*)\\\"", RegexOptions.Compiled);
    private static readonly Regex PayloadJsonCamelRegex = new Regex("\\\"payloadJson\\\"\\s*:\\s*\\\"(?<v>(?:\\\\\\.|[^\\\"\\\\])*)\\\"", RegexOptions.Compiled);

    private enum RequestKind
    {
        None = 0,
        ConnectionStatus = 1,
        LatestSync = 2,
    }

    private const string MenuRoot = "MML Bridge/Live Sync/";
    private const string PrefEnabled = "MML.LiveSync.Enabled";
    private const string PrefServerUrl = "MML.LiveSync.ServerUrl";
    private const string PrefPollSeconds = "MML.LiveSync.PollSeconds";
    private const string PrefLastEventId = "MML.LiveSync.LastSnapshotEventId";
    private const string RootObjectName = "MML_LiveSync_Root";
    private const string ChildPrefix = "MML_LiveSync_";

    private static UnityWebRequest _activeRequest;
    private static RequestKind _activeRequestKind = RequestKind.None;
    private static double _nextPollAt;
    private static bool _connected;
    private static int _connectionStatusFailureCount;
    private static int _pollStep;
    private static Material _defaultMaterial;

    private static readonly object WsQueueLock = new object();
    private static readonly Queue<string> WsMessageQueue = new Queue<string>();
    private static ClientWebSocket _wsClient;
    private static CancellationTokenSource _wsCts;
    private static Task _wsTask;
    private static volatile bool _wsConnected;
    private static volatile bool _wsConnecting;
    private static double _nextWsConnectAt;
    private static string _lastWsError = string.Empty;
    private static string _lastWsErrorLogged = string.Empty;
    private static bool _wsConnectedLogState;
    private const int ConnectionFailureThreshold = 3;

    static MmlBlenderLiveSyncEditor()
    {
        EditorApplication.update += OnEditorUpdate;
        AssemblyReloadEvents.beforeAssemblyReload += StopWebSocketReceiver;
    }

    private static bool Enabled
    {
        get => EditorPrefs.GetBool(PrefEnabled, true);
        set => EditorPrefs.SetBool(PrefEnabled, value);
    }

    private static string ServerUrl
    {
        get => EditorPrefs.GetString(PrefServerUrl, "http://127.0.0.1:8000");
        set => EditorPrefs.SetString(PrefServerUrl, value.TrimEnd('/'));
    }

    private static float PollSeconds
    {
        get => Mathf.Clamp(EditorPrefs.GetFloat(PrefPollSeconds, 0.25f), 0.05f, 10f);
        set => EditorPrefs.SetFloat(PrefPollSeconds, Mathf.Clamp(value, 0.05f, 10f));
    }

    private static int LastEventId
    {
        get => EditorPrefs.GetInt(PrefLastEventId, 0);
        set => EditorPrefs.SetInt(PrefLastEventId, value);
    }

    [MenuItem(MenuRoot + "Enable")]
    private static void Enable() => Enabled = true;

    [MenuItem(MenuRoot + "Disable")]
    private static void Disable() => Enabled = false;

    [MenuItem(MenuRoot + "Settings")]
    private static void OpenSettings() => MmlBlenderLiveSyncWindow.Open();

    [MenuItem(MenuRoot + "Reset Last Event ID")]
    private static void ResetLastEventId() => LastEventId = 0;

    private static void OnEditorUpdate()
    {
        if (!Enabled)
        {
            StopWebSocketReceiver();
            return;
        }

        DrainWebSocketMessages();
        UpdateWebSocketStateLog();

        if (_activeRequest != null)
        {
            if (_activeRequest.isDone)
            {
                HandleRequestDone(_activeRequestKind, _activeRequest);
                _activeRequest.Dispose();
                _activeRequest = null;
                _activeRequestKind = RequestKind.None;
            }
            return;
        }

        if (EditorApplication.timeSinceStartup < _nextPollAt)
        {
            return;
        }

        _nextPollAt = EditorApplication.timeSinceStartup + PollSeconds;

        if (_pollStep == 0)
        {
            StartConnectionStatusRequest();
            _pollStep = 1;
            return;
        }

        if (_connected)
        {
            EnsureWebSocketConnection();
            if (!_wsConnected)
            {
                StartLatestRequest();
            }
        }
        else
        {
            StopWebSocketReceiver();
        }

        _pollStep = 0;
    }

    private static void StartConnectionStatusRequest()
    {
        _activeRequest = UnityWebRequest.Get($"{ServerUrl}/sync/connection/status");
        _activeRequest.timeout = 2;
        _activeRequestKind = RequestKind.ConnectionStatus;
        _activeRequest.SendWebRequest();
    }

    private static void StartLatestRequest()
    {
        _activeRequest = UnityWebRequest.Get($"{ServerUrl}/sync/latest_snapshot?since_id={LastEventId}");
        _activeRequest.timeout = 2;
        _activeRequestKind = RequestKind.LatestSync;
        _activeRequest.SendWebRequest();
    }

    private static void HandleRequestDone(RequestKind kind, UnityWebRequest req)
    {
        if (req.result != UnityWebRequest.Result.Success)
        {
            if (kind == RequestKind.ConnectionStatus)
            {
                _connectionStatusFailureCount++;
                if (_connectionStatusFailureCount >= ConnectionFailureThreshold)
                {
                    if (_connected)
                    {
                        Debug.LogWarning("[MML Live Sync] Connection status check failed repeatedly; marking disconnected.");
                    }
                    _connected = false;
                    StopWebSocketReceiver();
                }
            }
            return;
        }

        switch (kind)
        {
            case RequestKind.ConnectionStatus:
                HandleConnectionStatus(req.downloadHandler.text);
                break;
            case RequestKind.LatestSync:
                HandleLatestResponse(req.downloadHandler.text);
                break;
        }
    }

    private static void HandleConnectionStatus(string json)
    {
        MmlConnectionStatusResponse response;
        try
        {
            response = JsonUtility.FromJson<MmlConnectionStatusResponse>(json);
        }
        catch
        {
            _connectionStatusFailureCount++;
            if (_connectionStatusFailureCount >= ConnectionFailureThreshold)
            {
                _connected = false;
                StopWebSocketReceiver();
            }
            return;
        }

        if (response == null)
        {
            _connectionStatusFailureCount++;
            if (_connectionStatusFailureCount >= ConnectionFailureThreshold)
            {
                _connected = false;
                StopWebSocketReceiver();
            }
            return;
        }

        _connectionStatusFailureCount = 0;
        bool previous = _connected;
        _connected = response.connected;

        if (_connected != previous)
        {
            Debug.Log($"[MML Live Sync] Connection state changed: {(_connected ? "Connected" : "Disconnected")}");
            if (!_connected)
            {
                StopWebSocketReceiver();
            }
        }
    }

    private static void HandleLatestResponse(string json)
    {
        // AssetDatabase / EditorUtility APIs cannot be used during Play Mode
        if (EditorApplication.isPlaying)
        {
            return;
        }

        MmlSnapshotLatestResponse response;
        try
        {
            response = JsonUtility.FromJson<MmlSnapshotLatestResponse>(json);
        }
        catch
        {
            return;
        }

        if (response == null)
        {
            return;
        }

        MmlSnapshotEvent syncEvent = SelectBestEvent(response.latestEvent, response.latest_event);
        syncEvent = PopulateFromRawJsonIfNeeded(syncEvent, json);
        if (syncEvent == null || syncEvent.snapshot == null)
        {
            return;
        }

        int eventId = GetEventId(syncEvent);
        if (eventId <= 0)
        {
            return;
        }
        if (eventId <= LastEventId)
        {
            return;
        }

        ApplySnapshotEvent(syncEvent);
        LastEventId = Mathf.Max(LastEventId, eventId);
    }

    private static int GetEventId(MmlSnapshotEvent syncEvent)
    {
        if (syncEvent == null)
        {
            return 0;
        }

        if (syncEvent.id > 0)
        {
            return syncEvent.id;
        }

        return syncEvent.eventId;
    }

    private static MmlSnapshotEvent SelectBestEvent(MmlSnapshotEvent first, MmlSnapshotEvent second)
    {
        if (first != null)
        {
            return first;
        }

        return second;
    }

    private static MmlSnapshotEvent PopulateFromRawJsonIfNeeded(MmlSnapshotEvent syncEvent, string rawJson)
    {
        if (syncEvent == null)
        {
            syncEvent = new MmlSnapshotEvent();
        }

        if (GetEventId(syncEvent) <= 0)
        {
            int id = ExtractInt(rawJson, EventIdRegex);
            if (id <= 0)
            {
                id = ExtractInt(rawJson, EventIdCamelRegex);
            }
            if (id > 0)
            {
                syncEvent.id = id;
            }
        }

        if (syncEvent.snapshot == null)
        {
            string payload = ExtractString(rawJson, PayloadJsonRegex) ?? ExtractString(rawJson, PayloadJsonCamelRegex);
            if (!string.IsNullOrWhiteSpace(payload))
            {
                try
                {
                    syncEvent.snapshot = JsonUtility.FromJson<MmlSnapshotPayload>(payload);
                }
                catch
                {
                    syncEvent.snapshot = null;
                }
            }
        }

        return syncEvent;
    }

    private static int ExtractInt(string rawJson, Regex regex)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return 0;
        }

        Match match = regex.Match(rawJson);
        if (!match.Success)
        {
            return 0;
        }

        if (int.TryParse(match.Groups["v"].Value, out int value))
        {
            return value;
        }

        return 0;
    }

    private static string ExtractString(string rawJson, Regex regex)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        Match match = regex.Match(rawJson);
        if (!match.Success)
        {
            return null;
        }

        string value = match.Groups["v"].Value;
        value = value.Replace("\\\\", "\\");
        value = value.Replace("\\/", "/");
        value = value.Replace("\\\"", "\"");
        return value;
    }

    private static MmlSnapshotObject[] GetObjects(MmlSnapshotEvent syncEvent)
    {
        if (syncEvent == null || syncEvent.snapshot == null || syncEvent.snapshot.objects == null)
        {
            return Array.Empty<MmlSnapshotObject>();
        }

        return syncEvent.snapshot.objects;
    }

    private static void ApplySnapshotEvent(MmlSnapshotEvent syncEvent)
    {
        MmlSnapshotObject[] objects = GetObjects(syncEvent);
        Scene activeScene = SceneManager.GetActiveScene();
        if (!activeScene.IsValid() || !activeScene.isLoaded)
        {
            return;
        }

        GameObject root = GetOrCreateRoot(activeScene);
        root.transform.position = Vector3.zero;
        root.transform.rotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;

        var existingChildren = new Dictionary<string, Transform>(StringComparer.Ordinal);
        for (int i = 0; i < root.transform.childCount; i++)
        {
            Transform child = root.transform.GetChild(i);
            existingChildren[child.name] = child;
        }

        var keepNames = new HashSet<string>(StringComparer.Ordinal);
        bool changed = false;
        bool anyMaterialDirty = false;   // ★ batch SaveAssets

        foreach (var obj in objects)
        {
            string objectName = GetObjectName(obj);
            if (string.IsNullOrWhiteSpace(objectName))
            {
                continue;
            }

            string childName = ChildPrefix + MakeSafeObjectName(objectName);
            keepNames.Add(childName);

            Transform child;
            if (!existingChildren.TryGetValue(childName, out child) || child == null)
            {
                var go = new GameObject(childName);
                child = go.transform;
                child.SetParent(root.transform, false);
                changed = true;
            }

            ApplyTransform(child, obj);
            if (ApplyMesh(child.gameObject, childName, obj, ref anyMaterialDirty))
                changed = true;
        }

        // ★ Single SaveAssets for the entire snapshot (was once-per-material)
        if (anyMaterialDirty)
            AssetDatabase.SaveAssets();

        for (int i = root.transform.childCount - 1; i >= 0; i--)
        {
            Transform child = root.transform.GetChild(i);
            if (!keepNames.Contains(child.name))
            {
                UnityEngine.Object.DestroyImmediate(child.gameObject);
                changed = true;
            }
        }

        if (changed)
        {
            EditorSceneManager.MarkSceneDirty(activeScene);
        }

        Debug.Log($"[MML Live Sync] Snapshot applied (event {GetEventId(syncEvent)}, objects {objects.Length})");
    }

    private static GameObject GetOrCreateRoot(Scene activeScene)
    {
        GameObject[] roots = activeScene.GetRootGameObjects();
        foreach (GameObject root in roots)
        {
            if (root != null && root.name == RootObjectName)
            {
                return root;
            }
        }

        var created = new GameObject(RootObjectName);
        SceneManager.MoveGameObjectToScene(created, activeScene);
        return created;
    }

    private static string GetObjectName(MmlSnapshotObject obj)
    {
        if (obj == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(obj.objectName))
        {
            return obj.objectName;
        }

        return obj.object_name;
    }

    private static string MakeSafeObjectName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unnamed";
        }

        foreach (char c in new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' })
        {
            value = value.Replace(c, '_');
        }

        return value.Trim();
    }

    private static void ApplyTransform(Transform transform, MmlSnapshotObject obj)
    {
        transform.localPosition = ReadVector3(obj.position, Vector3.zero);
        transform.localRotation = ReadQuaternion(obj.rotation, Quaternion.identity);
        transform.localScale = ReadVector3(obj.scale, Vector3.one);
    }

    private static bool ApplyMesh(GameObject target, string meshName, MmlSnapshotObject obj,
                                  ref bool anyMaterialDirty)
    {
        var filter = target.GetComponent<MeshFilter>();
        if (filter == null)
        {
            filter = target.AddComponent<MeshFilter>();
        }

        var renderer = target.GetComponent<MeshRenderer>();
        if (renderer == null)
        {
            renderer = target.AddComponent<MeshRenderer>();
        }

        if (obj.node_graph != null && obj.node_graph.nodes != null && obj.node_graph.nodes.Length > 0)
        {
            string matName = (obj.material != null && !string.IsNullOrEmpty(obj.material.material_name))
                ? obj.material.material_name : meshName;
            ApplyNodeGraphMaterial(renderer, obj.node_graph, matName);
            anyMaterialDirty = true;
        }
        else if (obj.material != null && !string.IsNullOrEmpty(obj.material.material_name))
        {
            ApplyMaterial(renderer, obj.material);
            anyMaterialDirty = true;
        }
        else if (renderer.sharedMaterial == null)
        {
            renderer.sharedMaterial = GetDefaultMaterial();
        }

        Mesh mesh = filter.sharedMesh;
        if (mesh == null)
        {
            mesh = new Mesh();
            mesh.name = meshName;
            filter.sharedMesh = mesh;
        }

        Vector3[] vertices = ReadVertices(obj.vertices);
        int[] triangles = ReadTriangles(obj.triangles, vertices.Length);
        Vector3[] normals = ReadNormals(obj.normals, vertices.Length);
        Vector2[] uv = ReadUv(obj.uv, vertices.Length);

        mesh.Clear();
        mesh.indexFormat = vertices.Length > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.vertices = vertices;
        mesh.triangles = triangles;

        if (normals != null && normals.Length == vertices.Length)
        {
            mesh.normals = normals;
        }
        else
        {
            mesh.RecalculateNormals();
        }

        if (uv != null && uv.Length == vertices.Length)
        {
            mesh.uv = uv;
        }

        mesh.RecalculateBounds();
        EditorUtility.SetDirty(mesh);
        return true;
    }

    private static void ApplyNodeGraphMaterial(MeshRenderer renderer, MmlNodeGraphData graph, string matName)
    {
        try
        {
            Shader shader = MmlShaderGenerator.GetOrGenerateShader(graph, matName);
            if (shader == null)
            {
                Debug.LogWarning($"[MML Live Sync] Shader generation failed for '{matName}', falling back.");
                return;
            }
            string safeName  = MakeSafeObjectName(matName);
            string matPath   = $"Assets/Materials/MML_LiveSync_{safeName}.mat";
            if (!AssetDatabase.IsValidFolder("Assets/Materials"))
                AssetDatabase.CreateFolder("Assets", "Materials");

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(shader) { name = matName };
                AssetDatabase.CreateAsset(mat, matPath);
            }
            else if (mat.shader != shader)
            {
                mat.shader = shader;
            }
            // Copy image texture assets referenced by the generator
            MmlShaderGenerator.AssignTextureProperties(mat, graph);

            EditorUtility.SetDirty(mat);
            renderer.sharedMaterial = mat;
            EditorUtility.SetDirty(renderer);
            Debug.Log($"[MML Live Sync] Node-graph shader applied: {shader.name}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MML Live Sync] ApplyNodeGraphMaterial error: {ex.Message}");
        }
    }

    private static void ApplyMaterial(MeshRenderer renderer, MmlMaterialData matData)
    {
        Debug.Log($"[MML Live Sync] ApplyMaterial: name={matData.material_name}, has_base_color={matData.base_color != null}, metallic={matData.metallic}, roughness={matData.roughness}");
        string safeName = MakeSafeObjectName(matData.material_name);
        string matPath = $"Assets/Materials/MML_LiveSync_{safeName}.mat";

        if (!AssetDatabase.IsValidFolder("Assets/Materials"))
        {
            AssetDatabase.CreateFolder("Assets", "Materials");
        }

        Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
            Debug.Log($"[MML Live Sync] Creating material with shader: {(shader != null ? shader.name : "NULL")}");
            mat = new Material(shader) { name = matData.material_name };
            AssetDatabase.CreateAsset(mat, matPath);
        }

        if (matData.base_color != null && matData.base_color.Length >= 3)
        {
            float a = matData.base_color.Length >= 4 ? matData.base_color[3] : 1f;
            Color c = new Color(matData.base_color[0], matData.base_color[1], matData.base_color[2], a);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Color"))     mat.SetColor("_Color", c);
        }

        if (mat.HasProperty("_Metallic"))
            mat.SetFloat("_Metallic", matData.metallic);
        if (mat.HasProperty("_Smoothness"))
            mat.SetFloat("_Smoothness", 1f - matData.roughness);
        if (mat.HasProperty("_Glossiness"))
            mat.SetFloat("_Glossiness", 1f - matData.roughness);

        if (matData.emission != null && matData.emission.Length >= 3 && matData.emission_strength > 0f)
        {
            mat.EnableKeyword("_EMISSION");
            Color ec = new Color(matData.emission[0], matData.emission[1], matData.emission[2]) * matData.emission_strength;
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", ec);
        }

        // Force Opaque surface type (URP defaults can be Transparent, causing see-through look)
        if (mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 0f); // 0 = Opaque, 1 = Transparent
            mat.SetOverrideTag("RenderType", "Opaque");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
            mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetShaderPassEnabled("ShadowCaster", true);
        }

        EditorUtility.SetDirty(mat);
        renderer.sharedMaterial = mat;
        EditorUtility.SetDirty(renderer);
        Debug.Log($"[MML Live Sync] Material applied: {mat.name}, shader={mat.shader.name}");
    }

    private static Material GetDefaultMaterial()
    {
        if (_defaultMaterial != null)
        {
            return _defaultMaterial;
        }

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
        {
            shader = Shader.Find("Standard");
        }

        if (shader == null)
        {
            shader = Shader.Find("Sprites/Default");
        }

        if (shader == null)
        {
            shader = Shader.Find("Hidden/InternalErrorShader");
        }

        _defaultMaterial = new Material(shader);
        _defaultMaterial.name = "MML_LiveSync_DefaultMat";
        _defaultMaterial.hideFlags = HideFlags.DontSave;
        return _defaultMaterial;
    }

    private static Vector3 ReadVector3(float[] values, Vector3 fallback)
    {
        if (values == null || values.Length < 3)
        {
            return fallback;
        }

        return new Vector3(values[0], values[1], values[2]);
    }

    private static Quaternion ReadQuaternion(float[] values, Quaternion fallback)
    {
        if (values == null || values.Length < 4)
        {
            return fallback;
        }

        return new Quaternion(values[0], values[1], values[2], values[3]);
    }

    private static Vector3[] ReadVertices(float[] values)
    {
        if (values == null || values.Length < 3)
        {
            return Array.Empty<Vector3>();
        }

        int count = values.Length / 3;
        var result = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            int offset = i * 3;
            result[i] = new Vector3(values[offset], values[offset + 1], values[offset + 2]);
        }

        return result;
    }

    private static int[] ReadTriangles(int[] values, int vertexCount)
    {
        if (values == null || values.Length < 3)
        {
            return Array.Empty<int>();
        }

        int triCount = values.Length - (values.Length % 3);
        var result = new List<int>(triCount);
        for (int i = 0; i < triCount; i++)
        {
            int index = values[i];
            if (index < 0 || index >= vertexCount)
            {
                continue;
            }
            result.Add(index);
        }

        int remainder = result.Count % 3;
        if (remainder != 0)
        {
            result.RemoveRange(result.Count - remainder, remainder);
        }

        return result.ToArray();
    }

    private static Vector3[] ReadNormals(float[] values, int vertexCount)
    {
        if (values == null || values.Length < vertexCount * 3)
        {
            return null;
        }

        var result = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            int offset = i * 3;
            result[i] = new Vector3(values[offset], values[offset + 1], values[offset + 2]);
        }

        return result;
    }

    private static Vector2[] ReadUv(float[] values, int vertexCount)
    {
        if (values == null || values.Length < vertexCount * 2)
        {
            return null;
        }

        var result = new Vector2[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            int offset = i * 2;
            result[i] = new Vector2(values[offset], values[offset + 1]);
        }

        return result;
    }

    private static void EnsureWebSocketConnection()
    {
        if (_wsConnected || _wsConnecting)
        {
            return;
        }

        if (EditorApplication.timeSinceStartup < _nextWsConnectAt)
        {
            return;
        }

        _nextWsConnectAt = EditorApplication.timeSinceStartup + 1.0;
        _wsConnecting = true;
        _lastWsError = string.Empty;

        _wsCts = new CancellationTokenSource();
        string wsUrl = BuildWebSocketUrl(ServerUrl) + "/ws/sync_snapshot";
        _wsTask = Task.Run(async () => await RunWebSocketLoop(wsUrl, _wsCts.Token));
    }

    private static async Task RunWebSocketLoop(string wsUrl, CancellationToken token)
    {
        ClientWebSocket client = new ClientWebSocket();
        _wsClient = client;

        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(3));
                await client.ConnectAsync(new Uri(wsUrl), connectCts.Token);
            }
            _wsConnected = true;
            _wsConnecting = false;
            _lastWsError = string.Empty;

            byte[] buffer = new byte[64 * 1024];
            var segment = new ArraySegment<byte>(buffer);

            while (!token.IsCancellationRequested && client.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await client.ReceiveAsync(segment, token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                string message = Encoding.UTF8.GetString(ms.ToArray());
                lock (WsQueueLock)
                {
                    WsMessageQueue.Enqueue(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _lastWsError = ex.Message;
        }
        finally
        {
            _wsConnected = false;
            _wsConnecting = false;

            try
            {
                client.Dispose();
            }
            catch
            {
            }

            _wsClient = null;
        }
    }

    private static void StopWebSocketReceiver()
    {
        if (_wsCts != null)
        {
            try
            {
                _wsCts.Cancel();
            }
            catch
            {
            }
        }

        if (_wsClient != null)
        {
            try
            {
                _wsClient.Abort();
                _wsClient.Dispose();
            }
            catch
            {
            }
            _wsClient = null;
        }

        _wsConnected = false;
        _wsConnecting = false;

        if (_wsConnectedLogState)
        {
            _wsConnectedLogState = false;
            Debug.Log("[MML Live Sync] Snapshot WS disconnected.");
        }
    }

    private static void DrainWebSocketMessages()
    {
        int processed = 0;
        while (processed < 8)
        {
            string message = null;
            lock (WsQueueLock)
            {
                if (WsMessageQueue.Count > 0)
                {
                    message = WsMessageQueue.Dequeue();
                }
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                break;
            }

            HandleLatestResponse(message);
            processed++;
        }
    }

    private static void UpdateWebSocketStateLog()
    {
        if (_wsConnected != _wsConnectedLogState)
        {
            _wsConnectedLogState = _wsConnected;
            if (_wsConnected)
            {
                _lastWsErrorLogged = string.Empty;
                Debug.Log("[MML Live Sync] Snapshot WS connected.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_lastWsError))
                {
                    Debug.Log("[MML Live Sync] Snapshot WS disconnected.");
                }
                else
                {
                    Debug.LogWarning($"[MML Live Sync] Snapshot WS disconnected: {_lastWsError}");
                    _lastWsErrorLogged = _lastWsError;
                }
            }
        }

        if (!_wsConnected && !_wsConnecting && !string.IsNullOrWhiteSpace(_lastWsError) && _lastWsError != _lastWsErrorLogged)
        {
            _lastWsErrorLogged = _lastWsError;
            Debug.LogWarning($"[MML Live Sync] Snapshot WS error: {_lastWsError}");
        }
    }

    private static string BuildWebSocketUrl(string httpUrl)
    {
        if (httpUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "wss://" + httpUrl.Substring("https://".Length).TrimEnd('/');
        }

        if (httpUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return "ws://" + httpUrl.Substring("http://".Length).TrimEnd('/');
        }

        if (httpUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) || httpUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            return httpUrl.TrimEnd('/');
        }

        return "ws://" + httpUrl.TrimEnd('/');
    }

    public static bool GetEnabled() => Enabled;
    public static string GetServerUrl() => ServerUrl;
    public static float GetPollSeconds() => PollSeconds;

    public static void SetServerUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        ServerUrl = value;
        _nextWsConnectAt = 0;
        StopWebSocketReceiver();
    }

    public static void SetPollSeconds(float value) => PollSeconds = value;
    public static void SetEnabled(bool value)
    {
        Enabled = value;
        if (!value)
        {
            StopWebSocketReceiver();
        }
    }
}

public class MmlBlenderLiveSyncWindow : EditorWindow
{
    private string _serverUrl;
    private float _pollSeconds;
    private bool _enabled;

    public static void Open()
    {
        var window = GetWindow<MmlBlenderLiveSyncWindow>("MML Live Sync");
        window.minSize = new Vector2(360, 120);
        window.LoadValues();
        window.Show();
    }

    private void OnEnable()
    {
        LoadValues();
    }

    private void LoadValues()
    {
        _serverUrl = MmlBlenderLiveSyncEditor.GetServerUrl();
        _pollSeconds = MmlBlenderLiveSyncEditor.GetPollSeconds();
        _enabled = MmlBlenderLiveSyncEditor.GetEnabled();
    }

    private void OnGUI()
    {
        GUILayout.Label("Bridge Settings", EditorStyles.boldLabel);
        _enabled = EditorGUILayout.Toggle("Enabled", _enabled);
        _serverUrl = EditorGUILayout.TextField("Server URL", _serverUrl);
        _pollSeconds = EditorGUILayout.Slider("Fallback Poll (seconds)", _pollSeconds, 0.05f, 10f);

        GUILayout.Space(8);
        if (GUILayout.Button("Save"))
        {
            MmlBlenderLiveSyncEditor.SetEnabled(_enabled);
            MmlBlenderLiveSyncEditor.SetServerUrl(_serverUrl);
            MmlBlenderLiveSyncEditor.SetPollSeconds(_pollSeconds);
        }
    }
}
