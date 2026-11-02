using GBNG.Assets;
using GBNG.Domain;
using Godot;

namespace GBNG.World;

public partial class WorldActorNode : RigidBody3D
{
    private Node3D? _renderableRoot;
    private RayCast3D? _groundProbe;
    private bool _playMode;

    public LegacyActorData State { get; private set; } = null!;
    public string LegacyName => State.Name;
    public string RenderableUri => State.RenderableUri;
    public bool IsGrounded => _groundProbe?.IsColliding() ?? false;
    public Vector3 DesiredVelocity { get; set; }
    public Vector3 StickyDesiredVelocity { get; set; }
    public Vector3 StickyForce { get; set; }
    public bool UseDesiredVelocity { get; set; }
    public bool IgnoreVerticalDesiredVelocity { get; set; }
    public bool UseStickyDesiredVelocity { get; set; }

    public Aabb GetWorldRenderBounds()
    {
        Aabb? bounds = null;
        foreach (Node node in FindChildren("*", "MeshInstance3D", true, false))
        {
            if (node is not MeshInstance3D meshInstance || meshInstance.Mesh is null)
                continue;

            Aabb localBounds = meshInstance.GetAabb();
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 localPoint = localBounds.Position + new Vector3(
                    (corner & 1) == 0 ? 0f : localBounds.Size.X,
                    (corner & 2) == 0 ? 0f : localBounds.Size.Y,
                    (corner & 4) == 0 ? 0f : localBounds.Size.Z);
                Vector3 worldPoint = meshInstance.ToGlobal(localPoint);
                bounds = bounds is null ? new Aabb(worldPoint, Vector3.Zero) : bounds.Value.Expand(worldPoint);
            }
        }

        return bounds ?? new Aabb(GlobalPosition, Vector3.Zero);
    }


    public void ApplyDesiredVelocity(double deltaSeconds)
    {
        if (!_playMode)
            return;

        if (State.EnablePhysics && StickyForce.LengthSquared() > 0f)
            ApplyCentralForce(StickyForce);

        if (!UseDesiredVelocity && !UseStickyDesiredVelocity)
            return;

        Vector3 velocity = (UseDesiredVelocity ? DesiredVelocity : Vector3.Zero)
            + (UseStickyDesiredVelocity ? StickyDesiredVelocity : Vector3.Zero);
        if (State.EnablePhysics)
        {
            if (Freeze)
                return;
            if (IgnoreVerticalDesiredVelocity)
                velocity.Y = LinearVelocity.Y;
            LinearVelocity = velocity;
        }
        else
        {
            if (IgnoreVerticalDesiredVelocity)
                velocity.Y = 0f;
            GlobalPosition += velocity * (float)deltaSeconds;
        }
    }


    public void SetTint(Color tint)
    {
        if (State is not null)
            State.WriteColor(tint);
        if (_renderableRoot is not null)
            ApplyTint(_renderableRoot, tint);
    }

    public void SetRenderableOffset(Vector3 legacyOffset)
    {
        State.WriteLegacyVector("renderableOffset", legacyOffset);
        if (_renderableRoot is not null)
            _renderableRoot.Position = CoordinateSpace.ToGodot(legacyOffset);
    }

    public void SetRenderableRotation(Quaternion legacyRotation)
    {
        State.WriteLegacyQuaternion("renderableRotation", legacyRotation);
        if (_renderableRoot is not null)
            _renderableRoot.Quaternion = CoordinateSpace.ToGodot(legacyRotation);
    }

    public void SetLoopingAnimation(string animationName)
    {
        State.WriteString("loopingAnimation", animationName);
        ApplyLoopingAnimation();
    }
    public bool PlayOneShotAnimation(string animationName)
    {
        if (_renderableRoot is null)
            return false;
        foreach (AnimationPlayer player in _renderableRoot.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>())
        {
            if (!player.HasAnimation(animationName))
                continue;
            player.Play(animationName);
            return true;
        }
        return false;
    }



    public void ApplyLegacyPhysicsSettings()
    {
        if (State is null)
            return;

        GravityScale = State.EnableGravity ? 1f : 0f;
        Mass = State.Mass;
        LinearDamp = State.Drag;
        AngularDamp = State.AngularDrag;
        LockRotation = State.FreezeRotations;
        AxisLockLinearX = State.FreezeX;
        AxisLockLinearY = State.FreezeY;
        AxisLockLinearZ = State.FreezeZ;
        CollisionLayer = State.IsSolid ? 1u : 0u;
        CollisionMask = State.IsSolid ? 3u : 0u;
        if (_renderableRoot is not null)
            _renderableRoot.Visible = !(_playMode && State.HideInPlayMode);
    }

    public void SetPlayMode(bool enabled)
    {
        if (State is null)
            return;

        _playMode = enabled;
        Freeze = !enabled || !State.EnablePhysics;
        ApplyLegacyPhysicsSettings();
    }

    public void Initialize(LegacyActorData state, RenderableEntry? renderable)
    {
        State = state;
        DesiredVelocity = CoordinateSpace.ToGodot(state.ReadLegacyVector("desiredVelocity", Vector3.Zero));
        StickyDesiredVelocity = CoordinateSpace.ToGodot(state.ReadLegacyVector("stickyDesiredVelocity", Vector3.Zero));
        StickyForce = CoordinateSpace.ToGodot(state.ReadLegacyVector("stickyForce", Vector3.Zero));
        UseDesiredVelocity = LegacySaveDocument.ReadBool(state.Data["useDesiredVelocity"]);
        IgnoreVerticalDesiredVelocity = LegacySaveDocument.ReadBool(state.Data["ignoreVerticalDesiredVelocity"]);
        UseStickyDesiredVelocity = LegacySaveDocument.ReadBool(state.Data["useStickyDesiredVelocity"]);

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
        CollisionMask = state.IsSolid ? 3u : 0u;
        Freeze = true;
        FreezeMode = FreezeModeEnum.Static;
        LinearVelocity = state.Velocity;
        AngularVelocity = state.AngularVelocity;
        ApplyLegacyPhysicsSettings();


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
        _renderableRoot = modelRoot;
        AddChild(modelRoot);
        SetTint(state.Tint);
    }

    public override void _Ready()
    {
        CreateBodyCollider();
        if (_groundProbe is not null)
            _groundProbe.Enabled = true;
        ApplyLoopingAnimation();
    }
    private void ApplyLoopingAnimation()
    {
        string animationName = State?.LoopingAnimation ?? "";
        if (animationName.Length == 0 || _renderableRoot is null)
            return;

        foreach (AnimationPlayer player in _renderableRoot.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>())
        {
            if (player.HasAnimation(animationName))
            {
                player.Play(animationName);
                return;
            }
        }

        GD.PushWarning($"Actor '{LegacyName}' requests missing animation '{animationName}'.");
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
        foreach (Node child in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D meshInstance || meshInstance.Mesh is not Mesh mesh)
                continue;

            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                Material? sourceMaterial = mesh.SurfaceGetMaterial(surface);
                if (sourceMaterial is not BaseMaterial3D sourceBase)
                {
                    meshInstance.SetSurfaceOverrideMaterial(surface, null);
                    continue;
                }

                if (tint == Colors.White)
                {
                    meshInstance.SetSurfaceOverrideMaterial(surface, null);
                    continue;
                }

                if (sourceBase.Duplicate() is BaseMaterial3D tintedMaterial)
                {
                    tintedMaterial.AlbedoColor = sourceBase.AlbedoColor * tint;
                    meshInstance.SetSurfaceOverrideMaterial(surface, tintedMaterial);
                }
            }
        }
    }
}
