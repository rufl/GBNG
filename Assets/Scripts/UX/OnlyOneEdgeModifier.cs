using UnityEngine;
using UnityEngine.UI.ProceduralImage;

[ModifierID("Only One Edge")]
public class OnlyOneEdgeModifier : ProceduralImageModifier
{
  public enum ProceduralImageEdge
  {
    Top,
    Bottom,
    Left,
    Right
  }

  [SerializeField] private float radius;
  [SerializeField] private ProceduralImageEdge side;

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

  public ProceduralImageEdge Side
  {
    get => side;
    set
    {
      if (side == value) return;
      side = value;
      RefreshImage();
    }
  }

  public override Vector4 CalculateRadius(Rect imageRect)
  {
    switch (side)
    {
      case ProceduralImageEdge.Top:
        return new Vector4(radius, radius, 0f, 0f);
      case ProceduralImageEdge.Right:
        return new Vector4(0f, radius, radius, 0f);
      case ProceduralImageEdge.Bottom:
        return new Vector4(0f, 0f, radius, radius);
      case ProceduralImageEdge.Left:
        return new Vector4(radius, 0f, 0f, radius);
      default:
        return Vector4.zero;
    }
  }

  protected void OnValidate() => RefreshImage();
}
