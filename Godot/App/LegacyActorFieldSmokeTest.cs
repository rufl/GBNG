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
            if (!actor.State.UseConcaveCollider
                || !runtime.GetActorBoolean(actorId, 15)
                || bodyCollider.Shape is not ConcavePolygonShape3D concaveShape
                || concaveShape.GetFaces().Length < 3)
                throw new InvalidOperationException("Boolean setter field 15 did not apply a triangle-mesh collider to a static solid actor.");
            runtime.SetActorBoolean(actorId, 2, true);
            if (bodyCollider.Shape is ConcavePolygonShape3D)
                throw new InvalidOperationException("Enabling actor physics did not restore a convex-safe collider mode.");
            runtime.SetActorBoolean(actorId, 2, false);
            if (bodyCollider.Shape is not ConcavePolygonShape3D)
                throw new InvalidOperationException("Disabling actor physics did not restore its requested static concave collider.");
            runtime.SetActorBoolean(actorId, 1, false);
            if (bodyCollider.Shape is ConcavePolygonShape3D)
                throw new InvalidOperationException("A non-solid actor retained a concave collider.");
            runtime.SetActorBoolean(actorId, 1, true);
            runtime.SetActorBoolean(actorId, 15, false);
            if (actor.State.UseConcaveCollider || bodyCollider.Shape is ConcavePolygonShape3D)
                throw new InvalidOperationException("Disabling useConcaveCollider did not restore the convex box fallback.");

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

            GD.Print("ACTOR_FIELD_SMOKE_OK concaveColliderLive=true bouncinessLive=true booleanSetterIds=true speculativeCcd=true keepUpright=true");
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
