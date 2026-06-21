using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Sends a sprite PNG to a local rembg HTTP server and receives a
/// background-removed RGBA PNG in return.
///
/// Local setup:
///   pip install rembg[gpu] pillow
///   rembg s --host 127.0.0.1 --port 7000 --no-ui
///
/// Unity will POST generated sprite PNGs to:
///   http://127.0.0.1:7000/api/remove
///
/// If the server is unavailable, the project falls back to TextureUtils cleanup.
/// </summary>
public class RembgClient : MonoBehaviour
{
    [Header("Rembg Server")]
    [SerializeField] private string baseUrl = "http://127.0.0.1:7000";
    [SerializeField] private bool enableRembg = true;
    [SerializeField] private float timeoutSeconds = 25f;

    [Header("Alpha Matting (optional)")]
    [Tooltip("Send alpha_matting fields to rembg server. If the server version does not support them, a single retry without them is performed.")]
    [SerializeField] private bool addAlphaMattingFields = true;

    public bool IsEnabled => enableRembg;
    public string BaseUrl  => baseUrl;

    /// <summary>
    /// Coroutine: sends <paramref name="source"/> to the rembg server and returns a
    /// background-removed texture through <paramref name="onSuccess"/>.
    /// Calls <paramref name="onError"/> with an explanation on any failure.
    /// </summary>
    public IEnumerator RemoveBackground(
        Texture2D source,
        System.Action<Texture2D> onSuccess,
        System.Action<string>    onError)
    {
        if (!enableRembg)
        {
            onError?.Invoke("rembg disabled");
            yield break;
        }

        if (source == null)
        {
            onError?.Invoke("source texture is null");
            yield break;
        }

        byte[] pngBytes;
        try
        {
            pngBytes = source.EncodeToPNG();
        }
        catch (System.Exception ex)
        {
            onError?.Invoke($"rembg: failed to encode source PNG: {ex.Message}");
            yield break;
        }

        if (pngBytes == null || pngBytes.Length == 0)
        {
            onError?.Invoke("rembg: EncodeToPNG returned empty bytes.");
            yield break;
        }

        string url = baseUrl.TrimEnd('/') + "/api/remove";

        // First attempt (with optional alpha matting fields)
        bool success = false;
        string lastError = null;
        Texture2D result = null;

        yield return SendRequest(url, pngBytes, addAlphaMattingFields, r => result = r, e => lastError = e);

        if (result != null)
        {
            success = true;
        }
        else if (lastError != null && addAlphaMattingFields && lastError.StartsWith("400"))
        {
            // Server returned 400 — possibly does not accept alpha matting fields.
            // Retry once without them.
            Debug.LogWarning("[RembgClient] Server returned 400 with alpha matting fields. Retrying without them...");
            result     = null;
            lastError  = null;
            yield return SendRequest(url, pngBytes, false, r => result = r, e => lastError = e);

            if (result != null)
                success = true;
        }

        if (success && result != null)
        {
            onSuccess?.Invoke(result);
        }
        else
        {
            onError?.Invoke(lastError ?? "rembg: unknown error");
        }
    }

    private IEnumerator SendRequest(
        string url,
        byte[] pngBytes,
        bool withAlphaMatting,
        System.Action<Texture2D> onResult,
        System.Action<string>    onError)
    {
        var form = new List<IMultipartFormSection>
        {
            new MultipartFormFileSection("file", pngBytes, "input.png", "image/png")
        };

        if (withAlphaMatting)
        {
            form.Add(new MultipartFormDataSection("alpha_matting", "true"));
            form.Add(new MultipartFormDataSection("a", "true"));
        }

        using var www = UnityWebRequest.Post(url, form);
        www.timeout = Mathf.CeilToInt(timeoutSeconds);

        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            string prefix = www.responseCode > 0 ? $"{www.responseCode} " : "";
            onError?.Invoke($"{prefix}{www.error}");
            yield break;
        }

        byte[] responseBytes = www.downloadHandler.data;
        if (responseBytes == null || responseBytes.Length == 0)
        {
            onError?.Invoke("rembg: empty response body.");
            yield break;
        }

        Texture2D tex;
        try
        {
            tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.name       = "rembg_result";
            tex.hideFlags  = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode   = TextureWrapMode.Clamp;

            if (!tex.LoadImage(responseBytes, false))
            {
                Object.Destroy(tex);
                onError?.Invoke("rembg: LoadImage failed on response bytes.");
                yield break;
            }
        }
        catch (System.Exception ex)
        {
            onError?.Invoke($"rembg: exception loading response image: {ex.Message}");
            yield break;
        }

        onResult?.Invoke(tex);
    }
}
