using UnityEngine;
using UnityEngine.UI.ProceduralImage;

[ModifierID("Uniform")]
public class UniformModifier : ProceduralImageModifier
{
  [SerializeField] private float radius;

  public float Radius
  {
    get => radius;
    set
    {
      if (Mathf.Approximately(radius, value)) return;
      radius = value;
      RefreshImage();
    }
  }

  public override Vector4 CalculateRadius(Rect imageRect)
  {
    return new Vector4(radius, radius, radius, radius);
  }

  protected void OnValidate() => RefreshImage();
}
