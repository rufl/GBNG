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

    private LegacyTerrainNode() { }

    public static LegacyTerrainNode Create(LegacyTerrainData data)
    {
        var terrain = new LegacyTerrainNode { Name = "Terrain" };
        terrain.Build(data);
        return terrain;
    }

    private void Build(LegacyTerrainData data)
    {
        foreach (TerrainCell cell in data.EnumerateCells())
        {
            Vector3 center = new(
                (cell.X - data.Width * 0.5f) * LegacyTerrainData.CellWidth,
                (cell.Y + LegacyTerrainData.CellsYStart) * LegacyTerrainData.CellHeight + HalfHeight,
                -(cell.Z - data.Depth * 0.5f) * LegacyTerrainData.CellWidth);
            float directionAngle = -cell.Direction * Mathf.Pi * 0.5f;
            var rotation = new Basis(Vector3.Up, directionAngle);

            switch (cell.Shape)
            {
                case 0:
                    AddFullBlock(data, cell, center, rotation);
                    break;
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

        if (_surfaces.Count == 0)
            return;

        var mesh = new ArrayMesh();
        foreach ((ushort style, SurfaceTool surface) in _surfaces.OrderBy(pair => pair.Key))
        {
            surface.SetMaterial(_materials[style]);
            surface.Commit(mesh);
        }

        var meshInstance = new MeshInstance3D { Name = "TerrainMesh", Mesh = mesh };
        AddChild(meshInstance);
        if (mesh.GetSurfaceCount() == 0)
            return;

        Shape3D collisionShape = mesh.CreateTrimeshShape();
        if (collisionShape is null)
        {
            GD.PushError("Godot could not create static collision for decoded terrain.");
            return;
        }

        var body = new StaticBody3D { Name = "TerrainCollision" };
        body.AddChild(new CollisionShape3D { Name = "TerrainCollisionShape", Shape = collisionShape });
        AddChild(body);
    }

    private void AddFullBlock(LegacyTerrainData data, TerrainCell cell, Vector3 center, Basis rotation)
    {
        if (!IsFull(data, cell.X - 1, cell.Y, cell.Z))
            AddQuad(cell.Style, center, rotation, new(-HalfWidth, -HalfHeight, -HalfWidth), new(-HalfWidth, -HalfHeight, HalfWidth), new(-HalfWidth, HalfHeight, HalfWidth), new(-HalfWidth, HalfHeight, -HalfWidth), Vector3.Left);
        if (!IsFull(data, cell.X + 1, cell.Y, cell.Z))
            AddQuad(cell.Style, center, rotation, new(HalfWidth, -HalfHeight, HalfWidth), new(HalfWidth, -HalfHeight, -HalfWidth), new(HalfWidth, HalfHeight, -HalfWidth), new(HalfWidth, HalfHeight, HalfWidth), Vector3.Right);
        if (!IsFull(data, cell.X, cell.Y + 1, cell.Z))
            AddQuad(cell.Style, center, rotation, new(-HalfWidth, HalfHeight, HalfWidth), new(HalfWidth, HalfHeight, HalfWidth), new(HalfWidth, HalfHeight, -HalfWidth), new(-HalfWidth, HalfHeight, -HalfWidth), Vector3.Up);
        if (!IsFull(data, cell.X, cell.Y - 1, cell.Z))
            AddQuad(cell.Style, center, rotation, new(-HalfWidth, -HalfHeight, -HalfWidth), new(HalfWidth, -HalfHeight, -HalfWidth), new(HalfWidth, -HalfHeight, HalfWidth), new(-HalfWidth, -HalfHeight, HalfWidth), Vector3.Down);
        if (!IsFull(data, cell.X, cell.Y, cell.Z - 1))
            AddQuad(cell.Style, center, rotation, new(HalfWidth, -HalfHeight, -HalfWidth), new(-HalfWidth, -HalfHeight, -HalfWidth), new(-HalfWidth, HalfHeight, -HalfWidth), new(HalfWidth, HalfHeight, -HalfWidth), Vector3.Back);
        if (!IsFull(data, cell.X, cell.Y, cell.Z + 1))
            AddQuad(cell.Style, center, rotation, new(-HalfWidth, -HalfHeight, HalfWidth), new(HalfWidth, -HalfHeight, HalfWidth), new(HalfWidth, HalfHeight, HalfWidth), new(-HalfWidth, HalfHeight, HalfWidth), Vector3.Forward);
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
        _materials.Add(style, CreateMaterial(style));
        return surface;
    }

    private static bool IsFull(LegacyTerrainData data, int x, int y, int z)
    {
        return data.TryGetCell(x, y, z, out byte shape, out _, out _) && shape == 0;
    }

    private static StandardMaterial3D CreateMaterial(ushort style)
    {
        if (style < Palette.Length)
        {
            return new StandardMaterial3D
            {
                AlbedoColor = Palette[style],
                Roughness = 0.82f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            };
        }

        string? textureName = style < StyleTextures.Length ? StyleTextures[style] : null;
        if (textureName is null)
        {
            GD.PushWarning($"Legacy custom terrain style {style} has no local texture; the original project stores that texture in an external workshop item.");
            return new StandardMaterial3D
            {
                AlbedoColor = new Color("#f68946"),
                Roughness = 0.82f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            };
        }

        string directory = style >= 18 && style <= 28
            ? "res://Assets/GameAssets/Environment/OrganicStyleTextures/"
            : "res://Assets/GameAssets/Environment/NonOrganicStyleTextures/";
        string path = directory + textureName;
        Texture2D? texture = ResourceLoader.Load<Texture2D>(path);
        if (texture is null)
        {
            GD.PushWarning($"Terrain style {style} texture is missing: {path}.");
            return new StandardMaterial3D { AlbedoColor = Colors.White, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        }

        return new StandardMaterial3D
        {
            AlbedoTexture = texture,
            AlbedoColor = Colors.White,
            Roughness = style == 26 ? 0.35f : 0.82f,
            Uv1Triplanar = true,
            Uv1TriplanarSharpness = 1f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
    }
}
