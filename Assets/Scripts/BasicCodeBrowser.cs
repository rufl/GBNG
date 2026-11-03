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

using System.Collections.Generic;
using UnityEngine;

public class BasicCodeBrowser : MonoBehaviour, ICodeBrowser
{
  [SerializeField] TMPro.TMP_InputField inputField;
  [SerializeField] TMPro.TMP_Text statusText;
  [SerializeField] TMPro.TMP_Text errorText;
  [SerializeField] GameObject errorPanel;
  private readonly List<int> lineStartPos = new List<int>();
  private string oldText = "";
  private int curLine, curCol;
  private float defaultFontSize;
  private float zoom;

  void Start()
  {
    defaultFontSize = inputField.textComponent.fontSize;
    ApplyZoom();
  }
  void Awake()
  {
    errorPanel.gameObject.SetActive(false);
  }

  public void Show(bool show)
  {
    gameObject.SetActive(show);
  }

  public bool KeyboardHasFocus()
  {
    return inputField.isFocused;
  }

  public void SetCode(string liveCode)
  {
    inputField.text = liveCode;
  }

  public void SetScrollTop(int scrollTop)
  {
    if (inputField.verticalScrollbar == null) return;
    float maxScroll = GetMaxScrollTop();
    inputField.verticalScrollbar.value = maxScroll <= 0f
      ? 1f
      : 1f - Mathf.Clamp(scrollTop, 0f, maxScroll) / maxScroll;
  }

  public void SetReadOnly(bool readOnly)
  {
    inputField.readOnly = readOnly;
  }

  public void CheckIsReadyAsync(System.Action<bool> callback)
  {
    callback.Invoke(true);
  }

  public void GetCodeAsync(System.Action<string> callback)
  {
    callback.Invoke(inputField.text);
  }

  public void GetScrollTopAsync(System.Action<int> callback, System.Action onError)
  {
    if (inputField.verticalScrollbar == null)
    {
      onError.Invoke();
      return;
    }
    callback.Invoke(Mathf.RoundToInt((1f - inputField.verticalScrollbar.value) * GetMaxScrollTop()));
  }

  public void AddError(int baseOneLineNumber, string message)
  {
    errorPanel.SetActive(true);
    errorText.text += baseOneLineNumber + ": " + message + "\n";
  }

  public void ClearErrors()
  {
    errorPanel.SetActive(false);
    errorText.text = "";
  }

  public void ZoomIn()
  {
    SetZoom(zoom + 1f);
  }

  public void ZoomOut()
  {
    SetZoom(zoom - 1f);
  }

  public float GetZoom()
  {
    return zoom;
  }

  public void SetZoom(float newZoom)
  {
    zoom = Mathf.Clamp(newZoom, -8f, 16f);
    ApplyZoom();
  }

  private void ApplyZoom()
  {
    if (inputField != null && inputField.textComponent != null && defaultFontSize > 0f)
    {
      inputField.textComponent.fontSize = defaultFontSize * Mathf.Pow(1.1f, zoom);
    }
  }

  private float GetMaxScrollTop()
  {
    return Mathf.Max(0f, inputField.textComponent.preferredHeight - inputField.textViewport.rect.height);
  }

  void Update()
  {
    UpdateCurLineAndCol();
    statusText.text = gameObject.activeSelf ? "Line " + curLine + " Col " + curCol : "";
  }

  private void UpdateCurLineAndCol()
  {
    if (inputField.text != oldText)
    {
      oldText = inputField.text;
      string[] lines = oldText.Split('\n');
      lineStartPos.Clear();
      int pos = 0;
      foreach (string line in lines)
      {
        lineStartPos.Add(pos);
        pos += line.Length + 1;
      }
    }
    curLine = 0;
    for (int i = 0; i < lineStartPos.Count; i++)
    {
      int lineEndPos = i + 1 < lineStartPos.Count ? lineStartPos[i + 1] : int.MaxValue;
      if (inputField.caretPosition < lineEndPos)
      {
        // We add 1 to convert to human-friendy 1-based index.
        curLine = i + 1;
        curCol = inputField.caretPosition - lineStartPos[i] + 1;
        break;
      }
    }
  }
}
