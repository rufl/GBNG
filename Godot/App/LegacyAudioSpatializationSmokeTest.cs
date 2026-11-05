using GBNG.Audio;
using GBNG.Behaviors;
using GBNG.Domain;
using GBNG.World;
using Godot;
using System.Text.Json.Nodes;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunAudioSpatializationSmoke()
    {
        int exitCode = 0;
        LegacySaveDocument document = _document ?? throw new InvalidOperationException("The audio spatialization smoke project did not load.");
        LegacyBehaviorRuntime runtime = _behaviorRuntime ?? throw new InvalidOperationException("The behavior runtime is not available for the audio spatialization smoke.");
        WorldActorNode actor = _actors.FirstOrDefault() ?? throw new InvalidOperationException("The audio spatialization smoke project has no actors.");
        bool hadSojoDatabase = document.Root.ContainsKey("sojoDatabase");
        JsonNode? originalSojoDatabase = document.Root["sojoDatabase"]?.DeepClone();
        var playbackNodes = new List<Node>();
        string spatializedActorId = Guid.NewGuid().ToString("N");
        string nonSpatializedActorId = Guid.NewGuid().ToString("N");
        string positionalSoundId = Guid.NewGuid().ToString("N");
        try
        {
            await WaitForUiFrames(2);
            JsonObject database = document.Root["sojoDatabase"] as JsonObject
                ?? LegacySaveDocument.EnsureObject(document.Root, "sojoDatabase");
            JsonArray sojos = database["sojos"] as JsonArray ?? new JsonArray();
            database["sojos"] = sojos;
            sojos.Add(CreateSpatializationSmokeSound(spatializedActorId, spatialized: true));
            sojos.Add(CreateSpatializationSmokeSound(nonSpatializedActorId, spatialized: false));
            sojos.Add(CreateSpatializationSmokeSound(positionalSoundId, spatialized: false));

            HashSet<Node> existingActorChildren = actor.GetChildren().ToHashSet();
            if (runtime.PlaySound(CreateSpatializationSmokeRequest(spatializedActorId, actor.LegacyName, Vector3.Zero)) != "true")
                throw new InvalidOperationException("A spatialized actor sound was not played.");
            AudioStreamPlayer3D spatialPlayer = actor.GetChildren().OfType<AudioStreamPlayer3D>()
                .FirstOrDefault(player => !existingActorChildren.Contains(player))
                ?? throw new InvalidOperationException("Spatialized actor sounds must use AudioStreamPlayer3D.");
            playbackNodes.Add(spatialPlayer);
            if (!spatialPlayer.Playing
                || spatialPlayer.Bus != LegacyAudioSettings.SfxBusName
                || !spatialPlayer.GlobalPosition.IsEqualApprox(actor.GlobalPosition)
                || spatialPlayer.Stream is not AudioStreamWav { Data.Length: > 0 })
                throw new InvalidOperationException("The spatialized actor sound did not play at its actor position on the SFX bus.");

            existingActorChildren = actor.GetChildren().ToHashSet();
            if (runtime.PlaySound(CreateSpatializationSmokeRequest(nonSpatializedActorId, actor.LegacyName, Vector3.Zero)) != "true")
                throw new InvalidOperationException("A non-spatialized actor sound was not played.");
            AudioStreamPlayer nonSpatialPlayer = actor.GetChildren().OfType<AudioStreamPlayer>()
                .FirstOrDefault(player => !existingActorChildren.Contains(player))
                ?? throw new InvalidOperationException("Non-spatialized actor sounds must use AudioStreamPlayer.");
            playbackNodes.Add(nonSpatialPlayer);
            if (!nonSpatialPlayer.Playing
                || nonSpatialPlayer.Bus != LegacyAudioSettings.SfxBusName
                || nonSpatialPlayer.Stream is not AudioStreamWav { Data.Length: > 0 })
                throw new InvalidOperationException("The non-spatialized actor sound did not play through the non-positional SFX player.");

            HashSet<Node> existingWorldChildren = _world.GetChildren().ToHashSet();
            Vector3 legacyPosition = new(7f, 3f, -5f);
            if (runtime.PlaySound(CreateSpatializationSmokeRequest(positionalSoundId, "", legacyPosition)) != "true")
                throw new InvalidOperationException("A positional sound without an actor was not played.");
            AudioStreamPlayer3D positionalPlayer = _world.GetChildren().OfType<AudioStreamPlayer3D>()
                .FirstOrDefault(player => !existingWorldChildren.Contains(player))
                ?? throw new InvalidOperationException("Position-based sounds must use AudioStreamPlayer3D.");
            playbackNodes.Add(positionalPlayer);
            if (!positionalPlayer.Playing
                || positionalPlayer.Bus != LegacyAudioSettings.SfxBusName
                || !positionalPlayer.GlobalPosition.IsEqualApprox(CoordinateSpace.ToGodot(legacyPosition)))
                throw new InvalidOperationException("The position-based sound did not preserve its legacy world position.");

            GD.Print("AUDIO_SPATIALIZATION_SMOKE_OK actor3D=true actor2D=true position3D=true");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"AUDIO_SPATIALIZATION_SMOKE_FAILED: {exception}");
        }
        finally
        {
            foreach (Node player in playbackNodes)
            {
                if (!GodotObject.IsInstanceValid(player))
                    continue;
                if (player is AudioStreamPlayer audioPlayer)
                    audioPlayer.Stop();
                else if (player is AudioStreamPlayer3D spatialAudioPlayer)
                    spatialAudioPlayer.Stop();
                player.QueueFree();
            }
            if (hadSojoDatabase)
                document.Root["sojoDatabase"] = originalSojoDatabase?.DeepClone();
            else
                document.Root.Remove("sojoDatabase");
            GetTree().Quit(exitCode);
        }
    }

    private static JsonObject CreateSpatializationSmokeSound(string id, bool spatialized)
    {
        var volume = new JsonArray();
        var pitch = new JsonArray();
        for (int index = 0; index < 32; index++)
        {
            volume.Add(16);
            pitch.Add(32);
        }

        var content = new JsonObject
        {
            ["effectType"] = 0,
            ["synthParams"] = new JsonObject
            {
                ["waveShape"] = 0,
                ["speed"] = 16,
                ["volume"] = volume,
                ["pitch"] = pitch
            },
            ["spatialized"] = spatialized
        };
        return new JsonObject
        {
            ["id"] = id,
            ["name"] = "Spatialization smoke",
            ["contentType"] = "SoundEffect",
            ["content"] = content.ToJsonString()
        };
    }

    private static JsonObject CreateSpatializationSmokeRequest(string soundId, string actorName, Vector3 legacyPosition)
        => new()
        {
            ["soundId"] = soundId,
            ["actorName"] = actorName,
            ["position"] = new JsonObject
            {
                ["x"] = legacyPosition.X,
                ["y"] = legacyPosition.Y,
                ["z"] = legacyPosition.Z
            }
        };
}
