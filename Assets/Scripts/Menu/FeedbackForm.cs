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

using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public class FeedbackForm : MonoBehaviour
{
  [SerializeField] TMPro.TMP_InputField inputField;
  [SerializeField] UnityEngine.UI.Button sendInput;
  [SerializeField] UnityEngine.UI.Button cancelButton;
  [SerializeField] GameObject mainObject;
  const string FormUrl = "https://docs.google.com/forms/d/e/1FAIpQLSewpreN5kzQlvGGaFeU1-izfoo9v5V7Rf-nT67GrLiK4rK8rQ/formResponse";
  const string FormEntryId = "entry.470414579";

  public void Start()
  {
    inputField.onEndEdit.AddListener(OnEndEdit);
    sendInput.onClick.AddListener(() => SendInput(inputField.text));
    cancelButton.onClick.AddListener(Close);
  }

  void OnEndEdit(string inputstring)
  {
    if (Input.GetButtonDown("Submit"))
    {
      SendInput(inputstring);
    }
  }

  public IEnumerator PostToForm(string input)
  {
    WWWForm form = new WWWForm();
    form.AddField(FormEntryId, input);

    using (UnityWebRequest request = UnityWebRequest.Post(FormUrl, form))
    {
      yield return request.SendWebRequest();
      if (request.result != UnityWebRequest.Result.Success)
      {
        Debug.LogWarning("Could not submit feedback: " + request.error);
      }
    }
  }

  void SendInput(string inputstring)
  {
    if (inputstring == "") return;

    StartCoroutine(PostToForm(inputstring));
    Close();
  }

  public void Close()
  {
    Destroy(mainObject);
  }
}
