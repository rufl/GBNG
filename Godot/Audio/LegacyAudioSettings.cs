using Godot;

namespace GBNG.Audio;

internal static class LegacyAudioSettings
{
    internal const string SfxBusName = "SFX";
    internal const float DefaultSfxVolume = 0.5f;
    private const float MutedVolumeDb = -80f;

    internal static float ClampSfxVolume(float volume)
        => float.IsFinite(volume) ? Mathf.Clamp(volume, 0f, 1f) : DefaultSfxVolume;

    internal static float ToDecibels(float linearVolume)
    {
        float volume = ClampSfxVolume(linearVolume);
        return volume == 0f ? MutedVolumeDb : Mathf.LinearToDb(volume);
    }

    internal static void ApplySfxVolume(float linearVolume)
    {
        int busIndex = AudioServer.GetBusIndex(SfxBusName);
        if (busIndex < 0)
        {
            busIndex = AudioServer.BusCount;
            AudioServer.AddBus(busIndex);
            AudioServer.SetBusName(busIndex, SfxBusName);
        }

        AudioServer.SetBusSend(busIndex, "Master");
        AudioServer.SetBusVolumeDb(busIndex, ToDecibels(linearVolume));
    }
}
