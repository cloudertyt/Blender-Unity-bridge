using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

[Serializable]
public class BubCollectPayload
{
    public string player_id;
    public string session_id;
    public string event_type;
    public EventData event_data;
    public string timestamp;
}

[Serializable]
public class EventData
{
    public string source;
    public string scene;
    public string detail;
}

public class BlenderUnityBridgeClient : MonoBehaviour
{
    private const string BaseUrl = "http://127.0.0.1:8000";
    private string _playerId;
    private string _sessionId;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindFirstObjectByType<BlenderUnityBridgeClient>() != null)
        {
            return;
        }

        var go = new GameObject("BlenderUnityBridgeClient");
        DontDestroyOnLoad(go);
        go.AddComponent<BlenderUnityBridgeClient>();
    }

    private void Awake()
    {
        _playerId = SystemInfo.deviceUniqueIdentifier;
        _sessionId = Guid.NewGuid().ToString("N");
    }

    private IEnumerator Start()
    {
        yield return StartCoroutine(CheckHealth());
        yield return StartCoroutine(SendStartupEvent());
    }

    private IEnumerator CheckHealth()
    {
        using var req = UnityWebRequest.Get($"{BaseUrl}/health");
        req.timeout = 5;
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            Debug.Log($"[Blender-Unity-Bridge] Connected: {req.downloadHandler.text}");
        }
        else
        {
            Debug.LogWarning($"[Blender-Unity-Bridge] Health check failed: {req.error}");
        }
    }

    private IEnumerator SendStartupEvent()
    {
        var payload = new BubCollectPayload
        {
            player_id = _playerId,
            session_id = _sessionId,
            event_type = "unity_startup",
            event_data = new EventData
            {
                source = "Unity",
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                detail = "Auto handshake from My project MML",
            },
            timestamp = DateTime.UtcNow.ToString("o"),
        };

        string json = JsonUtility.ToJson(payload);
        byte[] body = Encoding.UTF8.GetBytes(json);

        using var req = new UnityWebRequest($"{BaseUrl}/collect", "POST");
        req.uploadHandler = new UploadHandlerRaw(body);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 5;

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            Debug.Log($"[Blender-Unity-Bridge] Event sent: {req.downloadHandler.text}");
        }
        else
        {
            Debug.LogWarning($"[Blender-Unity-Bridge] Event send failed: {req.error}");
        }
    }
}
