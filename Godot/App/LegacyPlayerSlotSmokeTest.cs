using GBNG.Domain;
using GBNG.Behaviors;
using GBNG.Networking;
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
            LegacyLanPlayer[] players =
            {
                new(101, 1, "Host"),
                new(202, 2, "Client")
            };
            WorldActorNode clientPlayer = CreatePlayerSlotSmokeActor("SlotSmokeClient", isPlayer: true, "202");
            WorldActorNode nonPlayer = CreatePlayerSlotSmokeActor("SlotSmokeNonPlayer", isPlayer: false, string.Empty);
            WorldActorNode hostPlayer = CreatePlayerSlotSmokeActor("SlotSmokeHost", isPlayer: true, "101");
            actors.Add(clientPlayer);
            actors.Add(nonPlayer);
            actors.Add(hostPlayer);

            var playerControlSlots = new LegacyPlayerControlSlots(actors, players, hasPeer: true);
            if (WorldActorNode.FindPlayerActorAtSlot(actors, 1, players, hasPeer: true) != hostPlayer
                || WorldActorNode.FindPlayerActorAtSlot(actors, 2, players, hasPeer: true) != clientPlayer
                || WorldActorNode.FindPlayerActorAtSlot(actors, 3, players, hasPeer: true) is not null
                || WorldActorNode.FindPlayerActorAtSlot(actors, 0, players, hasPeer: true) is not null
                || playerControlSlots.GetActorAtSlot(actors, 1) != hostPlayer
                || playerControlSlots.GetActorAtSlot(actors, 2) != clientPlayer
                || playerControlSlots.GetSlotForActor(nonPlayer.LegacyName) != 0)
                throw new InvalidOperationException("Player slots did not follow controller identities independently of actor order.");

            WorldActorNode duplicatePlayer = CreatePlayerSlotSmokeActor("SlotSmokeDuplicate", isPlayer: true, "101");
            actors.Add(duplicatePlayer);
            playerControlSlots.Reconcile(actors, players, hasPeer: true);
            if (playerControlSlots.GetActorAtSlot(actors, 1) is not null
                || playerControlSlots.GetSlotForActor(hostPlayer.LegacyName) != 0
                || WorldActorNode.FindPlayerActorAtSlot(actors, 1, players, hasPeer: true) is not null)
                throw new InvalidOperationException("Ambiguous player identity selected an arbitrary actor.");
            actors.Remove(duplicatePlayer);
            duplicatePlayer.Free();
            playerControlSlots.Reconcile(actors, players, hasPeer: true);

            if (!playerControlSlots.TryTransferControl(1, hostPlayer.LegacyName, nonPlayer, "101", actors))
                throw new InvalidOperationException("The local player's control transfer was rejected.");
            playerControlSlots.Reconcile(actors, players, hasPeer: true);
            if (playerControlSlots.GetActorAtSlot(actors, 1) != nonPlayer
                || playerControlSlots.GetActorAtSlot(actors, 2) != clientPlayer
                || playerControlSlots.GetSlotForActor(hostPlayer.LegacyName) != 0
                || playerControlSlots.GetSlotForActor(clientPlayer.LegacyName) != 2
                || playerControlSlots.GetSlotForActor(nonPlayer.LegacyName) != 1
                || LegacySaveDocument.ReadString(hostPlayer.State.Data["controllingVirtualPlayerId"]).Length != 0
                || LegacySaveDocument.ReadString(nonPlayer.State.Data["controllingVirtualPlayerId"]) != "101"
                || !hostPlayer.State.IsPlayerControllable
                || !clientPlayer.State.IsPlayerControllable
                || !nonPlayer.State.IsPlayerControllable)
                throw new InvalidOperationException("Transferring host control did not update player identity or preserve the other slot.");
            hostPlayer.State.WriteString("controllingVirtualPlayerId", "101");
            playerControlSlots.Reconcile(actors, players, hasPeer: true);
            if (playerControlSlots.GetActorAtSlot(actors, 1) != nonPlayer
                || playerControlSlots.GetSlotForActor(hostPlayer.LegacyName) != 0)
                throw new InvalidOperationException("Reapplying the configured PlayerNumber panel displaced a transferred player slot.");


            if (playerControlSlots.TryTransferControl(1, hostPlayer.LegacyName, clientPlayer, "101", actors))
                throw new InvalidOperationException("A no-longer-controlled actor initiated a second local transfer.");
            if (!playerControlSlots.TryTransferControl(1, nonPlayer.LegacyName, clientPlayer, "101", actors))
                throw new InvalidOperationException("Transferring control to an assigned player was rejected.");
            playerControlSlots.Reconcile(actors, players, hasPeer: true);
            if (playerControlSlots.GetActorAtSlot(actors, 1) != clientPlayer
                || playerControlSlots.GetActorAtSlot(actors, 2) is not null
                || playerControlSlots.GetSlotForActor(clientPlayer.LegacyName) != 1
                || playerControlSlots.GetSlotForActor(nonPlayer.LegacyName) != 0
                || LegacySaveDocument.ReadString(clientPlayer.State.Data["controllingVirtualPlayerId"]) != "101"
                || !nonPlayer.State.IsPlayerControllable
                || !hostPlayer.State.IsPlayerControllable
                || !clientPlayer.State.IsPlayerControllable)
                throw new InvalidOperationException("Transferring onto another player's actor did not move its identity slot.");

            GD.Print("PLAYER_SLOT_SMOKE_OK identitySlots=true duplicateAmbiguous=true transferUpdatesIdentity=true panelReapplyStable=true");
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

    private static WorldActorNode CreatePlayerSlotSmokeActor(string name, bool isPlayer, string playerId)
    {
        var actor = new WorldActorNode();
        actor.Initialize(new LegacyActorData(new JsonObject
        {
            ["name"] = name,
            ["isPlayerControllable"] = isPlayer,
            ["controllingVirtualPlayerId"] = playerId,
            ["isSolid"] = false
        }), renderable: null);
        return actor;
    }
}
