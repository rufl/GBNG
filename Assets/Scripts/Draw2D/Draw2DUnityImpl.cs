/*
 * Copyright 2019 Google LLC
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     https://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using UnityEngine;

/// <summary>Immediate-mode renderer using Unity's built-in OnGUI and GL APIs.</summary>
public class Draw2DUnityImpl : IDraw2D
{
  private const float DEFAULT_LINE_WIDTH = 1f;
  private const int MAX_CIRCLE_SIDES = 256;

  private readonly Material material;
  private readonly Texture2D bitmapFontAtlas;
  private readonly Dictionary<char, BitmapGlyph> glyphs = new Dictionary<char, BitmapGlyph>();

  private struct BitmapGlyph
  {
    public float xOffset;
    public float yOffset;
    public float width;
    public float height;
    public float xAdvance;
    public float uLeft;
    public float vTop;
    public float uRight;
    public float vBottom;
  }

  public Draw2DUnityImpl(TextAsset fontConfig, Texture2D fontAtlas)
  {
    if (fontConfig == null) throw new ArgumentNullException(nameof(fontConfig));
    if (fontAtlas == null) throw new ArgumentNullException(nameof(fontAtlas));

    bitmapFontAtlas = fontAtlas;
    bitmapFontAtlas.filterMode = FilterMode.Bilinear;
    bitmapFontAtlas.wrapMode = TextureWrapMode.Clamp;

    Shader shader = Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Hidden/Internal-Colored");
    if (shader == null) Debug.LogError("No Unity shader is available for the built-in 2D renderer.");
    if (shader != null)
    {
      material = new Material(shader);
      material.hideFlags = HideFlags.HideAndDontSave;
      material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
      material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
      material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
      material.SetInt("_ZWrite", 0);
      material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
    }

    XmlDocument document = new XmlDocument();
    document.LoadXml(fontConfig.text);
    XmlElement root = document.DocumentElement;
    if (root == null || root.Name != "font")
      throw new InvalidOperationException("The bitmap font config must have a <font> root.");

    XmlElement info = root.SelectSingleNode("info") as XmlElement;
    XmlElement common = root.SelectSingleNode("common") as XmlElement;
    XmlNodeList pages = root.SelectNodes("pages/page");
    XmlNodeList characters = root.SelectNodes("chars/char");
    int fontSize = ReadFontMetric(info, "size");
    if (fontSize <= 0)
      throw new InvalidOperationException("The bitmap font size must be positive.");
    if (ReadFontMetric(common, "scaleW") != bitmapFontAtlas.width ||
      ReadFontMetric(common, "scaleH") != bitmapFontAtlas.height)
      throw new InvalidOperationException("The bitmap font atlas dimensions do not match its config.");
    if (pages == null || pages.Count != 1 || ReadFontMetric(pages[0] as XmlElement, "id") != 0)
      throw new InvalidOperationException("The bitmap font must use one atlas page with id 0.");
    if (characters == null)
      throw new InvalidOperationException("The bitmap font config has no character table.");

    float atlasWidth = bitmapFontAtlas.width;
    float atlasHeight = bitmapFontAtlas.height;
    foreach (XmlNode characterNode in characters)
    {
      XmlElement character = characterNode as XmlElement;
      if (character == null) continue;
      int x = ReadFontMetric(character, "x");
      int y = ReadFontMetric(character, "y");
      int width = ReadFontMetric(character, "width");
      int height = ReadFontMetric(character, "height");
      glyphs.Add(Convert.ToChar(ReadFontMetric(character, "id")), new BitmapGlyph
      {
        xOffset = ReadFontMetric(character, "xoffset") / (float)fontSize,
        yOffset = ReadFontMetric(character, "yoffset") / (float)fontSize,
        width = width / (float)fontSize,
        height = height / (float)fontSize,
        xAdvance = ReadFontMetric(character, "xadvance") / (float)fontSize,
        uLeft = x / atlasWidth,
        // ProggyClean's BMFont y metrics are measured from the bottom of the atlas.
        vTop = (y + height) / atlasHeight,
        uRight = (x + width) / atlasWidth,
        vBottom = y / atlasHeight
      });
    }
  }

  private static int ReadFontMetric(XmlElement element, string attribute)
  {
    XmlAttribute value = element != null ? element.GetAttributeNode(attribute) : null;
    int metric;
    if (value == null || !int.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out metric))
      throw new InvalidOperationException("The bitmap font config has an invalid '" + attribute + "' value.");
    return metric;
  }

  public Draw2DMode GetDraw2DMode() { return Draw2DMode.OnGui; }
  public void StartRendering(RenderTexture source, RenderTexture destination) { }
  public void EndRendering() { }


  private bool CanDraw()
  {
    return material != null && Event.current != null && Event.current.type == EventType.Repaint &&
      Screen.width > 0 && Screen.height > 0;
  }

  private void Begin(Color color, Texture texture)
  {
    material.SetTexture("_MainTex", texture != null ? texture : Texture2D.whiteTexture);
    material.SetPass(0);
    GL.PushMatrix();
    GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
    GL.Begin(GL.TRIANGLES);
    GL.Color(color);
  }

  private static void Vertex(Vector2 point) { GL.Vertex3(point.x, point.y, 0); }
  private static void Vertex(float x, float y) { GL.Vertex3(x, y, 0); }

  private static void End()
  {
    GL.End();
    GL.PopMatrix();
  }

  private void FillRectInternal(Rect rect, Color color, Texture texture, Rect uv)
  {
    if (!CanDraw() || float.IsNaN(rect.x) || float.IsNaN(rect.y) ||
      float.IsNaN(rect.width) || float.IsNaN(rect.height) || rect.width == 0 || rect.height == 0) return;
    Vector2 tl = new Vector2(rect.xMin, rect.yMin);
    Vector2 bl = new Vector2(rect.xMin, rect.yMax);
    Vector2 br = new Vector2(rect.xMax, rect.yMax);
    Vector2 tr = new Vector2(rect.xMax, rect.yMin);
    Begin(color, texture);
    GL.TexCoord2(uv.x, uv.y); Vertex(tl);
    GL.TexCoord2(uv.x, uv.y + uv.height); Vertex(bl);
    GL.TexCoord2(uv.x + uv.width, uv.y + uv.height); Vertex(br);
    GL.TexCoord2(uv.x, uv.y); Vertex(tl);
    GL.TexCoord2(uv.x + uv.width, uv.y + uv.height); Vertex(br);
    GL.TexCoord2(uv.x + uv.width, uv.y); Vertex(tr);
    End();
  }

  public void FillRect(Rect rect, Color color)
  {
    FillRectInternal(rect, color, null, new Rect(0, 0, 1, 1));
  }

  public void DrawRect(Rect rect, Color color)
  {
    DrawRect(rect, color, DEFAULT_LINE_WIDTH, false);
  }

  public void DrawDashedRect(Rect rect, Color color, float lineWidth)
  {
    DrawRect(rect, color, lineWidth, true);
  }

  private void DrawRect(Rect rect, Color color, float width, bool dashed)
  {
    if (float.IsNaN(rect.x) || float.IsNaN(rect.y) || float.IsNaN(rect.width) || float.IsNaN(rect.height)) return;
    Vector2 a = new Vector2(rect.xMin, rect.yMin);
    Vector2 b = new Vector2(rect.xMax, rect.yMin);
    Vector2 c = new Vector2(rect.xMax, rect.yMax);
    Vector2 d = new Vector2(rect.xMin, rect.yMax);
    if (dashed)
    {
      DrawDashedLine(a, b, color, width); DrawDashedLine(b, c, color, width);
      DrawDashedLine(c, d, color, width); DrawDashedLine(d, a, color, width);
    }
    else
    {
      DrawThickLine(a, b, color, width); DrawThickLine(b, c, color, width);
      DrawThickLine(c, d, color, width); DrawThickLine(d, a, color, width);
    }
  }

  public void DrawText(string text, float x, float y, float rotationDegs, float worldSizePixels, Color color)
  {
    if (string.IsNullOrEmpty(text) || !CanDraw() || float.IsNaN(x) || float.IsNaN(y) ||
      float.IsNaN(worldSizePixels) || float.IsInfinity(worldSizePixels) || worldSizePixels <= 0) return;

    if (float.IsNaN(rotationDegs)) rotationDegs = 0;
    float axisXX = 1, axisXY = 0, axisYX = 0, axisYY = 1;
    if (!Mathf.Approximately(rotationDegs, 0))
    {
      float radians = rotationDegs * Mathf.Deg2Rad;
      float cosine = Mathf.Cos(radians);
      float sine = Mathf.Sin(radians);
      axisXX = cosine;
      axisXY = sine;
      axisYX = -sine;
      axisYY = cosine;
    }

    float cursorX = x, cursorY = y;
    Begin(color, bitmapFontAtlas);
    for (int i = 0; i < text.Length; i++)
    {
      if (!glyphs.TryGetValue(text[i], out BitmapGlyph glyph)) continue;
      float left = cursorX + axisXX * glyph.xOffset * worldSizePixels + axisYX * glyph.yOffset * worldSizePixels;
      float top = cursorY + axisXY * glyph.xOffset * worldSizePixels + axisYY * glyph.yOffset * worldSizePixels;
      float rightX = left + axisXX * glyph.width * worldSizePixels;
      float rightY = top + axisXY * glyph.width * worldSizePixels;
      float bottomLeftX = left + axisYX * glyph.height * worldSizePixels;
      float bottomLeftY = top + axisYY * glyph.height * worldSizePixels;
      float bottomRightX = rightX + axisYX * glyph.height * worldSizePixels;
      float bottomRightY = rightY + axisYY * glyph.height * worldSizePixels;

      if (glyph.width > 0 && glyph.height > 0)
      {
        GL.TexCoord2(glyph.uLeft, glyph.vTop); Vertex(left, top);
        GL.TexCoord2(glyph.uLeft, glyph.vBottom); Vertex(bottomLeftX, bottomLeftY);
        GL.TexCoord2(glyph.uRight, glyph.vBottom); Vertex(bottomRightX, bottomRightY);
        GL.TexCoord2(glyph.uLeft, glyph.vTop); Vertex(left, top);
        GL.TexCoord2(glyph.uRight, glyph.vBottom); Vertex(bottomRightX, bottomRightY);
        GL.TexCoord2(glyph.uRight, glyph.vTop); Vertex(rightX, rightY);
      }

      cursorX += axisXX * glyph.xAdvance * worldSizePixels;
      cursorY += axisXY * glyph.xAdvance * worldSizePixels;
    }
    End();
  }

  public void DrawTexture(Rect rect, Color color, Texture2D texture)
  {
    if (texture != null) FillRectInternal(rect, color, texture, new Rect(0, 1, 1, -1));
  }

  public void FillQuads(Vector2[] vertices, Vector2[] texCoords, Color color, Texture2D texture)
  {
    if (!CanDraw() || vertices == null || texCoords == null || texture == null) return;
    int quadCount = Mathf.Min(vertices.Length, texCoords.Length) / 4;
    if (quadCount == 0) return;
    Begin(color, texture);
    for (int i = 0; i < quadCount; i++)
    {
      int n = i * 4;
      QuadVertex(vertices, texCoords, n); QuadVertex(vertices, texCoords, n + 1); QuadVertex(vertices, texCoords, n + 2);
      QuadVertex(vertices, texCoords, n); QuadVertex(vertices, texCoords, n + 2); QuadVertex(vertices, texCoords, n + 3);
    }
    End();
  }

  private static void QuadVertex(Vector2[] vertices, Vector2[] uv, int index)
  {
    GL.TexCoord2(uv[index].x, uv[index].y);
    Vertex(vertices[index]);
  }

  public void DrawDashedLine(Vector2 pointA, Vector2 pointB, Color color)
  {
    DrawDashedLine(pointA, pointB, color, DEFAULT_LINE_WIDTH);
  }

  private void DrawDashedLine(Vector2 pointA, Vector2 pointB, Color color, float width)
  {
    if (!CanDraw()) return;
    Vector2 delta = pointB - pointA;
    float length = delta.magnitude;
    if (length <= 0.001f || float.IsNaN(length)) return;
    Vector2 direction = delta / length;
    const float dashLength = 6f;
    const float gapLength = 4f;
    for (float start = 0; start < length; start += dashLength + gapLength)
      DrawThickLine(pointA + direction * start, pointA + direction * Mathf.Min(start + dashLength, length), color, width);
  }

  public void DrawLine(Vector2 pointA, Vector2 pointB, Color color)
  {
    DrawThickLine(pointA, pointB, color, DEFAULT_LINE_WIDTH);
  }

  private void DrawThickLine(Vector2 a, Vector2 b, Color color, float width)
  {
    if (!CanDraw()) return;
    Vector2 delta = b - a;
    float length = delta.magnitude;
    if (length <= 0.001f || float.IsNaN(length)) return;
    Vector2 perpendicular = new Vector2(-delta.y, delta.x) * (Mathf.Max(0.1f, width) / (2 * length));
    Vector2 a0 = a - perpendicular, a1 = a + perpendicular;
    Vector2 b0 = b - perpendicular, b1 = b + perpendicular;
    Begin(color, null);
    Vertex(a0); Vertex(a1); Vertex(b1);
    Vertex(a0); Vertex(b1); Vertex(b0);
    End();
  }

  public void DrawDashedTriangle(Vector2 a, Vector2 b, Vector2 c, Color color, float lineWidth)
  {
    DrawDashedLine(a, b, color, lineWidth); DrawDashedLine(b, c, color, lineWidth); DrawDashedLine(c, a, color, lineWidth);
  }

  public void DrawTriangle(Vector2 a, Vector2 b, Vector2 c, Color color, float lineWidth)
  {
    DrawThickLine(a, b, color, lineWidth); DrawThickLine(b, c, color, lineWidth); DrawThickLine(c, a, color, lineWidth);
  }

  public void FillTriangle(Vector2 a, Vector2 b, Vector2 c, Color color)
  {
    if (!CanDraw()) return;
    Begin(color, null); Vertex(a); Vertex(b); Vertex(c); End();
  }

  public void DrawDashedCircle(Vector2 center, float radius, int sides, Color color, float lineWidth)
  {
    DrawCircleInternal(center, radius, sides, color, lineWidth, true);
  }

  public void DrawCircle(Vector2 center, float radius, int sides, Color color, float lineWidth)
  {
    DrawCircleInternal(center, radius, sides, color, lineWidth, false);
  }

  private void DrawCircleInternal(Vector2 center, float radius, int sides, Color color, float lineWidth, bool dashed)
  {
    if (radius <= 0 || float.IsNaN(radius)) return;
    sides = Mathf.Clamp(sides, 3, MAX_CIRCLE_SIDES);
    Vector2 previous = center + new Vector2(radius, 0);
    for (int i = 1; i <= sides; i++)
    {
      float angle = i * Mathf.PI * 2 / sides;
      Vector2 next = center + new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
      if (!dashed || (i & 1) == 1) DrawThickLine(previous, next, color, lineWidth);
      previous = next;
    }
  }

  public void FillCircle(Vector2 center, float radius, Color color)
  {
    if (!CanDraw() || radius <= 0 || float.IsNaN(radius)) return;
    const int sides = 32;
    Begin(color, null);
    Vector2 previous = center + new Vector2(radius, 0);
    for (int i = 1; i <= sides; i++)
    {
      float angle = i * Mathf.PI * 2 / sides;
      Vector2 next = center + new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
      Vertex(center); Vertex(previous); Vertex(next);
      previous = next;
    }
    End();
  }
}
