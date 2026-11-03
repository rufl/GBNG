using System.Text.Json;
using GBNG.Networking;
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
    private JsonObject? _collaborationSnapshot;
    private bool _applyingCollaborativePatch;
    private string? _networkHostProjectPath;
    private LegacyBehaviorRuntime? _behaviorRuntime;
    private LegacyBehaviorUi _behaviorUi = null!;
    private Node3D _world = null!;
    private Camera3D _camera = null!;
    private ItemList _actorList = null!;
    private OptionButton _assetPicker = null!;
    private LineEdit _assetSearch = null!;
    private Label _assetCount = null!;
    private LegacyAssetPreviewControl _assetPreview = null!;
    private LegacyAssetStoreWindow _assetStoreWindow = null!;
    private OptionButton _samplePicker = null!;
    private SpinBox[] _positionFields = Array.Empty<SpinBox>();
    private SpinBox[] _rotationFields = Array.Empty<SpinBox>();
    private Control _welcome = null!;
    private Control _editor = null!;
    private Label _status = null!;
    private Label _selectionTitle = null!;
    private Button _playButton = null!;
    private FileDialog _openDialog = null!;
    private FileDialog _saveDialog = null!;
    private FileDialog _importModelDialog = null!;
    private AcceptDialog _errorDialog = null!;
    private LegacyLanSession _lanSession = null!;
    private LegacyLanNetworkWindow _networkWindow = null!;
    private WorldActorNode? _selectedActor;

    private LegacyTerrainNode? _terrain;
    private MeshInstance3D? _stageGroundMesh;
    private StaticBody3D? _stageGroundBody;
    private LegacyTerrainEditorWindow? _terrainEditorWindow;
    private readonly Dictionary<Vector3I, TerrainCellSnapshot> _terrainStrokeBefore = new();
    private bool _terrainStrokeActive;
    private bool _terrainStrokeCreatedTerrain;
    private readonly record struct TerrainCellSnapshot(int Shape, int Style, int Direction);
    private bool _updatingInspector;
    private bool _playMode;
    private float _cameraYaw = Mathf.DegToRad(35f);
    private float _cameraPitch = Mathf.DegToRad(-28f);
    private float _cameraDistance = 26f;
    private Vector3 _cameraTarget = new(0f, 2f, 0f);
    private Vector3 _temporaryCameraOffset;
    private Vector2 _lookAxes;
    private float _mouseWheelRaw;
    private Vector2 _networkLookAxes;
    private float _networkWheelRaw;
    private readonly List<Control> _inspectorInputs = new();
    private LineEdit _displayNameField = null!;
    private TextEdit _descriptionField = null!;
    private SpinBox[] _scaleFields = Array.Empty<SpinBox>();
    private SpinBox[] _tintFields = Array.Empty<SpinBox>();
    private CheckBox _solidField = null!;
    private CheckBox _physicsField = null!;
    private CheckBox _gravityField = null!;
    private CheckBox _playerField = null!;
    private OptionButton _brainField = null!;
    private Button _behaviorEditorButton = null!;
    private string? _behaviorRuntimeError;
    private const int MaxHistoryItems = 50;
    private readonly List<HistoryEntry> _history = new();
    private int _historyPosition;
    private bool _applyingHistory;
    private (string ActorName, string Property, string Value)? _pendingTextEdit;
    private Button _undoButton = null!;
    private Button _redoButton = null!;
    private sealed record HistoryEntry(string Label, Action Undo, Action Redo);
    private sealed record ActorDeletionSnapshot(string Name, string DisplayName, string ActorJson, int Index, string[] ChildNames);
    private readonly record struct ActorMotionSnapshot(Transform3D Transform, Vector3 Velocity, Vector3 AngularVelocity);
    private sealed record NetworkActorSnapshot(
        string Name,
        float X, float Y, float Z,
        float RotationX, float RotationY, float RotationZ, float RotationW,
        float ScaleX, float ScaleY, float ScaleZ,
        float VelocityX, float VelocityY, float VelocityZ,
        float AngularX, float AngularY, float AngularZ,
        float TintR, float TintG, float TintB, float TintA,
        float OffsetX, float OffsetY, float OffsetZ,
        float RenderRotationX, float RenderRotationY, float RenderRotationZ, float RenderRotationW,
        string Animation);



    public override void _Ready()
    {
        _catalog = LegacyRenderableCatalog.Load();
        _lanSession = new LegacyLanSession { Name = "LanSession" };
        _lanSession.SnapshotReceived += ApplyNetworkSnapshot;
        _lanSession.HostPlayModeChanged += enabled =>
        {
            if (_camera is not null && IsInstanceValid(_camera) && _camera.IsInsideTree())
                SetPlayMode(enabled, fromHost: true);
        };
        _lanSession.ActorStructureReceived += ApplyNetworkActorStructure;
        _lanSession.ClientDocumentPatchReceived += ApplyClientDocumentPatch;
        _lanSession.DocumentPatchReceived += ApplyNetworkDocumentPatch;
        _lanSession.DocumentPatchRejected += message =>
        {
            _status.Text = $"Collaborative edit rejected: {message}";
            GD.PushWarning($"Collaborative edit rejected: {message}");
        };
        _lanSession.ClientAssetBundleReceived += ApplyClientAssetBundle;
        _lanSession.AssetBundleReceived += ApplyNetworkAssetBundle;
        _lanSession.AssetBundleRejected += message =>
        {
            _status.Text = $"Project asset sync failed: {message}";
            GD.PushWarning($"Project asset sync failed: {message}");
        };
        _lanSession.ClientProjectReadyReceived += OnClientProjectReady;
        _lanSession.HostProjectCopyEnding += PreserveHostProjectAssetsBeforeLeave;
        _lanSession.StateChanged += OnLanSessionStateChanged;
        AddChild(_lanSession);
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
        if (commandLineArgs.Contains("--smoke-card-library", StringComparer.Ordinal))
            RunCardLibrarySmoke();
        if (commandLineArgs.Contains("--smoke-asset-store", StringComparer.Ordinal))
            RunAssetStoreSmoke();
        if (commandLineArgs.Contains("--smoke-asset-import", StringComparer.Ordinal))
            RunAssetImportSmoke();
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
        if (_lanSession.IsClient && _playMode)
        {
            _networkLookAxes += _lookAxes;
            _networkWheelRaw += _mouseWheelRaw;
            if (_lanSession.ShouldSendLocalInput(delta, _playMode))
            {
                Vector2 pointerPosition = GetViewport().GetMousePosition();
                Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
                float uiScale = viewportSize.X / 1600f;
                Vector2 mouseUiPosition = uiScale > 0f ? pointerPosition / uiScale : Vector2.Zero;
                _lanSession.SendLocalInput(
                    _networkLookAxes,
                    mouseUiPosition,
                    _networkWheelRaw,
                    IsPointerOverUi(pointerPosition),
                    _camera.ProjectRayOrigin(pointerPosition),
                    _camera.ProjectRayNormal(pointerPosition),
                    _camera.GlobalPosition,
                    _camera.GlobalBasis.GetRotationQuaternion());
                _networkLookAxes = Vector2.Zero;
                _networkWheelRaw = 0f;
            }
        }
        else
        {
            _networkLookAxes = Vector2.Zero;
            _networkWheelRaw = 0f;
        }
        if (_playMode && !_lanSession.IsClient)
        {
            Vector2 pointerPosition = GetViewport().GetMousePosition();
            _behaviorRuntime?.SetLookAxes(_lookAxes);
            _behaviorRuntime?.SetPointerState(pointerPosition, _mouseWheelRaw, IsPointerOverUi(pointerPosition));
            _behaviorRuntime?.Tick(delta);
        }
        _lanSession.Tick(delta, _playMode, _actors);
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
            {
                if (IsTerrainBrushActive)
                {
                    BeginTerrainStroke();
                    ApplyTerrainBrush(mouseButton.Position);
                }
                else
                {
                    SelectActorAt(mouseButton.Position);
                }
            }
            if (mouseButton.ButtonIndex == MouseButton.Left && !mouseButton.Pressed)
                EndTerrainStroke();
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
        else if (@event is InputEventMouseMotion motion)
        {
            if (IsTerrainBrushActive && Input.IsMouseButtonPressed(MouseButton.Left))
                ApplyTerrainBrush(motion.Position);
            if (Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                _cameraYaw -= motion.Relative.X * 0.004f;
                _cameraPitch = Mathf.Clamp(_cameraPitch - motion.Relative.Y * 0.004f, -1.35f, 0.15f);
                _lookAxes += new Vector2(-motion.Relative.X * 0.004f, -motion.Relative.Y * 0.004f);
                UpdateOrbitCamera();
            }
        }
        else if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            Control? focusedControl = GetViewport().GuiGetFocusOwner();
            bool editingText = focusedControl is LineEdit or TextEdit;
            if (!_playMode && !editingText && (key.CtrlPressed || key.MetaPressed))
            {
                if (key.Keycode == Key.Y || (key.Keycode == Key.Z && key.ShiftPressed))
                    RedoEdit();
                else if (key.Keycode == Key.Z)
                    UndoEdit();
            }
            else if (key.Keycode == Key.Escape)
            {
                if (_playMode)
                    SetPlayMode(false);
                else
                    Input.MouseMode = Input.MouseModeEnum.Visible;
            }
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
        _networkWindow = new LegacyLanNetworkWindow(_lanSession, PrepareNetworkHostProjectCopy, OpenReceivedNetworkProject);
        _assetStoreWindow = new LegacyAssetStoreWindow(
            _catalog,
            () => _document is not null && !_playMode && !_lanSession.HasPendingAssetSync,
            uri => AddActor(uri),
            OpenModelImportDialog);
        AddChild(_assetStoreWindow);
        AddChild(_networkWindow);

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
        _importModelDialog = new FileDialog
        {
            Title = "Import a 3D model",
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true
        };
        _importModelDialog.AddFilter("*.glb", "Binary glTF model");
        _importModelDialog.AddFilter("*.gltf", "glTF scene with external resources");
        _importModelDialog.AddFilter("*.fbx", "FBX model with animations");
        _importModelDialog.FileSelected += ImportModelFromPath;
        root.AddChild(_importModelDialog);

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
        title.CustomMinimumSize = new Vector2(70f, 0f);
        title.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        toolbar.AddChild(title);
        AddToolbarButton(toolbar, "Open", () => _openDialog.PopupCentered(new Vector2I(1000, 700)));
        AddToolbarButton(toolbar, "Host", () => _networkWindow.PopupCenteredClamped(new Vector2I(760, 470)));
        AddToolbarButton(toolbar, "Save", SaveProject);
        AddToolbarButton(toolbar, "Save as…", () =>
        {
            _saveDialog.CurrentFile = _document is null ? "project.voos" : Path.GetFileName(_document.SourcePath);
            _saveDialog.PopupCentered(new Vector2I(1000, 700));
        });
        _playButton = new Button { Text = "Play", CustomMinimumSize = new Vector2(84f, 36f) };
        _playButton.Pressed += () => SetPlayMode(!_playMode);
        toolbar.AddChild(_playButton);
        AddToolbarButton(toolbar, "Terrain", OpenTerrainEditor);
        AddToolbarButton(toolbar, "Reset", ResetActors);
        _undoButton = new Button { Text = "Undo", CustomMinimumSize = new Vector2(70f, 36f), Disabled = true };
        _undoButton.Pressed += UndoEdit;
        toolbar.AddChild(_undoButton);
        _redoButton = new Button { Text = "Redo", CustomMinimumSize = new Vector2(70f, 36f), Disabled = true };
        _redoButton.Pressed += RedoEdit;
        toolbar.AddChild(_redoButton);

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
        left.AddChild(CreateLabel("Asset Store · offline", 14));
        var browseAssetStore = new Button { Text = "Browse catalog…" };
        browseAssetStore.Pressed += OpenAssetStore;
        left.AddChild(browseAssetStore);
        var importGlb = new Button { Text = "Import 3D model…" };
        importGlb.Pressed += OpenModelImportDialog;
        left.AddChild(importGlb);
        _assetSearch = new LineEdit { PlaceholderText = "Search bundled and imported assets…" };
        _assetSearch.TextChanged += _ => PopulateAssetPicker();
        left.AddChild(_assetSearch);
        _assetPicker = new OptionButton { CustomMinimumSize = new Vector2(0f, 34f) };
        _assetPicker.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        left.AddChild(_assetPicker);
        _assetCount = CreateLabel("No assets loaded", 12);
        left.AddChild(_assetCount);
        _assetPreview = new LegacyAssetPreviewControl();
        left.AddChild(_assetPreview);
        _assetPicker.ItemSelected += _ => ShowSelectedAssetPreview();
        var addActor = new Button { Text = "Add actor" };
        addActor.Pressed += () => AddActor();
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
        inspector.AddChild(CreateLabel("Brain", 14));
        _brainField = new OptionButton { CustomMinimumSize = new Vector2(0f, 34f) };
        _brainField.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _brainField.ItemSelected += UpdateSelectedBrain;
        inspector.AddChild(_brainField);
        _inspectorInputs.Add(_brainField);
        _behaviorEditorButton = new Button { Text = "Edit behaviors…" };
        _behaviorEditorButton.Pressed += OpenBehaviorEditor;
        inspector.AddChild(_behaviorEditorButton);
        inspector.AddChild(CreateLabel("Display name", 14));
        _displayNameField = new LineEdit();
        _displayNameField.TextChanged += UpdateSelectedDisplayName;
        _displayNameField.FocusEntered += () => BeginTextEdit("displayName");
        _displayNameField.FocusExited += EndTextEdit;
        inspector.AddChild(_displayNameField);
        _inspectorInputs.Add(_displayNameField);
        inspector.AddChild(CreateLabel("Description", 14));
        _descriptionField = new TextEdit { CustomMinimumSize = new Vector2(0f, 74f) };
        _descriptionField.TextChanged += UpdateSelectedDescription;
        _descriptionField.FocusEntered += () => BeginTextEdit("description");
        _descriptionField.FocusExited += EndTextEdit;
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
        inspector.AddChild(CreateLabel("World rotation (degrees)", 14));
        _rotationFields = new SpinBox[3];
        for (int axis = 0; axis < 3; axis++)
        {
            var row = new HBoxContainer();
            row.AddChild(CreateLabel(axes[axis], 14));
            SpinBox field = CreateSpinBox();
            int index = axis;
            field.ValueChanged += value => UpdateSelectedRotation(index, (float)value);
            row.AddChild(field);
            _rotationFields[axis] = field;
            inspector.AddChild(row);
        }
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
                case CheckBox checkBox: checkBox.Disabled = !enabled; break;
                case OptionButton optionButton: optionButton.Disabled = !enabled; break;
            }
        }
        RefreshBrainChoices();
    }

    private void RefreshBrainChoices()
    {
        if (_brainField is null)
            return;

        _brainField.Clear();
        string currentBrain = _selectedActor?.State.BrainName ?? "";
        JsonObject? database = _document?.BehaviorDatabase;
        bool hasDatabase = database is not null;
        if (database?["brainIds"] is JsonArray brainIds && database["brains"] is JsonArray brains)
        {
            int count = Math.Min(brainIds.Count, brains.Count);
            for (int index = 0; index < count; index++)
            {
                string id = LegacySaveDocument.ReadString(brainIds[index]);
                if (id.Length == 0)
                    continue;
                _brainField.AddItem(GetBrainChoiceLabel(id, brains[index] as JsonObject, index));
                _brainField.SetItemMetadata(_brainField.ItemCount - 1, id);
            }
        }

        int currentIndex = -1;
        for (int index = 0; index < _brainField.ItemCount; index++)
        {
            if (_brainField.GetItemMetadata(index).AsString() == currentBrain)
            {
                currentIndex = index;
                break;
            }
        }
        if (currentBrain.Length > 0 && currentIndex < 0)
        {
            _brainField.AddItem($"{currentBrain} (not in behavior database)");
            _brainField.SetItemMetadata(_brainField.ItemCount - 1, "");
            currentIndex = _brainField.ItemCount - 1;
        }
        _brainField.Select(currentIndex);
        _brainField.Disabled = _playMode || _selectedActor is null || !hasDatabase;
    }

    private static string GetBrainChoiceLabel(string id, JsonObject? brain, int index)
    {
        if (id == "__DEFAULT_BRAIN__")
            return "Default brain";

        JsonArray? behaviorUses = brain?["behaviorUses"] as JsonArray;
        string label = behaviorUses is { Count: > 0 } && behaviorUses[0] is JsonObject firstUse
            ? LegacySaveDocument.ReadString(firstUse["behaviorUri"])
            : "";
        int schemeSeparator = label.IndexOf(':');
        if (schemeSeparator >= 0)
            label = label[(schemeSeparator + 1)..];
        if (label.Length == 0)
            label = $"Brain {index + 1}";
        if (behaviorUses is { Count: > 1 })
            label += $" +{behaviorUses.Count - 1}";

        string shortId = id.Length > 8 ? id[..8] : id;
        return $"{label} · {shortId}";
    }

    private void UpdateSelectedBrain(long index)
    {
        if (_updatingInspector || _playMode || _selectedActor is null || _document?.BehaviorDatabase is null)
            return;
        string brainName = _brainField.GetItemMetadata((int)index).AsString();
        string actorName = _selectedActor.LegacyName;
        string oldBrain = _selectedActor.State.BrainName;
        if (brainName.Length == 0 || oldBrain == brainName)
            return;
        SetActorStringProperty(actorName, "brainName", brainName);
        RecordHistory($"Assign brain to {_selectedActor.State.DisplayName}",
            () => SetActorStringProperty(actorName, "brainName", oldBrain),
            () => SetActorStringProperty(actorName, "brainName", brainName));
    }
    private void OpenBehaviorEditor()
    {
        if (_document is null || _selectedActor is null || _playMode)
            return;
        var window = new LegacyBehaviorAuthoringWindow(
            _document,
            _selectedActor.State,
            OnBehaviorDataChanged,
            uri => _behaviorRuntimeError is null ? _behaviorRuntime?.GetBehaviorPropertyDefinitions(uri) : null);
        AddChild(window);
        window.PopupCentered();
    }
    private void OpenTerrainEditor()
    {
        if (_terrainEditorWindow is null)
        {
            _terrainEditorWindow = new LegacyTerrainEditorWindow();
            _terrainEditorWindow.ActiveChanged += active =>
            {
                if (!active)
                    EndTerrainStroke();
                _status.Text = active ? "Terrain brush active — click or drag in the viewport." : "Terrain brush disabled.";
            };
            AddChild(_terrainEditorWindow);
        }
        _terrainEditorWindow.PopupCentered(new Vector2I(380, 560));
    }

    private bool IsTerrainBrushActive
        => !_playMode && _terrainEditorWindow is { Visible: true, IsBrushActive: true };


    private void OnBehaviorDataChanged()
    {
        if (_document is null)
            return;
        try
        {
            LegacyBehaviorRuntime updatedRuntime = CreateBehaviorRuntime(_document);
            _behaviorRuntime?.Dispose();
            _behaviorRuntime = updatedRuntime;
            _behaviorRuntimeError = null;
            _behaviorUi.SetCommands(new JsonArray());
            if (_selectedActor is not null)
                SelectActor(_selectedActor);
            _status.Text = "Behavior changes applied. Save the project to persist them.";
        }
        catch (Exception exception)
        {
            _behaviorRuntimeError = exception.Message;
            GD.PushError($"Could not reload edited behavior database: {exception}");
            ShowError($"Behavior data changed, but the runtime rejected it:\n{exception.Message}");
        }
        NotifyCollaborativeEdits();
    }

    private LegacyBehaviorRuntime CreateBehaviorRuntime(LegacySaveDocument document)
        => new(
            _actors,
            document,
            _catalog,
            _world,
            _terrain,
            _camera,
            _behaviorUi.SetCommands,
            RefreshRuntimeActors,
            SetTemporaryCameraOffset,
            _lanSession);

    private void BeginTextEdit(string property)
    {
        if (_updatingInspector || _playMode || _selectedActor is null)
            return;
        string value = property == "displayName" ? _selectedActor.State.DisplayName : _selectedActor.State.Description;
        _pendingTextEdit = (_selectedActor.LegacyName, property, value);
    }

    private void EndTextEdit()
    {
        if (_pendingTextEdit is not { } edit)
            return;
        _pendingTextEdit = null;
        WorldActorNode? actor = FindActor(edit.ActorName);
        if (actor is null)
            return;
        string newValue = edit.Property == "displayName" ? actor.State.DisplayName : actor.State.Description;
        if (edit.Value == newValue)
            return;
        RecordHistory($"Edit {edit.Property} for {actor.State.DisplayName}",
            () => SetActorStringProperty(edit.ActorName, edit.Property, edit.Value),
            () => SetActorStringProperty(edit.ActorName, edit.Property, newValue));
    }

    private void UpdateSelectedDisplayName(string value)
    {
        if (_updatingInspector || _playMode || _selectedActor is null)
            return;
        SetActorStringProperty(_selectedActor.LegacyName, "displayName", value);
    }

    private void UpdateSelectedDescription()
    {
        if (!_updatingInspector && !_playMode && _selectedActor is not null)
            SetActorStringProperty(_selectedActor.LegacyName, "description", _descriptionField.Text);
    }

    private void SetActorStringProperty(string actorName, string property, string value)
    {
        WorldActorNode? actor = FindActor(actorName);
        if (actor is null)
            return;
        actor.State.WriteString(property, value);
        if (property == "displayName")
        {
            string label = value.Length > 0 ? value : actor.LegacyName;
            for (int index = 0; index < _actorList.ItemCount; index++)
            {
                if (_actorList.GetItemMetadata(index).AsString() == actor.LegacyName)
                {
                    _actorList.SetItemText(index, label);
                    break;
                }
            }
            if (_selectedActor == actor)
                _selectionTitle.Text = label;
        }
        if (_selectedActor != actor)
            return;
        _updatingInspector = true;
        if (property == "displayName" && _displayNameField.Text != value)
            _displayNameField.Text = value;
        else if (property == "description" && _descriptionField.Text != value)
            _descriptionField.Text = value;
        else if (property == "brainName")
            RefreshBrainChoices();
        _updatingInspector = false;
    }

    private void UpdateSelectedScale(int changedAxis)
    {
        if (_updatingInspector || _playMode || _selectedActor is null)
            return;
        string actorName = _selectedActor.LegacyName;
        Vector3 oldScale = _selectedActor.Scale;
        Vector3 newScale = oldScale;
        newScale[changedAxis] = (float)_scaleFields[changedAxis].Value;
        if (oldScale == newScale)
            return;
        SetActorScale(actorName, newScale);
        RecordHistory($"Scale {_selectedActor.State.DisplayName}",
            () => SetActorScale(actorName, oldScale),
            () => SetActorScale(actorName, newScale));
    }

    private void SetActorScale(string actorName, Vector3 scale)
    {
        WorldActorNode? actor = FindActor(actorName);
        if (actor is null)
            return;
        actor.Scale = scale;
        actor.State.SetScale(scale);
        if (_selectedActor == actor)
            SelectActor(actor);
    }

    private void UpdateSelectedTint()
    {
        if (_updatingInspector || _playMode || _selectedActor is null)
            return;
        string actorName = _selectedActor.LegacyName;
        Color oldTint = _selectedActor.State.Tint;
        Color newTint = new(
            (float)_tintFields[0].Value,
            (float)_tintFields[1].Value,
            (float)_tintFields[2].Value,
            (float)_tintFields[3].Value);
        if (oldTint == newTint)
            return;
        SetActorTint(actorName, newTint);
        RecordHistory($"Tint {_selectedActor.State.DisplayName}",
            () => SetActorTint(actorName, oldTint),
            () => SetActorTint(actorName, newTint));
    }

    private void SetActorTint(string actorName, Color tint)
    {
        WorldActorNode? actor = FindActor(actorName);
        if (actor is null)
            return;
        actor.SetTint(tint);
        if (_selectedActor == actor)
            SelectActor(actor);
    }

    private void UpdateSelectedBoolean(string property, bool value, bool refreshPhysics = false)
    {
        if (_updatingInspector || _playMode || _selectedActor is null)
            return;
        string actorName = _selectedActor.LegacyName;
        bool oldValue = LegacySaveDocument.ReadBool(_selectedActor.State.Data[property], property == "isSolid");
        if (oldValue == value)
            return;
        SetActorBoolean(actorName, property, value, refreshPhysics);
        RecordHistory($"Set {property} on {_selectedActor.State.DisplayName}",
            () => SetActorBoolean(actorName, property, oldValue, refreshPhysics),
            () => SetActorBoolean(actorName, property, value, refreshPhysics));
    }

    private void SetActorBoolean(string actorName, string property, bool value, bool refreshPhysics)
    {
        WorldActorNode? actor = FindActor(actorName);
        if (actor is null)
            return;
        actor.State.WriteBool(property, value);
        if (refreshPhysics)
            actor.ApplyLegacyPhysicsSettings();
        if (_selectedActor == actor)
            SelectActor(actor);
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
        if (_lanSession.IsHost)
        {
            ShowError("Leave the hosted session before opening a different project.");
            return;
        }
        _terrainEditorWindow?.SetBrushActive(false);
        EndTerrainStroke();
        try
        {
            LegacySaveDocument document = LegacySaveDocument.Load(path);
            ClearWorld();
            _document = document;
            DiscardRetainedHostAssetsForDifferentProject(document.SourcePath);
            _catalog.LoadProjectAssets(GetProjectAssetSourcePath());
            ClearHistory();
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
            _behaviorRuntime = CreateBehaviorRuntime(document);
            _behaviorRuntimeError = null;
            GD.Print($"Loaded {document.Actors.Count} actors from {path} (save version {document.Version}).");

            PopulateAssetPicker();
            _welcome.Visible = false;
            _editor.Visible = true;
            _status.Text = $"Loaded {Path.GetFileName(path)} — {document.Actors.Count} actors; save version {document.Version}.";
            SelectActor(_actors.FirstOrDefault());
            _cameraTarget = ComputeWorldCenter();
            UpdateOrbitCamera();
            ResetCollaborativeSnapshot();
        }
        catch (Exception exception)
        {
            _behaviorRuntimeError = exception.Message;
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
        _behaviorRuntime?.Dispose();
        _behaviorRuntime = null;
        _behaviorUi?.SetCommands(new JsonArray());
        foreach (Node child in _world.GetChildren())
            child.QueueFree();
        _terrain = null;
        _stageGroundMesh = null;
        _stageGroundBody = null;
        _terrainStrokeBefore.Clear();
        _terrainStrokeActive = false;
        _actors.Clear();
        _selectedActor = null;
        _actorList?.Clear();
    }
    public override void _ExitTree()
    {
        _behaviorRuntime?.Dispose();
        _behaviorRuntime = null;
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
        _stageGroundMesh = mesh;
        _stageGroundBody = body;
    }

    private void BeginTerrainStroke()
    {
        if (_document is null || _playMode || _terrainStrokeActive)
            return;
        _terrainStrokeBefore.Clear();
        _terrainStrokeCreatedTerrain = _terrain is null;
        _terrainStrokeActive = true;
    }

    private void ApplyTerrainBrush(Vector2 screenPosition)
    {
        if (!IsTerrainBrushActive || !_terrainStrokeActive || _document is null)
            return;

        Vector3 rayOrigin = _camera.ProjectRayOrigin(screenPosition);
        Vector3 rayEnd = rayOrigin + _camera.ProjectRayNormal(screenPosition) * 1000f;
        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayEnd, 2u);
        query.CollideWithAreas = false;
        query.CollideWithBodies = true;
        Godot.Collections.Dictionary hit = _world.GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node collider)
            return;

        bool terrainHit = IsTerrainCollider(collider);
        LegacyTerrainBrush brush = _terrainEditorWindow!.Brush;
        if (brush.Mode != LegacyTerrainBrushMode.Create && !terrainHit)
            return;
        if (brush.Mode == LegacyTerrainBrushMode.Create && !terrainHit && collider != _stageGroundBody)
            return;

        Vector3 hitPosition = hit["position"].AsVector3();
        Vector3 hitNormal = hit["normal"].AsVector3();
        if (_terrain is null && brush.Mode == LegacyTerrainBrushMode.Create)
            EnsureTerrain();
        if (_terrain is null)
            return;

        int x;
        int y;
        int z;
        if (brush.Mode == LegacyTerrainBrushMode.Create)
        {
            if (!_terrain.TryGetEmptyPlacementCell(hitPosition, hitNormal, out x, out y, out z))
                return;
        }
        else if (!_terrain.TryGetCellForHit(hitPosition, hitNormal, out x, out y, out z))
        {
            return;
        }

        var cell = new Vector3I(x, y, z);
        TerrainCellSnapshot previous = ReadTerrainCell(cell);
        TerrainCellSnapshot next = brush.Mode switch
        {
            LegacyTerrainBrushMode.Create when previous.Shape < 0
                => new TerrainCellSnapshot(brush.Shape, brush.Style, brush.Direction),
            LegacyTerrainBrushMode.Dig when previous.Shape >= 0
                => new TerrainCellSnapshot(-1, 0, 0),
            LegacyTerrainBrushMode.Paint when previous.Shape >= 0 && previous.Style != brush.Style
                => previous with { Style = brush.Style },
            _ => previous
        };
        if (previous == next)
            return;

        _terrainStrokeBefore.TryAdd(cell, previous);
        if (!_terrain.SetCell(cell.X, cell.Y, cell.Z, next.Shape, next.Style, next.Direction))
        {
            _terrainStrokeBefore.Remove(cell);
            return;
        }
        _status.Text = $"{brush.Mode} terrain cell ({x - _terrain.Width / 2}, {y + LegacyTerrainData.CellsYStart}, {z - _terrain.Depth / 2}).";
    }

    private bool IsTerrainCollider(Node collider)
    {
        for (Node? current = collider; current is not null; current = current.GetParent())
        {
            if (current == _terrain)
                return true;
        }
        return false;
    }

    private void EnsureTerrain()
    {
        if (_terrain is not null)
            return;
        _terrain = LegacyTerrainNode.Create(LegacyTerrainData.CreateEmpty());
        _world.AddChild(_terrain);
        if (_stageGroundMesh is not null)
            _stageGroundMesh.Visible = false;
    }

    private void RemoveTerrainCreatedByStroke()
    {
        if (_terrain is null)
            return;
        _world.RemoveChild(_terrain);
        _terrain.QueueFree();
        _terrain = null;
        if (_stageGroundMesh is not null)
            _stageGroundMesh.Visible = true;
    }

    private TerrainCellSnapshot ReadTerrainCell(Vector3I cell)
    {
        if (_terrain is null)
            return new TerrainCellSnapshot(-1, 0, 0);
        bool occupied = _terrain.TryGetCell(cell.X, cell.Y, cell.Z, out byte shape, out ushort style, out byte direction);
        return occupied ? new TerrainCellSnapshot(shape, style, direction) : new TerrainCellSnapshot(-1, 0, 0);
    }

    private void EndTerrainStroke()
    {
        if (!_terrainStrokeActive)
            return;
        _terrainStrokeActive = false;
        _terrain?.FlushUpdates();
        if (_terrainStrokeBefore.Count == 0)
        {
            if (_terrainStrokeCreatedTerrain)
                RemoveTerrainCreatedByStroke();
            _terrainStrokeCreatedTerrain = false;
            return;
        }

        var before = new Dictionary<Vector3I, TerrainCellSnapshot>(_terrainStrokeBefore);
        var after = new Dictionary<Vector3I, TerrainCellSnapshot>(before.Count);
        foreach (Vector3I cell in before.Keys)
            after.Add(cell, ReadTerrainCell(cell));
        bool createdTerrain = _terrainStrokeCreatedTerrain;
        _terrainStrokeBefore.Clear();
        _terrainStrokeCreatedTerrain = false;
        RecordHistory("Edit terrain",
            () => ApplyTerrainSnapshots(before, createdTerrain, terrainShouldExist: !createdTerrain),
            () => ApplyTerrainSnapshots(after, createdTerrain, terrainShouldExist: true));
    }

    private void ApplyTerrainSnapshots(
        IReadOnlyDictionary<Vector3I, TerrainCellSnapshot> snapshots,
        bool createdTerrain,
        bool terrainShouldExist)
    {
        if (createdTerrain && !terrainShouldExist)
        {
            RemoveTerrainCreatedByStroke();
            return;
        }
        EnsureTerrain();
        foreach ((Vector3I cell, TerrainCellSnapshot value) in snapshots)
            _terrain!.SetCell(cell.X, cell.Y, cell.Z, value.Shape, value.Style, value.Direction);
        _terrain!.FlushUpdates();
    }

    private void PopulateAssetPicker()
    {
        string selectedUri = _assetPicker.Selected >= 0
            ? _assetPicker.GetItemMetadata(_assetPicker.Selected).AsString()
            : "";
        string filter = _assetSearch.Text.Trim();
        _assetPicker.Clear();
        int selectedIndex = -1;
        foreach (RenderableEntry entry in _catalog.Entries.OrderBy(entry => entry.Uri, StringComparer.Ordinal))
        {
            if (filter.Length > 0
                && !entry.Uri.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            int separator = entry.Uri.LastIndexOf('/');
            string name = separator >= 0 ? entry.Uri[(separator + 1)..] : entry.Uri;
            string parent = separator >= 0 ? entry.Uri[..separator] : "";
            int parentSeparator = parent.LastIndexOf('/');
            string group = entry.Uri.StartsWith("custom:", StringComparison.Ordinal)
                ? "Imported"
                : parentSeparator >= 0 ? parent[(parentSeparator + 1)..] : parent;
            _assetPicker.AddItem(group.Length > 0 ? $"{name} · {group}" : name);
            _assetPicker.SetItemMetadata(_assetPicker.ItemCount - 1, entry.Uri);
            if (entry.Uri == selectedUri)
                selectedIndex = _assetPicker.ItemCount - 1;
        }
        if (_assetPicker.ItemCount > 0)
        {
            _assetPicker.Select(selectedIndex >= 0 ? selectedIndex : 0);
            _assetCount.Text = $"{_assetPicker.ItemCount} of {_catalog.Entries.Count} assets";
        }
        else
        {
            _assetCount.Text = "No matching assets";
        }
        _assetPicker.Disabled = _assetPicker.ItemCount == 0;
        ShowSelectedAssetPreview();
        if (_assetStoreWindow.Visible)
            _assetStoreWindow.RefreshCatalog();
    }

    private void ShowSelectedAssetPreview()
    {
        if (_assetPicker.Selected < 0)
        {
            _assetPreview.ShowAsset(null);
            return;
        }

        string uri = _assetPicker.GetItemMetadata(_assetPicker.Selected).AsString();
        _catalog.TryGet(uri, out RenderableEntry? entry);
        _assetPreview.ShowAsset(entry);
    }

    private void OpenAssetStore()
    {
        _assetStoreWindow.PopupCenteredClamped(new Vector2I(1080, 700));
        _assetStoreWindow.RefreshCatalog();
    }

    private void OpenModelImportDialog()
    {
        if (_document is null)
        {
            ShowError("Open a .voos project before importing a model.");
            return;
        }
        if (GetProjectAssetSourcePath().StartsWith("res://", StringComparison.Ordinal))
        {
            ShowError("Save a writable .voos copy before importing a model.");
            return;
        }
        _importModelDialog.PopupCentered(new Vector2I(1000, 700));
    }

    private void ImportModelFromPath(string path)
    {
        if (_document is null)
            return;
        try
        {
            string assetProjectPath = GetProjectAssetSourcePath();
            RenderableEntry entry = _catalog.ImportProjectModel(assetProjectPath, path);
            if (_lanSession.IsClient)
            {
                if (_lanSession.SubmitAssetBundle(assetProjectPath, out long transferId))
                {
                    _unsharedAssetUris.Add(entry.Uri);
                    _pendingAssetUrisByTransferId.Add(transferId, entry.Uri);
                }
                else
                {
                    _unsharedAssetUris.Add(entry.Uri);
                }
            }
            else if (_lanSession.IsHost && _networkHostProjectPath is string hostProjectPath)
            {
                if (!_document.SourcePath.StartsWith("res://", StringComparison.Ordinal))
                    _catalog.CopyProjectAssets(hostProjectPath, _document.SourcePath);
                PersistNetworkHostProjectCopy();
                _catalog.LoadProjectAssets(hostProjectPath);
                _lanSession.BroadcastAssetBundle(hostProjectPath, _lanSession.LocalPeerId, 0);
            }
            bool clearedSearch = _assetSearch.Text.Length > 0;
            _assetSearch.Text = "";
            if (!clearedSearch)
                PopulateAssetPicker();
            for (int index = 0; index < _assetPicker.ItemCount; index++)
            {
                if (_assetPicker.GetItemMetadata(index).AsString() != entry.Uri)
                    continue;
                _assetPicker.Select(index);
                ShowSelectedAssetPreview();
                break;
            }
            _status.Text = _lanSession.IsClient
                ? _unsharedAssetUris.Contains(entry.Uri)
                    ? $"Imported {Path.GetFileName(path)} locally; wait for host asset sync before adding it."
                    : $"Imported {Path.GetFileName(path)} into this project."
                : $"Imported {Path.GetFileName(path)} into this project.";
        }
        catch (Exception exception)
        {
            ShowError($"Could not import model:\n{exception.Message}");
        }
    }

    private bool AddActor(string? selectedUri = null)
    {
        if (_document is null || _playMode || (selectedUri is null && _assetPicker.Selected < 0))
            return false;
        if (_lanSession.HasPendingAssetSync)
        {
            _status.Text = "Wait for the host to finish syncing imported models before adding an actor.";
            return false;
        }

        string uri = selectedUri ?? _assetPicker.GetItemMetadata(_assetPicker.Selected).AsString();
        if (!_catalog.TryGet(uri, out _))
            return false;
        if (_lanSession.IsClient && _unsharedAssetUris.Contains(uri))
        {
            _status.Text = "This imported model is not synchronized with the host yet.";
            return false;
        }
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
        int index = _document.Actors.Count;
        string actorJson = actorData.ToJsonString();
        LegacyActorData state = _document.InsertActor(actorData, index);
        WorldActorNode actor = CreateActorNode(state, index);
        RefreshActorList();
        SelectActor(actor);
        RecordHistory($"Add {state.DisplayName}",
            () => RemoveActorFromWorld(name),
            () => RestoreActorFromJson(actorJson, index));
        return true;
    }

    private void RemoveSelectedActor()
    {
        if (_document is null || _selectedActor is null || _playMode)
            return;
        if (_selectedActor.LegacyName.StartsWith("builtin:", StringComparison.Ordinal))
        {
            ShowError($"Built-in actors such as {_selectedActor.State.DisplayName} cannot be deleted.");
            return;
        }
        ActorDeletionSnapshot snapshot = CaptureActorDeletion(_selectedActor);
        if (!RemoveActorFromWorld(snapshot.Name))
            return;
        RecordHistory($"Delete {snapshot.DisplayName}",
            () => RestoreDeletedActor(snapshot),
            () => RemoveActorFromWorld(snapshot.Name));
    }
    private ActorDeletionSnapshot CaptureActorDeletion(WorldActorNode actor)
    {
        actor.SaveTransform();
        return new ActorDeletionSnapshot(
            actor.LegacyName,
            actor.State.DisplayName,
            actor.State.Data.ToJsonString(),
            _actors.IndexOf(actor),
            actor.GetChildren().OfType<WorldActorNode>().Select(child => child.LegacyName).ToArray());
    }

    private bool RemoveActorFromWorld(string actorName)
    {
        if (_document is null)
            return false;
        WorldActorNode? actor = _actors.FirstOrDefault(candidate => candidate.LegacyName == actorName);
        if (actor is null || actor.LegacyName.StartsWith("builtin:", StringComparison.Ordinal))
            return false;
        bool wasSelected = _selectedActor == actor;
        if (!_document.RemoveActor(actorName))
            return false;
        foreach (WorldActorNode child in actor.GetChildren().OfType<WorldActorNode>().ToArray())
        {
            Transform3D globalTransform = child.GlobalTransform;
            child.Reparent(_world, keepGlobalTransform: false);
            child.GlobalTransform = globalTransform;
        }
        _actors.Remove(actor);
        actor.QueueFree();
        RefreshActorList();
        if (wasSelected)
            SelectActor(_actors.FirstOrDefault());
        return true;
    }

    private WorldActorNode RestoreActorFromJson(string actorJson, int index)
    {
        if (_document is null || JsonNode.Parse(actorJson) is not JsonObject actorData)
            throw new InvalidDataException("The editor history contains an invalid actor snapshot.");
        LegacyActorData state = _document.InsertActor(actorData, index);
        WorldActorNode actor = CreateActorNode(state, index);
        RefreshActorList();
        SelectActor(actor);
        return actor;
    }

    private void RestoreDeletedActor(ActorDeletionSnapshot snapshot)
    {
        WorldActorNode actor = RestoreActorFromJson(snapshot.ActorJson, snapshot.Index);
        foreach (string childName in snapshot.ChildNames)
        {
            WorldActorNode? child = _actors.FirstOrDefault(candidate => candidate.LegacyName == childName);
            if (child is null)
                continue;
            Transform3D globalTransform = child.GlobalTransform;
            child.State.SetTransformParent(actor.LegacyName);
            child.Reparent(actor, keepGlobalTransform: false);
            child.GlobalTransform = globalTransform;
        }
    }

    private WorldActorNode CreateActorNode(LegacyActorData state, int index)
    {
        _catalog.TryGet(state.RenderableUri, out RenderableEntry? renderable);
        var actor = new WorldActorNode();
        actor.Initialize(state, renderable);
        actor.SetPlayMode(_playMode);
        _world.AddChild(actor);
        _actors.Insert(Math.Clamp(index, 0, _actors.Count), actor);
        WorldActorNode? parent = _actors.FirstOrDefault(candidate => candidate.LegacyName == state.TransformParent);
        if (parent is not null)
        {
            Transform3D globalTransform = actor.GlobalTransform;
            actor.Reparent(parent, keepGlobalTransform: false);
            actor.GlobalTransform = globalTransform;
        }
        return actor;
    }
    private void RefreshRuntimeActors()
    {
        RefreshActorList();
        RefreshBrainChoices();
        if (_lanSession.IsHost && !_applyingCollaborativePatch)
            _lanSession.MarkActorStructureChanged();
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
        for (int axis = 0; axis < 3; axis++)
        {
            _rotationFields[axis].Editable = hasActor && !_playMode;
            _rotationFields[axis].Value = hasActor ? actor!.GlobalRotationDegrees[axis] : 0d;
        }
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
        _behaviorEditorButton.Disabled = !hasActor || _playMode;
        _updatingInspector = false;
    }

    private void UpdateSelectedPosition(int axis, float value)
    {
        if (_updatingInspector || _playMode || _selectedActor is null)
            return;
        string actorName = _selectedActor.LegacyName;
        Transform3D oldTransform = _selectedActor.GlobalTransform;
        Vector3 position = _selectedActor.GlobalPosition;
        position[axis] = value;
        _selectedActor.GlobalPosition = position;
        Transform3D newTransform = _selectedActor.GlobalTransform;
        RecordHistory($"Move {_selectedActor.State.DisplayName}",
            () => SetActorTransform(actorName, oldTransform),
            () => SetActorTransform(actorName, newTransform));
    }

    private void UpdateSelectedRotation(int axis, float degrees)
    {
        if (_updatingInspector || _playMode || _selectedActor is null)
            return;
        string actorName = _selectedActor.LegacyName;
        Transform3D oldTransform = _selectedActor.GlobalTransform;
        Vector3 rotation = _selectedActor.GlobalRotationDegrees;
        rotation[axis] = degrees;
        _selectedActor.GlobalRotationDegrees = rotation;
        Transform3D newTransform = _selectedActor.GlobalTransform;
        RecordHistory($"Rotate {_selectedActor.State.DisplayName}",
            () => SetActorTransform(actorName, oldTransform),
            () => SetActorTransform(actorName, newTransform));
    }

    private void SetPlayMode(bool enabled, bool fromHost = false)
    {
        if (_document is null)
            return;
        if (_lanSession.IsClient && !fromHost)
        {
            _status.Text = "The host controls play mode; connected clients observe the shared simulation.";
            return;
        }
        if (enabled && (_behaviorRuntime is null || _behaviorRuntimeError is not null))
        {
            ShowError($"Behavior runtime cannot start:\n{_behaviorRuntimeError ?? "the project has not loaded successfully."}");
            return;
        }
        if (enabled)
        {
            _terrainEditorWindow?.SetBrushActive(false);
            EndTerrainStroke();
        }
        _playMode = enabled;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        foreach (WorldActorNode actor in _actors)
            actor.SetPlayMode(enabled);
        _playButton.Text = enabled ? "Stop" : "Play";
        _playButton.Disabled = _lanSession.IsClient;
        _behaviorUi.Visible = enabled;
        _actorList.Visible = !enabled;
        _positionFields[0].Editable = !enabled && _selectedActor is not null;
        _positionFields[1].Editable = !enabled && _selectedActor is not null;
        _positionFields[2].Editable = !enabled && _selectedActor is not null;
        foreach (SpinBox rotationField in _rotationFields)
            rotationField.Editable = !enabled && _selectedActor is not null;
        SetInspectorInputsEnabled(!enabled && _selectedActor is not null);
        _behaviorEditorButton.Disabled = enabled || _selectedActor is null;
        UpdateHistoryButtons();
        _status.Text = enabled
            ? _lanSession.IsHost
                ? "Host play mode — WASD/arrows to move; connected clients observe the simulation."
                : "Shared play mode — the host runs the simulation."
            : "Edit mode — select an actor and change its transform.";
        if (enabled && GetPlayerActor() is WorldActorNode player)
            _cameraTarget = player.GlobalPosition + new Vector3(0f, 3f, 0f);
        UpdateOrbitCamera();
        if (!fromHost)
            _lanSession.SetHostPlayMode(enabled);
    }

    private string? PrepareNetworkHostProjectCopy()
    {
        if (_document is null)
            return null;

        string fileName = Path.GetFileName(_document.SourcePath);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".voos", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The current project does not have a .voos filename.");

        string hostDirectory = ProjectSettings.GlobalizePath($"user://network-host/{Guid.NewGuid():N}");
        Directory.CreateDirectory(hostDirectory);
        string hostProjectPath = Path.Combine(hostDirectory, fileName);
        try
        {
            foreach (WorldActorNode actor in _actors)
                actor.SaveTransform();
            if (_terrain is not null)
            {
                _terrain.FlushUpdates();
                _document.SaveTerrainPayload(_terrain.Encode());
            }
            _catalog.CopyProjectAssets(GetProjectAssetSourcePath(), hostProjectPath);
            _document.SaveCopyTo(hostProjectPath);
            _networkHostProjectPath = hostProjectPath;
            return hostProjectPath;
        }
        catch
        {
            if (Directory.Exists(hostDirectory))
                Directory.Delete(hostDirectory, recursive: true);
            throw;
        }
    }

    private void ApplyNetworkSnapshot(string snapshotJson)
    {
        NetworkActorSnapshot[]? snapshots;
        try
        {
            snapshots = JsonSerializer.Deserialize<NetworkActorSnapshot[]>(
                snapshotJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException exception)
        {
            GD.PushWarning($"Ignored invalid network actor snapshot: {exception.Message}");
            return;
        }
        if (snapshots is null)
            return;

        foreach (NetworkActorSnapshot snapshot in snapshots)
        {
            WorldActorNode? actor = _actors.FirstOrDefault(candidate => candidate.LegacyName == snapshot.Name);
            if (actor is null)
                continue;
            var scale = new Vector3(snapshot.ScaleX, snapshot.ScaleY, snapshot.ScaleZ);
            var rotation = new Quaternion(snapshot.RotationX, snapshot.RotationY, snapshot.RotationZ, snapshot.RotationW);
            actor.GlobalTransform = new Transform3D(new Basis(rotation).Scaled(scale), new Vector3(snapshot.X, snapshot.Y, snapshot.Z));
            actor.LinearVelocity = new Vector3(snapshot.VelocityX, snapshot.VelocityY, snapshot.VelocityZ);
            actor.AngularVelocity = new Vector3(snapshot.AngularX, snapshot.AngularY, snapshot.AngularZ);
            var tint = new Color(snapshot.TintR, snapshot.TintG, snapshot.TintB, snapshot.TintA);
            if (actor.State.Tint != tint)
                actor.SetTint(tint);
            Vector3 renderableOffset = new(snapshot.OffsetX, snapshot.OffsetY, snapshot.OffsetZ);
            if (actor.State.ReadLegacyVector("renderableOffset", Vector3.Zero) != renderableOffset)
                actor.SetRenderableOffset(renderableOffset);
            var renderableRotation = new Quaternion(
                snapshot.RenderRotationX, snapshot.RenderRotationY, snapshot.RenderRotationZ, snapshot.RenderRotationW);
            if (actor.State.ReadLegacyQuaternion("renderableRotation") != renderableRotation)
                actor.SetRenderableRotation(renderableRotation);
            if (actor.State.LoopingAnimation != snapshot.Animation)
                actor.SetLoopingAnimation(snapshot.Animation);
            actor.SaveTransform();
        }
    }
    private void OpenReceivedNetworkProject(string path)
    {
        LoadProject(path);
        if (string.Equals(_document?.SourcePath, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            _lanSession.NotifyProjectLoaded();
    }

    private void ApplyNetworkActorStructure(string actorStatesJson)
    {
        if ((!_lanSession.IsClient && !_applyingCollaborativePatch) || _document is null)
            return;
        string[]? serializedActors;
        try
        {
            serializedActors = JsonSerializer.Deserialize<string[]>(actorStatesJson);
        }
        catch (JsonException exception)
        {
            GD.PushWarning($"Ignored invalid network actor structure: {exception.Message}");
            return;
        }
        if (serializedActors is null || serializedActors.Length > ushort.MaxValue)
        {
            GD.PushWarning("Ignored network actor structure with an invalid actor count.");
            return;
        }

        var actorData = new List<JsonObject>(serializedActors.Length);
        var receivedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string serializedActor in serializedActors)
        {
            if (JsonNode.Parse(serializedActor) is not JsonObject data)
            {
                GD.PushWarning("Ignored network actor structure containing a non-object actor.");
                return;
            }
            string name = LegacySaveDocument.ReadString(data["name"]);
            if (string.IsNullOrWhiteSpace(name) || !receivedNames.Add(name))
            {
                GD.PushWarning("Ignored network actor structure containing an empty or duplicate actor name.");
                return;
            }
            actorData.Add(data);
        }

        foreach (WorldActorNode actor in _actors.Where(existing => !receivedNames.Contains(existing.LegacyName)).ToArray())
        {
            _document.RemoveActor(actor.LegacyName);
            _actors.Remove(actor);
            actor.QueueFree();
        }

        var actorsByName = _actors.ToDictionary(actor => actor.LegacyName, StringComparer.Ordinal);
        foreach (JsonObject data in actorData)
        {
            string name = LegacySaveDocument.ReadString(data["name"]);
            if (actorsByName.TryGetValue(name, out WorldActorNode? existing))
            {
                existing.State.ApplyNetworkData(data);
                continue;
            }

            LegacyActorData state = _document.AddActor(data);
            _catalog.TryGet(state.RenderableUri, out RenderableEntry? renderable);
            var created = new WorldActorNode();
            created.Initialize(state, renderable);
            created.SetPlayMode(_playMode);
            _world.AddChild(created);
            _actors.Add(created);
            actorsByName.Add(name, created);
        }

        foreach (JsonObject data in actorData)
        {
            string name = LegacySaveDocument.ReadString(data["name"]);
            WorldActorNode actor = actorsByName[name];
            actor.ApplyNetworkState();
            actor.SetPlayMode(_playMode);
        }
        foreach (WorldActorNode actor in _actors)
        {
            WorldActorNode? desiredParent = actor.State.TransformParent.Length > 0
                ? _actors.FirstOrDefault(candidate => candidate.LegacyName == actor.State.TransformParent)
                : null;
            Node parent = desiredParent ?? _world;
            if (actor.GetParent() == parent)
                continue;
            Transform3D globalTransform = actor.GlobalTransform;
            actor.Reparent(parent, keepGlobalTransform: false);
            actor.GlobalTransform = globalTransform;
        }
        RefreshRuntimeActors();
        if (!_applyingCollaborativePatch)
            ResetCollaborativeSnapshot();
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
            _catalog.CopyProjectAssets(GetProjectAssetSourcePath(), path);
            _document.SaveTo(path);
            _catalog.LoadProjectAssets(path);
            PopulateAssetPicker();
            ReleaseRetainedHostAssetsAfterSave();
            _status.Text = $"Saved {Path.GetFileName(path)} — {_actors.Count} actors.";
        }
        catch (Exception exception)
        {
            ShowError($"Could not save project:\n{exception.Message}");
        }
    }

    private void ResetActors()
    {
        Dictionary<string, ActorMotionSnapshot> before = CaptureActorMotionStates();
        foreach (WorldActorNode actor in _actors)
        {
            actor.GlobalTransform = actor.State.GetWorldTransform();
            actor.LinearVelocity = actor.State.Velocity;
            actor.AngularVelocity = actor.State.AngularVelocity;
        }
        Dictionary<string, ActorMotionSnapshot> after = CaptureActorMotionStates();
        if (!MotionStatesEqual(before, after))
            RecordHistory("Reset actors", () => ApplyActorMotionStates(before), () => ApplyActorMotionStates(after));
        if (_selectedActor is not null)
            SelectActor(_selectedActor);
    }

    private Dictionary<string, ActorMotionSnapshot> CaptureActorMotionStates()
    {
        var states = new Dictionary<string, ActorMotionSnapshot>(_actors.Count, StringComparer.Ordinal);
        foreach (WorldActorNode actor in _actors)
            states.Add(actor.LegacyName, new ActorMotionSnapshot(actor.GlobalTransform, actor.LinearVelocity, actor.AngularVelocity));
        return states;
    }

    private static bool MotionStatesEqual(
        IReadOnlyDictionary<string, ActorMotionSnapshot> left,
        IReadOnlyDictionary<string, ActorMotionSnapshot> right)
    {
        if (left.Count != right.Count)
            return false;
        foreach ((string actorName, ActorMotionSnapshot state) in left)
        {
            if (!right.TryGetValue(actorName, out ActorMotionSnapshot other) || state != other)
                return false;
        }
        return true;
    }

    private void ApplyActorMotionStates(IReadOnlyDictionary<string, ActorMotionSnapshot> states)
    {
        foreach ((string actorName, ActorMotionSnapshot state) in states)
        {
            WorldActorNode? actor = _actors.FirstOrDefault(candidate => candidate.LegacyName == actorName);
            if (actor is null)
                continue;
            actor.GlobalTransform = state.Transform;
            actor.LinearVelocity = state.Velocity;
            actor.AngularVelocity = state.AngularVelocity;
        }
        if (_selectedActor is not null)
            SelectActor(_selectedActor);
    }

    private void SetActorTransform(string actorName, Transform3D transform)
    {
        WorldActorNode? actor = FindActor(actorName);
        if (actor is null)
            return;
        actor.GlobalTransform = transform;
        if (_selectedActor == actor)
            SelectActor(actor);
    }

    private void RecordHistory(string label, Action undo, Action redo)
    {
        if (_applyingHistory || _playMode)
            return;
        if (_historyPosition < _history.Count)
            _history.RemoveRange(_historyPosition, _history.Count - _historyPosition);
        _history.Add(new HistoryEntry(label, undo, redo));
        if (_history.Count > MaxHistoryItems)
        {
            _history.RemoveAt(0);
            _historyPosition--;
        }
        _historyPosition = _history.Count;
        UpdateHistoryButtons();
        NotifyCollaborativeEdits();
    }

    private void UndoEdit()
    {
        if (_playMode || _historyPosition == 0)
            return;
        HistoryEntry entry = _history[--_historyPosition];
        _applyingHistory = true;
        try
        {
            entry.Undo();
        }
        finally
        {
            _applyingHistory = false;
        }
        NotifyCollaborativeEdits();
        _status.Text = $"Undid: {entry.Label}";
        UpdateHistoryButtons();
    }

    private void RedoEdit()
    {
        if (_playMode || _historyPosition >= _history.Count)
            return;
        HistoryEntry entry = _history[_historyPosition++];
        _applyingHistory = true;
        try
        {
            entry.Redo();
        }
        finally
        {
            _applyingHistory = false;
        }
        NotifyCollaborativeEdits();
        _status.Text = $"Redid: {entry.Label}";
        UpdateHistoryButtons();
    }

    private void ClearHistory()
    {
        _history.Clear();
        _historyPosition = 0;
        _pendingTextEdit = null;
        UpdateHistoryButtons();
    }

    private void UpdateHistoryButtons()
    {
        if (_undoButton is null || _redoButton is null)
            return;
        bool enabled = _document is not null && !_playMode;
        _undoButton.Disabled = !enabled || _historyPosition == 0;
        _redoButton.Disabled = !enabled || _historyPosition >= _history.Count;
    }

    private WorldActorNode? FindActor(string actorName)
        => _actors.FirstOrDefault(actor => actor.LegacyName == actorName);

    private WorldActorNode? GetPlayerActor()
    {
        int targetSlot = _lanSession.IsClient ? _lanSession.LocalPlayerSlot : 1;
        int slot = 0;
        foreach (WorldActorNode actor in _actors)
        {
            if (!actor.State.IsPlayerControllable)
                continue;
            if (++slot == targetSlot)
                return actor;
        }
        return null;
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
