using GBNG.Domain;
using GBNG.Behaviors;
using GBNG.World;
using Godot;
using System.Text.Json.Nodes;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private void RunPlayerSlotSmoke()
    {
        int exitCode = 0;
        var actors = new List<WorldActorNode>();
        try
        {
            WorldActorNode nonPlayer = CreatePlayerSlotSmokeActor("SlotSmokeNonPlayer", isPlayer: false);
            WorldActorNode hostPlayer = CreatePlayerSlotSmokeActor("SlotSmokeHost", isPlayer: true);
            WorldActorNode clientPlayer = CreatePlayerSlotSmokeActor("SlotSmokeClient", isPlayer: true);
            actors.Add(nonPlayer);
            actors.Add(hostPlayer);
            actors.Add(clientPlayer);

            var playerControlSlots = new LegacyPlayerControlSlots(actors);
            if (WorldActorNode.FindPlayerActorAtSlot(actors, 1) != hostPlayer
                || WorldActorNode.FindPlayerActorAtSlot(actors, 2) != clientPlayer
                || WorldActorNode.FindPlayerActorAtSlot(actors, 3) is not null
                || WorldActorNode.FindPlayerActorAtSlot(actors, 0) is not null
                || playerControlSlots.GetActorAtSlot(actors, 1) != hostPlayer
                || playerControlSlots.GetActorAtSlot(actors, 2) != clientPlayer)
                throw new InvalidOperationException("Player-slot lookup did not resolve host/client actors and reject invalid slots.");

            if (!playerControlSlots.TryTransferControl(1, hostPlayer.LegacyName, nonPlayer))
                throw new InvalidOperationException("The local player's control transfer was rejected.");
            playerControlSlots.Reconcile(actors);
            if (playerControlSlots.GetActorAtSlot(actors, 1) != nonPlayer
                || playerControlSlots.GetActorAtSlot(actors, 2) != clientPlayer
                || playerControlSlots.GetSlotForActor(hostPlayer.LegacyName) != 0
                || playerControlSlots.GetSlotForActor(clientPlayer.LegacyName) != 2
                || playerControlSlots.GetSlotForActor(nonPlayer.LegacyName) != 1
                || !hostPlayer.State.IsPlayerControllable
                || !clientPlayer.State.IsPlayerControllable
                || !nonPlayer.State.IsPlayerControllable)
                throw new InvalidOperationException("Transferring host control changed other controllable actors or shifted LAN player slots.");

            if (playerControlSlots.TryTransferControl(1, hostPlayer.LegacyName, clientPlayer))
                throw new InvalidOperationException("A no-longer-controlled actor initiated a second local transfer.");
            if (!playerControlSlots.TryTransferControl(1, nonPlayer.LegacyName, clientPlayer))
                throw new InvalidOperationException("Transferring control to an assigned player was rejected.");
            playerControlSlots.Reconcile(actors);
            if (playerControlSlots.GetActorAtSlot(actors, 1) != clientPlayer
                || playerControlSlots.GetActorAtSlot(actors, 2) is not null
                || playerControlSlots.GetSlotForActor(clientPlayer.LegacyName) != 1
                || playerControlSlots.GetSlotForActor(nonPlayer.LegacyName) != 0
                || !nonPlayer.State.IsPlayerControllable
                || !hostPlayer.State.IsPlayerControllable
                || !clientPlayer.State.IsPlayerControllable)
                throw new InvalidOperationException("Transferring onto an assigned player did not move its slot without clearing controllability flags.");

            GD.Print("PLAYER_SLOT_SMOKE_OK transferPreservesOtherPlayers=true stableSlots=true assignedTargetMovesSlot=true");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"PLAYER_SLOT_SMOKE_FAILED: {exception}");
        }
        finally
        {
            foreach (WorldActorNode actor in actors)
                if (GodotObject.IsInstanceValid(actor))
                    actor.Free();
            GetTree().Quit(exitCode);
        }
    }

    private static WorldActorNode CreatePlayerSlotSmokeActor(string name, bool isPlayer)
    {
        var actor = new WorldActorNode();
        actor.Initialize(new LegacyActorData(new JsonObject
        {
            ["name"] = name,
            ["isPlayerControllable"] = isPlayer,
            ["isSolid"] = false
        }), renderable: null);
        return actor;
    }
}
