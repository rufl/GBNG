using System.Text.Json.Nodes;
using GBNG.Behaviors;
using GBNG.Assets;
using GBNG.Domain;
using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp : Node3D
{
    private readonly List<WorldActorNode> _actors = new();
    private LegacyRenderableCatalog _catalog = null!;
    private LegacySaveDocument? _document;
    private LegacyBehaviorRuntime? _behaviorRuntime;
    private LegacyBehaviorUi _behaviorUi = null!;
    private Node3D _world = null!;
    private Camera3D _camera = null!;
    private ItemList _actorList = null!;
    private OptionButton _assetPicker = null!;
    private OptionButton _samplePicker = null!;
    private SpinBox[] _positionFields = Array.Empty<SpinBox>();
    private SpinBox _yawField = null!;
    private Control _welcome = null!;
    private Control _editor = null!;
    private Label _status = null!;
    private Label _selectionTitle = null!;
    private Button _playButton = null!;
    private FileDialog _openDialog = null!;
    private FileDialog _saveDialog = null!;
    private AcceptDialog _errorDialog = null!;
    private WorldActorNode? _selectedActor;

    private LegacyTerrainNode? _terrain;
    private bool _updatingInspector;
    private bool _playMode;
    private float _cameraYaw = Mathf.DegToRad(35f);
    private float _cameraPitch = Mathf.DegToRad(-28f);
    private float _cameraDistance = 26f;
    private Vector3 _cameraTarget = new(0f, 2f, 0f);
    private Vector3 _temporaryCameraOffset;
    private Vector2 _lookAxes;
    private float _mouseWheelRaw;
    private readonly List<Control> _inspectorInputs = new();
    private LineEdit _displayNameField = null!;
    private TextEdit _descriptionField = null!;
    private SpinBox[] _scaleFields = Array.Empty<SpinBox>();
    private SpinBox[] _tintFields = Array.Empty<SpinBox>();
    private CheckBox _solidField = null!;
    private CheckBox _physicsField = null!;
    private CheckBox _gravityField = null!;
    private CheckBox _playerField = null!;


    public override void _Ready()
    {
        _catalog = LegacyRenderableCatalog.Load();
        BuildWorldServices();
        BuildInterface();
        PopulateSamples();
        string[] commandLineArgs = OS.GetCmdlineUserArgs();
        if (commandLineArgs.Length > 0)
        {
            LoadProject(commandLineArgs[0]);
            if (commandLineArgs.Contains("--play", StringComparer.Ordinal))
                SetPlayMode(true);
        }
        else
        {
            ShowWelcome();
        }
    }

    public override void _Process(double delta)
    {
        if (!_playMode || GetPlayerActor() is not WorldActorNode player)
            return;

        WorldActorNode? cameraActor = GetCameraActor(player);
        if (cameraActor is not null)
        {
            _camera.GlobalPosition = cameraActor.GlobalPosition + _temporaryCameraOffset;
            _camera.GlobalRotation = cameraActor.GlobalRotation;
            _camera.Fov = ReadCameraFieldOfView(cameraActor);
            return;
        }

        Vector3 target = player.GlobalPosition + new Vector3(0f, 3f, 0f) + _temporaryCameraOffset;
        _cameraTarget = _cameraTarget.Lerp(target, 1f - Mathf.Exp(-5f * (float)delta));
        UpdateOrbitCamera();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_playMode)
        {
            Vector2 pointerPosition = GetViewport().GetMousePosition();
            _behaviorRuntime?.SetLookAxes(_lookAxes);
            _behaviorRuntime?.SetPointerState(pointerPosition, _mouseWheelRaw, IsPointerOverUi(pointerPosition));
            _behaviorRuntime?.Tick(delta);
        }
        _lookAxes = Vector2.Zero;
        _mouseWheelRaw = 0f;
    }

    private bool IsPointerOverUi(Vector2 viewportPosition)
    {
        Control? hovered = GetViewport().GuiGetHoveredControl();
        return (hovered is not null && hovered != _behaviorUi) || _behaviorUi.IsInteractivePoint(viewportPosition);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseButton)
        {
            if (mouseButton.ButtonIndex == MouseButton.Right)
                Input.MouseMode = mouseButton.Pressed ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            if (mouseButton.ButtonIndex == MouseButton.Left && mouseButton.Pressed && !_playMode)
                SelectActorAt(mouseButton.Position);
            if (mouseButton.Pressed && mouseButton.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                float wheelDelta = 0.1f * mouseButton.Factor;
                _mouseWheelRaw += mouseButton.ButtonIndex == MouseButton.WheelUp ? wheelDelta : -wheelDelta;
            }
            if (mouseButton.ButtonIndex == MouseButton.WheelUp)
                _cameraDistance = Mathf.Max(4f, _cameraDistance * 0.88f);
            if (mouseButton.ButtonIndex == MouseButton.WheelDown)
                _cameraDistance = Mathf.Min(220f, _cameraDistance * 1.12f);
            UpdateOrbitCamera();
        }
        else if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _cameraYaw -= motion.Relative.X * 0.004f;
            _cameraPitch = Mathf.Clamp(_cameraPitch - motion.Relative.Y * 0.004f, -1.35f, 0.15f);
            _lookAxes += new Vector2(-motion.Relative.X * 0.004f, -motion.Relative.Y * 0.004f);
            UpdateOrbitCamera();
        }
        else if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.Escape)
        {
            if (_playMode)
                SetPlayMode(false);
            else
                Input.MouseMode = Input.MouseModeEnum.Visible;
        }
    }

    private void BuildWorldServices()
    {
        var environment = new WorldEnvironment
        {
            Name = "WorldEnvironment",
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color("#182431"),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color("#7d8da0"),
                AmbientLightEnergy = 0.65f
            }
        };
        AddChild(environment);

        var light = new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-52f, -35f, 0f),
            LightEnergy = 1.2f,
            ShadowEnabled = true
        };
        AddChild(light);

        _camera = new Camera3D { Name = "EditorCamera", Current = true, Fov = 55f };
        AddChild(_camera);
        UpdateOrbitCamera();

        _world = new Node3D { Name = "LoadedProject" };
        AddChild(_world);
    }

    private void BuildInterface()
    {
        var layer = new CanvasLayer { Name = "EditorInterface", Layer = 10 };
        AddChild(layer);

        var root = new Control
        {
            Name = "InterfaceRoot",
            AnchorRight = 1f,
            AnchorBottom = 1f,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        layer.AddChild(root);

        _welcome = BuildWelcomePanel();
        root.AddChild(_welcome);
        _editor = BuildEditorPanel();
        root.AddChild(_editor);

        _behaviorUi = new LegacyBehaviorUi { Name = "BehaviorUi", Visible = false };
        _behaviorUi.SetMessageHandler((actorName, messageName, argumentJson) =>
            _behaviorRuntime?.SendUiMessage(actorName, messageName, argumentJson));
        root.AddChild(_behaviorUi);

        _openDialog = new FileDialog
        {
            Title = "Open Game Builder project",
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true
        };
        _openDialog.AddFilter("*.voos", "Game Builder project");
        _openDialog.FileSelected += LoadProject;
        root.AddChild(_openDialog);

        _saveDialog = new FileDialog
        {
            Title = "Save Game Builder project",
            FileMode = FileDialog.FileModeEnum.SaveFile,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            CurrentFile = "untitled.voos"
        };
        _saveDialog.AddFilter("*.voos", "Game Builder project");
        _saveDialog.FileSelected += SaveProjectAs;
        root.AddChild(_saveDialog);

        _errorDialog = new AcceptDialog { Title = "GBNG" };
        root.AddChild(_errorDialog);
    }

    private Control BuildWelcomePanel()
    {
        var center = new CenterContainer
        {
            Name = "Welcome",
            AnchorRight = 1f,
            AnchorBottom = 1f
        };
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(520f, 320f), MouseFilter = Control.MouseFilterEnum.Stop };
        panel.AddThemeStyleboxOverride("panel", MakePanelStyle("#1b2734", "#566e83"));
        center.AddChild(panel);
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 22);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_right", 22);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        panel.AddChild(margin);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 14);
        margin.AddChild(box);
        box.AddChild(CreateLabel("GBNG — New Gen", 30));
        box.AddChild(CreateLabel("Open a .voos project or choose a bundled example.", 16));

        var open = new Button { Text = "Open project…", CustomMinimumSize = new Vector2(0f, 44f) };
        open.Pressed += () => _openDialog.PopupCentered(new Vector2I(1000, 700));
        box.AddChild(open);

        var samples = new HBoxContainer();
        _samplePicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Name = "SamplePicker" };
        samples.AddChild(_samplePicker);
        var openSample = new Button { Text = "Open example" };
        openSample.Pressed += () =>
        {
            if (_samplePicker.Selected >= 0)
                LoadProject(_samplePicker.GetItemMetadata(_samplePicker.Selected).AsString());
        };
        samples.AddChild(openSample);
        box.AddChild(samples);
        box.AddChild(CreateLabel("Original Unity assets and .voos data stay in place. Saves from bundled examples go to a writable copy.", 14));
        return center;
    }

    private Control BuildEditorPanel()
    {
        var layout = new VBoxContainer
        {
            Name = "Editor",
            AnchorRight = 1f,
            AnchorBottom = 1f,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        var header = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        header.AddThemeStyleboxOverride("panel", MakePanelStyle("#151e29", "#334455"));
        layout.AddChild(header);
        var toolbar = new HBoxContainer();
        header.AddChild(toolbar);

        var title = CreateLabel("GBNG", 18);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        toolbar.AddChild(title);
        AddToolbarButton(toolbar, "Open", () => _openDialog.PopupCentered(new Vector2I(1000, 700)));
        AddToolbarButton(toolbar, "Save", SaveProject);
        AddToolbarButton(toolbar, "Save as…", () =>
        {
            _saveDialog.CurrentFile = _document is null ? "project.voos" : Path.GetFileName(_document.SourcePath);
            _saveDialog.PopupCentered(new Vector2I(1000, 700));
        });
        _playButton = new Button { Text = "Play", CustomMinimumSize = new Vector2(84f, 36f) };
        _playButton.Pressed += () => SetPlayMode(!_playMode);
        toolbar.AddChild(_playButton);
        AddToolbarButton(toolbar, "Reset", ResetActors);

        var body = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        layout.AddChild(body);

        var leftPanel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(260f, 0f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        leftPanel.AddThemeStyleboxOverride("panel", MakePanelStyle("#1b2734", "#334455"));
        body.AddChild(leftPanel);
        var left = new VBoxContainer();
        leftPanel.AddChild(left);
        left.AddChild(CreateLabel("Actors", 18));
        _actorList = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SelectMode = ItemList.SelectModeEnum.Single };
        _actorList.ItemSelected += SelectActorByIndex;
        left.AddChild(_actorList);
        left.AddChild(CreateLabel("Add built-in asset", 14));
        _assetPicker = new OptionButton { CustomMinimumSize = new Vector2(0f, 34f) };
        _assetPicker.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        left.AddChild(_assetPicker);
        var addActor = new Button { Text = "Add actor" };
        addActor.Pressed += AddActor;
        left.AddChild(addActor);
        var removeActor = new Button { Text = "Delete selected actor" };
        removeActor.Pressed += RemoveSelectedActor;
        left.AddChild(removeActor);

        var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddChild(spacer);

        var rightPanel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(290f, 0f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        rightPanel.AddThemeStyleboxOverride("panel", MakePanelStyle("#1b2734", "#334455"));
        body.AddChild(rightPanel);
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        rightPanel.AddChild(scroll);
        var inspector = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        inspector.AddThemeConstantOverride("separation", 7);
        scroll.AddChild(inspector);
        _selectionTitle = CreateLabel("Select an actor", 18);
        inspector.AddChild(_selectionTitle);
        inspector.AddChild(CreateLabel("Display name", 14));
        _displayNameField = new LineEdit();
        _displayNameField.TextChanged += UpdateSelectedDisplayName;
        inspector.AddChild(_displayNameField);
        _inspectorInputs.Add(_displayNameField);
        inspector.AddChild(CreateLabel("Description", 14));
        _descriptionField = new TextEdit { CustomMinimumSize = new Vector2(0f, 74f) };
        _descriptionField.TextChanged += UpdateSelectedDescription;
        inspector.AddChild(_descriptionField);
        _inspectorInputs.Add(_descriptionField);
        inspector.AddChild(CreateLabel("World position", 14));
        _positionFields = new SpinBox[3];
        string[] axes = ["X", "Y", "Z"];
        for (int axis = 0; axis < 3; axis++)
        {
            var row = new HBoxContainer();
            row.AddChild(CreateLabel(axes[axis], 14));
            SpinBox field = CreateSpinBox();
            int index = axis;
            field.ValueChanged += value => UpdateSelectedPosition(index, (float)value);
            row.AddChild(field);
            _positionFields[axis] = field;
            inspector.AddChild(row);
        }
        inspector.AddChild(CreateLabel("Yaw (degrees)", 14));
        _yawField = CreateSpinBox();
        _yawField.ValueChanged += value => UpdateSelectedYaw((float)value);
        inspector.AddChild(_yawField);
        inspector.AddChild(CreateLabel("Local scale", 14));
        _scaleFields = new SpinBox[3];
        for (int axis = 0; axis < 3; axis++)
        {
            var row = new HBoxContainer();
            row.AddChild(CreateLabel(axes[axis], 14));
            SpinBox field = CreateSpinBox();
            int index = axis;
            field.ValueChanged += _ => UpdateSelectedScale(index);
            row.AddChild(field);
            _scaleFields[axis] = field;
            _inspectorInputs.Add(field);
            inspector.AddChild(row);
        }
        inspector.AddChild(CreateLabel("Tint (RGBA)", 14));
        _tintFields = new SpinBox[4];
        string[] channels = ["R", "G", "B", "A"];
        for (int channel = 0; channel < 4; channel++)
        {
            var row = new HBoxContainer();
            row.AddChild(CreateLabel(channels[channel], 14));
            SpinBox field = CreateUnitSpinBox();
            field.ValueChanged += _ => UpdateSelectedTint();
            row.AddChild(field);
            _tintFields[channel] = field;
            _inspectorInputs.Add(field);
            inspector.AddChild(row);
        }
        _solidField = CreateFlagField(inspector, "Solid", value => UpdateSelectedBoolean("isSolid", value, true));
        _physicsField = CreateFlagField(inspector, "Physics", value => UpdateSelectedBoolean("enablePhysics", value));
        _gravityField = CreateFlagField(inspector, "Gravity", value => UpdateSelectedBoolean("enableGravity", value, true));
        _playerField = CreateFlagField(inspector, "Player controllable", value => UpdateSelectedBoolean("isPlayerControllable", value));
        _status = CreateLabel("Ready", 13);
        _status.AutowrapMode = TextServer.AutowrapMode.Word;
        inspector.AddChild(_status);

        return layout;
    }


    private CheckBox CreateFlagField(VBoxContainer parent, string label, Action<bool> changed)
    {
        var field = new CheckBox { Text = label };
        field.Toggled += value => changed(value);
        parent.AddChild(field);
        _inspectorInputs.Add(field);
        return field;
    }

    private void SetInspectorInputsEnabled(bool enabled)
    {
        foreach (Control input in _inspectorInputs)
        {
            switch (input)
            {
                case LineEdit lineEdit: lineEdit.Editable = enabled; break;
                case TextEdit textEdit: textEdit.Editable = enabled; break;
                case SpinBox spinBox: spinBox.Editable = enabled; break;
                case CheckBox checkBox: checkBox.Disabled = !enabled; break;
            }
        }
    }

    private void UpdateSelectedDisplayName(string value)
    {
        if (_updatingInspector || _playMode || _selectedActor is null)
            return;
        _selectedActor.State.WriteString("displayName", value);
        _selectionTitle.Text = value.Length > 0 ? value : _selectedActor.LegacyName;
        for (int index = 0; index < _actorList.ItemCount; index++)
        {
            if (_actorList.GetItemMetadata(index).AsString() == _selectedActor.LegacyName)
            {
                _actorList.SetItemText(index, value.Length > 0 ? value : _selectedActor.LegacyName);
                break;
            }
        }
    }

    private void UpdateSelectedDescription()
    {
        if (!_updatingInspector && !_playMode && _selectedActor is not null)
            _selectedActor.State.WriteString("description", _descriptionField.Text);
    }

    private void UpdateSelectedScale(int changedAxis)
    {
        if (_updatingInspector || _playMode || _selectedActor is null)
            return;
        Vector3 scale = _selectedActor.Scale;
        scale[changedAxis] = (float)_scaleFields[changedAxis].Value;
        _selectedActor.Scale = scale;
        _selectedActor.State.SetScale(scale);
    }

    private void UpdateSelectedTint()
    {
        if (_updatingInspector || _playMode || _selectedActor is null)
            return;
        _selectedActor.SetTint(new Color(
            (float)_tintFields[0].Value,
            (float)_tintFields[1].Value,
            (float)_tintFields[2].Value,
            (float)_tintFields[3].Value));
    }

    private void UpdateSelectedBoolean(string property, bool value, bool refreshPhysics = false)
    {
        if (_updatingInspector || _playMode || _selectedActor is null)
            return;
        _selectedActor.State.WriteBool(property, value);
        if (refreshPhysics)
            _selectedActor.ApplyLegacyPhysicsSettings();
    }
    private void ShowWelcome()
    {
        _welcome.Visible = true;
        _editor.Visible = false;
        _playMode = false;
        _playButton.Text = "Play";
    }

    private void LoadProject(string path)
    {
        try
        {
            LegacySaveDocument document = LegacySaveDocument.Load(path);
            ClearWorld();
            _document = document;
            LegacyTerrainData? terrainData = LegacyTerrainData.Decode(document.GridState);
            if (terrainData is not null)
            {
                _terrain = LegacyTerrainNode.Create(terrainData);
                _world.AddChild(_terrain);
            }
            else
            {
                _terrain = null;
                AddStageGround(document.Stage);
            }

            BuildActors(document);
            _behaviorRuntime = new LegacyBehaviorRuntime(
                _actors,
                document,
                _catalog,
                _world,
                _terrain,
                _camera,
                _behaviorUi.SetCommands,
                RefreshRuntimeActors,
                SetTemporaryCameraOffset);
            GD.Print($"Loaded {document.Actors.Count} actors from {path} (save version {document.Version}).");

            PopulateAssetPicker();
            _welcome.Visible = false;
            _editor.Visible = true;
            _status.Text = $"Loaded {Path.GetFileName(path)} — {document.Actors.Count} actors; save version {document.Version}.";
            SelectActor(_actors.FirstOrDefault());
            _cameraTarget = ComputeWorldCenter();
            UpdateOrbitCamera();
        }
        catch (Exception exception)
        {
            GD.PushError($"Could not load project: {exception}");
            ShowError($"Could not load project:\n{exception.Message}");
        }
    }

    private void BuildActors(LegacySaveDocument document)
    {
        _actors.Clear();
        var byName = new Dictionary<string, WorldActorNode>(StringComparer.Ordinal);
        foreach (LegacyActorData state in document.Actors)
        {
            _catalog.TryGet(state.RenderableUri, out RenderableEntry? renderable);
            var actor = new WorldActorNode();
            actor.Initialize(state, renderable);
            actor.SetPlayMode(false);
            _world.AddChild(actor);
            _actors.Add(actor);
            byName.Add(state.Name, actor);
        }

        foreach (WorldActorNode actor in _actors)
        {
            if (actor.State.TransformParent.Length == 0 || !byName.TryGetValue(actor.State.TransformParent, out WorldActorNode? parent))
                continue;
            Transform3D originalGlobal = actor.GlobalTransform;
            actor.Reparent(parent, keepGlobalTransform: false);
            actor.GlobalTransform = originalGlobal;
        }
        RefreshActorList();
    }

    private void ClearWorld()
    {
        _behaviorRuntime = null;
        _behaviorUi?.SetCommands(new JsonArray());
        foreach (Node child in _world.GetChildren())
            child.QueueFree();
        _actors.Clear();
        _selectedActor = null;
        _actorList?.Clear();
    }

    private void AddStageGround(JsonObject? stage)
    {
        float width = LegacySaveDocument.ReadFloat(stage?["groundSizeX"], 500f);
        float depth = LegacySaveDocument.ReadFloat(stage?["groundSizeZ"], 500f);
        if (width <= 0f) width = 500f;
        if (depth <= 0f) depth = 500f;
        var mesh = new MeshInstance3D
        {
            Name = "StageGround",
            Mesh = new PlaneMesh { Size = new Vector2(width, depth), SubdivideWidth = 1, SubdivideDepth = 1 },
            Position = new Vector3(0f, -0.05f, 0f)
        };
        Color ground = stage?["groundColor"] is JsonObject groundColor
            ? new Color(
                LegacySaveDocument.ReadFloat(groundColor["r"], 0.3f),
                LegacySaveDocument.ReadFloat(groundColor["g"], 0.55f),
                LegacySaveDocument.ReadFloat(groundColor["b"], 0.25f),
                LegacySaveDocument.ReadFloat(groundColor["a"], 1f))
            : new Color("#48704a");
        mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = ground, Roughness = 1f };
        _world.AddChild(mesh);

        var body = new StaticBody3D { Name = "StageGroundCollision", CollisionLayer = 2u, CollisionMask = 1u };
        var shape = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(width, 0.1f, depth) },
            Position = new Vector3(0f, -0.05f, 0f)
        };
        body.AddChild(shape);
        _world.AddChild(body);
    }

    private void PopulateAssetPicker()
    {
        _assetPicker.Clear();
        foreach (RenderableEntry entry in _catalog.Entries.OrderBy(entry => entry.Uri, StringComparer.Ordinal))
        {
            _assetPicker.AddItem(entry.Uri[(entry.Uri.LastIndexOf('/') + 1)..]);
            _assetPicker.SetItemMetadata(_assetPicker.ItemCount - 1, entry.Uri);
        }
    }

    private void AddActor()
    {
        if (_document is null || _assetPicker.Selected < 0)
            return;

        string uri = (string)_assetPicker.GetItemMetadata(_assetPicker.Selected);
        string name = $"actor-{Guid.NewGuid():N}";
        var actorData = new JsonObject
        {
            ["version"] = 8,
            ["name"] = name,
            ["brainName"] = "__DEFAULT_BRAIN__",
            ["tags"] = new JsonArray(),
            ["renderableUri"] = uri,
            ["memoryJson"] = "{}",
            ["displayName"] = uri[(uri.LastIndexOf('/') + 1)..],
            ["description"] = "",
            ["tint"] = new JsonObject { ["r"] = 1f, ["g"] = 1f, ["b"] = 1f, ["a"] = 1f },
            ["transformParent"] = "",
            ["position"] = new JsonObject { ["x"] = 0f, ["y"] = 1f, ["z"] = 0f },
            ["rotation"] = new JsonObject { ["x"] = 0f, ["y"] = 0f, ["z"] = 0f, ["w"] = 1f },
            ["localScale"] = new JsonObject { ["x"] = 1f, ["y"] = 1f, ["z"] = 1f },
            ["renderableOffset"] = new JsonObject { ["x"] = 0f, ["y"] = 0f, ["z"] = 0f },
            ["renderableRotation"] = new JsonObject { ["x"] = 0f, ["y"] = 0f, ["z"] = 0f, ["w"] = 1f },
            ["preferOffstage"] = false,
            ["isSolid"] = true,
            ["enablePhysics"] = true,
            ["enableGravity"] = true,
            ["bounciness"] = 0f,
            ["drag"] = 0f,
            ["angularDrag"] = 0.05f,
            ["mass"] = 1f,
            ["freezeRotations"] = false,
            ["freezeX"] = false,
            ["freezeY"] = false,
            ["freezeZ"] = false,
            ["enableAiming"] = false,
            ["hideInPlayMode"] = false,
            ["keepUpright"] = false,
            ["isPlayerControllable"] = false,
            ["velocity"] = new JsonObject { ["x"] = 0f, ["y"] = 0f, ["z"] = 0f },
            ["angularVelocity"] = new JsonObject { ["x"] = 0f, ["y"] = 0f, ["z"] = 0f }
        };
        LegacyActorData state = _document.AddActor(actorData);
        _catalog.TryGet(uri, out RenderableEntry? renderable);
        var actor = new WorldActorNode();
        actor.Initialize(state, renderable);
        actor.SetPlayMode(_playMode);
        _world.AddChild(actor);
        _actors.Add(actor);
        RefreshActorList();
        SelectActor(actor);
    }

    private void RemoveSelectedActor()
    {
        if (_document is null || _selectedActor is null)
            return;
        string name = _selectedActor.LegacyName;
        _document.RemoveActor(name);
        _actors.Remove(_selectedActor);
        _selectedActor.QueueFree();
        _selectedActor = null;
        RefreshActorList();
        SelectActor(_actors.FirstOrDefault());
    }
    private void RefreshRuntimeActors()
    {
        RefreshActorList();
        if (_selectedActor is not null && !_actors.Contains(_selectedActor))
            SelectActor(_actors.FirstOrDefault());
    }

    private void SetTemporaryCameraOffset(Vector3 legacyOffset)
    {
        _temporaryCameraOffset = CoordinateSpace.ToGodot(legacyOffset);
    }


    private void RefreshActorList()
    {
        _actorList.Clear();
        foreach (WorldActorNode actor in _actors)
        {
            string label = actor.State.DisplayName.Length > 0 ? actor.State.DisplayName : actor.LegacyName;
            _actorList.AddItem(label);
            _actorList.SetItemMetadata(_actorList.ItemCount - 1, actor.LegacyName);
        }
    }

    private void SelectActorByIndex(long index)
    {
        if ((ulong)index >= (ulong)_actorList.ItemCount)
            return;
        string name = (string)_actorList.GetItemMetadata((int)index);
        SelectActor(_actors.FirstOrDefault(actor => actor.LegacyName == name));
    }
    private void SelectActorAt(Vector2 screenPosition)
    {
        Vector3 origin = _camera.ProjectRayOrigin(screenPosition);
        Vector3 direction = _camera.ProjectRayNormal(screenPosition);
        var query = PhysicsRayQueryParameters3D.Create(origin, origin + direction * 10000f);
        query.CollisionMask = 1u;
        query.CollideWithBodies = true;
        Godot.Collections.Dictionary hit = _world.GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node node)
            return;
        WorldActorNode? actor = _actors.FirstOrDefault(candidate => candidate == node || candidate.IsAncestorOf(node));
        if (actor is not null)
            SelectActor(actor);
    }


    private void SelectActor(WorldActorNode? actor)
    {
        _selectedActor = actor;
        _updatingInspector = true;
        bool hasActor = actor is not null;
        _displayNameField.Text = hasActor ? actor!.State.DisplayName : "";
        _descriptionField.Text = hasActor ? actor!.State.Description : "";
        for (int axis = 0; axis < 3; axis++)
        {
            _positionFields[axis].Editable = hasActor && !_playMode;
            _positionFields[axis].Value = hasActor ? actor!.GlobalPosition[axis] : 0d;
            _scaleFields[axis].Value = hasActor ? actor!.Scale[axis] : 1d;
        }
        _yawField.Editable = hasActor && !_playMode;
        _yawField.Value = hasActor ? actor!.GlobalRotationDegrees.Y : 0d;
        Color tint = hasActor ? actor!.State.Tint : Colors.White;
        _tintFields[0].Value = tint.R;
        _tintFields[1].Value = tint.G;
        _tintFields[2].Value = tint.B;
        _tintFields[3].Value = tint.A;
        _solidField.ButtonPressed = hasActor && actor!.State.IsSolid;
        _physicsField.ButtonPressed = hasActor && actor!.State.EnablePhysics;
        _gravityField.ButtonPressed = hasActor && actor!.State.EnableGravity;
        _playerField.ButtonPressed = hasActor && actor!.State.IsPlayerControllable;
        SetInspectorInputsEnabled(hasActor && !_playMode);
        _updatingInspector = false;
    }

    private void UpdateSelectedPosition(int axis, float value)
    {
        if (_updatingInspector || _playMode || _selectedActor is null)
            return;
        Vector3 position = _selectedActor.GlobalPosition;
        position[axis] = value;
        _selectedActor.GlobalPosition = position;
    }

    private void UpdateSelectedYaw(float degrees)
    {
        if (_updatingInspector || _playMode || _selectedActor is null)
            return;
        Vector3 rotation = _selectedActor.GlobalRotationDegrees;
        rotation.Y = degrees;
        _selectedActor.GlobalRotationDegrees = rotation;
    }

    private void SetPlayMode(bool enabled)
    {
        if (_document is null)
            return;
        _playMode = enabled;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        foreach (WorldActorNode actor in _actors)
            actor.SetPlayMode(enabled);
        _playButton.Text = enabled ? "Stop" : "Play";
        _behaviorUi.Visible = enabled;
        _actorList.Visible = !enabled;
        _positionFields[0].Editable = !enabled && _selectedActor is not null;
        _positionFields[1].Editable = !enabled && _selectedActor is not null;
        _positionFields[2].Editable = !enabled && _selectedActor is not null;
        _yawField.Editable = !enabled && _selectedActor is not null;
        SetInspectorInputsEnabled(!enabled && _selectedActor is not null);
        _status.Text = enabled
            ? "Play mode — WASD/arrows to move, Space to jump, Esc to return to editing."
            : "Edit mode — select an actor and change its transform.";
        if (enabled && GetPlayerActor() is WorldActorNode player)
            _cameraTarget = player.GlobalPosition + new Vector3(0f, 3f, 0f);
        UpdateOrbitCamera();
    }

    private void SaveProject()
    {
        if (_document is null)
            return;
        if (_document.SourcePath.StartsWith("res://", StringComparison.Ordinal))
        {
            _saveDialog.CurrentFile = Path.GetFileName(_document.SourcePath);
            _saveDialog.PopupCentered(new Vector2I(1000, 700));
            return;
        }
        SaveTo(_document.SourcePath);
    }

    private void SaveProjectAs(string path)
    {
        if (_document is null)
            return;
        if (Path.GetExtension(path).Length == 0)
            path += ".voos";
        SaveTo(path);
    }

    private void SaveTo(string path)
    {
        if (_document is null)
            return;
        try
        {
            var transforms = new Dictionary<string, Transform3D>(StringComparer.Ordinal);
            foreach (WorldActorNode actor in _actors)
            {
                actor.SaveTransform();
                transforms.Add(actor.LegacyName, actor.GlobalTransform);
            }
            _document.SaveActorTransforms(transforms);
            if (_terrain is not null)
            {
                _terrain.FlushUpdates();
                _document.SaveTerrainPayload(_terrain.Encode());
            }
            _document.SaveTo(path);
            _status.Text = $"Saved {Path.GetFileName(path)} — {_actors.Count} actors.";
        }
        catch (Exception exception)
        {
            ShowError($"Could not save project:\n{exception.Message}");
        }
    }

    private void ResetActors()
    {
        foreach (WorldActorNode actor in _actors)
        {
            actor.GlobalTransform = actor.State.GetWorldTransform();
            actor.LinearVelocity = actor.State.Velocity;
            actor.AngularVelocity = actor.State.AngularVelocity;
        }
        if (_selectedActor is not null)
            SelectActor(_selectedActor);
    }

    private WorldActorNode? GetPlayerActor()
    {
        return _actors.FirstOrDefault(actor => actor.State.IsPlayerControllable);
    }
    private WorldActorNode? GetCameraActor(WorldActorNode player)
    {
        string name = LegacySaveDocument.ReadString(player.State.Data["cameraActor"]);
        return name.Length == 0 ? null : _actors.FirstOrDefault(actor => actor.LegacyName == name);
    }

    private static float ReadCameraFieldOfView(WorldActorNode cameraActor)
    {
        const float defaultFov = 60f;
        string settings = LegacySaveDocument.ReadString(cameraActor.State.Data["cameraSettingsJson"]);
        if (settings.Length == 0)
            return defaultFov;
        try
        {
            JsonObject? values = JsonNode.Parse(settings) as JsonObject;
            float fov = LegacySaveDocument.ReadFloat(values?["fov"], defaultFov);
            return fov > 0f ? Mathf.Clamp(fov, 1f, 179f) : defaultFov;
        }
        catch (System.Text.Json.JsonException)
        {
            return defaultFov;
        }
    }


    private Vector3 ComputeWorldCenter()
    {
        if (_actors.Count == 0)
            return Vector3.Zero;
        Vector3 sum = Vector3.Zero;
        foreach (WorldActorNode actor in _actors)
            sum += actor.GlobalPosition;
        return sum / _actors.Count;
    }

    private void UpdateOrbitCamera()
    {
        if (_camera is null)
            return;
        var orbit = new Basis(Vector3.Up, _cameraYaw) * new Basis(Vector3.Right, _cameraPitch);
        Vector3 offset = orbit * new Vector3(0f, 0f, _cameraDistance);
        _camera.GlobalPosition = _cameraTarget + offset;
        _camera.LookAt(_cameraTarget, Vector3.Up);
    }

    private void AddToolbarButton(HBoxContainer toolbar, string text, Action action)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(70f, 36f) };
        button.Pressed += action;
        toolbar.AddChild(button);
    }

    private void PopulateSamples()
    {
        _samplePicker.Clear();
        string directory = "res://Assets/StreamingAssets/ExampleGames/Public";
        foreach (string file in DirAccess.GetFilesAt(directory).Where(file => file.EndsWith(".voos", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            string path = $"{directory}/{file}";
            _samplePicker.AddItem(Path.GetFileNameWithoutExtension(file) ?? file);
            _samplePicker.SetItemMetadata(_samplePicker.ItemCount - 1, path);
        }
        if (_samplePicker.ItemCount > 0)
            _samplePicker.Select(0);
    }

    private void ShowError(string message)
    {
        _errorDialog.DialogText = message;
        _errorDialog.PopupCentered(new Vector2I(640, 240));
    }


    private static Label CreateLabel(string text, int fontSize)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        return label;
    }

    private static SpinBox CreateSpinBox()
    {
        return new SpinBox
        {
            MinValue = -100000f,
            MaxValue = 100000f,
            Step = 0.1f,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Editable = true
        };
    }
    private static SpinBox CreateUnitSpinBox()
    {
        return new SpinBox
        {
            MinValue = 0f,
            MaxValue = 1f,
            Step = 0.01f,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Editable = true
        };
    }


    private static StyleBoxFlat MakePanelStyle(string background, string border)
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(background),
            BorderColor = new Color(border),
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 10,
            ContentMarginBottom = 10
        };
    }

}
