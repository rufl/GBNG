using Godot;

namespace GBNG.Networking;

public sealed class LegacyNetworkSettings
{
    private const string SettingsPath = "user://network-settings.cfg";
    private const string SectionName = "network";

    public string MatchmakingUrl { get; set; } = string.Empty;
    public string PlayerName { get; set; } = System.Environment.UserName;

    public static LegacyNetworkSettings Load()
    {
        var settings = new LegacyNetworkSettings();
        var config = new ConfigFile();
        if (config.Load(SettingsPath) != Error.Ok)
            return settings;

        settings.MatchmakingUrl = config.GetValue(SectionName, "matchmaking_url", string.Empty).AsString().Trim();
        string playerName = config.GetValue(SectionName, "player_name", settings.PlayerName).AsString().Trim();
        if (playerName.Length > 0)
            settings.PlayerName = playerName;
        return settings;
    }

    public Error Save()
    {
        var config = new ConfigFile();
        config.SetValue(SectionName, "matchmaking_url", MatchmakingUrl.Trim());
        config.SetValue(SectionName, "player_name", PlayerName.Trim());
        return config.Save(SettingsPath);
    }
}
