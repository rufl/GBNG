using UnityEngine;

internal sealed class RuntimeSpriteOwner
{
  Sprite ownedSprite;

  public Sprite Replace(Sprite sprite)
  {
    if (ownedSprite == sprite) return ownedSprite;
    Release();
    ownedSprite = sprite;
    return ownedSprite;
  }

  public void Release()
  {
    if (ownedSprite == null) return;
    Sprite sprite = ownedSprite;
    ownedSprite = null;
    if (Application.isPlaying) Object.Destroy(sprite);
    else Object.DestroyImmediate(sprite);
  }
}
