using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

[Serializable]
public class A1111Txt2ImgRequest
{
    public string prompt;
    public string negative_prompt;
    public int steps = 20;
    public int width = 512;
    public int height = 512;
    public float cfg_scale = 7f;
    public int seed = -1;
    public string sampler_name = "DPM++ 2M";  // A1111 autocorrects "DPM++ 2M Karras" → "DPM++ 2M"
    public bool tiling = false;
    public bool do_not_save_samples = true;
    public bool do_not_save_grid = true;
    public bool save_images = false;
    public A1111OverrideSettings override_settings;
    public bool override_settings_restore_afterwards = false;
}

[Serializable]
public class A1111OverrideSettings
{
    public string sd_model_checkpoint;
}

[Serializable]
public class A1111Txt2ImgResponse
{
    public string[] images;
}

public class StableDiffusion : MonoBehaviour
{
    public string baseUrl = "http://127.0.0.1:7860";
    [Tooltip("Seconds to wait for a single SD response. 0 = Unity default (no limit).")]
    public int requestTimeoutSeconds = 120;
    public bool useInMemoryCache = true;

    private static readonly Dictionary<int, Texture2D> RequestCache = new Dictionary<int, Texture2D>();

    // FIX: Allow callers to invalidate the cache when a new generation run starts,
    //      so that re-using the same prompt doesn't silently return stale textures.
    public static void ClearCache() => RequestCache.Clear();

    /// <summary>
    /// Quick reachability check — GET /sdapi/v1/progress with a 10-second coroutine timeout.
    /// Uses manual poll loop because www.timeout is unreliable in the Unity Editor.
    /// Calls onResult(true) if reachable, onResult(false) with an error message otherwise.
    /// </summary>
    public IEnumerator CheckReachable(Action<bool, string> onResult)
    {
        var url = $"{baseUrl}/sdapi/v1/progress";
        Debug.Log($"[SD] Checking A1111 reachability at {url}...");
        var www = new UnityWebRequest(url, "GET");
        www.downloadHandler = new DownloadHandlerBuffer();

        bool aborted = false;
        float startTime = Time.realtimeSinceStartup;
        var op = www.SendWebRequest();
        while (!op.isDone)
        {
            if (Time.realtimeSinceStartup - startTime >= 10f)
            {
                aborted = true;
                www.Abort();
                break;
            }
            yield return null;
        }

        bool ok = !aborted && www.result == UnityWebRequest.Result.Success;
        if      (aborted) Debug.LogError($"[SD] A1111 not reachable at {baseUrl}: timed out after 10s");
        else if (!ok)     Debug.LogError($"[SD] A1111 not reachable: {www.error} (code {www.responseCode})");
        else              Debug.Log($"[SD] A1111 reachable (code {www.responseCode})");
        string errMsg = ok ? null : (aborted ? "timed out after 10s" : $"{www.error} (code {www.responseCode})");
        www.Dispose();
        onResult?.Invoke(ok, errMsg);
    }

    public IEnumerator Txt2Img(A1111Txt2ImgRequest req, Action<Texture2D> onDone, Action<string> onError)
    {
        int cacheKey = BuildCacheKey(req);
        if (useInMemoryCache && RequestCache.TryGetValue(cacheKey, out var cachedTexture) && cachedTexture != null)
        {
            Debug.Log($"[SD] In-memory cache HIT (key={cacheKey})");
            onDone?.Invoke(cachedTexture);
            yield break;
        }

        var url = $"{baseUrl}/sdapi/v1/txt2img";
        string promptPreview = req.prompt != null && req.prompt.Length > 120
            ? req.prompt.Substring(0, 120) + "…"
            : req.prompt ?? "";
        Debug.Log($"[SD] → POST {url} | {req.width}x{req.height} steps={req.steps} " +
                  $"model=\"{req.override_settings?.sd_model_checkpoint}\" " +
                  $"prompt=\"{promptPreview}\"");

        var json = JsonUtility.ToJson(req);
        var body = Encoding.UTF8.GetBytes(json);

        var www = new UnityWebRequest(url, "POST");
        www.uploadHandler   = new UploadHandlerRaw(body);
        www.downloadHandler = new DownloadHandlerBuffer();
        www.SetRequestHeader("Content-Type", "application/json");
        if (requestTimeoutSeconds > 0)
            www.timeout = requestTimeoutSeconds;

        float startTime = UnityEngine.Time.realtimeSinceStartup;
        bool  aborted   = false;
        float nextHeartbeatAt = 10f;
        var op = www.SendWebRequest();
        while (!op.isDone)
        {
            float waitingSeconds = UnityEngine.Time.realtimeSinceStartup - startTime;
            if (requestTimeoutSeconds > 0 && waitingSeconds >= requestTimeoutSeconds)
            {
                aborted = true;
                www.Abort();
                break;
            }

            if (waitingSeconds >= nextHeartbeatAt)
            {
                Debug.Log($"[SD] Still waiting for A1111 response... {waitingSeconds:F0}s elapsed");
                nextHeartbeatAt += 10f;
            }
            yield return null;
        }

        float elapsed = UnityEngine.Time.realtimeSinceStartup - startTime;
        Debug.Log($"[SD] ← Response in {elapsed:F1}s: aborted={aborted}, result={www.result}, code={www.responseCode}");

        if (aborted)
        {
            Debug.LogError($"[SD] Request TIMED OUT after {requestTimeoutSeconds}s — " +
                           $"A1111 may be overloaded or frozen. Check {baseUrl}.");
            www.Dispose();
            onError?.Invoke($"SD request timed out after {requestTimeoutSeconds}s");
            yield break;
        }

        if (www.result != UnityWebRequest.Result.Success)
        {
            string detail = www.downloadHandler?.text;
            if (!string.IsNullOrEmpty(detail) && detail.Length > 300) detail = detail.Substring(0, 300) + "...";
            Debug.LogError($"[SD] Request FAILED — {www.error} (code {www.responseCode}) | body: {detail}");
            string errMsg = $"A1111 error {www.responseCode}: {www.error}";
            www.Dispose();
            onError?.Invoke(errMsg);
            yield break;
        }

        string responseBody = www.downloadHandler?.text;
        www.Dispose();

        A1111Txt2ImgResponse resp;
        try
        {
            resp = JsonUtility.FromJson<A1111Txt2ImgResponse>(responseBody);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SD] Failed to parse JSON response: {ex.Message}");
            onError?.Invoke($"SD JSON parse error: {ex.Message}");
            yield break;
        }

        if (resp?.images == null || resp.images.Length == 0)
        {
            Debug.LogError("[SD] Response has no images[]!");
            onError?.Invoke("A1111 returned empty images[]");
            yield break;
        }

        Debug.Log($"[SD] Decoding image ({resp.images[0].Length} base64 chars)...");
        Texture2D tex;
        try
        {
            var png = Convert.FromBase64String(resp.images[0]);
            tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            // HideAndDontSave: prevent Unity's asset GC from destroying this
            // runtime texture (causes the "everything disappears" visual glitch).
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.LoadImage(png, false);
            tex.filterMode = FilterMode.Point;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SD] Failed to decode image: {ex.Message}");
            onError?.Invoke($"SD image decode error: {ex.Message}");
            yield break;
        }

        Debug.Log($"[SD] Texture ready: {tex.width}x{tex.height}");

        if (useInMemoryCache)
            RequestCache[cacheKey] = tex;

        onDone?.Invoke(tex);
    }

    private static int BuildCacheKey(A1111Txt2ImgRequest req)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (req.prompt?.GetHashCode() ?? 0);
            hash = hash * 31 + (req.negative_prompt?.GetHashCode() ?? 0);
            hash = hash * 31 + req.steps;
            hash = hash * 31 + req.width;
            hash = hash * 31 + req.height;
            hash = hash * 31 + req.cfg_scale.GetHashCode();
            hash = hash * 31 + req.seed;
            hash = hash * 31 + (req.sampler_name?.GetHashCode() ?? 0);
            hash = hash * 31 + req.tiling.GetHashCode();
            hash = hash * 31 + req.do_not_save_samples.GetHashCode();
            hash = hash * 31 + req.do_not_save_grid.GetHashCode();
            hash = hash * 31 + req.save_images.GetHashCode();
            hash = hash * 31 + (req.override_settings?.sd_model_checkpoint?.GetHashCode() ?? 0);
            hash = hash * 31 + req.override_settings_restore_afterwards.GetHashCode();
            return hash;
        }
    }
}