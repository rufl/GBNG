using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using GBNG.Domain;
using GBNG.World;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private const int DefaultParticleLanSmokePort = 57_322;
    private static readonly Vector3 ParticleLanSmokePosition = new(17f, 3f, 29f);

    private async void RunParticleLanHostSmoke()
    {
        int exitCode = 0;
        LegacyBehaviorAuthoringWindow? editor = null;
        try
        {
            await WaitForUiFrames(2);
            LegacySaveDocument document = _document ?? throw new InvalidOperationException("The particle LAN smoke project did not load.");
            WorldActorNode player = GetPlayerActor() ?? throw new InvalidOperationException("The particle LAN smoke project has no controllable player.");
            string particleEffectId = FindParticleEffectId(document);
            editor = new LegacyBehaviorAuthoringWindow(
                document,
                player.State,
                OnBehaviorDataChanged,
                _ => null,
                ValidateBehaviorSourceForEditor,
                _catalog);
            AddChild(editor);
            editor.PopupCentered(new Vector2I(1000, 700));
            await WaitForUiFrames(2);
            InvokeSmoke(editor, "CreateCustomBehavior");

            var behaviorCode = (LegacyJavaScriptCodeEdit)editor.FindChild("BehaviorJavaScript", true, false);
            string positionJson = JsonSerializer.Serialize(new
            {
                x = ParticleLanSmokePosition.X,
                y = ParticleLanSmokePosition.Y,
                z = ParticleLanSmokePosition.Z
            });
            behaviorCode.Text = "let particleSmokeSpawned = false;\n"
                + "export function OnTick(api) {\n"
                + "  if (particleSmokeSpawned) return;\n"
                + "  particleSmokeSpawned = true;\n"
                + "  callVoosService('SpawnParticleEffect', { pfxId: "
                + JsonSerializer.Serialize(particleEffectId)
                + ", position: " + positionJson
                + ", rotation: { x: 0, y: 0, z: 0 }, scale: 1 });\n"
                + "}\n";
            InvokeSmoke(editor, "ApplyScript");
            editor.QueueFree();
            editor = null;
            await WaitForUiFrames(2);

            long clientPeerId = 0;
            bool clientReady = false;
            _lanSession.ClientProjectReadyReceived += peerId =>
            {
                clientPeerId = peerId;
                clientReady = true;
            };
            int port = ReadParticleLanSmokePort();
            string hostPath = PrepareNetworkHostProjectCopy()
                ?? throw new InvalidOperationException("The particle LAN smoke project could not be prepared for hosting.");
            Error hostError = _lanSession.Host(hostPath, "Particle smoke", port, "Host");
            if (hostError != Error.Ok)
                throw new InvalidOperationException($"The particle LAN smoke host could not start on port {port}: {_lanSession.Status}");

            ulong readyDeadline = Time.GetTicksMsec() + 45_000UL;
            while (!clientReady && Time.GetTicksMsec() < readyDeadline)
                await WaitForUiFrames(1);
            if (!clientReady)
                throw new TimeoutException("The particle LAN smoke client did not finish loading the hosted project.");

            SetPlayMode(true);
            ulong spawnDeadline = Time.GetTicksMsec() + 10_000UL;
            while (FindParticleLanSmokeEffect() is null && Time.GetTicksMsec() < spawnDeadline)
                await WaitForUiFrames(1);
            if (FindParticleLanSmokeEffect() is null)
                throw new TimeoutException("The host behavior did not create its local particle effect.");

            ulong clientDeadline = Time.GetTicksMsec() + 45_000UL;
            while (clientPeerId > 1
                && _lanSession.Players.Any(playerInfo => playerInfo.Id == clientPeerId)
                && Time.GetTicksMsec() < clientDeadline)
                await WaitForUiFrames(1);
            if (clientPeerId <= 1 || _lanSession.Players.Any(playerInfo => playerInfo.Id == clientPeerId))
                throw new TimeoutException("The particle LAN smoke client did not complete its replicated-effect check.");
            GD.Print($"PARTICLE_LAN_HOST_OK port={port} effect={particleEffectId} hostSpawned=true clientCompleted=true");
        }
        catch (System.Exception exception)
        {
            exitCode = 1;
            GD.PushError($"PARTICLE_LAN_HOST_FAILED: {exception}");
        }
        finally
        {
            if (editor is not null && IsInstanceValid(editor))
                editor.QueueFree();
            if (_playMode)
                SetPlayMode(false);
            _lanSession.Leave();
            GetTree().Quit(exitCode);
        }
    }

    private async void RunParticleLanClientSmoke()
    {
        int exitCode = 0;
        try
        {
            await WaitForUiFrames(2);
            int port = ReadParticleLanSmokePort();
            Error joinError = _lanSession.Join("127.0.0.1", port, "Client");
            if (joinError != Error.Ok)
                throw new InvalidOperationException($"The particle LAN smoke client could not join port {port}: {_lanSession.Status}");

            ulong deadline = Time.GetTicksMsec() + 45_000UL;
            while (FindParticleLanSmokeEffect() is null && Time.GetTicksMsec() < deadline)
                await WaitForUiFrames(1);
            CpuParticles3D? particles = FindParticleLanSmokeEffect();
            if (particles is null)
                throw new TimeoutException($"The client did not render the host's particle effect: status='{_lanSession.Status}'.");
            GD.Print($"PARTICLE_LAN_CLIENT_OK port={port} effectNode={particles.Name} position={particles.Position} replicated=true");
        }
        catch (System.Exception exception)
        {
            exitCode = 1;
            GD.PushError($"PARTICLE_LAN_CLIENT_FAILED: {exception}");
        }
        finally
        {
            if (_playMode)
                SetPlayMode(false, fromHost: true);
            _lanSession.Leave();
            GetTree().Quit(exitCode);
        }
    }

    private CpuParticles3D? FindParticleLanSmokeEffect()
    {
        Vector3 expectedPosition = CoordinateSpace.ToGodot(ParticleLanSmokePosition);
        return _world.GetChildren()
            .OfType<CpuParticles3D>()
            .FirstOrDefault(particles => particles.Name == "LegacyParticleEffect"
                && particles.Position.DistanceTo(expectedPosition) < 0.01f);
    }

    private static string FindParticleEffectId(LegacySaveDocument document)
    {
        if (document.Root["sojoDatabase"]?["sojos"] is JsonArray sojos)
        {
            foreach (JsonNode? node in sojos)
            {
                if (node is JsonObject sojo
                    && LegacySaveDocument.ReadString(sojo["contentType"]) == "ParticleEffect")
                {
                    string id = LegacySaveDocument.ReadString(sojo["id"]);
                    if (id.Length > 0)
                        return id;
                }
            }
        }
        throw new InvalidDataException("The particle LAN smoke project has no stored particle effect.");
    }

    private static int ReadParticleLanSmokePort()
    {
        const string prefix = "--smoke-particles-lan-port=";
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(argument.AsSpan(prefix.Length), out int port)
                && port is >= 1 and <= 65_535)
                return port;
        }
        return DefaultParticleLanSmokePort;
    }
}
