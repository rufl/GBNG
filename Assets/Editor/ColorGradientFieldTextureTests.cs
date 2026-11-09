#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class ColorGradientFieldTextureTests
{
  const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

  [Test]
  public void GradientPreviewReusesTextureAndReleasesItOnResizeAndTeardown()
  {
    GameObject owner = new GameObject("Gradient preview texture test");
    owner.SetActive(false);
    RawImage image = owner.AddComponent<RawImage>();
    ColorGradientFieldUI gradient = owner.AddComponent<ColorGradientFieldUI>();
    FieldInfo imageField = typeof(ColorGradientFieldUI).GetField("gradientImage", PrivateInstance);
    Assert.IsNotNull(imageField);
    imageField.SetValue(gradient, image);
    Texture2D first = null;
    Texture2D current = null;

    try
    {
      first = GetTexture(gradient, 16);
      Assert.AreSame(first, GetTexture(gradient, 16), "Repeated redraws at the same width must reuse the texture.");
      image.texture = first;

      current = GetTexture(gradient, 32);
      Assert.AreNotSame(first, current);
      Assert.IsTrue(first == null, "Changing preview width must release the previous texture.");
      Assert.IsNull(image.texture, "The replaced texture must be detached from the RawImage.");
      Assert.AreEqual(32, current.width);

      image.texture = current;
      Invoke(gradient, "OnDestroy");
      Assert.IsTrue(current == null, "Tearing down the field must release its owned texture.");
      Assert.IsNull(image.texture, "Tearing down the field must detach its owned texture.");
    }
    finally
    {
      if (owner != null) Object.DestroyImmediate(owner);
      if (first != null) Object.DestroyImmediate(first);
      if (current != null) Object.DestroyImmediate(current);
    }
  }

  [Test]
  public void GradientPreviewTeardownPreservesCallerAssignedTexture()
  {
    GameObject owner = new GameObject("Gradient external texture test");
    owner.SetActive(false);
    RawImage image = owner.AddComponent<RawImage>();
    ColorGradientFieldUI gradient = owner.AddComponent<ColorGradientFieldUI>();
    typeof(ColorGradientFieldUI).GetField("gradientImage", PrivateInstance).SetValue(gradient, image);
    Texture2D generated = null;
    Texture2D external = new Texture2D(1, 1, TextureFormat.RGBA32, false);

    try
    {
      generated = GetTexture(gradient, 8);
      image.texture = external;
      Invoke(gradient, "OnDestroy");

      Assert.IsTrue(generated == null, "The field still releases its generated texture.");
      Assert.AreSame(external, image.texture, "Teardown must not clear a texture owned by another component.");
      Assert.IsNotNull(external);
    }
    finally
    {
      if (owner != null) Object.DestroyImmediate(owner);
      if (generated != null) Object.DestroyImmediate(generated);
      if (external != null) Object.DestroyImmediate(external);
    }
  }

  static Texture2D GetTexture(ColorGradientFieldUI gradient, int width)
  {
    return (Texture2D)Invoke(gradient, "GetOrCreateGradientTexture", width);
  }

  static object Invoke(object target, string methodName, params object[] arguments)
  {
    MethodInfo method = target.GetType().GetMethod(methodName, PrivateInstance);
    Assert.IsNotNull(method, "Missing method " + methodName);
    return method.Invoke(target, arguments);
  }
}
#endif
