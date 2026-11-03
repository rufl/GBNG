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
        Title = "Play together";
        Size = new Vector2I(760, 470);
        MinSize = new Vector2I(640, 400);
        Hide();
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
        margin.AddThemeConstantOverride("margin_left", 22);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_right", 22);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        AddChild(margin);

        var scroll = new ScrollContainer();
        margin.AddChild(scroll);

        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(layout);
        layout.AddChild(CreateLabel("Play together", 26));
        layout.AddChild(CreateLabel(
            "Host a private family game or join one with an invite code. Your original project files stay on the host.",
            14,
            "#aebdcb"));

        var identity = new HBoxContainer { CustomMinimumSize = new Vector2(0f, 40f) };
        var nameLabel = CreateLabel("Your name", 14);
        nameLabel.CustomMinimumSize = new Vector2(100f, 0f);
        nameLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        identity.AddChild(nameLabel);
        _playerName = new LineEdit
        {
            Name = "PlayerName",
            Text = _settings.PlayerName,
            MaxLength = 32,
            CustomMinimumSize = new Vector2(180f, 40f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        identity.AddChild(_playerName);
        layout.AddChild(identity);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 12);
        layout.AddChild(actions);

        var hostCard = CreateCard();
        hostCard.CustomMinimumSize = new Vector2(0f, 172f);
        hostCard.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        actions.AddChild(hostCard);
        var hostContent = new VBoxContainer();
        hostContent.AddThemeConstantOverride("separation", 8);
        hostCard.AddChild(hostContent);
        hostContent.AddChild(CreateLabel("HOST A GAME", 12, "#83d5f5"));
        hostContent.AddChild(CreateLabel("Start a family room", 19));
        hostContent.AddChild(CreateLabel(
            "Works on your home network by default. Add your own matchmaking service in Advanced options for Internet invites.",
            13,
            "#aebdcb"));
        _hostButton = new Button
        {
            Name = "HostGame",
            Text = "Host game",
            CustomMinimumSize = new Vector2(0f, 44f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        StylePrimaryButton(_hostButton);
        _hostButton.Pressed += HostGame;
        hostContent.AddChild(_hostButton);

        var joinCard = CreateCard();
        joinCard.CustomMinimumSize = new Vector2(0f, 172f);
        joinCard.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        actions.AddChild(joinCard);
        var joinContent = new VBoxContainer();
        joinContent.AddThemeConstantOverride("separation", 8);
        joinCard.AddChild(joinContent);
        joinContent.AddChild(CreateLabel("JOIN A GAME", 12, "#83d5f5"));
        joinContent.AddChild(CreateLabel("Use an invite code", 19));
        joinContent.AddChild(CreateLabel(
            "Enter the eight-character code shared by the host. Internet joining requires their matchmaking service address.",
            13,
            "#aebdcb"));
        var inviteRow = new HBoxContainer();
        inviteRow.AddThemeConstantOverride("separation", 8);
        _joinCode = new LineEdit
        {
            Name = "InviteCode",
            PlaceholderText = "Invite code",
            MaxLength = 12,
            CustomMinimumSize = new Vector2(0f, 40f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        inviteRow.AddChild(_joinCode);
        _joinCodeButton = new Button
        {
            Name = "JoinByCode",
            Text = "Join game",
            CustomMinimumSize = new Vector2(112f, 40f)
        };
        _joinCodeButton.Pressed += JoinByCode;
        inviteRow.AddChild(_joinCodeButton);
        joinContent.AddChild(inviteRow);

        var sessionCard = CreateCard("#182733", "#375367");
        layout.AddChild(sessionCard);
        var sessionContent = new VBoxContainer();
        sessionContent.AddThemeConstantOverride("separation", 6);
        sessionCard.AddChild(sessionContent);
        sessionContent.AddChild(CreateLabel("Game status", 15));
        var statusRow = new HBoxContainer();
        sessionContent.AddChild(statusRow);
        _status = CreateLabel("Not connected.", 14, "#d3dfeb");
        _status.Name = "NetworkStatus";
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.CustomMinimumSize = new Vector2(0f, 34f);
        _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        statusRow.AddChild(_status);
        _inviteCodeLabel = CreateLabel(string.Empty, 14, "#9ce7bd");
        _inviteCodeLabel.Name = "InviteCodeStatus";
        _inviteCodeLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _inviteCodeLabel.Visible = false;
        statusRow.AddChild(_inviteCodeLabel);
        _copyInviteButton = new Button
        {
            Name = "CopyInviteCode",
            Text = "Copy code",
            CustomMinimumSize = new Vector2(104f, 36f),
            Disabled = true,
            Visible = false
        };
        _copyInviteButton.Pressed += CopyInviteCode;
        statusRow.AddChild(_copyInviteButton);

        var playersCard = CreateCard();
        layout.AddChild(playersCard);
        var playersContent = new VBoxContainer();
        playersContent.AddThemeConstantOverride("separation", 8);
        playersCard.AddChild(playersContent);
        var playerHeading = new HBoxContainer();
        playersContent.AddChild(playerHeading);
        var playerTitle = CreateLabel("Players in this game", 15);
        playerTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        playerHeading.AddChild(playerTitle);
        _leaveButton = new Button
        {
            Name = "LeaveRoom",
            Text = "Leave game",
            CustomMinimumSize = new Vector2(112f, 36f)
        };
        _leaveButton.Pressed += _session.Leave;
        playerHeading.AddChild(_leaveButton);
        _playerList = new ItemList
        {
            Name = "Players",
            CustomMinimumSize = new Vector2(0f, 82f),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        playersContent.AddChild(_playerList);

        var advancedToggle = new Button
        {
            Name = "AdvancedNetworkOptions",
            Text = "Advanced options · Internet service, LAN discovery and direct IP",
            Flat = true,
            CustomMinimumSize = new Vector2(0f, 36f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        advancedToggle.Pressed += ToggleAdvanced;
        layout.AddChild(advancedToggle);

        _advancedPanel = CreateCard();
        _advancedPanel.Name = "AdvancedNetworkPanel";
        _advancedPanel.Visible = false;
        layout.AddChild(_advancedPanel);
        var advanced = new VBoxContainer();
        advanced.AddThemeConstantOverride("separation", 8);
        _advancedPanel.AddChild(advanced);
        advanced.AddChild(CreateLabel("Internet matchmaking", 16));
        advanced.AddChild(CreateLabel(
            "Optional. This project does not bundle a public service. Use a trusted WSS endpoint or self-host the included matchmaking and relay tools.",
            13,
            "#aebdcb"));
        _matchmakingUrl = new LineEdit
        {
            Name = "MatchmakingUrl",
            Text = _settings.MatchmakingUrl,
            PlaceholderText = "wss://your-host/v1/session",
            CustomMinimumSize = new Vector2(0f, 40f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        advanced.AddChild(_matchmakingUrl);
        var settingsButtons = new HBoxContainer();
        var saveSettings = new Button { Name = "SaveNetworkSettings", Text = "Save settings" };
        saveSettings.Pressed += SaveSettings;
        settingsButtons.AddChild(saveSettings);
        var hostLan = new Button { Name = "HostOnLan", Text = "Host on LAN" };
        hostLan.Pressed += HostLan;
        settingsButtons.AddChild(hostLan);
        advanced.AddChild(settingsButtons);

        advanced.AddChild(CreateLabel("Local network (LAN)", 16));
        advanced.AddChild(CreateLabel(
            "Discover rooms on this network or connect directly to the host's IP address. The host may need to allow the game through its firewall.",
            13,
            "#aebdcb"));
        var lanSettings = new GridContainer { Columns = 2 };
        lanSettings.AddChild(CreateLabel("ENet port"));
        _port = new LineEdit
        {
            Name = "LanPort",
            Text = "7777",
            MaxLength = 5,
            CustomMinimumSize = new Vector2(0f, 38f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        lanSettings.AddChild(_port);
        lanSettings.AddChild(CreateLabel("Host IP"));
        _address = new LineEdit
        {
            Name = "DirectAddress",
            PlaceholderText = "IPv4 or IPv6 address",
            CustomMinimumSize = new Vector2(0f, 38f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        lanSettings.AddChild(_address);
        advanced.AddChild(lanSettings);

        var lanControls = new HBoxContainer();
        var discover = new Button
        {
            Name = "DiscoverLanRooms",
            Text = "Discover LAN rooms",
            CustomMinimumSize = new Vector2(0f, 38f)
        };
        discover.Pressed += _session.StartDiscovery;
        lanControls.AddChild(discover);
        var joinAddress = new Button
        {
            Name = "JoinLanRoom",
            Text = "Join by IP",
            CustomMinimumSize = new Vector2(0f, 38f)
        };
        joinAddress.Pressed += JoinAddress;
        lanControls.AddChild(joinAddress);
        advanced.AddChild(lanControls);
        advanced.AddChild(CreateLabel("Discovered rooms", 14));
        _roomList = new ItemList
        {
            Name = "LanRooms",
            CustomMinimumSize = new Vector2(0f, 100f),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        _roomList.ItemSelected += _ => CopySelectedRoomAddress();
        advanced.AddChild(_roomList);
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
            : string.Empty;
        _inviteCodeLabel.Visible = inviteReady;
        _copyInviteButton.Visible = inviteReady;
        _copyInviteButton.Disabled = !inviteReady;
        _status.AddThemeColorOverride("font_color", _session.HasPeer ? new Color("#9ce7bd") : new Color("#d3dfeb"));
        _hostButton.Disabled = _session.IsBusy;
        _joinCode.Editable = !_session.IsBusy;
        _joinCodeButton.Disabled = _session.IsBusy;
        _leaveButton.Disabled = !_session.HasPeer;
    }

    private static Label CreateLabel(string text, int fontSize = 14, string? color = null)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        if (color is not null)
            label.AddThemeColorOverride("font_color", new Color(color));
        return label;
    }

    private static PanelContainer CreateCard(string background = "#1b2734", string border = "#3a5065")
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(background),
            BorderColor = new Color(border),
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10,
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            ContentMarginLeft = 16,
            ContentMarginRight = 16,
            ContentMarginTop = 14,
            ContentMarginBottom = 14
        });
        return card;
    }

    private static void StylePrimaryButton(Button button)
    {
        button.AddThemeStyleboxOverride("normal", CreateButtonStyle("#83d5f5"));
        button.AddThemeStyleboxOverride("hover", CreateButtonStyle("#a4e2fa"));
        button.AddThemeStyleboxOverride("pressed", CreateButtonStyle("#65bfdf"));
        button.AddThemeStyleboxOverride("disabled", CreateButtonStyle("#35485a"));
        button.AddThemeColorOverride("font_color", new Color("#10212b"));
        button.AddThemeColorOverride("font_hover_color", new Color("#10212b"));
        button.AddThemeColorOverride("font_pressed_color", new Color("#10212b"));
        button.AddThemeColorOverride("font_disabled_color", new Color("#aebdcb"));
    }

    private static StyleBoxFlat CreateButtonStyle(string color)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(color),
            BorderColor = new Color(color),
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 9,
            ContentMarginBottom = 9
        };
        return style;
    }

    private static string FormatRoom(LegacyLanRoom room)
        => $"{room.Name} — {room.Address}:{room.Port} — {room.PlayerCount}/{room.Capacity} players";


    public override void _ExitTree()
    {
        _session.StateChanged -= RefreshState;
        _session.RoomDiscovered -= OnRoomDiscovered;
        _session.ProjectReceived -= OnProjectReceived;
        _session.InviteCodeChanged -= OnInviteCodeChanged;
    }
}
