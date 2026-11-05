using System;
using UnityEngine;
using UnityEngine.UI;

namespace UnityEngine.UI.ProceduralImage
{
  /// <summary>
  /// Sprite-compatible rounded UI image. Corner data is sent per vertex so images remain batchable.
  /// </summary>
  [ExecuteInEditMode]
  [AddComponentMenu("UI/Procedural Image")]
  public class ProceduralImage : Image
  {
    private const string ShaderName = "UI/Procedural UI Image";
    private static Material sharedMaterial;
    private static Sprite emptySprite;

    [SerializeField] private float borderWidth;
    [SerializeField] private float falloffDistance = 1f;
    private ProceduralImageModifier modifier;

    public float BorderWidth
    {
      get => borderWidth;
      set
      {
        if (Mathf.Approximately(borderWidth, value)) return;
        borderWidth = value;
        SetVerticesDirty();
      }
    }

    public float FalloffDistance
    {
      get => falloffDistance;
      set
      {
        if (Mathf.Approximately(falloffDistance, value)) return;
        falloffDistance = value;
        SetVerticesDirty();
      }
    }

    protected ProceduralImageModifier Modifier
    {
      get
      {
        if (!modifier) modifier = GetComponent<ProceduralImageModifier>();
        if (!modifier) modifier = gameObject.AddComponent<FreeModifier>();
        return modifier;
      }
      set => modifier = value;
    }

    public System.Type ModifierType
    {
      get => Modifier.GetType();
      set
      {
        if (value == null || value.IsAbstract || !typeof(ProceduralImageModifier).IsAssignableFrom(value))
          throw new ArgumentException("ModifierType must be a concrete ProceduralImageModifier.", nameof(value));

        var current = Modifier;
        if (current.GetType() == value) return;

        DestroyImmediate(current);
        Modifier = (ProceduralImageModifier)gameObject.AddComponent(value);
        SetAllDirty();
      }
    }

    protected override void OnEnable()
    {
      base.OnEnable();
      preserveAspect = false;
      EnsureSprite();
      EnsureMaterial();
      EnsureCanvasChannels();
    }

    protected override void OnTransformParentChanged()
    {
      base.OnTransformParentChanged();
      EnsureCanvasChannels();
    }

    protected override void OnPopulateMesh(VertexHelper toFill)
    {
      EnsureSprite();
      base.OnPopulateMesh(toFill);
      if (toFill.currentVertCount == 0) return;

      Rect rect = GetPixelAdjustedRect();
      float minDimension = Mathf.Min(rect.width, rect.height);
      if (minDimension <= 0f) return;

      Vector4 radius = FitRadii(Modifier.CalculateRadius(rect), rect);
      float padding = Mathf.Max(0f, falloffDistance);
      float encodedBorderWidth = borderWidth <= 0f
        ? 1f
        : Mathf.Clamp01(borderWidth * 2f / minDimension);
      Vector2 center = rect.center;

      for (int index = 0; index < toFill.currentVertCount; index++)
      {
        UIVertex vertex = default;
        toFill.PopulateUIVertex(ref vertex, index);

        // Keep one pixel-scale margin for the shader's antialiased edge.
        vertex.position.x += Mathf.Sign(vertex.position.x - center.x) * padding * 0.5f;
        vertex.position.y += Mathf.Sign(vertex.position.y - center.y) * padding * 0.5f;
        vertex.normal = new Vector3(center.x, center.y, 0f);
        vertex.uv1 = new Vector2(rect.width, rect.height);
        vertex.uv2 = new Vector2(
          EncodePair(radius.x / minDimension, radius.y / minDimension),
          EncodePair(radius.z / minDimension, radius.w / minDimension));
        vertex.uv3 = new Vector2(encodedBorderWidth, padding);
        toFill.SetUIVertex(vertex, index);
      }
    }

    private void EnsureSprite()
    {
      if (!sprite) sprite = GetEmptySprite();
    }

    private void EnsureMaterial()
    {
      if (!sharedMaterial)
      {
        Shader shader = Shader.Find(ShaderName);
        if (!shader) return;
        sharedMaterial = new Material(shader)
        {
          name = "Procedural UI Image (shared)",
          hideFlags = HideFlags.HideAndDontSave
        };
      }

      if (sharedMaterial) material = sharedMaterial;
    }

    private void EnsureCanvasChannels()
    {
      Canvas canvas = GetComponentInParent<Canvas>();
      if (!canvas) return;

      canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.Normal
        | AdditionalCanvasShaderChannels.TexCoord1
        | AdditionalCanvasShaderChannels.TexCoord2
        | AdditionalCanvasShaderChannels.TexCoord3;
    }

    private static Sprite GetEmptySprite()
    {
      if (emptySprite) return emptySprite;

      var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
      {
        name = "Procedural UI Image fallback",
        hideFlags = HideFlags.HideAndDontSave
      };
      texture.SetPixel(0, 0, Color.white);
      texture.Apply(false, true);
      emptySprite = Sprite.Create(texture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 100f);
      emptySprite.name = "Procedural UI Image fallback";
      emptySprite.hideFlags = HideFlags.HideAndDontSave;
      return emptySprite;
    }

    private static Vector4 FitRadii(Vector4 radii, Rect rect)
    {
      radii = new Vector4(
        Mathf.Max(0f, radii.x), Mathf.Max(0f, radii.y),
        Mathf.Max(0f, radii.z), Mathf.Max(0f, radii.w));

      float scale = 1f;
      scale = MinimumRatio(scale, rect.width, radii.x + radii.y);
      scale = MinimumRatio(scale, rect.width, radii.z + radii.w);
      scale = MinimumRatio(scale, rect.height, radii.x + radii.w);
      scale = MinimumRatio(scale, rect.height, radii.z + radii.y);
      return radii * scale;
    }

    private static float MinimumRatio(float current, float available, float requested)
    {
      return requested > 0f ? Mathf.Min(current, available / requested) : current;
    }

    private static float EncodePair(float first, float second)
    {
      const float componentMax = 65534f;
      const float packingScale = 65535f;
      float high = Mathf.Floor(Mathf.Clamp01(first) * componentMax);
      float low = Mathf.Floor(Mathf.Clamp01(second) * componentMax);
      return high / packingScale + low / (packingScale * packingScale);
    }
  }
}
