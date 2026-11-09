using System.Collections.Generic;
using UnityEngine;

internal sealed class RuntimeSpriteCollection
{
  readonly List<Sprite> sprites = new List<Sprite>();

  public Sprite Track(Sprite sprite)
  {
    if (sprite != null) sprites.Add(sprite);
    return sprite;
  }

  public void ReleaseAll()
  {
    for (int i = 0; i < sprites.Count; i++)
    {
      Sprite sprite = sprites[i];
      if (sprite == null) continue;
      if (Application.isPlaying) Object.Destroy(sprite);
      else Object.DestroyImmediate(sprite);
    }
    sprites.Clear();
  }
}
