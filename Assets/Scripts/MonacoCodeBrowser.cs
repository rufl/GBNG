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
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
#endif
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
using WebViewToolkit;
using WebViewToolkit.Native;
#endif

public interface ISystemSourceBrowser
{
  bool IsAvailable { get; }
  void AddSystemSource(string contents, string fileName);
}

public sealed class MonacoCodeBrowser : MonoBehaviour, ICodeBrowser, ISystemSourceBrowser,
  IPointerDownHandler, IPointerUpHandler, IPointerMoveHandler, IDragHandler,
  IPointerExitHandler, IScrollHandler
{
  const float InitializationTimeoutSeconds = 15f;
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
  static readonly KeyMapping[] KeyMappings = CreateKeyMappings();
#endif

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

#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
  private struct KeyMapping
  {
    public KeyCode key;
    public uint virtualKey;
    public bool printable;

    public KeyMapping(KeyCode key, uint virtualKey, bool printable)
    {
      this.key = key;
      this.virtualKey = virtualKey;
      this.printable = printable;
    }
  }
#endif

  private BasicCodeBrowser fallback;
  private RawImage output;
  private Texture originalTexture;
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
  private WebViewInstance webView;
  private MonacoAssetServer assetServer;
  private WebViewManager manager;
#endif
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
    if (output != null) originalTexture = output.texture;
  }

  private void Start()
  {
    startTime = Time.unscaledTime;
    TryStartWebView();
  }

  private void TryStartWebView()
  {
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
    if (fallback == null || output == null)
    {
      SwitchToFallback("The code canvas is missing its fallback editor or RawImage.");
      return;
    }

    if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11 &&
        SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D12)
    {
      SwitchToFallback("The free embedded browser requires Direct3D 11 or Direct3D 12.");
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

      assetServer = new MonacoAssetServer(thirdPartyRoot);
      string editorUrl = assetServer.Start();
      manager = WebViewManager.Instance;
      if (manager == null || !manager.IsInitialized)
      {
        SwitchToFallback("The free WebView2 runtime could not initialize.");
        return;
      }

      webView = manager.CreateWebView(1280, 720, editorUrl, false);
      if (webView == null || webView.Texture == null)
      {
        SwitchToFallback("The free WebView2 runtime could not create a browser surface.");
        return;
      }

      webView.NavigationCompleted += OnNavigationCompleted;
      webView.MessageReceived += OnMessageReceived;
      output.texture = webView.Texture;
      output.enabled = visible;
    }
    catch (Exception exception)
    {
      SwitchToFallback($"The free Monaco editor could not start: {exception.Message}");
    }
#else
    SwitchToFallback("The rich Monaco editor is currently supported on Windows builds only.");
#endif
  }

  private void Update()
  {
    if (fallbackRequested)
    {
      fallbackRequested = false;
      SwitchToFallback(fallbackReason);
    }

    if (usingFallback) return;
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
    if (webView == null) return;

    manager?.Tick();

    if (!pageReady && Time.unscaledTime - startTime > InitializationTimeoutSeconds)
    {
      SwitchToFallback("The Monaco page did not finish loading in time.");
      return;
    }

    UpdateKeyboardFocus();
    if (pageReady && keyboardFocus) ForwardKeyboardInput();
#endif
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

#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
  private void ForwardKeyboardInput()
  {
    string typed = Input.inputString;
    if (!string.IsNullOrEmpty(typed))
    {
      StringBuilder text = null;
      for (int i = 0; i < typed.Length; i++)
      {
        char character = typed[i];
        if (char.IsControl(character)) continue;
        if (text == null) text = new StringBuilder(typed.Length);
        text.Append(character);
      }

      if (text != null && text.Length > 0)
      {
        string json = JsonUtility.ToJson(new CodePayload { code = text.ToString() });
        ExecuteScript($"editor.trigger('keyboard', 'type', {{ text: ({json}).code }});");
      }
    }

    bool shortcutModifier = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
      Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) || Input.GetKey(KeyCode.AltGr);
    bool systemKey = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) || Input.GetKey(KeyCode.AltGr);

    foreach (KeyMapping mapping in KeyMappings)
    {
      if (mapping.printable && !shortcutModifier) continue;

      bool keyDown = Input.GetKeyDown(mapping.key);
      bool keyUp = Input.GetKeyUp(mapping.key);
      if (!keyDown && !keyUp) continue;

      uint scanCode = MapVirtualKeyCode(mapping.virtualKey);
      webView.SendKeyEvent(mapping.virtualKey, scanCode, keyDown, systemKey);
    }
  }
#endif

  private void OnNavigationCompleted(string url, bool succeeded)
  {
    if (!succeeded)
    {
      RequestFallback("The local Monaco page could not be opened.");
    }
  }

#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
  private void OnMessageReceived(string message)
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
    if (pageReady || usingFallback || webView == null) return;

    pageReady = true;
    if (fallback != null) fallback.gameObject.SetActive(false);
    if (output != null)
    {
      output.texture = webView.Texture;
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
#endif

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

#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
    if (webView != null)
    {
      webView.NavigationCompleted -= OnNavigationCompleted;
      webView.MessageReceived -= OnMessageReceived;
      try { webView.Dispose(); }
      catch (Exception exception) { Debug.LogWarning($"Could not dispose the embedded browser cleanly: {exception.Message}"); }
      webView = null;
    }
    if (assetServer != null)
    {
      assetServer.Dispose();
      assetServer = null;
    }
#endif

    if (output != null)
    {
      output.texture = originalTexture;
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
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
    if (webView != null)
    {
      webView.NavigationCompleted -= OnNavigationCompleted;
      webView.MessageReceived -= OnMessageReceived;
      try { webView.Dispose(); }
      catch (Exception exception) { Debug.LogWarning($"Could not dispose the embedded browser cleanly: {exception.Message}"); }
      webView = null;
    }
    assetServer?.Dispose();
#endif
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
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
    return webView != null && !webView.IsDestroyed && webView.ExecuteScript(script);
#else
    return false;
#endif
  }

  public void OnPointerDown(PointerEventData eventData)
  {
    if (usingFallback) return;
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
    SendMouseEvent(MouseEventType.Down, ToMouseButton(eventData.button), eventData.position);
    if (eventData.button == PointerEventData.InputButton.Left)
    {
      keyboardFocus = true;
      if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(gameObject);
    }
#endif
  }

  public void OnPointerUp(PointerEventData eventData)
  {
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
    if (!usingFallback) SendMouseEvent(MouseEventType.Up, ToMouseButton(eventData.button), eventData.position);
#endif
  }

  public void OnPointerMove(PointerEventData eventData)
  {
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
    if (!usingFallback) SendMouseEvent(MouseEventType.Move, MouseButton.None, eventData.position);
#endif
  }

  public void OnDrag(PointerEventData eventData)
  {
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
    if (!usingFallback) SendMouseEvent(MouseEventType.Move, ToMouseButton(eventData.button), eventData.position);
#endif
  }

  public void OnPointerExit(PointerEventData eventData)
  {
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
    if (!usingFallback && webView != null)
    {
      webView.SendMouseEvent(MouseEventType.Leave, MouseButton.None, 0.5f, 0.5f);
    }
#endif
  }

  public void OnScroll(PointerEventData eventData)
  {
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
    if (!usingFallback) SendMouseEvent(MouseEventType.Wheel, MouseButton.None, eventData.position, -eventData.scrollDelta.y);
#endif
  }

#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
  private void SendMouseEvent(MouseEventType eventType, MouseButton button, Vector2 screenPosition, float wheelDelta = 0f)
  {
    if (webView == null || webView.IsDestroyed || output == null) return;
    Canvas canvas = output.canvas;
    Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
    Vector2 localPosition;
    if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(output.rectTransform, screenPosition, camera, out localPosition)) return;

    Rect rect = output.rectTransform.rect;
    if (rect.width <= 0f || rect.height <= 0f) return;
    float x = Mathf.Clamp01((localPosition.x - rect.xMin) / rect.width);
    float y = Mathf.Clamp01(1f - (localPosition.y - rect.yMin) / rect.height);
    webView.SendMouseEvent(eventType, button, x, y, wheelDelta);
  }

  private static MouseButton ToMouseButton(PointerEventData.InputButton button)
  {
    switch (button)
    {
      case PointerEventData.InputButton.Left: return MouseButton.Left;
      case PointerEventData.InputButton.Right: return MouseButton.Right;
      case PointerEventData.InputButton.Middle: return MouseButton.Middle;
      default: return MouseButton.None;
    }
  }
#endif

  private static string GetThirdPartyRoot()
  {
#if UNITY_EDITOR
    return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "third_party"));
#else
    return Path.Combine(Application.streamingAssetsPath, "third_party");
#endif
  }

#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
  [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
  private static extern uint MapVirtualKeyCodeNative(uint virtualKey, uint mapType);
#endif

  private static uint MapVirtualKeyCode(uint virtualKey)
  {
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
    return MapVirtualKeyCodeNative(virtualKey, 4);
#else
    return 0;
#endif
  }

#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
  private static KeyMapping[] CreateKeyMappings()
  {
    return new[]
    {
      new KeyMapping(KeyCode.A, 0x41, true), new KeyMapping(KeyCode.B, 0x42, true),
      new KeyMapping(KeyCode.C, 0x43, true), new KeyMapping(KeyCode.D, 0x44, true),
      new KeyMapping(KeyCode.E, 0x45, true), new KeyMapping(KeyCode.F, 0x46, true),
      new KeyMapping(KeyCode.G, 0x47, true), new KeyMapping(KeyCode.H, 0x48, true),
      new KeyMapping(KeyCode.I, 0x49, true), new KeyMapping(KeyCode.J, 0x4A, true),
      new KeyMapping(KeyCode.K, 0x4B, true), new KeyMapping(KeyCode.L, 0x4C, true),
      new KeyMapping(KeyCode.M, 0x4D, true), new KeyMapping(KeyCode.N, 0x4E, true),
      new KeyMapping(KeyCode.O, 0x4F, true), new KeyMapping(KeyCode.P, 0x50, true),
      new KeyMapping(KeyCode.Q, 0x51, true), new KeyMapping(KeyCode.R, 0x52, true),
      new KeyMapping(KeyCode.S, 0x53, true), new KeyMapping(KeyCode.T, 0x54, true),
      new KeyMapping(KeyCode.U, 0x55, true), new KeyMapping(KeyCode.V, 0x56, true),
      new KeyMapping(KeyCode.W, 0x57, true), new KeyMapping(KeyCode.X, 0x58, true),
      new KeyMapping(KeyCode.Y, 0x59, true), new KeyMapping(KeyCode.Z, 0x5A, true),
      new KeyMapping(KeyCode.Alpha0, 0x30, true), new KeyMapping(KeyCode.Alpha1, 0x31, true),
      new KeyMapping(KeyCode.Alpha2, 0x32, true), new KeyMapping(KeyCode.Alpha3, 0x33, true),
      new KeyMapping(KeyCode.Alpha4, 0x34, true), new KeyMapping(KeyCode.Alpha5, 0x35, true),
      new KeyMapping(KeyCode.Alpha6, 0x36, true), new KeyMapping(KeyCode.Alpha7, 0x37, true),
      new KeyMapping(KeyCode.Alpha8, 0x38, true), new KeyMapping(KeyCode.Alpha9, 0x39, true),
      new KeyMapping(KeyCode.Space, 0x20, true),
      new KeyMapping(KeyCode.Comma, 0xBC, true), new KeyMapping(KeyCode.Minus, 0xBD, true),
      new KeyMapping(KeyCode.Period, 0xBE, true), new KeyMapping(KeyCode.Slash, 0xBF, true),
      new KeyMapping(KeyCode.Semicolon, 0xBA, true), new KeyMapping(KeyCode.Equals, 0xBB, true),
      new KeyMapping(KeyCode.LeftBracket, 0xDB, true), new KeyMapping(KeyCode.Backslash, 0xDC, true),
      new KeyMapping(KeyCode.RightBracket, 0xDD, true), new KeyMapping(KeyCode.Quote, 0xDE, true),
      new KeyMapping(KeyCode.BackQuote, 0xC0, true),
      new KeyMapping(KeyCode.Keypad0, 0x60, true), new KeyMapping(KeyCode.Keypad1, 0x61, true),
      new KeyMapping(KeyCode.Keypad2, 0x62, true), new KeyMapping(KeyCode.Keypad3, 0x63, true),
      new KeyMapping(KeyCode.Keypad4, 0x64, true), new KeyMapping(KeyCode.Keypad5, 0x65, true),
      new KeyMapping(KeyCode.Keypad6, 0x66, true), new KeyMapping(KeyCode.Keypad7, 0x67, true),
      new KeyMapping(KeyCode.Keypad8, 0x68, true), new KeyMapping(KeyCode.Keypad9, 0x69, true),
      new KeyMapping(KeyCode.KeypadPeriod, 0x6E, true), new KeyMapping(KeyCode.KeypadDivide, 0x6F, true),
      new KeyMapping(KeyCode.KeypadMultiply, 0x6A, true), new KeyMapping(KeyCode.KeypadMinus, 0x6D, true),
      new KeyMapping(KeyCode.KeypadPlus, 0x6B, true),
      new KeyMapping(KeyCode.Backspace, 0x08, false), new KeyMapping(KeyCode.Tab, 0x09, false),
      new KeyMapping(KeyCode.Return, 0x0D, false), new KeyMapping(KeyCode.KeypadEnter, 0x0D, false),
      new KeyMapping(KeyCode.Escape, 0x1B, false), new KeyMapping(KeyCode.Delete, 0x2E, false),
      new KeyMapping(KeyCode.Insert, 0x2D, false), new KeyMapping(KeyCode.Home, 0x24, false),
      new KeyMapping(KeyCode.End, 0x23, false), new KeyMapping(KeyCode.PageUp, 0x21, false),
      new KeyMapping(KeyCode.PageDown, 0x22, false), new KeyMapping(KeyCode.LeftArrow, 0x25, false),
      new KeyMapping(KeyCode.UpArrow, 0x26, false), new KeyMapping(KeyCode.RightArrow, 0x27, false),
      new KeyMapping(KeyCode.DownArrow, 0x28, false),
      new KeyMapping(KeyCode.LeftShift, 0xA0, false), new KeyMapping(KeyCode.RightShift, 0xA1, false),
      new KeyMapping(KeyCode.LeftControl, 0xA2, false), new KeyMapping(KeyCode.RightControl, 0xA3, false),
      new KeyMapping(KeyCode.LeftAlt, 0xA4, false), new KeyMapping(KeyCode.RightAlt, 0xA5, false),
      new KeyMapping(KeyCode.AltGr, 0xA5, false), new KeyMapping(KeyCode.CapsLock, 0x14, false),
      new KeyMapping(KeyCode.Numlock, 0x90, false), new KeyMapping(KeyCode.ScrollLock, 0x91, false),
      new KeyMapping(KeyCode.F1, 0x70, false), new KeyMapping(KeyCode.F2, 0x71, false),
      new KeyMapping(KeyCode.F3, 0x72, false), new KeyMapping(KeyCode.F4, 0x73, false),
      new KeyMapping(KeyCode.F5, 0x74, false), new KeyMapping(KeyCode.F6, 0x75, false),
      new KeyMapping(KeyCode.F7, 0x76, false), new KeyMapping(KeyCode.F8, 0x77, false),
      new KeyMapping(KeyCode.F9, 0x78, false), new KeyMapping(KeyCode.F10, 0x79, false),
      new KeyMapping(KeyCode.F11, 0x7A, false), new KeyMapping(KeyCode.F12, 0x7B, false)
    };
  }
#endif
}

#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && UNITY_64)
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
#endif
