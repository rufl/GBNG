using GBNG.Networking;
using Godot;

namespace GBNG.App;

public partial class LegacyLanNetworkWindow : Window
{
    private readonly LegacyLanSession _session;
    private readonly Func<string?> _projectPath;
    private readonly Action<string> _openReceivedProject;
    private readonly List<LegacyLanRoom> _roomItems = new();
    private LineEdit _playerName = null!;
    private LineEdit _roomName = null!;
    private LineEdit _port = null!;
    private LineEdit _address = null!;
    private ItemList _roomList = null!;
    private ItemList _playerList = null!;
    private Label _status = null!;

    public LegacyLanNetworkWindow(LegacyLanSession session, Func<string?> projectPath, Action<string> openReceivedProject)
    {
        _session = session;
        _projectPath = projectPath;
        _openReceivedProject = openReceivedProject;
        Title = "LAN multiplayer";
        Size = new Vector2I(760, 660);
        CloseRequested += () => Hide();
        _session.StateChanged += RefreshState;
        _session.RoomDiscovered += OnRoomDiscovered;
        _session.ProjectReceived += OnProjectReceived;
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

        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 10);
        margin.AddChild(layout);
        layout.AddChild(CreateLabel("Direct host/join on your local network. The host sends a copy of its .voos project and custom asset sidecar; the host runs the simulation and clients observe it. No global matchmaking."));

        var identity = new GridContainer { Columns = 2 };
        identity.AddChild(CreateLabel("Player name"));
        _playerName = new LineEdit { Text = System.Environment.UserName, MaxLength = 32, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        identity.AddChild(_playerName);
        identity.AddChild(CreateLabel("Room name"));
        _roomName = new LineEdit { Text = "Game Builder room", MaxLength = 48, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        identity.AddChild(_roomName);
        identity.AddChild(CreateLabel("ENet port"));
        _port = new LineEdit { Text = "7777", MaxLength = 5, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        identity.AddChild(_port);
        layout.AddChild(identity);

        var hostControls = new HBoxContainer();
        var host = new Button { Name = "HostLanRoom", Text = "Host current project", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        host.Pressed += HostCurrentProject;
        hostControls.AddChild(host);
        var discover = new Button { Name = "DiscoverLanRooms", Text = "Discover LAN rooms" };
        discover.Pressed += _session.StartDiscovery;
        hostControls.AddChild(discover);
        var leave = new Button { Name = "LeaveLanRoom", Text = "Leave" };
        leave.Pressed += _session.Leave;
        hostControls.AddChild(leave);
        layout.AddChild(hostControls);

        layout.AddChild(CreateLabel("LAN rooms"));
        _roomList = new ItemList { Name = "LanRooms", CustomMinimumSize = new Vector2(0f, 150f), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _roomList.ItemSelected += _ => CopySelectedRoomAddress();
        layout.AddChild(_roomList);

        var direct = new HBoxContainer();
        _address = new LineEdit { Name = "DirectAddress", PlaceholderText = "Host IP address", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        direct.AddChild(_address);
        var join = new Button { Name = "JoinLanRoom", Text = "Join IP" };
        join.Pressed += JoinAddress;
        direct.AddChild(join);
        layout.AddChild(direct);

        layout.AddChild(CreateLabel("Players in room"));
        _playerList = new ItemList { Name = "LanPlayers", CustomMinimumSize = new Vector2(0f, 110f), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        layout.AddChild(_playerList);
        _status = CreateLabel("");
        _status.Name = "LanStatus";
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.CustomMinimumSize = new Vector2(0f, 42f);
        layout.AddChild(_status);
    }

    private void HostCurrentProject()
    {
        string? projectPath;
        try
        {
            projectPath = _projectPath();
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not prepare the host project: {exception.Message}";
            return;
        }
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            _status.Text = "Open a .voos project before hosting.";
            return;
        }
        if (!int.TryParse(_port.Text, out int port))
        {
            _status.Text = "Enter a valid ENet port.";
            return;
        }
        _session.Host(projectPath, _roomName.Text, port, _playerName.Text);
    }

    private void JoinAddress()
    {
        if (!int.TryParse(_port.Text, out int port))
        {
            _status.Text = "Enter a valid ENet port.";
            return;
        }
        string address = _address.Text.Trim();
        if (_roomList.GetSelectedItems().Length > 0)
        {
            int selected = _roomList.GetSelectedItems()[0];
            if ((uint)selected < (uint)_roomItems.Count)
            {
                LegacyLanRoom room = _roomItems[selected];
                address = room.Address;
                port = room.Port;
            }
        }
        _session.Join(address, port, _playerName.Text);
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

    private void OnProjectReceived(string path) => _openReceivedProject(path);

    private void RefreshState()
    {
        if (!IsInstanceValid(_status))
            return;
        _status.Text = _session.Status;
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
    }
}
