using UnityEngine;
using UnityEngine.UI.ProceduralImage;

[ModifierID("Round")]
public class RoundModifier : ProceduralImageModifier
{
  public override Vector4 CalculateRadius(Rect imageRect)
  {
    float radius = Mathf.Min(imageRect.width, imageRect.height) * 0.5f;
    return new Vector4(radius, radius, radius, radius);
  }
}
