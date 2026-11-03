using GBNG.Networking;
using Godot;

namespace GBNG.App;

public partial class LegacyLanNetworkWindow : Window
{
    private readonly LegacyLanSession _session;
    private readonly LegacyNetworkSettings _settings;
    private readonly Func<string?> _projectPath;
    private readonly Action<string> _openReceivedProject;
    private readonly List<LegacyLanRoom> _roomItems = new();
    private LineEdit _playerName = null!;
    private LineEdit _matchmakingUrl = null!;
    private LineEdit _joinCode = null!;
    private LineEdit _port = null!;
    private LineEdit _address = null!;
    private ItemList _roomList = null!;
    private ItemList _playerList = null!;
    private Label _status = null!;
    private Label _inviteCodeLabel = null!;
    private Button _hostButton = null!;
    private Button _joinCodeButton = null!;
    private Button _copyInviteButton = null!;
    private Button _leaveButton = null!;
    private PanelContainer _advancedPanel = null!;
    private bool _localNetworkOnly;

    public LegacyLanNetworkWindow(LegacyLanSession session, Func<string?> projectPath, Action<string> openReceivedProject)
    {
        _session = session;
        _settings = LegacyNetworkSettings.Load();
        _projectPath = projectPath;
        _openReceivedProject = openReceivedProject;
        Title = "Host & join";
        Size = new Vector2I(760, 650);
        CloseRequested += () => Hide();
        _session.StateChanged += RefreshState;
        _session.RoomDiscovered += OnRoomDiscovered;
        _session.ProjectReceived += OnProjectReceived;
        _session.InviteCodeChanged += OnInviteCodeChanged;
        BuildInterface();
        RefreshState();
    }

    private void BuildInterface()
    {
        var margin = new MarginContainer
        {
            AnchorRight = 1f,
            AnchorBottom = 1f
        };
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        AddChild(margin);

        var scroll = new ScrollContainer();
        margin.AddChild(scroll);

        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(layout);
        layout.AddChild(CreateLabel("Start a private game for your family. The host shares a project copy and runs the simulation; other players join without editing the host's files."));

        var identity = new GridContainer { Columns = 2 };
        identity.AddChild(CreateLabel("Your name"));
        _playerName = new LineEdit
        {
            Name = "PlayerName",
            Text = _settings.PlayerName,
            MaxLength = 32,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        identity.AddChild(_playerName);
        layout.AddChild(identity);

        _hostButton = new Button
        {
            Name = "HostGame",
            Text = "Host game",
            CustomMinimumSize = new Vector2(0f, 46f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _hostButton.Pressed += HostGame;
        layout.AddChild(_hostButton);

        var inviteRow = new HBoxContainer();
        _joinCode = new LineEdit
        {
            Name = "InviteCode",
            PlaceholderText = "Invite code",
            MaxLength = 12,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        inviteRow.AddChild(_joinCode);
        _joinCodeButton = new Button { Name = "JoinByCode", Text = "Join with code" };
        _joinCodeButton.Pressed += JoinByCode;
        inviteRow.AddChild(_joinCodeButton);
        layout.AddChild(inviteRow);

        var inviteStatus = new HBoxContainer();
        _inviteCodeLabel = CreateLabel("Internet invite code appears here when the room is ready.");
        _inviteCodeLabel.Name = "InviteCodeStatus";
        _inviteCodeLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        inviteStatus.AddChild(_inviteCodeLabel);
        _copyInviteButton = new Button { Name = "CopyInviteCode", Text = "Copy code", Disabled = true };
        _copyInviteButton.Pressed += CopyInviteCode;
        inviteStatus.AddChild(_copyInviteButton);
        layout.AddChild(inviteStatus);

        var advancedToggle = new Button { Name = "AdvancedNetworkOptions", Text = "Advanced network options ▸", Flat = true };
        advancedToggle.Pressed += ToggleAdvanced;
        layout.AddChild(advancedToggle);

        _advancedPanel = new PanelContainer { Name = "AdvancedNetworkPanel", Visible = false };
        layout.AddChild(_advancedPanel);
        var advanced = new VBoxContainer();
        advanced.AddThemeConstantOverride("separation", 8);
        _advancedPanel.AddChild(advanced);
        advanced.AddChild(CreateLabel("Internet service (optional)"));
        advanced.AddChild(CreateLabel("A WSS matchmaking address enables invite codes. No public service is bundled; use a service you trust or self-host the included tooling."));
        _matchmakingUrl = new LineEdit
        {
            Name = "MatchmakingUrl",
            Text = _settings.MatchmakingUrl,
            PlaceholderText = "wss://your-host/v1/session",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        advanced.AddChild(_matchmakingUrl);
        var settingsButtons = new HBoxContainer();
        var saveSettings = new Button { Name = "SaveNetworkSettings", Text = "Save settings" };
        saveSettings.Pressed += SaveSettings;
        settingsButtons.AddChild(saveSettings);
        var hostLan = new Button { Name = "HostOnLan", Text = "Host on this LAN" };
        hostLan.Pressed += HostLan;
        settingsButtons.AddChild(hostLan);
        advanced.AddChild(settingsButtons);

        var lanHeading = CreateLabel("Local network");
        advanced.AddChild(lanHeading);
        var lanSettings = new GridContainer { Columns = 2 };
        lanSettings.AddChild(CreateLabel("ENet port"));
        _port = new LineEdit { Name = "LanPort", Text = "7777", MaxLength = 5 };
        lanSettings.AddChild(_port);
        lanSettings.AddChild(CreateLabel("Host IP"));
        _address = new LineEdit { Name = "DirectAddress", PlaceholderText = "IPv4 or IPv6 address" };
        lanSettings.AddChild(_address);
        advanced.AddChild(lanSettings);

        var lanControls = new HBoxContainer();
        var discover = new Button { Name = "DiscoverLanRooms", Text = "Discover LAN rooms" };
        discover.Pressed += _session.StartDiscovery;
        lanControls.AddChild(discover);
        var joinAddress = new Button { Name = "JoinLanRoom", Text = "Join by IP" };
        joinAddress.Pressed += JoinAddress;
        lanControls.AddChild(joinAddress);
        _leaveButton = new Button { Name = "LeaveRoom", Text = "Leave game" };
        _leaveButton.Pressed += _session.Leave;
        lanControls.AddChild(_leaveButton);
        advanced.AddChild(lanControls);

        advanced.AddChild(CreateLabel("LAN rooms"));
        _roomList = new ItemList
        {
            Name = "LanRooms",
            CustomMinimumSize = new Vector2(0f, 110f),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        _roomList.ItemSelected += _ => CopySelectedRoomAddress();
        advanced.AddChild(_roomList);

        layout.AddChild(CreateLabel("Players in this game"));
        _playerList = new ItemList
        {
            Name = "Players",
            CustomMinimumSize = new Vector2(0f, 86f),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        layout.AddChild(_playerList);
        _status = CreateLabel(string.Empty);
        _status.Name = "NetworkStatus";
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.CustomMinimumSize = new Vector2(0f, 42f);
        layout.AddChild(_status);
    }

    private void HostGame()
    {
        if (!TryGetProjectPath(out string projectPath))
            return;
        SavePreferences();
        _localNetworkOnly = false;
        _roomItems.Clear();
        _roomList.Clear();
        if (string.IsNullOrWhiteSpace(_matchmakingUrl.Text))
        {
            int port = GetLanPort();
            if (port == 0)
                return;
            _localNetworkOnly = _session.Host(projectPath, "Family game", port, _playerName.Text) == Error.Ok;
            RefreshState();
            return;
        }

        _session.HostOnline(projectPath, _playerName.Text, _matchmakingUrl.Text);
    }

    private void HostLan()
    {
        if (!TryGetProjectPath(out string projectPath))
            return;
        int port = GetLanPort();
        if (port == 0)
            return;
        SavePreferences();
        _roomItems.Clear();
        _roomList.Clear();
        _localNetworkOnly = _session.Host(projectPath, "Family game", port, _playerName.Text) == Error.Ok;
        RefreshState();
    }

    private bool TryGetProjectPath(out string projectPath)
    {
        try
        {
            projectPath = _projectPath() ?? string.Empty;
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not prepare the host project: {exception.Message}";
            projectPath = string.Empty;
            return false;
        }
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            _status.Text = "Open a project before hosting.";
            return false;
        }
        return true;
    }

    private int GetLanPort()
    {
        if (int.TryParse(_port.Text, out int port) && port is >= 1 and <= 65_535)
            return port;
        _status.Text = "Enter a LAN port between 1 and 65535.";
        return 0;
    }

    private void JoinByCode()
    {
        if (string.IsNullOrWhiteSpace(_matchmakingUrl.Text))
        {
            _advancedPanel.Visible = true;
            _status.Text = "Invite-code joining needs a WSS matchmaking address. Configure one in Advanced network options.";
            return;
        }
        SavePreferences();
        _localNetworkOnly = false;
        _session.JoinOnline(_joinCode.Text, _playerName.Text, _matchmakingUrl.Text);
    }

    private void JoinAddress()
    {
        int port = GetLanPort();
        if (port == 0)
            return;
        string address = _address.Text.Trim();
        int[] selected = _roomList.GetSelectedItems();
        if (selected.Length > 0 && (uint)selected[0] < (uint)_roomItems.Count)
        {
            LegacyLanRoom room = _roomItems[selected[0]];
            address = room.Address;
            port = room.Port;
        }
        _localNetworkOnly = false;
        _session.Join(address, port, _playerName.Text);
    }

    private void SaveSettings()
    {
        Error error = SavePreferences();
        _status.Text = error == Error.Ok
            ? "Network settings saved on this device."
            : $"Could not save network settings: {error}.";
    }

    private Error SavePreferences()
    {
        _settings.MatchmakingUrl = _matchmakingUrl.Text.Trim();
        _settings.PlayerName = _playerName.Text.Trim();
        return _settings.Save();
    }

    private void ToggleAdvanced()
        => _advancedPanel.Visible = !_advancedPanel.Visible;

    private void CopyInviteCode()
    {
        if (_session.OnlineRoomCode.Length == 8)
            DisplayServer.ClipboardSet(_session.OnlineRoomCode);
    }

    private void CopySelectedRoomAddress()
    {
        int[] selected = _roomList.GetSelectedItems();
        if (selected.Length == 0 || (uint)selected[0] >= (uint)_roomItems.Count)
            return;
        LegacyLanRoom room = _roomItems[selected[0]];
        _address.Text = room.Address;
        _port.Text = room.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnRoomDiscovered(LegacyLanRoom room)
    {
        int index = _roomItems.FindIndex(existing => existing.Id == room.Id);
        if (index < 0)
        {
            _roomItems.Add(room);
            _roomList.AddItem(FormatRoom(room));
        }
        else
        {
            _roomItems[index] = room;
            _roomList.SetItemText(index, FormatRoom(room));
        }
    }

    private void OnInviteCodeChanged(string _) => RefreshState();

    private void OnProjectReceived(string path) => _openReceivedProject(path);

    private void RefreshState()
    {
        if (!IsInstanceValid(_status))
            return;
        _status.Text = _session.Status;
        if (_localNetworkOnly && _session.HasPeer)
            _status.Text += " Local network only; set up matchmaking in Advanced options for Internet invites.";
        _playerList.Clear();
        foreach (LegacyLanPlayer player in _session.Players.OrderBy(player => player.SlotNumber))
            _playerList.AddItem($"{player.SlotNumber}. {player.Name}");

        if (_roomList.ItemCount == 0 && _session.Rooms.Count > 0)
        {
            _roomItems.Clear();
            _roomList.Clear();
            foreach (LegacyLanRoom room in _session.Rooms)
            {
                _roomItems.Add(room);
                _roomList.AddItem(FormatRoom(room));
            }
        }
        if (_session.Rooms.Count == 0 && _roomList.ItemCount > 0 && _session.Status.StartsWith("Searching", StringComparison.Ordinal))
        {
            _roomItems.Clear();
            _roomList.Clear();
        }

        string inviteCode = _session.OnlineRoomCode;
        bool inviteReady = inviteCode.Length == 8;
        _inviteCodeLabel.Text = inviteReady
            ? $"Share this private invite code: {inviteCode}"
            : "Internet invite code appears here when the room is ready.";
        _copyInviteButton.Disabled = !inviteReady;
        _hostButton.Disabled = _session.IsBusy;
        _joinCodeButton.Disabled = _session.IsBusy;
        _leaveButton.Disabled = !_session.HasPeer;
    }

    private static string FormatRoom(LegacyLanRoom room)
        => $"{room.Name} — {room.Address}:{room.Port} — {room.PlayerCount}/{room.Capacity} players";

    private static Label CreateLabel(string text)
        => new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };

    public override void _ExitTree()
    {
        _session.StateChanged -= RefreshState;
        _session.RoomDiscovered -= OnRoomDiscovered;
        _session.ProjectReceived -= OnProjectReceived;
        _session.InviteCodeChanged -= OnInviteCodeChanged;
    }
}
