/*
 * Copyright 2019 Google LLC
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     https://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using VoltstroStudios.UnityWebBrowser;
using VoltstroStudios.UnityWebBrowser.Communication;
using VoltstroStudios.UnityWebBrowser.Core.Engines;
using VoltstroStudios.UnityWebBrowser.Input;


public interface ISystemSourceBrowser
{
  bool IsAvailable { get; }
  void AddSystemSource(string contents, string fileName);
}

public sealed class MonacoCodeBrowser : MonoBehaviour, ICodeBrowser, ISystemSourceBrowser, IPointerDownHandler

{
  const float InitializationTimeoutSeconds = 15f;


  [Serializable]
  private sealed class PageMessage
  {
    public string type;
    public string value;
  }

  [Serializable]
  private sealed class CodePayload
  {
    public string code;
  }

  [Serializable]
  private sealed class SystemSourcePayload
  {
    public string contents;
    public string filename;
  }

  [Serializable]
  private sealed class ErrorPayload
  {
    public int line;
    public string message;
  }

  private struct PendingError
  {
    public int line;
    public string message;
  }


  private BasicCodeBrowser fallback;
  private RawImage output;
  private Texture originalTexture;
  private Rect originalUvRect;
  private WebBrowserUIBasic browser;
  private WebBrowserOldInputHandler inputHandler;
  private TCPCommunicationLayer communicationLayer;
  private MonacoAssetServer assetServer;
  private readonly List<Action<bool>> readinessCallbacks = new List<Action<bool>>();
  private readonly List<PendingError> pendingErrors = new List<PendingError>();
  private string code = "";
  private int scrollTop;
  private float zoom;
  private float startTime;
  private bool readOnly;
  private bool pageReady;
  private bool usingFallback;
  private bool fallbackRequested;
  private bool keyboardFocus;
  private bool visible = true;
  private string fallbackReason;

  public bool IsAvailable => pageReady && !usingFallback;

  public void Configure(BasicCodeBrowser basicCodeBrowser)
  {
    fallback = basicCodeBrowser;
    output = GetComponent<RawImage>();
    if (output != null)
    {
      originalTexture = output.texture;
      originalUvRect = output.uvRect;
    }
  }

  private void Update()
  {
    if (fallbackRequested)
    {
      fallbackRequested = false;
      SwitchToFallback(fallbackReason);
    }

    if (usingFallback) return;

    if (browser == null)
    {
      if (assetServer != null)
      {
        SwitchToFallback("The CEF browser surface was destroyed unexpectedly.");
        return;
      }

      if (!visible || output == null) return;

      Rect outputRect = output.rectTransform.rect;
      float canvasScaleFactor = output.canvas != null ? output.canvas.scaleFactor : 1f;
      int width = Mathf.RoundToInt(outputRect.width * canvasScaleFactor);
      int height = Mathf.RoundToInt(outputRect.height * canvasScaleFactor);
      if (width < 2 || height < 2) return;

      startTime = Time.unscaledTime;
      TryStartWebView(width, height);
      if (usingFallback || browser == null) return;
    }
    if (browser.browserClient == null || browser.browserClient.HasDisposed)
    {
      SwitchToFallback("The CEF browser process stopped unexpectedly.");
      return;
    }

    if (!pageReady && Time.unscaledTime - startTime > InitializationTimeoutSeconds)
    {
      SwitchToFallback("The Monaco page did not finish loading in time.");
      return;
    }


    UpdateKeyboardFocus();
  }

  private void TryStartWebView(int width, int height)
  {
    if (fallback == null || output == null)
    {
      SwitchToFallback("The code canvas is missing its fallback editor or RawImage.");
      return;
    }

    try
    {
      string thirdPartyRoot = GetThirdPartyRoot();
      string editorPage = Path.Combine(thirdPartyRoot, "js-editor", "js-editor.html");
      if (!File.Exists(editorPage))
      {
        SwitchToFallback($"The bundled Monaco editor page is missing: {editorPage}");
        return;
      }

      Engine cefEngine = Resources.Load<Engine>("Cef Engine Configuration");
      if (cefEngine == null)
      {
        SwitchToFallback("The free CEF browser engine configuration is missing.");
        return;
      }

      assetServer = new MonacoAssetServer(thirdPartyRoot);
      string editorUrl = assetServer.Start();

      inputHandler = ScriptableObject.CreateInstance<WebBrowserOldInputHandler>();
      communicationLayer = ScriptableObject.CreateInstance<TCPCommunicationLayer>();
      browser = output.gameObject.AddComponent<WebBrowserUIBasic>();
      browser.browserClient.engine = cefEngine;
      browser.browserClient.initialUrl = editorUrl;
      browser.browserClient.communicationLayer = communicationLayer;
      browser.browserClient.windowlessFrameRate = 30;
      browser.browserClient.jsMethodManager.jsMethodsEnable = true;
      browser.browserClient.RegisterJsMethod<string>("OnMonacoMessage", OnMonacoMessage);
      browser.inputHandler = inputHandler;

      // UWB exposes resizing only after startup; set its serialized startup resolution instead.
      string resolutionJson = "{\"resolution\":{\"Width\":" +
        width.ToString(CultureInfo.InvariantCulture) + ",\"Height\":" +
        height.ToString(CultureInfo.InvariantCulture) + "}}";
      JsonUtility.FromJsonOverwrite(resolutionJson, browser.browserClient);
      VoltstroStudios.UnityWebBrowser.Shared.Resolution resolution = browser.browserClient.Resolution;
      if (resolution.Width != width || resolution.Height != height)
      {
        SwitchToFallback("The CEF browser could not use the code canvas resolution.");
        return;
      }

      output.enabled = visible;
    }
    catch (Exception exception)
    {
      SwitchToFallback($"The free Monaco editor could not start: {exception.Message}");
    }
  }

  private void UpdateKeyboardFocus()
  {
    if (!keyboardFocus || EventSystem.current == null) return;
    GameObject selected = EventSystem.current.currentSelectedGameObject;
    if (selected != null && selected != gameObject && !selected.transform.IsChildOf(transform))
    {
      keyboardFocus = false;
    }
  }

  private void OnMonacoMessage(string message)
  {
    try
    {
      PageMessage pageMessage = JsonUtility.FromJson<PageMessage>(message);
      if (pageMessage == null || string.IsNullOrEmpty(pageMessage.type)) return;

      switch (pageMessage.type)
      {
        case "ready":
          FinishPageInitialization();
          break;
        case "code":
          code = pageMessage.value ?? "";
          break;
        case "scroll":
          int parsedScroll;
          if (int.TryParse(pageMessage.value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedScroll))
          {
            scrollTop = Mathf.Max(0, parsedScroll);
          }
          break;
        case "focus":
          keyboardFocus = string.Equals(pageMessage.value, "true", StringComparison.OrdinalIgnoreCase);
          break;
      }
    }
    catch (Exception exception)
    {
      Debug.LogWarning($"Ignoring an invalid Monaco browser message: {exception.Message}");
    }
  }

  private void FinishPageInitialization()
  {
    if (pageReady || usingFallback || browser == null || !browser.browserClient.IsConnected) return;

    pageReady = true;
    if (fallback != null) fallback.gameObject.SetActive(false);
    if (output != null)
    {
      output.texture = browser.browserClient.BrowserTexture;
      output.enabled = visible;
    }

    ExecuteScript("setCode((" + JsonUtility.ToJson(new CodePayload { code = code }) + ").code);");
    ExecuteScript("setReadOnly(" + readOnly.ToString().ToLowerInvariant() + ");");
    ExecuteScript("setScrollTop(" + scrollTop.ToString(CultureInfo.InvariantCulture) + ");");
    ExecuteScript("setZoom(" + zoom.ToString(CultureInfo.InvariantCulture) + ");");
    foreach (PendingError error in pendingErrors)
    {
      AddErrorToPage(error.line, error.message);
    }
    pendingErrors.Clear();

    for (int i = 0; i < readinessCallbacks.Count; i++) readinessCallbacks[i]?.Invoke(true);
    readinessCallbacks.Clear();
  }

  private void RequestFallback(string reason)
  {
    fallbackReason = reason;
    fallbackRequested = true;
  }

  private void SwitchToFallback(string reason)
  {
    if (usingFallback) return;
    usingFallback = true;
    keyboardFocus = false;
    pageReady = false;

    if (!string.IsNullOrEmpty(reason)) Debug.LogWarning(reason + " Using the built-in multiline editor instead.");

    DisposeBrowser();
    assetServer?.Dispose();
    assetServer = null;

    if (output != null)
    {
      output.texture = originalTexture;
      output.uvRect = originalUvRect;
      output.enabled = false;
    }

    if (fallback != null)
    {
      fallback.gameObject.SetActive(true);
      fallback.SetCode(code);
      fallback.SetScrollTop(scrollTop);
      fallback.SetReadOnly(readOnly);
      fallback.SetZoom(zoom);
      fallback.ClearErrors();
      foreach (PendingError error in pendingErrors) fallback.AddError(error.line, error.message);
      fallback.Show(visible);
    }
    pendingErrors.Clear();

    for (int i = 0; i < readinessCallbacks.Count; i++) readinessCallbacks[i]?.Invoke(true);
    readinessCallbacks.Clear();
  }

  private void DisposeBrowser()
  {
    if (browser != null)
    {
      WebBrowserUIBasic activeBrowser = browser;
      browser = null;
      activeBrowser.enabled = false;
      try
      {
        if (activeBrowser.browserClient != null && !activeBrowser.browserClient.HasDisposed)
        {
          activeBrowser.browserClient.Dispose();
        }
      }
      catch (Exception exception)
      {
        Debug.LogWarning($"Could not dispose the CEF browser cleanly: {exception.Message}");
      }
      DestroyUnityObject(activeBrowser);
    }

    if (inputHandler != null)
    {
      DestroyUnityObject(inputHandler);
      inputHandler = null;
    }

    if (communicationLayer != null)
    {
      DestroyUnityObject(communicationLayer);
      communicationLayer = null;
    }
  }

  private static void DestroyUnityObject(UnityEngine.Object target)
  {
    if (target == null) return;
    if (Application.isPlaying) UnityEngine.Object.Destroy(target);
    else UnityEngine.Object.DestroyImmediate(target);
  }

  private void OnEnable()
  {
    keyboardFocus = false;
  }

  private void OnDisable()
  {
    keyboardFocus = false;
  }

  private void OnDestroy()
  {
    DisposeBrowser();
    assetServer?.Dispose();
    assetServer = null;
    if (output != null)
    {
      output.texture = originalTexture;
      output.uvRect = originalUvRect;
    }
  }

  public void Show(bool show)
  {
    visible = show;
    if (!show) keyboardFocus = false;
    if (usingFallback || !pageReady) fallback?.Show(show);

    if (!usingFallback && output != null) output.enabled = show;
  }

  public bool KeyboardHasFocus()
  {
    return usingFallback ? fallback != null && fallback.KeyboardHasFocus() : keyboardFocus;
  }

  public void SetCode(string liveCode)
  {
    code = liveCode ?? "";
    fallback?.SetCode(code);
    if (!usingFallback && pageReady)
    {
      string json = JsonUtility.ToJson(new CodePayload { code = code });
      ExecuteScript("setCode((" + json + ").code);");
    }
  }

  public void SetScrollTop(int top)
  {
    scrollTop = Mathf.Max(0, top);
    fallback?.SetScrollTop(scrollTop);
    if (!usingFallback && pageReady)
    {
      ExecuteScript("setScrollTop(" + scrollTop.ToString(CultureInfo.InvariantCulture) + ");");
    }
  }

  public void SetReadOnly(bool value)
  {
    readOnly = value;
    fallback?.SetReadOnly(value);
    if (!usingFallback && pageReady) ExecuteScript("setReadOnly(" + value.ToString().ToLowerInvariant() + ");");
  }

  public void CheckIsReadyAsync(Action<bool> callback)
  {
    if (usingFallback)
    {
      fallback.CheckIsReadyAsync(callback);
    }
    else if (pageReady)
    {
      callback?.Invoke(true);
    }
    else if (callback != null)
    {
      readinessCallbacks.Add(callback);
    }
  }

  public void GetCodeAsync(Action<string> callback)
  {
    if (usingFallback) fallback.GetCodeAsync(callback);
    else callback?.Invoke(code);
  }

  public void GetScrollTopAsync(Action<int> callback, Action onError)
  {
    if (usingFallback) fallback.GetScrollTopAsync(callback, onError);
    else if (pageReady) callback?.Invoke(scrollTop);
    else onError?.Invoke();
  }

  public void AddError(int baseOneLineNumber, string message)
  {
    if (usingFallback)
    {
      fallback.AddError(baseOneLineNumber, message);
      return;
    }

    if (pageReady) AddErrorToPage(baseOneLineNumber, message);
    else pendingErrors.Add(new PendingError { line = baseOneLineNumber, message = message });
  }

  private void AddErrorToPage(int line, string message)
  {
    string json = JsonUtility.ToJson(new ErrorPayload { line = line, message = message ?? "" });
    ExecuteScript("(function(error) { addError(error.line, error.message); })(" + json + ");");
  }

  public void ClearErrors()
  {
    pendingErrors.Clear();
    fallback?.ClearErrors();
    if (!usingFallback && pageReady) ExecuteScript("clearErrors();");
  }

  public void ZoomIn() { SetZoom(zoom + 1f); }
  public void ZoomOut() { SetZoom(zoom - 1f); }
  public float GetZoom() { return usingFallback && fallback != null ? fallback.GetZoom() : zoom; }

  public void SetZoom(float newZoom)
  {
    zoom = Mathf.Clamp(newZoom, -8f, 16f);
    fallback?.SetZoom(zoom);
    if (!usingFallback && pageReady) ExecuteScript("setZoom(" + zoom.ToString(CultureInfo.InvariantCulture) + ");");
  }

  public void AddSystemSource(string contents, string fileName)
  {
    if (!IsAvailable) return;
    string json = JsonUtility.ToJson(new SystemSourcePayload { contents = contents ?? "", filename = fileName ?? "" });
    ExecuteScript("(function(source) { addSystemSource(source.contents, source.filename); })(" + json + ");");
  }

  private bool ExecuteScript(string script)
  {
    if (browser == null || !pageReady || browser.browserClient == null ||
        browser.browserClient.HasDisposed || !browser.browserClient.ReadySignalReceived ||
        !browser.browserClient.IsConnected)
    {
      return false;
    }

    try
    {
      browser.browserClient.ExecuteJs(script);
      return true;
    }
    catch (Exception exception)
    {
      RequestFallback($"The CEF browser stopped accepting Monaco updates: {exception.Message}");
      return false;
    }
  }

  public void OnPointerDown(PointerEventData eventData)
  {
    if (usingFallback || !pageReady || eventData.button != PointerEventData.InputButton.Left) return;
    keyboardFocus = true;
    if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(gameObject);
  }

  private static string GetThirdPartyRoot()
  {
#if UNITY_EDITOR
    return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "third_party"));
#else
    return Path.Combine(Application.streamingAssetsPath, "third_party");
#endif
  }

}

internal sealed class MonacoAssetServer : IDisposable
{
  private readonly string root;
  private TcpListener listener;

  public MonacoAssetServer(string root)
  {
    this.root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
  }

  public string Start()
  {
    if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
    listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    TcpListener activeListener = listener;
    Task.Run(() => AcceptRequests(activeListener));
    return $"http://127.0.0.1:{port}/js-editor/js-editor.html";
  }

  private async Task AcceptRequests(TcpListener activeListener)
  {
    while (true)
    {
      TcpClient client;
      try { client = await activeListener.AcceptTcpClientAsync().ConfigureAwait(false); }
      catch (ObjectDisposedException) { return; }
      catch (SocketException) { return; }
      ThreadPool.QueueUserWorkItem(ServeRequest, client);
    }
  }

  private void ServeRequest(object state)
  {
    using (TcpClient client = (TcpClient)state)
    using (NetworkStream stream = client.GetStream())
    {
      try
      {
        string requestLine = ReadRequestLine(stream);
        if (string.IsNullOrEmpty(requestLine)) { WriteResponse(stream, 400, "Bad Request", null, null); return; }
        string[] parts = requestLine.Split(' ');
        if (parts.Length < 2 || (parts[0] != "GET" && parts[0] != "HEAD"))
        {
          WriteResponse(stream, 405, "Method Not Allowed", null, null);
          return;
        }

        string requestPath = Uri.UnescapeDataString(parts[1].Split('?')[0]).TrimStart('/');
        string filePath = Path.GetFullPath(Path.Combine(root, requestPath.Replace('/', Path.DirectorySeparatorChar)));
        string rootPrefix = root + Path.DirectorySeparatorChar;
        if (!filePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
          WriteResponse(stream, 403, "Forbidden", null, null);
          return;
        }
        if (!File.Exists(filePath))
        {
          WriteResponse(stream, 404, "Not Found", null, null);
          return;
        }

        FileInfo info = new FileInfo(filePath);
        WriteResponse(stream, 200, "OK", ContentType(filePath), info.Length);
        if (parts[0] == "GET")
        {
          using (FileStream file = File.OpenRead(filePath)) file.CopyTo(stream);
        }
      }
      catch (Exception)
      {
        try { WriteResponse(stream, 500, "Internal Server Error", null, null); }
        catch (Exception) { }
      }
    }
  }

  private static string ReadRequestLine(Stream stream)
  {
    byte[] buffer = new byte[8192];
    int count = 0;
    while (count < buffer.Length)
    {
      int read = stream.Read(buffer, count, buffer.Length - count);
      if (read <= 0) return null;
      count += read;
      if (count >= 4 && buffer[count - 4] == '\r' && buffer[count - 3] == '\n' &&
          buffer[count - 2] == '\r' && buffer[count - 1] == '\n') break;
    }
    int lineEnd = -1;
    for (int i = 0; i < count - 1; i++)
    {
      if (buffer[i] == '\r' && buffer[i + 1] == '\n') { lineEnd = i; break; }
    }
    return lineEnd < 0 ? null : Encoding.ASCII.GetString(buffer, 0, lineEnd);
  }

  private static void WriteResponse(Stream stream, int statusCode, string status, string contentType, long? contentLength)
  {
    string headers = $"HTTP/1.1 {statusCode} {status}\r\n" +
      (contentType == null ? "" : "Content-Type: " + contentType + "\r\n") +
      (contentLength.HasValue ? "Content-Length: " + contentLength.Value.ToString(CultureInfo.InvariantCulture) + "\r\n" : "Content-Length: 0\r\n") +
      "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
    byte[] bytes = Encoding.ASCII.GetBytes(headers);
    stream.Write(bytes, 0, bytes.Length);
  }

  private static string ContentType(string path)
  {
    switch (Path.GetExtension(path).ToLowerInvariant())
    {
      case ".html": return "text/html; charset=utf-8";
      case ".js": return "application/javascript; charset=utf-8";
      case ".css": return "text/css; charset=utf-8";
      case ".json":
      case ".map": return "application/json; charset=utf-8";
      case ".woff": return "font/woff";
      case ".woff2": return "font/woff2";
      case ".ttf": return "font/ttf";
      case ".eot": return "application/vnd.ms-fontobject";
      case ".svg": return "image/svg+xml";
      case ".png": return "image/png";
      default: return "application/octet-stream";
    }
  }

  public void Dispose()
  {
    TcpListener activeListener = listener;
    listener = null;
    activeListener?.Stop();
  }
}
