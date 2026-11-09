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

internal sealed class RuntimeTextureOwner
{
  Texture2D ownedTexture;

  public Texture2D Current
  {
    get { return ownedTexture; }
  }

  public Texture2D Replace(Texture2D texture)
  {
    if (ownedTexture == texture) return ownedTexture;
    Release();
    ownedTexture = texture;
    return ownedTexture;
  }

  public void Release()
  {
    if (ownedTexture == null) return;
    Texture2D texture = ownedTexture;
    ownedTexture = null;
    if (Application.isPlaying) Object.Destroy(texture);
    else Object.DestroyImmediate(texture);
  }
}
