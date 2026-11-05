using GBNG.Domain;
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

            if (WorldActorNode.FindPlayerActorAtSlot(actors, 1) != hostPlayer
                || WorldActorNode.FindPlayerActorAtSlot(actors, 2) != clientPlayer
                || WorldActorNode.FindPlayerActorAtSlot(actors, 3) is not null
                || WorldActorNode.FindPlayerActorAtSlot(actors, 0) is not null)
                throw new InvalidOperationException("Player-slot lookup did not resolve host/client actors and reject invalid slots.");

            GD.Print("PLAYER_SLOT_SMOKE_OK hostSlot1=true clientSlot2=true nonPlayersSkipped=true invalidSlotsNull=true");
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
