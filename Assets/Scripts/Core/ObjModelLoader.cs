using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>Loads Wavefront OBJ files and their adjacent MTL/texture files at runtime.</summary>
public static class ObjModelLoader
{
  private sealed class MaterialData
  {
    public Color color = Color.white;
    public string texturePath;
  }

  private sealed class MeshData
  {
    public readonly List<Vector3> vertices = new List<Vector3>();
    public readonly List<Vector2> uvs = new List<Vector2>();
    public readonly List<Vector3> normals = new List<Vector3>();
    public readonly List<int> triangles = new List<int>();
  }

  private struct FaceVertex
  {
    public int position;
    public int uv;
    public int normal;
  }

  /// <summary>Loads an OBJ and returns a root object containing one child renderer per material.</summary>
  public static GameObject Load(string objPath)
  {
    if (string.IsNullOrWhiteSpace(objPath)) throw new IOException("OBJ path is empty.");
    string fullObjPath;
    try { fullObjPath = Path.GetFullPath(objPath); }
    catch (Exception e) { throw new IOException("Invalid OBJ path: " + objPath, e); }
    if (!File.Exists(fullObjPath)) throw new FileNotFoundException("OBJ file was not found: " + fullObjPath, fullObjPath);

    string directory = Path.GetDirectoryName(fullObjPath);
    var positions = new List<Vector3>();
    var uvs = new List<Vector2>();
    var normals = new List<Vector3>();
    var materials = new Dictionary<string, MaterialData>(StringComparer.Ordinal);
    var meshes = new Dictionary<string, MeshData>(StringComparer.Ordinal);
    var materialOrder = new List<string>();
    string currentMaterial = "Default";
    try
    {
      string[] lines = File.ReadAllLines(fullObjPath, Encoding.UTF8);
      for (int lineNumber = 0; lineNumber < lines.Length; lineNumber++)
      {
        string line = StripComment(lines[lineNumber]).Trim();
        if (line.Length == 0) continue;
        string[] parts = SplitWhitespace(line);
        switch (parts[0])
        {
          case "v":
            if (parts.Length < 4) throw ObjError(fullObjPath, lineNumber, "A vertex requires at least three coordinates.");
            positions.Add(new Vector3(ParseFloat(parts[1], fullObjPath, lineNumber), ParseFloat(parts[2], fullObjPath, lineNumber), ParseFloat(parts[3], fullObjPath, lineNumber)));
            break;
          case "vt":
            if (parts.Length < 3) throw ObjError(fullObjPath, lineNumber, "A texture coordinate requires at least two coordinates.");
            uvs.Add(new Vector2(ParseFloat(parts[1], fullObjPath, lineNumber), ParseFloat(parts[2], fullObjPath, lineNumber)));
            break;
          case "vn":
            if (parts.Length < 4) throw ObjError(fullObjPath, lineNumber, "A normal requires three coordinates.");
            normals.Add(new Vector3(ParseFloat(parts[1], fullObjPath, lineNumber), ParseFloat(parts[2], fullObjPath, lineNumber), ParseFloat(parts[3], fullObjPath, lineNumber)));
            break;
          case "mtllib":
            if (parts.Length < 2) throw ObjError(fullObjPath, lineNumber, "mtllib requires a filename.");
            for (int i = 1; i < parts.Length; i++) LoadMtl(ResolveContainedPath(directory, parts[i], fullObjPath, lineNumber), materials);
            break;
          case "usemtl":
            if (parts.Length < 2) throw ObjError(fullObjPath, lineNumber, "usemtl requires a material name.");
            currentMaterial = string.Join(" ", parts, 1, parts.Length - 1);
            break;
          case "f":
            if (parts.Length < 4) throw ObjError(fullObjPath, lineNumber, "A face requires at least three vertices.");
            var face = new FaceVertex[parts.Length - 1];
            for (int i = 1; i < parts.Length; i++) face[i - 1] = ParseFaceVertex(parts[i], positions.Count, uvs.Count, normals.Count, fullObjPath, lineNumber);
            MeshData data;
            if (!meshes.TryGetValue(currentMaterial, out data))
            {
              data = new MeshData();
              meshes.Add(currentMaterial, data);
              materialOrder.Add(currentMaterial);
            }
            for (int i = 1; i < face.Length - 1; i++)
            {
              AddTriangle(data, face[0], face[i], face[i + 1], positions, uvs, normals);
            }
            break;
          // Other standard and vendor-specific OBJ directives do not affect geometry.
        }
      }
    }
    catch (InvalidDataException) { throw; }
    catch (IOException) { throw; }
    catch (Exception e) { throw new IOException("Could not read OBJ file '" + fullObjPath + "': " + e.Message, e); }

    if (positions.Count == 0) throw new InvalidDataException("OBJ file contains no vertex positions: " + fullObjPath);
    if (materialOrder.Count == 0) throw new InvalidDataException("OBJ file contains no valid faces: " + fullObjPath);

    var root = new GameObject(Path.GetFileNameWithoutExtension(fullObjPath));
    var createdMeshes = new List<Mesh>();
    var createdMaterials = new List<Material>();
    try
    {
      for (int i = 0; i < materialOrder.Count; i++)
      {
        string name = materialOrder[i];
        MeshData data = meshes[name];
        if (data.triangles.Count == 0) continue;
        var mesh = new Mesh { name = name + " mesh" };
        createdMeshes.Add(mesh);
        mesh.indexFormat = data.vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(data.vertices);
        mesh.SetUVs(0, data.uvs);
        mesh.SetNormals(data.normals);
        mesh.SetTriangles(data.triangles, 0);
        mesh.RecalculateBounds();

        Material material = CreateUnityMaterial(name, materials);
        createdMaterials.Add(material);
        var child = new GameObject(name);
        child.transform.SetParent(root.transform, false);
        child.AddComponent<MeshFilter>().sharedMesh = mesh;
        child.AddComponent<MeshRenderer>().sharedMaterial = material;
      }
      return root;
    }
    catch
    {
      UnityEngine.Object.Destroy(root);
      foreach (Material material in createdMaterials)
      {
        if (material == null) continue;
        if (material.mainTexture != null) UnityEngine.Object.Destroy(material.mainTexture);
        UnityEngine.Object.Destroy(material);
      }
      foreach (Mesh mesh in createdMeshes)
      {
        if (mesh != null) UnityEngine.Object.Destroy(mesh);
      }
      throw;
    }
  }

  private static void AddTriangle(MeshData data, FaceVertex a, FaceVertex b, FaceVertex c, List<Vector3> positions, List<Vector2> uvs, List<Vector3> normals)
  {
    Vector3 pa = positions[a.position], pb = positions[b.position], pc = positions[c.position];
    Vector3 generated = Vector3.Cross(pb - pa, pc - pa);
    if (generated.sqrMagnitude > 0) generated.Normalize();
    AddCorner(data, a, pa, generated, uvs, normals);
    AddCorner(data, b, pb, generated, uvs, normals);
    AddCorner(data, c, pc, generated, uvs, normals);
  }

  private static void AddCorner(MeshData data, FaceVertex corner, Vector3 position, Vector3 generatedNormal, List<Vector2> uvs, List<Vector3> normals)
  {
    data.triangles.Add(data.vertices.Count);
    data.vertices.Add(position);
    data.uvs.Add(corner.uv >= 0 ? uvs[corner.uv] : Vector2.zero);
    data.normals.Add(corner.normal >= 0 ? normals[corner.normal] : generatedNormal);
  }

  private static FaceVertex ParseFaceVertex(string token, int positionCount, int uvCount, int normalCount, string path, int line)
  {
    string[] indexes = token.Split('/');
    if (indexes.Length < 1 || indexes.Length > 3 || indexes[0].Length == 0) throw ObjError(path, line, "Invalid face vertex '" + token + "'.");
    return new FaceVertex
    {
      position = ResolveIndex(indexes[0], positionCount, path, line, "position"),
      uv = indexes.Length > 1 && indexes[1].Length > 0 ? ResolveIndex(indexes[1], uvCount, path, line, "texture coordinate") : -1,
      normal = indexes.Length > 2 && indexes[2].Length > 0 ? ResolveIndex(indexes[2], normalCount, path, line, "normal") : -1
    };
  }

  private static int ResolveIndex(string text, int count, string path, int line, string kind)
  {
    int index;
    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index == 0) throw ObjError(path, line, "Invalid " + kind + " index '" + text + "'.");
    int resolved = index > 0 ? index - 1 : count + index;
    if (resolved < 0 || resolved >= count) throw ObjError(path, line, kind + " index is out of range: " + text + ".");
    return resolved;
  }

  private static void LoadMtl(string path, Dictionary<string, MaterialData> materials)
  {
    if (!File.Exists(path)) throw new FileNotFoundException("Material library was not found: " + path, path);
    string current = null;
    string[] lines;
    try { lines = File.ReadAllLines(path, Encoding.UTF8); }
    catch (Exception e) { throw new IOException("Could not read material library '" + path + "': " + e.Message, e); }
    for (int lineNumber = 0; lineNumber < lines.Length; lineNumber++)
    {
      string line = StripComment(lines[lineNumber]).Trim();
      if (line.Length == 0) continue;
      string[] parts = SplitWhitespace(line);
      if (parts[0] == "newmtl")
      {
        if (parts.Length < 2) throw ObjError(path, lineNumber, "newmtl requires a material name.");
        current = string.Join(" ", parts, 1, parts.Length - 1);
        materials[current] = new MaterialData();
      }
      else if (current != null && parts[0] == "Kd")
      {
        if (parts.Length < 4) throw ObjError(path, lineNumber, "Kd requires three color components.");
        MaterialData material = materials[current];
        material.color.r = ParseFloat(parts[1], path, lineNumber);
        material.color.g = ParseFloat(parts[2], path, lineNumber);
        material.color.b = ParseFloat(parts[3], path, lineNumber);
      }
      else if (current != null && (parts[0] == "d" || parts[0] == "Tr"))
      {
        if (parts.Length < 2) throw ObjError(path, lineNumber, parts[0] + " requires an alpha value.");
        float alpha = ParseFloat(parts[1], path, lineNumber);
        materials[current].color.a = Mathf.Clamp01(parts[0] == "Tr" ? 1f - alpha : alpha);
      }
      else if (current != null && parts[0] == "map_Kd")
      {
        if (parts.Length < 2) throw ObjError(path, lineNumber, "map_Kd requires a texture filename.");
        string texture = string.Join(" ", parts, 1, parts.Length - 1);
        materials[current].texturePath = ResolveContainedPath(Path.GetDirectoryName(path), texture, path, lineNumber);
      }
    }
  }

  private static Material CreateUnityMaterial(string name, Dictionary<string, MaterialData> materials)
  {
    MaterialData data;
    if (!materials.TryGetValue(name, out data)) data = new MaterialData();
    Shader shader = Shader.Find("Standard");
    if (shader == null) shader = Shader.Find("Sprites/Default");
    if (shader == null) throw new InvalidDataException("No supported Unity shader is available to render OBJ material '" + name + "'.");
    var material = new Material(shader) { name = name, color = data.color };
    Texture2D texture = null;
    try
    {
      if (data.color.a < 1f && shader.name == "Standard")
      {
        material.SetFloat("_Mode", 3f);
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = 3000;
      }
      if (!string.IsNullOrEmpty(data.texturePath))
      {
        if (!File.Exists(data.texturePath)) throw new FileNotFoundException("Diffuse texture referenced by OBJ material was not found: " + data.texturePath, data.texturePath);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(data.texturePath); }
        catch (Exception e) { throw new IOException("Could not read diffuse texture '" + data.texturePath + "': " + e.Message, e); }
        texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(texture, bytes))
          throw new InvalidDataException("Diffuse texture is not a supported image: " + data.texturePath);
        material.mainTexture = texture;
      }
      return material;
    }
    catch
    {
      if (texture != null) UnityEngine.Object.Destroy(texture);
      UnityEngine.Object.Destroy(material);
      throw;
    }
  }

  private static string ResolveContainedPath(string baseDirectory, string relativePath, string source, int line)
  {
    string candidate;
    try { candidate = Path.GetFullPath(Path.Combine(baseDirectory, relativePath)); }
    catch (Exception e) { throw new InvalidDataException("Invalid referenced path '" + relativePath + "' in '" + source + "'.", e); }
    string root = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    StringComparison comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    if (!candidate.StartsWith(root, comparison)) throw ObjError(source, line, "Referenced path escapes the OBJ directory: '" + relativePath + "'.");
    return candidate;
  }

  private static string[] SplitWhitespace(string line) { return line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries); }
  private static string StripComment(string line) { int comment = line.IndexOf('#'); return comment < 0 ? line : line.Substring(0, comment); }
  private static float ParseFloat(string value, string path, int line)
  {
    float result;
    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) || float.IsNaN(result) || float.IsInfinity(result)) throw ObjError(path, line, "Invalid number '" + value + "'.");
    return result;
  }
  private static InvalidDataException ObjError(string path, int zeroBasedLine, string message)
  {
    return new InvalidDataException(path + ": line " + (zeroBasedLine + 1).ToString(CultureInfo.InvariantCulture) + ": " + message);
  }
}
