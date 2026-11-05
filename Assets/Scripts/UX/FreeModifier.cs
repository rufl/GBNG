using UnityEngine;
using UnityEngine.UI.ProceduralImage;

[ModifierID("Free")]
public class FreeModifier : ProceduralImageModifier
{
  [SerializeField] private Vector4 radius;

  public Vector4 Radius
  {
    get => radius;
    set
    {
      if (radius == value) return;
      radius = value;
      RefreshImage();
    }
  }

  public override Vector4 CalculateRadius(Rect imageRect) => radius;

  protected void OnValidate()
  {
    radius = new Vector4(
      Mathf.Max(0f, radius.x), Mathf.Max(0f, radius.y),
      Mathf.Max(0f, radius.z), Mathf.Max(0f, radius.w));
    RefreshImage();
  }
}
