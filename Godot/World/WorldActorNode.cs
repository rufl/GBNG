using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GBNG.Assets;
using GBNG.Domain;
using GBNG.Networking;
using Godot;

namespace GBNG.World;

public readonly record struct LegacyBehaviorMessage(string Name, string OtherActorName, int BlockStyle);
public partial class WorldActorNode : RigidBody3D
{
    private Node3D? _renderableRoot;
    private LegacyCommentPanelRenderer? _commentPanelRenderer;
    private bool _billboardTextPanel;
    private RayCast3D? _groundProbe;
    private bool _playMode;
    private ulong _oneShotAnimationGeneration;
    private readonly List<LegacyBehaviorMessage> _pendingBehaviorMessages = new();
    private HashSet<ulong> _previousContactColliderIds = new();
    private HashSet<ulong> _currentContactColliderIds = new();
    private readonly Dictionary<ulong, Node3D> _triggerOverlaps = new();
    private HashSet<ulong> _previousTriggerOverlapIds = new();
    private HashSet<ulong> _currentTriggerOverlapIds = new();
    private CollisionShape3D? _bodyCollider;
    private CollisionShape3D? _concaveBodyCollider;
    private readonly List<CollisionShape3D> _convexBodyColliders = new();
    private readonly List<CollisionShape3D> _convexTriggerColliders = new();
    private BoxShape3D? _bodyBoxColliderShape;
    private Vector3 _bodyBoxColliderPosition;
    private CollisionShape3D? _triggerCollider;
    private bool? _lastConcaveColliderMode;
    private Area3D? _triggerArea;
    private bool _hasLegacyCollisionHandlers;
    private ulong _triggerStartPhysicsFrame;

    public LegacyActorData State { get; private set; } = null!;
    public string LegacyName => State.Name;
    public string RenderableUri => State.RenderableUri;
    public bool SupportsCommentText => LegacyCommentPanelRenderer.SupportsRenderable(State.RenderableUri);
    public bool IsBillboardTextPanel => _billboardTextPanel;
    internal Node3D? RenderableRoot => _renderableRoot;
    internal LegacyCommentPanelRenderer? CommentPanelRenderer => _commentPanelRenderer;
    public bool IsGrounded => _groundProbe?.IsColliding() ?? false;
    public bool IsOffstageEffective
    {
        get
        {
            WorldActorNode root = this;
            while (root.GetParent() is WorldActorNode parent)
                root = parent;
            return root.State.IsOffstage;
        }
    }
    public Vector3 DesiredVelocity { get; set; }
    public Vector3 StickyDesiredVelocity { get; set; }
    public Vector3 StickyForce { get; set; }
    public bool UseDesiredVelocity { get; set; }
    public bool IgnoreVerticalDesiredVelocity { get; set; }
    public bool UseStickyDesiredVelocity { get; set; }

    internal static WorldActorNode? FindPlayerActorAtSlot(
        IReadOnlyList<WorldActorNode> actors,
        int targetSlot,
        IReadOnlyCollection<LegacyLanPlayer> players,
        bool hasPeer,
        LegacySaveDocument? document = null)
    {
        if (targetSlot <= 0)
            return null;

        bool isLocalPlayer = !hasPeer;
        bool hasTargetPlayerIdentity = isLocalPlayer;
        long targetPlayerId = 0;
        if (hasPeer)
        {
            foreach (LegacyLanPlayer player in players)
            {
                if (player.SlotNumber != targetSlot)
                    continue;
                if (hasTargetPlayerIdentity)
                    return null;
                hasTargetPlayerIdentity = true;
                targetPlayerId = player.Id;
            }
        }

        WorldActorNode? match = null;
        bool hasControllerIdentity = false;
        foreach (WorldActorNode actor in actors)
        {
            string playerId = LegacySaveDocument.ReadString(actor.State.Data["controllingVirtualPlayerId"]);
            hasControllerIdentity |= hasPeer
                ? playerId.Length > 0 && playerId != "local"
                : playerId == "local";
            bool controlsTarget = hasTargetPlayerIdentity && (isLocalPlayer
                ? playerId == "local"
                : long.TryParse(playerId, NumberStyles.None, CultureInfo.InvariantCulture, out long numericPlayerId)
                    && numericPlayerId == targetPlayerId);
            if (!controlsTarget)
                continue;
            if (match is not null)
                return null;
            match = actor;
        }
        if (match is not null || hasControllerIdentity)
            return match;
        return FindConfiguredPlayerAtSlot(actors, targetSlot, document);
    }

    internal static WorldActorNode? FindConfiguredPlayerAtSlot(
        IReadOnlyList<WorldActorNode> actors,
        int targetSlot,
        LegacySaveDocument? document)
    {
        if (document?.BehaviorDatabase is not JsonObject database
            || database["brainIds"] is not JsonArray brainIds
            || database["brains"] is not JsonArray brains)
            return null;

        WorldActorNode? match = null;
        foreach (WorldActorNode actor in actors)
        {
            int playerNumber = ReadConfiguredPlayerNumber(actor.State.BrainName, brainIds, brains);
            if (playerNumber != targetSlot)
                continue;
            if (match is not null)
                return null;
            match = actor;
        }
        return match;
    }

    private static int ReadConfiguredPlayerNumber(string brainName, JsonArray brainIds, JsonArray brains)
    {
        for (int brainIndex = 0; brainIndex < Math.Min(brainIds.Count, brains.Count); brainIndex++)
        {
            if (LegacySaveDocument.ReadString(brainIds[brainIndex]) != brainName
                || brains[brainIndex] is not JsonObject brain
                || brain["behaviorUses"] is not JsonArray uses)
                continue;

            bool hasPlayerPanel = false;
            int playerNumber = 0;
            foreach (JsonNode? node in uses)
            {
                if (node is not JsonObject use)
                    continue;
                string behaviorUri = LegacySaveDocument.ReadString(use["behaviorUri"]);
                bool isV2Panel = behaviorUri == "builtin:Player Controls Panel v2";
                if (!isV2Panel && behaviorUri != "builtin:Player Controls Panel")
                    continue;
                if (hasPlayerPanel)
                    return 0;

                hasPlayerPanel = true;
                playerNumber = ReadPlayerNumberAssignment(use, isV2Panel ? "PlayerNumber" : "playerNumber", out bool autoAssign);
                if (autoAssign)
                    return 0;
            }
            return hasPlayerPanel ? playerNumber : 0;
        }
        return 0;
    }

    private static int ReadPlayerNumberAssignment(JsonObject use, string propertyName, out bool autoAssign)
    {
        const int defaultPlayerNumber = 1;
        int playerNumber = defaultPlayerNumber;
        autoAssign = false;
        if (use["propertyAssignments"] is not JsonArray assignments)
            return playerNumber;

        foreach (JsonNode? node in assignments)
        {
            if (node is not JsonObject assignment)
                continue;
            string assignedName = LegacySaveDocument.ReadString(assignment["propertyName"]);
            if (assignedName != propertyName && assignedName != "AutoAssign")
                continue;
            string encodedValue = LegacySaveDocument.ReadString(assignment["valueJson"]);
            if (encodedValue.Length == 0)
                continue;

            JsonNode? value;
            try
            {
                value = JsonNode.Parse(encodedValue)?["value"];
            }
            catch (JsonException)
            {
                return 0;
            }
            if (assignedName == "AutoAssign")
            {
                if (value is JsonValue boolValue && boolValue.TryGetValue(out bool enabled))
                    autoAssign = enabled;
            }
            else
            {
                playerNumber = ReadPlayerNumberValue(value);
            }
        }
        return playerNumber;
    }

    private static int ReadPlayerNumberValue(JsonNode? value)
    {
        if (value is not JsonValue jsonValue)
            return 0;
        if (jsonValue.TryGetValue(out int number))
            return number;
        return jsonValue.TryGetValue(out string? text)
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : 0;
    }

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
        _commentPanelRenderer?.SetTint(tint);
    }

    public void SetCommentText(string text)
    {
        State.WriteString("commentText", text);
        _commentPanelRenderer?.SetCommentText(text);
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
        _oneShotAnimationGeneration++;
        ApplyLoopingAnimation();
    }
    public bool PlayOneShotAnimation(string animationName)
    {
        if (_renderableRoot is null)
            return false;
        if (animationName == State.LoopingAnimation)
            return true;

        foreach (AnimationPlayer player in _renderableRoot.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>())
        {
            if (!player.HasAnimation(animationName))
                continue;

            Animation animation = player.GetAnimation(animationName);
            player.Play(animationName, 0.5f);
            player.Seek(0d, true);
            ulong generation = ++_oneShotAnimationGeneration;
            float playbackSpeed = Math.Abs(player.SpeedScale);
            if (playbackSpeed > 0f)
            {
                double duration = Math.Max(0.001d, animation.Length / playbackSpeed);
                GetTree().CreateTimer(duration).Timeout += () => FinishOneShotAnimation(player, generation);
            }
            return true;
        }
        return false;
    }

    private void FinishOneShotAnimation(AnimationPlayer player, ulong generation)
    {
        if (!GodotObject.IsInstanceValid(this)
            || !IsInsideTree()
            || !GodotObject.IsInstanceValid(player)
            || generation != _oneShotAnimationGeneration)
            return;

        string loopingAnimation = State.LoopingAnimation;
        if (loopingAnimation.Length == 0)
        {
            player.Stop();
            return;
        }
        if (player.HasAnimation(loopingAnimation))
        {
            PlayLoopingAnimation(player, loopingAnimation, 0.5f);
            return;
        }

        player.Stop();
        ApplyLoopingAnimation(0.5f);
    }



    public void ApplyLegacyPhysicsSettings()
    {
        if (State is null)
            return;

        bool hasTransformParent = GetParent() is WorldActorNode;
        Freeze = !_playMode || !State.EnablePhysics || IsOffstageEffective || hasTransformParent;
        ContactMonitor = _playMode && State.IsSolid && _hasLegacyCollisionHandlers && !hasTransformParent;
        MaxContactsReported = ContactMonitor ? 16 : 0;
        GravityScale = State.EnableGravity ? 1f : 0f;
        Mass = State.Mass;
        if (PhysicsMaterialOverride is PhysicsMaterial physicsMaterial)
            physicsMaterial.Bounce = State.Bounciness;
        LinearDamp = State.Drag;
        AngularDamp = State.AngularDrag;
        bool keepUpright = LegacySaveDocument.ReadBool(State.Data["keepUpright"]);
        LockRotation = State.FreezeRotations;
        AxisLockAngularX = State.FreezeRotations || keepUpright;
        AxisLockAngularY = State.FreezeRotations;
        AxisLockAngularZ = State.FreezeRotations || keepUpright;
        AxisLockLinearX = State.FreezeX;
        AxisLockLinearY = State.FreezeY;
        AxisLockLinearZ = State.FreezeZ;
        ContinuousCd = LegacySaveDocument.ReadBool(State.Data["speculativeColDet"]);
        CollisionLayer = !hasTransformParent && State.IsSolid ? 1u : 0u;
        EnsureTriggerArea();
        if (_triggerArea is not null)
        {
            // Solid actors need the sensor only to receive events from other actors' trigger areas.
            bool shouldMonitor = _playMode && _hasLegacyCollisionHandlers && !hasTransformParent;
            _triggerArea.CollisionLayer = hasTransformParent ? 0u : State.IsSolid ? 0u : 4u;
            _triggerArea.CollisionMask = hasTransformParent ? 0u : State.IsSolid ? 4u : 7u;
            if (_triggerArea.Monitoring != shouldMonitor)
            {
                _triggerArea.Monitoring = shouldMonitor;
                _triggerStartPhysicsFrame = Engine.GetPhysicsFrames();
                _triggerOverlaps.Clear();
                _previousTriggerOverlapIds.Clear();
                _currentTriggerOverlapIds.Clear();
            }
        }
        CollisionMask = !hasTransformParent && State.IsSolid ? 3u : 0u;
        UpdateBodyColliderShape();
        if (_renderableRoot is not null)
            _renderableRoot.Visible = !(_playMode && State.HideInPlayMode);
    }

    public void SetPlayMode(bool enabled)
    {
        if (State is null)
            return;

        _playMode = enabled;
        if (!enabled)
        {
            _pendingBehaviorMessages.Clear();
            _previousContactColliderIds.Clear();
            _currentContactColliderIds.Clear();
            _triggerOverlaps.Clear();
            _previousTriggerOverlapIds.Clear();
            _currentTriggerOverlapIds.Clear();
        }
        ApplyLegacyPhysicsSettings();
    }
    public void SetLegacyCollisionHandling(bool enabled)
    {
        if (_hasLegacyCollisionHandlers == enabled)
            return;
        _hasLegacyCollisionHandlers = enabled;
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
                GD.PushWarning($"No asset catalog entry for '{state.RenderableUri}' ({state.Name}).");
            return;
        }

        string? modelPath = renderable.SelectPrimaryModel();
        if (modelPath is null)
            return;

        Node3D? modelRoot = LegacyRenderableCatalog.LoadModelInstance(modelPath);
        if (modelRoot is null)
        {
            GD.PushError($"Could not load model '{modelPath}' for '{state.RenderableUri}'.");
            return;
        }
        modelRoot.Name = "Renderable";
        modelRoot.Position = state.RenderableOffset;
        modelRoot.Quaternion = state.RenderableRotation;
        _renderableRoot = modelRoot;
        AddChild(modelRoot);
        if (LegacyCommentPanelRenderer.SupportsRenderable(state.RenderableUri))
        {
            foreach (MeshInstance3D panelMesh in modelRoot.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
                panelMesh.Visible = false;

            _billboardTextPanel = state.RenderableUri == "builtin:BuiltinAssets/BillboardTextPanel";
            _commentPanelRenderer = new LegacyCommentPanelRenderer { Name = "LegacyCommentPanelRenderer" };
            modelRoot.AddChild(_commentPanelRenderer);
            _commentPanelRenderer.SetCommentText(state.CommentText);
        }
        SetTint(state.Tint);
    }

    public override void _EnterTree()
    {
        if (State is not null && _playMode)
            ApplyLegacyPhysicsSettings();
    }

    public override void _Ready()
    {
        CreateBodyCollider();
        if (_groundProbe is not null)
            _groundProbe.Enabled = true;
        ApplyLegacyPhysicsSettings();
        ApplyLoopingAnimation();
    }
    public override void _Process(double deltaSeconds)
    {
        if (!_billboardTextPanel || _renderableRoot is null)
            return;

        Camera3D? camera = GetViewport().GetCamera3D();
        if (camera is null)
            return;

        _renderableRoot.GlobalRotation = new Vector3(
            -camera.GlobalRotation.X,
            Mathf.Pi + camera.GlobalRotation.Y,
            0f);
    }
    public override void _PhysicsProcess(double deltaSeconds)
    {
        if (!_playMode || !_hasLegacyCollisionHandlers || State is null || _triggerArea is null || !_triggerArea.Monitoring)
            return;

        _currentTriggerOverlapIds.Clear();
        ulong physicsFrame = Engine.GetPhysicsFrames();
        foreach ((ulong colliderId, Node3D collider) in _triggerOverlaps)
        {
            if (!GodotObject.IsInstanceValid(collider))
                continue;
            _currentTriggerOverlapIds.Add(colliderId);
            WorldActorNode? otherActor = FindActorAncestor(collider);
            if (otherActor is not null)
            {
                if (otherActor == this || IsOffstageEffective || otherActor.IsOffstageEffective)
                    continue;
                QueueBehaviorMessage("Collision", otherActor.LegacyName);
                if (!_previousTriggerOverlapIds.Contains(colliderId))
                    QueueBehaviorMessage("TouchEnter", otherActor.LegacyName);
                continue;
            }

            if ((FindTerrainAncestor(collider) is not null || collider.Name == "StageGroundCollision")
                && !IsOffstageEffective
                && physicsFrame > _triggerStartPhysicsFrame + 1)
                QueueBehaviorMessage("TerrainCollision", blockStyle: 255);
        }

        (_previousTriggerOverlapIds, _currentTriggerOverlapIds) = (_currentTriggerOverlapIds, _previousTriggerOverlapIds);
        _currentTriggerOverlapIds.Clear();
    }

    private void OnTriggerBodyEntered(Node3D body) => TrackTriggerOverlap(body);
    private void OnTriggerAreaEntered(Area3D area) => TrackTriggerOverlap(area);
    private void OnTriggerBodyExited(Node3D body) => _triggerOverlaps.Remove(body.GetInstanceId());
    private void OnTriggerAreaExited(Area3D area) => _triggerOverlaps.Remove(area.GetInstanceId());

    private void TrackTriggerOverlap(Node3D collider)
    {
        if (FindActorAncestor(collider) == this)
            return;
        _triggerOverlaps[collider.GetInstanceId()] = collider;
    }

    public override void _IntegrateForces(PhysicsDirectBodyState3D state)
    {
        _currentContactColliderIds.Clear();
        if (_playMode && State.IsSolid && _hasLegacyCollisionHandlers)
        {
            for (int contact = 0; contact < state.GetContactCount(); contact++)
            {
                ulong colliderId = state.GetContactColliderId(contact);
                if (!_currentContactColliderIds.Add(colliderId) || state.GetContactColliderObject(contact) is not Node collider)
                    continue;

                WorldActorNode? otherActor = FindActorAncestor(collider);
                if (otherActor is not null)
                {
                    if (otherActor == this || IsOffstageEffective || otherActor.IsOffstageEffective)
                        continue;
                    QueueBehaviorMessage("Collision", otherActor.LegacyName);
                    if (!_previousContactColliderIds.Contains(colliderId))
                        QueueBehaviorMessage("TouchEnter", otherActor.LegacyName);
                    continue;
                }

                LegacyTerrainNode? terrain = FindTerrainAncestor(collider);
                bool isStageGround = collider.Name == "StageGroundCollision";
                if (terrain is null && !isStageGround)
                    continue;
                if (IsOffstageEffective)
                    continue;

                Vector3 contactPoint = GlobalTransform * state.GetContactLocalPosition(contact);
                Vector3 contactNormal = GlobalBasis * state.GetContactLocalNormal(contact);
                Vector3 legacyPoint = CoordinateSpace.ToLegacy(contactPoint);
                Vector3 legacyNormal = CoordinateSpace.ToLegacy(contactNormal);
                int blockStyle = terrain?.GetStyleAtLegacyContact(legacyPoint, legacyNormal) ?? 18;
                QueueBehaviorMessage("TerrainCollision", blockStyle: blockStyle);
            }
        }

        (_previousContactColliderIds, _currentContactColliderIds) = (_currentContactColliderIds, _previousContactColliderIds);
        _currentContactColliderIds.Clear();
    }

    public void DrainPendingBehaviorMessages(List<LegacyBehaviorMessage> destination)
    {
        destination.AddRange(_pendingBehaviorMessages);
        _pendingBehaviorMessages.Clear();
    }

    private void QueueBehaviorMessage(string messageName, string otherActorName = "", int blockStyle = 0)
    {
        _pendingBehaviorMessages.Add(new LegacyBehaviorMessage(messageName, otherActorName, blockStyle));
    }

    private static WorldActorNode? FindActorAncestor(Node node)
    {
        for (Node? current = node; current is not null; current = current.GetParent())
        {
            if (current is WorldActorNode actor)
                return actor;
        }
        return null;
    }

    private static LegacyTerrainNode? FindTerrainAncestor(Node node)
    {
        for (Node? current = node; current is not null; current = current.GetParent())
        {
            if (current is LegacyTerrainNode terrain)
                return terrain;
        }
        return null;
    }
    private bool ApplyLoopingAnimation(float customBlend = -1f)
    {
        string animationName = State?.LoopingAnimation ?? "";
        if (animationName.Length == 0 || _renderableRoot is null)
            return false;

        foreach (AnimationPlayer player in _renderableRoot.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>())
        {
            if (player.HasAnimation(animationName))
            {
                PlayLoopingAnimation(player, animationName, customBlend);
                return true;
            }
        }

        GD.PushWarning($"Actor '{LegacyName}' requests missing animation '{animationName}'.");
        return false;
    }

    private static void PlayLoopingAnimation(AnimationPlayer player, string animationName, float customBlend)
    {
        player.GetAnimation(animationName).LoopMode = Animation.LoopModeEnum.Linear;
        player.Play(animationName, customBlend);
    }




    public void SaveTransform()
    {
        State.SetWorldTransform(GlobalTransform);
        State.SetVelocity(LinearVelocity);
        State.SetAngularVelocity(AngularVelocity);
    }
    public void ApplyNetworkState()
    {
        if (State is null)
            return;
        DesiredVelocity = CoordinateSpace.ToGodot(State.ReadLegacyVector("desiredVelocity", Vector3.Zero));
        StickyDesiredVelocity = CoordinateSpace.ToGodot(State.ReadLegacyVector("stickyDesiredVelocity", Vector3.Zero));
        StickyForce = CoordinateSpace.ToGodot(State.ReadLegacyVector("stickyForce", Vector3.Zero));
        UseDesiredVelocity = LegacySaveDocument.ReadBool(State.Data["useDesiredVelocity"]);
        IgnoreVerticalDesiredVelocity = LegacySaveDocument.ReadBool(State.Data["ignoreVerticalDesiredVelocity"]);
        UseStickyDesiredVelocity = LegacySaveDocument.ReadBool(State.Data["useStickyDesiredVelocity"]);
        GlobalTransform = State.GetWorldTransform();
        LinearVelocity = State.Velocity;
        AngularVelocity = State.AngularVelocity;
        SetTint(State.Tint);
        SetRenderableOffset(State.ReadLegacyVector("renderableOffset", Vector3.Zero));
        SetRenderableRotation(State.ReadLegacyQuaternion("renderableRotation"));
        SetLoopingAnimation(State.LoopingAnimation);
        ApplyLegacyPhysicsSettings();
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
        _bodyBoxColliderShape = new BoxShape3D { Size = bodySize };
        _bodyBoxColliderPosition = actorBounds.Position + actorBounds.Size * 0.5f;
        var collider = new CollisionShape3D
        {
            Name = "ActorCollision",
            Shape = _bodyBoxColliderShape,
            Position = _bodyBoxColliderPosition
        };
        _bodyCollider = collider;
        AddChild(collider);
        CreateConvexBodyColliders();

        _groundProbe = new RayCast3D
        {
            Name = "GroundProbe",
            TargetPosition = new Vector3(0f, -(bodySize.Y * 0.5f + 0.22f), 0f),
            ExcludeParent = true,
            Enabled = true
        };
        AddChild(_groundProbe);
    }

    private void CreateConvexBodyColliders()
    {
        Transform3D actorInverse = GlobalTransform.AffineInverse();
        int colliderIndex = 0;
        foreach (Node node in FindChildren("*", "MeshInstance3D", true, false))
        {
            if (node is not MeshInstance3D meshInstance || meshInstance.Mesh is not Mesh mesh)
                continue;

            ConvexPolygonShape3D shape = mesh.CreateConvexShape();
            Vector3[] points = shape.GetPoints();
            if (points.Length < 4)
                continue;
            Transform3D meshToActor = actorInverse * meshInstance.GlobalTransform;
            for (int pointIndex = 0; pointIndex < points.Length; pointIndex++)
                points[pointIndex] = meshToActor * points[pointIndex];
            shape.SetPoints(points);

            var collider = new CollisionShape3D
            {
                Name = $"ActorConvexCollision_{colliderIndex++}",
                Shape = shape,
                Disabled = true
            };
            _convexBodyColliders.Add(collider);
            AddChild(collider);
        }
    }

    private void UpdateBodyColliderShape()
    {
        if (_bodyCollider is null || _bodyBoxColliderShape is null || State is null)
            return;

        bool wantsConcaveCollider = State.UseConcaveCollider && State.IsSolid && !State.EnablePhysics;
        if (_lastConcaveColliderMode == wantsConcaveCollider)
            return;

        if (wantsConcaveCollider && _concaveBodyCollider is null)
        {
            ConcavePolygonShape3D? concaveShape = CreateConcaveBodyShape();
            if (concaveShape is not null)
            {
                var collider = new CollisionShape3D
                {
                    Name = "ActorConcaveCollision",
                    Shape = concaveShape,
                    Disabled = true
                };
                _concaveBodyCollider = collider;
                AddChild(collider);
            }
        }

        bool useConcaveCollider = wantsConcaveCollider && _concaveBodyCollider is not null;
        bool useConvexMeshColliders = !useConcaveCollider && _convexBodyColliders.Count > 0;
        SetCollisionShapeDisabled(_bodyCollider, useConcaveCollider || useConvexMeshColliders);
        if (_concaveBodyCollider is not null)
            SetCollisionShapeDisabled(_concaveBodyCollider, !useConcaveCollider);
        foreach (CollisionShape3D collider in _convexBodyColliders)
            SetCollisionShapeDisabled(collider, useConcaveCollider);
        if (_triggerCollider is not null)
            SetCollisionShapeDisabled(_triggerCollider, _convexBodyColliders.Count > 0);
        foreach (CollisionShape3D collider in _convexTriggerColliders)
            SetCollisionShapeDisabled(collider, false);
        _lastConcaveColliderMode = wantsConcaveCollider;
    }

    private static void SetCollisionShapeDisabled(CollisionShape3D collider, bool disabled)
    {
        if (collider.IsInsideTree())
            collider.SetDeferred(CollisionShape3D.PropertyName.Disabled, disabled);
        else
            collider.Disabled = disabled;
    }

    private ConcavePolygonShape3D? CreateConcaveBodyShape()
    {
        List<Vector3> faces = new();
        Transform3D actorInverse = GlobalTransform.AffineInverse();
        foreach (Node node in FindChildren("*", "MeshInstance3D", true, false))
        {
            if (node is not MeshInstance3D meshInstance || meshInstance.Mesh is not Mesh mesh)
                continue;

            Vector3[] meshFaces = mesh.GetFaces();
            if (meshFaces.Length == 0)
                continue;
            Transform3D meshToActor = actorInverse * meshInstance.GlobalTransform;
            foreach (Vector3 faceVertex in meshFaces)
                faces.Add(meshToActor * faceVertex);
        }

        if (faces.Count < 3)
            return null;

        var shape = new ConcavePolygonShape3D { BackfaceCollision = true };
        shape.SetFaces(faces.ToArray());
        return shape;
    }

    private void EnsureTriggerArea()
    {
        if (_triggerArea is not null || _bodyCollider is null || _bodyBoxColliderShape is null)
            return;

        _triggerArea = new Area3D
        {
            Name = "ActorTrigger",
            Monitoring = false,
            Monitorable = true
        };
        _triggerArea.BodyEntered += OnTriggerBodyEntered;
        _triggerArea.BodyExited += OnTriggerBodyExited;
        _triggerArea.AreaEntered += OnTriggerAreaEntered;
        _triggerArea.AreaExited += OnTriggerAreaExited;
        AddChild(_triggerArea);
        var triggerCollider = new CollisionShape3D
        {
            Name = "ActorTriggerShape",
            Shape = _bodyBoxColliderShape,
            Position = _bodyBoxColliderPosition,
            Disabled = _convexBodyColliders.Count > 0
        };
        _triggerCollider = triggerCollider;
        _triggerArea.AddChild(triggerCollider);
        foreach (CollisionShape3D bodyMeshCollider in _convexBodyColliders)
        {
            if (bodyMeshCollider.Shape is not ConvexPolygonShape3D convexShape)
                continue;
            var triggerMeshCollider = new CollisionShape3D
            {
                Name = $"ActorTriggerConvexCollision_{_convexTriggerColliders.Count}",
                Shape = convexShape
            };
            _convexTriggerColliders.Add(triggerMeshCollider);
            _triggerArea.AddChild(triggerMeshCollider);
        }
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
