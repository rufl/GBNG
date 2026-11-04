using Godot;

namespace GBNG.Audio;

internal static class LegacyAudioSettings
{
    internal const string SfxBusName = "SFX";
    internal const string MusicBusName = "Music";
    internal const float DefaultSfxVolume = 0.5f;
    internal const float DefaultMusicVolume = 1f;
    private const float MutedVolumeDb = -80f;

    internal static float ClampSfxVolume(float volume)
        => ClampVolume(volume, DefaultSfxVolume);

    internal static float ClampMusicVolume(float volume)
        => ClampVolume(volume, DefaultMusicVolume);

    internal static float ToDecibels(float linearVolume)
    {
        float volume = float.IsFinite(linearVolume) ? Mathf.Clamp(linearVolume, 0f, 1f) : 0f;
        return volume == 0f ? MutedVolumeDb : Mathf.LinearToDb(volume);
    }

    internal static void ApplySfxVolume(float linearVolume)
        => ApplyVolume(SfxBusName, linearVolume);

    internal static void ApplyMusicVolume(float linearVolume)
        => ApplyVolume(MusicBusName, linearVolume);

    private static float ClampVolume(float volume, float fallback)
        => float.IsFinite(volume) ? Mathf.Clamp(volume, 0f, 1f) : fallback;

    private static void ApplyVolume(string busName, float linearVolume)
    {
        int busIndex = AudioServer.GetBusIndex(busName);
        if (busIndex < 0)
        {
            busIndex = AudioServer.BusCount;
            AudioServer.AddBus(busIndex);
            AudioServer.SetBusName(busIndex, busName);
        }

        AudioServer.SetBusSend(busIndex, "Master");
        AudioServer.SetBusVolumeDb(busIndex, ToDecibels(linearVolume));
    }
}
