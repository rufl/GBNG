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

using UnityEngine;

// This abstraction exposes the built-in code editor to the rest of the app.
public class CodeBrowserWrapper : MonoBehaviour
{
  bool initialized;
  ICodeBrowser codeBrowser;
  [SerializeField] BasicCodeBrowser basicCodeBrowser;

  private void LazyInitialize()
  {
    Debug.Assert(basicCodeBrowser != null, "basicCodeBrowser property not set in CodeBrowserWrapper");
    initialized = true;
    MonacoCodeBrowser monacoBrowser = gameObject.AddComponent<MonacoCodeBrowser>();
    monacoBrowser.Configure(basicCodeBrowser);
    codeBrowser = monacoBrowser;
  }

  public ICodeBrowser GetCodeBrowser()
  {
    if (!initialized)
    {
      LazyInitialize();
    }
    return codeBrowser;
  }
}

public interface ICodeBrowser
{
  void Show(bool show);
  bool KeyboardHasFocus();
  void SetCode(string liveCode);
  void SetScrollTop(int scrollTop);
  void SetReadOnly(bool readOnly);
  void CheckIsReadyAsync(System.Action<bool> callback);
  void GetCodeAsync(System.Action<string> callback);
  void GetScrollTopAsync(System.Action<int> scrollTop, System.Action onError);
  void AddError(int baseOneLineNumber, string message);
  void ClearErrors();
  void ZoomIn();
  void ZoomOut();
  float GetZoom();
  void SetZoom(float zoom);
}