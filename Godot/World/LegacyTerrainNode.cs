using GBNG.Domain;
using Godot;

namespace GBNG.World;

public partial class LegacyTerrainNode : Node3D
{
    private const float HalfWidth = LegacyTerrainData.CellWidth * 0.5f;
    private const float HalfHeight = LegacyTerrainData.CellHeight * 0.5f;
    private static readonly Color[] Palette =
    [
        new(1f, 1f, 1f), new(0.3137255f, 0.31764707f, 0.30980393f),
        new(1f, 0f, 0f), new(1f, 1f, 0f), new(0f, 1f, 0f),
        new(0f, 1f, 1f), new(0f, 0f, 1f), new(1f, 0f, 1f),
        new(0.15294118f, 0.5686275f, 0.8156863f), new(0.8627451f, 0.7921569f, 0.2784314f),
        new(0.9647059f, 0.54509807f, 0.27450982f), new(0.9372549f, 0.25490198f, 0.24313726f),
        new(0.63529414f, 0.24313726f, 0.28627452f), new(0.3254902f, 0.7529412f, 0.6509804f),
        new(0.02745098f, 0.6784314f, 0.4117647f), new(0.827451f, 0.5568628f, 0.7411765f)
    ];

    private static readonly string?[] StyleTextures =
    [
        null, null, null, null, null, null, null, null, null, null,
        null, null, null, null, null, null,
        "stone.png", "metalblue.png", "grassrock-top.png", "snow-top.png", "dirt-top.png",
        "grassstone-top.png", "greycraters-top.png", "ice-top.png", "lava-top.png", "sand-top.png",
        "water-top.png", "wooden.png", "redcraters-top.png", "industrialgreen.png", "industrialred.png",
        "greybricks.png", "metalbeige.png", "redbricks.png", "road.png", "roadcrossing.png",
        "roadwhitebrokenline.png", "roadyellowunbrokenline.png", "pavement.png", "pavementconcavecorner.png",
        "pavementconvexcorner.png"
    ];

    private readonly Dictionary<ushort, SurfaceTool> _surfaces = new();
    private readonly Dictionary<ushort, StandardMaterial3D> _materials = new();
    private LegacyTerrainData _data = null!;
    private MeshInstance3D? _meshInstance;
    private StaticBody3D? _body;
    private CollisionShape3D? _collisionShape;
    private bool _dirty;
    private bool _preview;

    private LegacyTerrainNode() { }

    public static LegacyTerrainNode Create(LegacyTerrainData data)
    {
        var terrain = new LegacyTerrainNode { Name = "Terrain", _data = data };
        terrain.Build();
        return terrain;
    }
    public static LegacyTerrainNode CreatePreview(LegacyTerrainData data)
    {
        var terrain = new LegacyTerrainNode { Name = "TerrainCopyPreview", _data = data, _preview = true };
        terrain.Build();
        return terrain;
    }

    public int Width => _data.Width;
    public int Depth => _data.Depth;
    internal LegacyTerrainData Data => _data;
    public static Vector3 GetCellCenterLocal(LegacyTerrainData data, int x, int y, int z)
        => new(
            (x - data.Width * 0.5f) * LegacyTerrainData.CellWidth,
            (y + LegacyTerrainData.CellsYStart) * LegacyTerrainData.CellHeight + HalfHeight,
            -(z - data.Depth * 0.5f) * LegacyTerrainData.CellWidth);

    public Vector3 GetCellCenterGlobal(int x, int y, int z)
        => ToGlobal(GetCellCenterLocal(_data, x, y, z));


    public bool TryGetCell(int x, int y, int z, out byte shape, out ushort style, out byte direction)
        => _data.TryGetCell(x, y, z, out shape, out style, out direction);
    public bool TryGetCellForHit(Vector3 worldPosition, Vector3 worldNormal, out int x, out int y, out int z)
    {
        Vector3 localPoint = CoordinateSpace.ToLegacy(ToLocal(worldPosition));
        Vector3 localNormal = CoordinateSpace.ToLegacy(GlobalBasis.Inverse() * worldNormal);
        Vector3 centeredPoint = localPoint - new Vector3(0f, HalfHeight, 0f);
        if (IsCellFace(centeredPoint.X / LegacyTerrainData.CellWidth)
            || IsCellFace(centeredPoint.Y / LegacyTerrainData.CellHeight)
            || IsCellFace(centeredPoint.Z / LegacyTerrainData.CellWidth))
        {
            localPoint -= localNormal * HalfHeight;
            centeredPoint = localPoint - new Vector3(0f, HalfHeight, 0f);
        }

        x = Mathf.RoundToInt(centeredPoint.X / LegacyTerrainData.CellWidth) + _data.Width / 2;
        y = Mathf.RoundToInt(centeredPoint.Y / LegacyTerrainData.CellHeight) - LegacyTerrainData.CellsYStart;
        z = Mathf.RoundToInt(centeredPoint.Z / LegacyTerrainData.CellWidth) + _data.Depth / 2;
        return IsCellInBounds(x, y, z);
    }

    public bool TryGetEmptyPlacementCell(Vector3 worldPosition, Vector3 worldNormal, out int x, out int y, out int z)
    {
        Vector3 localPoint = CoordinateSpace.ToLegacy(ToLocal(worldPosition));
        Vector3 localNormal = CoordinateSpace.ToLegacy(GlobalBasis.Inverse() * worldNormal);
        x = Mathf.RoundToInt(localPoint.X / LegacyTerrainData.CellWidth) + _data.Width / 2;
        y = Mathf.RoundToInt(localPoint.Y / LegacyTerrainData.CellHeight) - LegacyTerrainData.CellsYStart;
        z = Mathf.RoundToInt(localPoint.Z / LegacyTerrainData.CellWidth) + _data.Depth / 2;
        if (!IsCellInBounds(x, y, z))
            return false;
        if (!_data.TryGetCell(x, y, z, out _, out _, out _))
            return true;

        Vector3 absoluteNormal = new(Mathf.Abs(localNormal.X), Mathf.Abs(localNormal.Y), Mathf.Abs(localNormal.Z));
        if (absoluteNormal == Vector3.Zero)
            return false;
        if (absoluteNormal.X > absoluteNormal.Y)
        {
            if (absoluteNormal.X > absoluteNormal.Z)
                x += localNormal.X < 0f ? -1 : 1;
            else
                z += localNormal.Z < 0f ? -1 : 1;
        }
        else if (absoluteNormal.Y > absoluteNormal.Z)
        {
            y += localNormal.Y < 0f ? -1 : 1;
        }
        else
        {
            z += localNormal.Z < 0f ? -1 : 1;
        }
        return IsCellInBounds(x, y, z) && !_data.TryGetCell(x, y, z, out _, out _, out _);
    }

    private bool IsCellInBounds(int x, int y, int z)
        => (uint)x < (uint)_data.Width && (uint)y < (uint)_data.Height && (uint)z < (uint)_data.Depth;


    public int GetStyleAtLegacyContact(Vector3 legacyPoint, Vector3 legacyNormal)
    {
        Vector3 localPoint = CoordinateSpace.ToLegacy(ToLocal(CoordinateSpace.ToGodot(legacyPoint)));
        Vector3 localNormal = CoordinateSpace.ToLegacy(GlobalBasis.Inverse() * CoordinateSpace.ToGodot(legacyNormal));
        Vector3 centeredPoint = localPoint - new Vector3(0f, HalfHeight, 0f);
        if (IsCellFace(centeredPoint.X / LegacyTerrainData.CellWidth)
            || IsCellFace(centeredPoint.Y / LegacyTerrainData.CellHeight)
            || IsCellFace(centeredPoint.Z / LegacyTerrainData.CellWidth))
        {
            localPoint -= localNormal * HalfHeight;
            centeredPoint = localPoint - new Vector3(0f, HalfHeight, 0f);
        }

        int x = Mathf.RoundToInt(centeredPoint.X / LegacyTerrainData.CellWidth) + _data.Width / 2;
        int y = Mathf.RoundToInt(centeredPoint.Y / LegacyTerrainData.CellHeight) - LegacyTerrainData.CellsYStart;
        int z = Mathf.RoundToInt(centeredPoint.Z / LegacyTerrainData.CellWidth) + _data.Depth / 2;
        return _data.TryGetCell(x, y, z, out _, out ushort style, out _) ? style : 255;
    }

    private static bool IsCellFace(float coordinate)
        => Mathf.Abs(Mathf.Abs(coordinate % 1f) - 0.5f) < 0.0001f;

    public bool SetCell(int x, int y, int z, int shape, int style, int direction)
    {
        if (!_data.SetCell(x, y, z, shape, style, direction))
            return false;
        _dirty = true;
        return true;
    }

    public void FlushUpdates()
    {
        if (!_dirty)
            return;
        _dirty = false;
        Build();
    }

    public string Encode() => _data.Encode();

    private void Build()
    {
        LegacyTerrainData data = _data;
        _surfaces.Clear();
        _materials.Clear();
        BuildGreedyFullBlocks(data);
        foreach (TerrainCell cell in data.EnumerateCells())
        {
            if (cell.Shape == 0)
                continue;

            Vector3 center = GetCellCenterLocal(data, cell.X, cell.Y, cell.Z);
            float directionAngle = -cell.Direction * Mathf.Pi * 0.5f;
            var rotation = new Basis(Vector3.Up, directionAngle);

            switch (cell.Shape)
            {
                case 1:
                    AddHalfBlock(center, rotation, cell.Style);
                    break;
                case 2:
                    AddRamp(center, rotation, cell.Style);
                    break;
                case 3:
                    AddCorner(center, rotation, cell.Style);
                    break;
                default:
                    GD.PushWarning($"Unknown legacy terrain shape {cell.Shape} at ({cell.X},{cell.Y},{cell.Z}); cell skipped.");
                    break;
            }
        }

        var mesh = new ArrayMesh();
        foreach ((ushort style, SurfaceTool surface) in _surfaces.OrderBy(pair => pair.Key))
        {
            surface.SetMaterial(_materials[style]);
            surface.Commit(mesh);
        }

        if (_meshInstance is null && mesh.GetSurfaceCount() > 0)
        {
            _meshInstance = new MeshInstance3D { Name = "TerrainMesh" };
            AddChild(_meshInstance);
        }
        if (_meshInstance is not null)
            _meshInstance.Mesh = mesh;
        if (_preview)
            return;

        if (mesh.GetSurfaceCount() == 0)
        {
            if (_collisionShape is not null)
                _collisionShape.Shape = null;
            return;
        }

        Shape3D? collisionShape = mesh.CreateTrimeshShape();
        if (collisionShape is null)
        {
            GD.PushError("Godot could not create static collision for decoded terrain.");
            if (_collisionShape is not null)
                _collisionShape.Shape = null;
            return;
        }
        if (collisionShape is ConcavePolygonShape3D concaveShape)
            concaveShape.BackfaceCollision = true;

        if (_body is null)
        {
            _body = new StaticBody3D { Name = "TerrainCollision", CollisionLayer = 2u, CollisionMask = 1u };
            _collisionShape = new CollisionShape3D { Name = "TerrainCollisionShape", Shape = collisionShape };
            _body.AddChild(_collisionShape);
            AddChild(_body);
        }
        else
        {
            _collisionShape!.Shape = collisionShape;
        }
    }

    private void BuildGreedyFullBlocks(LegacyTerrainData data)
    {
        int[] mask = new int[Math.Max(
            Math.Max(data.Depth * data.Height, data.Width * data.Depth),
            data.Width * data.Height)];

        for (int axis = 0; axis < 3; axis++)
        {
            int planeCount = axis switch { 0 => data.Width, 1 => data.Height, _ => data.Depth };
            int uCount = axis == 1 ? data.Width : axis == 0 ? data.Depth : data.Width;
            int vCount = axis == 0 ? data.Height : axis == 1 ? data.Depth : data.Height;
            int maskLength = uCount * vCount;

            for (int plane = 0; plane < planeCount; plane++)
            {
                for (int side = 0; side < 2; side++)
                {
                    bool positive = side == 1;
                    Array.Clear(mask, 0, maskLength);

                    for (int v = 0; v < vCount; v++)
                    {
                        int rowStart = v * uCount;
                        for (int u = 0; u < uCount; u++)
                        {
                            int x = axis == 0 ? plane : u;
                            int y = axis == 1 ? plane : v;
                            int z = axis == 2 ? plane : axis == 0 ? u : v;
                            if (!data.TryGetCell(x, y, z, out byte shape, out ushort style, out _) || shape != 0)
                                continue;

                            int step = positive ? 1 : -1;
                            if (axis == 2)
                                step = -step;
                            int neighborX = x + (axis == 0 ? step : 0);
                            int neighborY = y + (axis == 1 ? step : 0);
                            int neighborZ = z + (axis == 2 ? step : 0);
                            if (data.TryGetCell(neighborX, neighborY, neighborZ, out byte neighborShape, out _, out _) && neighborShape == 0)
                                continue;

                            mask[rowStart + u] = style + 1;
                        }
                    }

                    for (int v = 0; v < vCount; v++)
                    {
                        for (int u = 0; u < uCount; u++)
                        {
                            int styleKey = mask[v * uCount + u];
                            if (styleKey == 0)
                                continue;

                            int width = 1;
                            while (u + width < uCount && mask[v * uCount + u + width] == styleKey)
                                width++;

                            int height = 1;
                            while (v + height < vCount)
                            {
                                int nextRow = (v + height) * uCount + u;
                                int column = 0;
                                while (column < width && mask[nextRow + column] == styleKey)
                                    column++;
                                if (column != width)
                                    break;
                                height++;
                            }

                            for (int row = 0; row < height; row++)
                                Array.Clear(mask, (v + row) * uCount + u, width);
                            AddGreedyFace(data, axis, positive, plane, u, v, width, height, (ushort)(styleKey - 1));
                        }
                    }
                }
            }
        }
    }

    private void AddGreedyFace(
        LegacyTerrainData data,
        int axis,
        bool positive,
        int plane,
        int u,
        int v,
        int width,
        int height,
        ushort style)
    {
        float xLow = 0f;
        float xHigh = 0f;
        float yLow = 0f;
        float yHigh = 0f;
        float zHigh = 0f;
        float zLow = 0f;

        Vector3 a;
        Vector3 b;
        Vector3 c;
        Vector3 d;
        Vector3 normal;
        if (axis == 0)
        {
            yLow = (v + LegacyTerrainData.CellsYStart) * LegacyTerrainData.CellHeight;
            yHigh = (v + height + LegacyTerrainData.CellsYStart) * LegacyTerrainData.CellHeight;
            zHigh = (data.Depth * 0.5f + 0.5f - u) * LegacyTerrainData.CellWidth;
            zLow = (data.Depth * 0.5f + 0.5f - u - width) * LegacyTerrainData.CellWidth;
            float x = (plane - data.Width * 0.5f + (positive ? 0.5f : -0.5f)) * LegacyTerrainData.CellWidth;
            if (positive)
            {
                a = new Vector3(x, yLow, zHigh);
                b = new Vector3(x, yLow, zLow);
                c = new Vector3(x, yHigh, zLow);
                d = new Vector3(x, yHigh, zHigh);
                normal = Vector3.Right;
            }
            else
            {
                a = new Vector3(x, yLow, zLow);
                b = new Vector3(x, yLow, zHigh);
                c = new Vector3(x, yHigh, zHigh);
                d = new Vector3(x, yHigh, zLow);
                normal = Vector3.Left;
            }
        }
        else if (axis == 1)
        {
            xLow = (u - data.Width * 0.5f - 0.5f) * LegacyTerrainData.CellWidth;
            xHigh = (u + width - data.Width * 0.5f - 0.5f) * LegacyTerrainData.CellWidth;
            zHigh = (data.Depth * 0.5f + 0.5f - v) * LegacyTerrainData.CellWidth;
            zLow = (data.Depth * 0.5f + 0.5f - v - height) * LegacyTerrainData.CellWidth;
            float y = (plane + LegacyTerrainData.CellsYStart + (positive ? 1f : 0f)) * LegacyTerrainData.CellHeight;
            if (positive)
            {
                a = new Vector3(xLow, y, zHigh);
                b = new Vector3(xHigh, y, zHigh);
                c = new Vector3(xHigh, y, zLow);
                d = new Vector3(xLow, y, zLow);
                normal = Vector3.Up;
            }
            else
            {
                a = new Vector3(xLow, y, zLow);
                b = new Vector3(xHigh, y, zLow);
                c = new Vector3(xHigh, y, zHigh);
                d = new Vector3(xLow, y, zHigh);
                normal = Vector3.Down;
            }
        }
        else
        {
            xLow = (u - data.Width * 0.5f - 0.5f) * LegacyTerrainData.CellWidth;
            xHigh = (u + width - data.Width * 0.5f - 0.5f) * LegacyTerrainData.CellWidth;
            yLow = (v + LegacyTerrainData.CellsYStart) * LegacyTerrainData.CellHeight;
            yHigh = (v + height + LegacyTerrainData.CellsYStart) * LegacyTerrainData.CellHeight;
            float z = (data.Depth * 0.5f + (positive ? 0.5f : -0.5f) - plane) * LegacyTerrainData.CellWidth;
            if (positive)
            {
                a = new Vector3(xLow, yLow, z);
                b = new Vector3(xHigh, yLow, z);
                c = new Vector3(xHigh, yHigh, z);
                d = new Vector3(xLow, yHigh, z);
                normal = Vector3.Back;
            }
            else
            {
                a = new Vector3(xHigh, yLow, z);
                b = new Vector3(xLow, yLow, z);
                c = new Vector3(xLow, yHigh, z);
                d = new Vector3(xHigh, yHigh, z);
                normal = Vector3.Forward;
            }
        }

        AddQuad(style, Vector3.Zero, Basis.Identity, a, b, c, d, normal);
    }


    private void AddHalfBlock(Vector3 center, Basis rotation, ushort style)
    {
        float top = 0f;
        float bottom = -HalfHeight;
        AddBox(center + Vector3.Up * ((top + bottom) * 0.5f), rotation, style, HalfWidth, (top - bottom) * 0.5f, HalfWidth);
    }

    private void AddRamp(Vector3 center, Basis rotation, ushort style)
    {
        Vector3 backLeft = new(-HalfWidth, -HalfHeight, -HalfWidth);
        Vector3 backRight = new(HalfWidth, -HalfHeight, -HalfWidth);
        Vector3 frontLeft = new(-HalfWidth, -HalfHeight, HalfWidth);
        Vector3 frontRight = new(HalfWidth, -HalfHeight, HalfWidth);
        Vector3 frontTopLeft = new(-HalfWidth, HalfHeight, HalfWidth);
        Vector3 frontTopRight = new(HalfWidth, HalfHeight, HalfWidth);

        AddQuad(style, center, rotation, backLeft, frontLeft, frontRight, backRight, Vector3.Down);
        AddTriangle(style, center, rotation, backLeft, frontLeft, frontTopLeft, Vector3.Left);
        AddTriangle(style, center, rotation, backRight, frontTopRight, frontRight, Vector3.Right);
        AddQuad(style, center, rotation, frontLeft, frontRight, frontTopRight, frontTopLeft, Vector3.Forward);
        AddQuad(style, center, rotation, backLeft, backRight, frontTopRight, frontTopLeft, Vector3.Up);
    }

    private void AddCorner(Vector3 center, Basis rotation, ushort style)
    {
        Vector3 a = new(-HalfWidth, -HalfHeight, -HalfWidth);
        Vector3 b = new(HalfWidth, -HalfHeight, -HalfWidth);
        Vector3 c = new(HalfWidth, -HalfHeight, HalfWidth);
        Vector3 d = new(-HalfWidth, -HalfHeight, HalfWidth);
        Vector3 peak = new(HalfWidth, HalfHeight, HalfWidth);
        AddQuad(style, center, rotation, a, d, c, b, Vector3.Down);
        AddTriangle(style, center, rotation, a, b, peak, Vector3.Back);
        AddTriangle(style, center, rotation, b, c, peak, Vector3.Right);
        AddTriangle(style, center, rotation, c, d, peak, Vector3.Forward);
        AddTriangle(style, center, rotation, d, a, peak, Vector3.Left);
    }

    private void AddBox(Vector3 center, Basis rotation, ushort style, float halfX, float halfY, float halfZ)
    {
        AddQuad(style, center, rotation, new(-halfX, -halfY, -halfZ), new(-halfX, -halfY, halfZ), new(-halfX, halfY, halfZ), new(-halfX, halfY, -halfZ), Vector3.Left);
        AddQuad(style, center, rotation, new(halfX, -halfY, halfZ), new(halfX, -halfY, -halfZ), new(halfX, halfY, -halfZ), new(halfX, halfY, halfZ), Vector3.Right);
        AddQuad(style, center, rotation, new(-halfX, halfY, halfZ), new(halfX, halfY, halfZ), new(halfX, halfY, -halfZ), new(-halfX, halfY, -halfZ), Vector3.Up);
        AddQuad(style, center, rotation, new(-halfX, -halfY, -halfZ), new(halfX, -halfY, -halfZ), new(halfX, -halfY, halfZ), new(-halfX, -halfY, halfZ), Vector3.Down);
        AddQuad(style, center, rotation, new(halfX, -halfY, -halfZ), new(-halfX, -halfY, -halfZ), new(-halfX, halfY, -halfZ), new(halfX, halfY, -halfZ), Vector3.Back);
        AddQuad(style, center, rotation, new(-halfX, -halfY, halfZ), new(halfX, -halfY, halfZ), new(halfX, halfY, halfZ), new(-halfX, halfY, halfZ), Vector3.Forward);
    }

    private void AddQuad(ushort style, Vector3 center, Basis rotation, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
    {
        AddTriangle(style, center, rotation, a, b, c, normal);
        AddTriangle(style, center, rotation, a, c, d, normal);
    }

    private void AddTriangle(ushort style, Vector3 center, Basis rotation, Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
    {
        SurfaceTool surface = GetSurface(style);
        Vector3 worldNormal = rotation * normal;
        surface.SetNormal(worldNormal);
        surface.AddVertex(center + rotation * a);
        surface.SetNormal(worldNormal);
        surface.AddVertex(center + rotation * b);
        surface.SetNormal(worldNormal);
        surface.AddVertex(center + rotation * c);
    }

    private SurfaceTool GetSurface(ushort style)
    {
        if (_surfaces.TryGetValue(style, out SurfaceTool? existing))
            return existing;

        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        _surfaces.Add(style, surface);
        _materials.Add(style, CreateMaterial(style, _preview));
        return surface;
    }


    private static StandardMaterial3D CreateMaterial(ushort style, bool preview)
    {
        StandardMaterial3D material;
        if (style < Palette.Length)
        {
            material = new StandardMaterial3D
            {
                AlbedoColor = Palette[style],
                Roughness = 0.82f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            };
        }
        else
        {
            string? textureName = style < StyleTextures.Length ? StyleTextures[style] : null;
            if (textureName is null)
            {
                GD.PushWarning($"Legacy custom terrain style {style} has no local texture; the original project stores that texture in an external workshop item.");
                material = new StandardMaterial3D
                {
                    AlbedoColor = new Color("#f68946"),
                    Roughness = 0.82f,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled
                };
            }
            else
            {
                bool organicStyle = style is >= 18 and <= 26 or 28;
                string directory = organicStyle
                    ? "res://Assets/GameAssets/Environment/OrganicStyleTextures/"
                    : "res://Assets/GameAssets/Environment/NonOrganicStyleTextures/";
                string path = directory + textureName;
                Texture2D? texture = ResourceLoader.Load<Texture2D>(path);
                if (texture is null)
                {
                    GD.PushWarning($"Terrain style {style} texture is missing: {path}.");
                    material = new StandardMaterial3D
                    {
                        AlbedoColor = Colors.White,
                        CullMode = BaseMaterial3D.CullModeEnum.Disabled
                    };
                }
                else
                {
                    material = new StandardMaterial3D
                    {
                        AlbedoTexture = texture,
                        AlbedoColor = Colors.White,
                        Roughness = style == 26 ? 0.35f : 0.82f,
                        Uv1Triplanar = true,
                        Uv1TriplanarSharpness = 1f,
                        TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
                        CullMode = BaseMaterial3D.CullModeEnum.Disabled
                    };
                }
            }
        }

        if (preview)
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            material.AlbedoColor = new Color(0.35f, 0.8f, 1f, 0.48f);
        }
        return material;
    }
}
