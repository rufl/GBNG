using GBNG.Assets;
using GBNG.Domain;
using Godot;

namespace GBNG.World;

public partial class WorldActorNode : RigidBody3D
{
    private RayCast3D? _groundProbe;

    public LegacyActorData State { get; private set; } = null!;
    public string LegacyName => State.Name;
    public string RenderableUri => State.RenderableUri;
    public bool IsGrounded => _groundProbe?.IsColliding() ?? false;

    public void Initialize(LegacyActorData state, RenderableEntry? renderable)
    {
        State = state;
        Name = state.Name.Replace('/', '_').Replace(':', '_');
        SetMeta("legacy_name", state.Name);
        GlobalTransform = state.GetWorldTransform();
        Mass = state.Mass;
        GravityScale = state.EnableGravity ? 1f : 0f;
        LinearDamp = state.Drag;
        AngularDamp = state.AngularDrag;
        LockRotation = State.FreezeRotations;
        PhysicsMaterialOverride = new PhysicsMaterial { Bounce = state.Bounciness, Friction = 0.7f };
        CollisionLayer = state.IsSolid ? 1u : 0u;
        CollisionMask = state.IsSolid ? 1u : 0u;
        Freeze = true;
        FreezeMode = FreezeModeEnum.Static;
        LinearVelocity = state.Velocity;
        AngularVelocity = state.AngularVelocity;

        if (renderable is null)
        {
            if (!string.IsNullOrEmpty(state.RenderableUri))
                GD.PushWarning($"No built-in asset catalog entry for '{state.RenderableUri}' ({state.Name}).");
            return;
        }

        string? modelPath = renderable.SelectPrimaryModel();
        if (modelPath is null)
            return;

        PackedScene? packedScene = ResourceLoader.Load<PackedScene>(modelPath);
        if (packedScene is null)
        {
            GD.PushError($"Could not load converted model '{modelPath}' for '{state.RenderableUri}'.");
            return;
        }

        Node model = packedScene.Instantiate();
        if (model is not Node3D modelRoot)
        {
            model.QueueFree();
            GD.PushError($"Converted model '{modelPath}' is not a 3D scene.");
            return;
        }

        modelRoot.Name = "Renderable";
        modelRoot.Position = state.RenderableOffset;
        modelRoot.Quaternion = state.RenderableRotation;
        AddChild(modelRoot);
        ApplyTint(modelRoot, state.Tint);
    }

    public override void _Ready()
    {
        CreateBodyCollider();
        if (_groundProbe is not null)
            _groundProbe.Enabled = true;
    }

    public void SetPlayMode(bool enabled)
    {
        if (State is null)
            return;

        Freeze = !enabled || !State.EnablePhysics;
        GravityScale = State.EnableGravity ? 1f : 0f;
        Mass = State.Mass;
        LinearDamp = State.Drag;
        AngularDamp = State.AngularDrag;
        LockRotation = State.FreezeRotations;
        CollisionLayer = State.IsSolid ? 1u : 0u;
        CollisionMask = State.IsSolid ? 1u : 0u;
        foreach (Node3D child in FindChildren("*", "Node3D", true, false).OfType<Node3D>())
        {
            if (child.Name == "Renderable" && State.HideInPlayMode)
                child.Visible = !enabled;
        }
    }

    public void SaveTransform()
    {
        State.SetWorldTransform(GlobalTransform);
        State.SetVelocity(LinearVelocity);
        State.SetAngularVelocity(AngularVelocity);
    }

    private void CreateBodyCollider()
    {
        Aabb? bounds = null;
        foreach (Node node in FindChildren("*", "MeshInstance3D", true, false))
        {
            if (node is not MeshInstance3D meshInstance || meshInstance.Mesh is null)
                continue;

            Aabb meshBounds = meshInstance.GetAabb();
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 localPoint = meshBounds.Position + new Vector3(
                    (corner & 1) == 0 ? 0f : meshBounds.Size.X,
                    (corner & 2) == 0 ? 0f : meshBounds.Size.Y,
                    (corner & 4) == 0 ? 0f : meshBounds.Size.Z);
                Vector3 actorPoint = ToLocal(meshInstance.ToGlobal(localPoint));
                if (bounds is null)
                {
                    bounds = new Aabb(actorPoint, Vector3.Zero);
                }
                else
                {
                    Aabb current = bounds.Value;
                    current = current.Expand(actorPoint);
                    bounds = current;
                }
            }
        }

        if (bounds is null || bounds.Value.Size.LengthSquared() < 0.0001f)
        {
            _groundProbe = new RayCast3D
            {
                Name = "GroundProbe",
                TargetPosition = new Vector3(0f, -0.3f, 0f),
                ExcludeParent = true,
                Enabled = true
            };
            AddChild(_groundProbe);
            return;
        }

        Aabb actorBounds = bounds.Value;
        Vector3 bodySize = actorBounds.Size;
        bodySize.X = Mathf.Max(bodySize.X, 0.05f);
        bodySize.Y = Mathf.Max(bodySize.Y, 0.05f);
        bodySize.Z = Mathf.Max(bodySize.Z, 0.05f);
        var collider = new CollisionShape3D
        {
            Name = "ActorCollision",
            Shape = new BoxShape3D { Size = bodySize },
            Position = actorBounds.Position + actorBounds.Size * 0.5f
        };
        AddChild(collider);

        _groundProbe = new RayCast3D
        {
            Name = "GroundProbe",
            TargetPosition = new Vector3(0f, -(bodySize.Y * 0.5f + 0.22f), 0f),
            ExcludeParent = true,
            Enabled = true
        };
        AddChild(_groundProbe);
    }

    private static void ApplyTint(Node root, Color tint)
    {
        if (tint == Colors.White)
            return;

        foreach (Node child in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D meshInstance || meshInstance.Mesh is not Mesh mesh)
                continue;

            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                Material? activeMaterial = meshInstance.GetActiveMaterial(surface);
                if (activeMaterial is not BaseMaterial3D baseMaterial)
                    continue;

                if (baseMaterial.Duplicate() is BaseMaterial3D tintedMaterial)
                {
                    tintedMaterial.AlbedoColor *= tint;
                    meshInstance.SetSurfaceOverrideMaterial(surface, tintedMaterial);
                }
            }
        }
    }
}
