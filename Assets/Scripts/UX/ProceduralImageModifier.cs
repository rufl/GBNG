using System;
using UnityEngine;

namespace UnityEngine.UI.ProceduralImage
{
  [AttributeUsage(AttributeTargets.Class)]
  public class ModifierID : Attribute
  {
    private readonly string name;
    public string Name => name;

    public ModifierID(string name)
    {
      this.name = name;
    }
  }

  [DisallowMultipleComponent]
  public abstract class ProceduralImageModifier : MonoBehaviour
  {
    public abstract Vector4 CalculateRadius(Rect imageRect);

    protected void RefreshImage()
    {
      ProceduralImage image = GetComponent<ProceduralImage>();
      if (image) image.SetVerticesDirty();
    }
  }
}
