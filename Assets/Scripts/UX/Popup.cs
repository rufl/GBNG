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
using System.Collections.Generic;
using UnityEngine;

public class Popup : MonoBehaviour
{
  public UnityEngine.UI.Image image;
  readonly RuntimeSpriteOwner ownedSprite = new RuntimeSpriteOwner();

  void Start()
  {
    GetComponent<RectTransform>().anchoredPosition = new Vector2(-30, 30);
  }

  public void Setup(Texture2D texture, float duration)
  {
    SetSprite(texture);
    StartCoroutine(PopupRoutine(duration));
  }

  void SetSprite(Texture2D texture)
  {
    Sprite sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 100);
    if (sprite != null) image.sprite = ownedSprite.Replace(sprite);
  }

  IEnumerator PopupRoutine(float duration)
  {
    yield return new WaitForSecondsRealtime(duration);
    float t = 0;
    while (t < 1)
    {
      t = Mathf.Clamp01(t + Time.unscaledDeltaTime);
      image.color = Color.Lerp(Color.white, Color.clear, t);
      yield return null;
    }

    Destroy(gameObject);
  }

  void OnDestroy()
  {
    StopAllCoroutines();
    ownedSprite.Release();
  }
}
