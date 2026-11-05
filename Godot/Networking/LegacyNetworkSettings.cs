using Godot;

namespace GBNG.Networking;

public sealed class LegacyNetworkSettings
{
    private const string SettingsPath = "user://network-settings.cfg";
    private const string SectionName = "network";
    private readonly string _settingsPath;
    private string _lastJoinedRoomCode = string.Empty;

    private LegacyNetworkSettings(string settingsPath) => _settingsPath = settingsPath;

    public string MatchmakingUrl { get; set; } = string.Empty;
    public string PlayerName { get; set; } = System.Environment.UserName;
    public string LastJoinedRoomCode
    {
        get => _lastJoinedRoomCode;
        set => _lastJoinedRoomCode = NormalizeRoomCode(value);
    }

    public static LegacyNetworkSettings Load(string? settingsPath = null)
    {
        var settings = new LegacyNetworkSettings(settingsPath ?? SettingsPath);
        var config = new ConfigFile();
        if (config.Load(settings._settingsPath) != Error.Ok)
            return settings;

        settings.MatchmakingUrl = config.GetValue(SectionName, "matchmaking_url", string.Empty).AsString().Trim();
        string playerName = config.GetValue(SectionName, "player_name", settings.PlayerName).AsString().Trim();
        if (playerName.Length > 0)
            settings.PlayerName = playerName;
        settings.LastJoinedRoomCode = config.GetValue(SectionName, "last_joined_room_code", string.Empty).AsString();
        return settings;
    }

    public Error Save()
    {
        var config = new ConfigFile();
        config.SetValue(SectionName, "matchmaking_url", MatchmakingUrl.Trim());
        config.SetValue(SectionName, "player_name", PlayerName.Trim());
        config.SetValue(SectionName, "last_joined_room_code", LastJoinedRoomCode);
        return config.Save(_settingsPath);
    }

    private static string NormalizeRoomCode(string? value)
    {
        string normalized = new((value ?? string.Empty).Where(char.IsAsciiLetterOrDigit).ToArray());
        normalized = normalized.ToUpperInvariant();
        return normalized.Length == 8 ? normalized : string.Empty;
    }
}
