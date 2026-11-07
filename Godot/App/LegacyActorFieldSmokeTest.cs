using System;
using System.Linq;
using GBNG.Behaviors;
using GBNG.Domain;
using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunActorFieldSmoke()
    {
        int exitCode = 0;
        string? originalProjectPath = _document?.SourcePath;
        bool originalPlayMode = _playMode;
        try
        {
            await WaitForUiFrames(2);
            if (originalProjectPath is null || _document is null || _behaviorRuntime is null)
                throw new InvalidOperationException("The actor-field smoke requires an open .voos project and behavior runtime.");
            if (!_playMode)
                SetPlayMode(true);
            if (!_playMode)
                throw new InvalidOperationException("Play mode could not start for the actor-field smoke.");

            WorldActorNode actor = _actors.FirstOrDefault(candidate =>
                    candidate.State.TransformParent.Length == 0
                    && !candidate.LegacyName.StartsWith("builtin:", StringComparison.Ordinal)
                    && candidate.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>()
                        .Any(meshInstance => meshInstance.Mesh is Mesh mesh && mesh.GetFaces().Length >= 3))
                ?? throw new InvalidOperationException("The project has no root actor with a triangle mesh for the actor-field smoke.");
            int actorId = _actors.IndexOf(actor);
            LegacyBehaviorRuntime runtime = _behaviorRuntime;
            CollisionShape3D bodyCollider = actor.GetNode<CollisionShape3D>("ActorCollision");
            actor.State.WriteBool("isSolid", true);
            actor.State.WriteBool("enablePhysics", false);
            actor.State.WriteBool("useConcaveCollider", false);
            actor.ApplyLegacyPhysicsSettings();
            runtime.SetActorBoolean(actorId, 15, true);
            await WaitForUiFrames(2);
            CollisionShape3D concaveCollider = actor.GetNode<CollisionShape3D>("ActorConcaveCollision");
            if (!actor.State.UseConcaveCollider
                || !runtime.GetActorBoolean(actorId, 15)
                || concaveCollider.Shape is not ConcavePolygonShape3D concaveShape
                || concaveShape.GetFaces().Length < 3
                || concaveCollider.Disabled
                || !bodyCollider.Disabled)
                throw new InvalidOperationException("Boolean setter field 15 did not apply a triangle-mesh collider to a static solid actor.");

            runtime.SetActorBoolean(actorId, 2, true);
            await WaitForUiFrames(2);
            CollisionShape3D[] convexBodyColliders = actor.GetChildren().OfType<CollisionShape3D>()
                .Where(collider => collider.Shape is ConvexPolygonShape3D convex && convex.GetPoints().Length >= 4)
                .ToArray();
            if (convexBodyColliders.Length == 0
                || convexBodyColliders.All(collider => collider.Disabled)
                || !bodyCollider.Disabled
                || !concaveCollider.Disabled)
                throw new InvalidOperationException("Enabling actor physics did not activate per-mesh convex body colliders.");
            Area3D triggerArea = actor.GetNode<Area3D>("ActorTrigger");
            CollisionShape3D[] convexTriggerColliders = triggerArea.GetChildren().OfType<CollisionShape3D>()
                .Where(collider => collider.Shape is ConvexPolygonShape3D convex && convex.GetPoints().Length >= 4)
                .ToArray();
            if (convexTriggerColliders.Length == 0 || convexTriggerColliders.Any(collider => collider.Disabled))
                throw new InvalidOperationException("The actor trigger area did not use enabled per-mesh convex colliders.");

            runtime.SetActorBoolean(actorId, 2, false);
            await WaitForUiFrames(2);
            if (concaveCollider.Disabled || convexBodyColliders.Any(collider => !collider.Disabled))
                throw new InvalidOperationException("Disabling actor physics did not restore its requested static concave collider.");
            runtime.SetActorBoolean(actorId, 1, false);
            await WaitForUiFrames(2);
            if (!concaveCollider.Disabled || convexBodyColliders.All(collider => collider.Disabled))
                throw new InvalidOperationException("A non-solid actor did not switch to convex mesh colliders.");
            runtime.SetActorBoolean(actorId, 1, true);
            runtime.SetActorBoolean(actorId, 15, false);
            await WaitForUiFrames(2);
            if (actor.State.UseConcaveCollider || !concaveCollider.Disabled || convexBodyColliders.Any(collider => collider.Disabled))
                throw new InvalidOperationException("Disabling useConcaveCollider did not restore convex mesh collision.");
            actor.GlobalBasis = Basis.Identity;
            actor.GlobalPosition = new Vector3(10000f, 10000f, 10000f);
            runtime.SetActorBoolean(actorId, 8, true);
            try
            {
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                var initialAimState = runtime.GetActorAimState(actor, false, Basis.Identity, Vector3.Zero, true);
                Vector3 aimDirection = initialAimState.Direction.Normalized();
                if (aimDirection == Vector3.Zero)
                    throw new InvalidOperationException("The non-player actor produced a zero aim direction.");
                var aimTrigger = new Area3D
                {
                    Name = "AimSmokeTrigger",
                    CollisionLayer = 4u,
                    CollisionMask = 0u,
                    Monitoring = false,
                    Monitorable = true
                };
                aimTrigger.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One } });
                _world.AddChild(aimTrigger);
                aimTrigger.GlobalPosition = initialAimState.Origin + aimDirection * 2f;
                var aimTarget = new StaticBody3D
                {
                    Name = "AimSmokeTarget",
                    CollisionLayer = 1u,
                    CollisionMask = 0u
                };
                aimTarget.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One } });
                _world.AddChild(aimTarget);
                aimTarget.GlobalPosition = initialAimState.Origin + aimDirection * 5f;
                try
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                    await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                    var aimState = runtime.GetActorAimState(actor, false, Basis.Identity, Vector3.Zero, true);
                    if (aimState.HitPoint == Vector3.Zero)
                        throw new InvalidOperationException("A non-player actor with enableAiming did not perform its configured forward raycast.");
                    float hitDistance = aimState.Origin.DistanceTo(CoordinateSpace.ToGodot(aimState.HitPoint));
                    if (Math.Abs(hitDistance - 4.5f) > 0.2f)
                        throw new InvalidOperationException($"The aim ray hit a trigger or missed the target body; expected 4.5 units, got {hitDistance}.");
                }
                finally
                {
                    aimTrigger.QueueFree();
                    aimTarget.QueueFree();
                }
            }
            finally
            {
                runtime.SetActorBoolean(actorId, 8, false);
            }
            await WaitForUiFrames(2);


            PhysicsMaterial material = actor.PhysicsMaterialOverride
                ?? throw new InvalidOperationException("The root actor has no physics material override.");
            actor.State.WriteFloat("bounciness", 0f);
            material.Bounce = 0f;
            runtime.SetActorFloat(actorId, 0, 0.75d);
            if (Math.Abs(actor.State.Bounciness - 0.75f) > 0.0001f
                || Math.Abs(material.Bounce - 0.75f) > 0.0001f
                || Math.Abs(runtime.GetActorFloat(actorId, 0) - 0.75d) > 0.0001d)
                throw new InvalidOperationException("Bounciness setter field 0 did not update actor state, its physics material, and its getter.");

            actor.State.WriteBool("useStickyDesiredVelocity", false);
            actor.UseStickyDesiredVelocity = false;
            actor.State.WriteBool("speculativeColDet", false);
            actor.ContinuousCd = false;
            runtime.SetActorBoolean(actorId, 17, true);
            if (!actor.UseStickyDesiredVelocity
                || !LegacySaveDocument.ReadBool(actor.State.Data["useStickyDesiredVelocity"])
                || LegacySaveDocument.ReadBool(actor.State.Data["speculativeColDet"])
                || actor.ContinuousCd)
                throw new InvalidOperationException("Boolean setter field 17 did not enable sticky desired velocity independently of speculative collision detection.");

            runtime.SetActorBoolean(actorId, 18, false);
            if (!actor.UseStickyDesiredVelocity || !runtime.GetActorBoolean(actorId, 18))
                throw new InvalidOperationException("Read-only Boolean getter field 18 changed sticky desired velocity.");

            runtime.SetActorBoolean(actorId, 16, true);
            if (!LegacySaveDocument.ReadBool(actor.State.Data["speculativeColDet"])
                || !actor.ContinuousCd
                || !runtime.GetActorBoolean(actorId, 17))
                throw new InvalidOperationException("Boolean setter field 16 did not enable speculative continuous collision detection.");
            runtime.SetActorBoolean(actorId, 16, false);
            if (LegacySaveDocument.ReadBool(actor.State.Data["speculativeColDet"]) || actor.ContinuousCd)
                throw new InvalidOperationException("Disabling speculative collision detection did not restore discrete collision mode.");

            runtime.SetActorBoolean(actorId, 4, false);
            runtime.SetActorBoolean(actorId, 10, true);
            if (actor.LockRotation || !actor.AxisLockAngularX || actor.AxisLockAngularY || !actor.AxisLockAngularZ)
                throw new InvalidOperationException("keepUpright did not lock only the actor's X and Z angular axes.");
            runtime.SetActorBoolean(actorId, 10, false);
            if (actor.AxisLockAngularX || actor.AxisLockAngularY || actor.AxisLockAngularZ)
                throw new InvalidOperationException("Disabling keepUpright did not release its angular axis locks.");

            GD.Print("ACTOR_FIELD_SMOKE_OK concaveColliderLive=true bouncinessLive=true nonPlayerAiming=true triggerIgnore=true booleanSetterIds=true speculativeCcd=true keepUpright=true");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ACTOR_FIELD_SMOKE_FAILED: {exception}");
        }
        finally
        {
            try
            {
                if (!originalPlayMode && _playMode)
                    SetPlayMode(false);
                if (originalProjectPath is not null)
                {
                    LoadProject(originalProjectPath);
                    await WaitForUiFrames(2);
                }
            }
            catch (Exception exception)
            {
                exitCode = 1;
                GD.PushError($"ACTOR_FIELD_SMOKE_CLEANUP_FAILED: {exception}");
            }
        }

        GetTree().Quit(exitCode);
    }
}
