using System.Text.Json.Nodes;
using GBNG.Domain;
using Godot;

namespace GBNG.Behaviors;

public static class LegacyParticleEffectFactory
{
    private static readonly SphereMesh SharedParticleMesh = new()
    {
        Radius = 0.25f,
        Height = 0.5f,
        Material = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        }
    };
    public static CpuParticles3D Create(JsonObject effect, bool stream, int maximumParticles = 4096)
    {
        float lifetime = Math.Max(0.05f, ReadFloat(effect["duration"], 1f));
        float speed = Math.Max(0f, ReadFloat(effect["speed"], 1f));
        float startSize = Math.Max(0.01f, ReadFloat(effect["startSize"], 0.25f));
        float endSize = Math.Max(0.01f, ReadFloat(effect["endSize"], startSize));
        int maximumAmount = Math.Clamp(maximumParticles, 1, 4096);
        int burstCount = Math.Max(1, ReadInt(effect["burstCount"], 32));
        double streamingAmount = Math.Round(burstCount * (double)lifetime);
        int amount = stream
            ? (int)Math.Clamp(streamingAmount, 1d, maximumAmount)
            : Math.Clamp(burstCount, 1, maximumAmount);
        float shapeSize = Math.Max(0f, ReadFloat(effect["shapeSize"], 0.5f));
        int shapeType = Math.Clamp(ReadInt(effect["shapeType"], 1), 0, 4);
        float gravityModifier = ReadFloat(effect["gravityModifier"], 0f);

        var particles = new CpuParticles3D
        {
            Name = "LegacyParticleEffect",
            Amount = amount,
            Lifetime = lifetime,
            OneShot = !stream,
            Explosiveness = stream ? 0f : 1f,
            Emitting = true,
            Mesh = SharedParticleMesh,
            Direction = Vector3.Up,
            Spread = shapeType == 1 ? 180f : 45f,
            InitialVelocityMin = speed,
            InitialVelocityMax = speed,
            Gravity = new Vector3(0f, -9.8f * gravityModifier, 0f),
            LocalCoords = LegacySaveDocument.ReadBool(effect["isLocal"], false),
            ScaleAmountMin = 1f,
            ScaleAmountMax = 1f,
            ScaleAmountCurve = CreateSizeCurve(startSize, endSize),
            AngularVelocityCurve = CreateAngularVelocityCurve(effect["rotationOverLifetime"] as JsonArray),
            ColorRamp = CreateColorGradient(effect["colorOverLifetime"] as JsonArray)
        };

        ConfigureEmissionShape(particles, shapeType, shapeSize);
        return particles;
    }

    private static void ConfigureEmissionShape(CpuParticles3D particles, int legacyShape, float shapeSize)
    {
        switch (legacyShape)
        {
            case 0: // Unity cone: Godot's ring emitter with a zero-degree cone angle.
                particles.EmissionShape = CpuParticles3D.EmissionShapeEnum.Ring;
                particles.EmissionRingAxis = Vector3.Up;
                particles.EmissionRingRadius = shapeSize;
                particles.EmissionRingInnerRadius = 0f;
                particles.EmissionRingHeight = shapeSize;
                particles.EmissionRingConeAngle = 0f;
                break;
            case 1: // Sphere.
                particles.EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere;
                particles.EmissionSphereRadius = shapeSize;
                break;
            case 2: // Box.
                particles.EmissionShape = CpuParticles3D.EmissionShapeEnum.Box;
                particles.EmissionBoxExtents = Vector3.One * shapeSize;
                break;
            case 3: // Flat circular disk.
                particles.EmissionShape = CpuParticles3D.EmissionShapeEnum.Ring;
                particles.EmissionRingAxis = Vector3.Up;
                particles.EmissionRingRadius = shapeSize;
                particles.EmissionRingInnerRadius = 0f;
                particles.EmissionRingHeight = 0f;
                particles.EmissionRingConeAngle = 90f;
                break;
            default: // Flat rectangle.
                particles.EmissionShape = CpuParticles3D.EmissionShapeEnum.Box;
                particles.EmissionBoxExtents = new Vector3(shapeSize, 0.001f, shapeSize);
                break;
        }
    }

    private static Curve CreateSizeCurve(float startSize, float endSize)
    {
        var curve = new Curve
        {
            MinValue = 0f,
            MaxValue = Math.Max(1f, Math.Max(startSize, endSize))
        };
        curve.AddPoint(new Vector2(0f, startSize));
        curve.AddPoint(new Vector2(1f, endSize));
        return curve;
    }

    private static Curve? CreateAngularVelocityCurve(JsonArray? rotationStops)
    {
        if (rotationStops is null || rotationStops.Count == 0)
            return null;

        var points = new List<(float Offset, float Speed)>(rotationStops.Count);
        foreach (JsonNode? node in rotationStops)
        {
            if (node is not JsonObject stop)
                continue;
            points.Add((
                Mathf.Clamp(ReadFloat(stop["position"], 0f), 0f, 1f),
                ReadFloat(stop["value"], 0f)));
        }
        points.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        for (int index = points.Count - 1; index > 0; index--)
        {
            if (Mathf.IsEqualApprox(points[index].Offset, points[index - 1].Offset))
                points.RemoveAt(index - 1);
        }
        if (points.Count == 0)
            return null;

        float minimum = Math.Min(0f, points.Min(point => point.Speed));
        float maximum = Math.Max(1f, points.Max(point => point.Speed));
        var curve = new Curve { MinValue = minimum, MaxValue = maximum };
        if (points.Count == 1)
        {
            curve.AddPoint(new Vector2(0f, points[0].Speed));
            curve.AddPoint(new Vector2(1f, points[0].Speed));
            return curve;
        }
        foreach ((float offset, float speed) in points)
            curve.AddPoint(new Vector2(offset, speed));
        return curve;
    }

    private static Gradient? CreateColorGradient(JsonArray? colorStops)
    {
        if (colorStops is null || colorStops.Count == 0)
            return null;

        var stops = new List<(float Offset, Color Color)>(colorStops.Count);
        foreach (JsonNode? node in colorStops)
        {
            if (node is not JsonObject stop || stop["value"] is not JsonObject color)
                continue;
            stops.Add((
                Mathf.Clamp(ReadFloat(stop["position"], 0f), 0f, 1f),
                new Color(
                    ReadFloat(color["r"], 1f),
                    ReadFloat(color["g"], 1f),
                    ReadFloat(color["b"], 1f),
                    ReadFloat(color["a"], 1f))));
        }
        stops.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));

        if (stops.Count == 0)
            return null;

        var gradient = new Gradient();
        if (stops.Count == 1)
        {
            gradient.SetColor(0, stops[0].Color);
            gradient.SetColor(1, stops[0].Color);
            return gradient;
        }

        gradient.SetOffset(0, stops[0].Offset);
        gradient.SetColor(0, stops[0].Color);
        gradient.SetOffset(1, stops[1].Offset);
        gradient.SetColor(1, stops[1].Color);
        for (int index = 2; index < stops.Count; index++)
            gradient.AddPoint(stops[index].Offset, stops[index].Color);
        return gradient;
    }

    private static float ReadFloat(JsonNode? node, float fallback)
    {
        try
        {
            return LegacySaveDocument.ReadFloat(node, fallback);
        }
        catch (InvalidDataException)
        {
            return fallback;
        }
    }

    private static int ReadInt(JsonNode? node, int fallback)
    {
        try
        {
            return LegacySaveDocument.ReadInt(node, fallback);
        }
        catch (InvalidDataException)
        {
            return fallback;
        }
    }
}
