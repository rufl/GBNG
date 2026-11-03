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

using UnityEngine;

/// <summary>Immediate-mode renderer using Unity's built-in OnGUI and GL APIs.</summary>
public class Draw2DUnityImpl : IDraw2D
{
  private const float DEFAULT_LINE_WIDTH = 1f;
  private const int MAX_CIRCLE_SIDES = 256;

  private readonly Material material;
  private readonly GUIStyle textStyle;

  public Draw2DUnityImpl()
  {
    Shader shader = Shader.Find("Hidden/Internal-Colored");
    if (shader == null) shader = Shader.Find("UI/Default");
    if (shader == null) shader = Shader.Find("Sprites/Default");
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
    textStyle = new GUIStyle
    {
      wordWrap = false,
      richText = false,
      alignment = TextAnchor.UpperLeft
    };
  }

  public Draw2DMode GetDraw2DMode() { return Draw2DMode.OnGui; }
  public void StartRendering(RenderTexture source, RenderTexture destination) { }
  public void EndRendering() { }

  public IBitmapFont2D CreateBitmapFont(TextAsset xmlFile, Texture2D fontImage)
  {
    // Unity's built-in GUI font cannot consume the Shaper bitmap-font XML/atlas.
    return new BitmapFontImpl();
  }

  private sealed class BitmapFontImpl : IBitmapFont2D { }

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

  public void DrawText(string text, float x, float y, float rotationDegs, float worldSizePixels, Color color, IBitmapFont2D font)
  {
    if (string.IsNullOrEmpty(text) || Event.current == null || Event.current.type != EventType.Repaint ||
      float.IsNaN(x) || float.IsNaN(y)) return;
    textStyle.normal.textColor = color;
    textStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(worldSizePixels), 1, 512);
    Matrix4x4 oldMatrix = GUI.matrix;
    if (!float.IsNaN(rotationDegs) && !Mathf.Approximately(rotationDegs, 0))
      GUIUtility.RotateAroundPivot(rotationDegs, new Vector2(x, y));
    GUI.Label(new Rect(x, y, Mathf.Max(Screen.width - x, 1), Screen.height), text, textStyle);
    GUI.matrix = oldMatrix;
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
