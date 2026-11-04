using Godot;

namespace GBNG.Audio;

public sealed partial class LegacyMusicController : Node
{
    private enum Stem
    {
        A,
        B,
        C,
        D,
        E,
        F
    }

    private static readonly string[][] TrackPathsByStem =
    {
        new[] { "res://Assets/Music/A1b.wav", "res://Assets/Music/A2.wav" },
        new[] { "res://Assets/Music/B1.wav", "res://Assets/Music/B2.wav", "res://Assets/Music/B3.wav", "res://Assets/Music/B4.wav" },
        new[] { "res://Assets/Music/C1.wav" },
        new[] { "res://Assets/Music/D1.wav" },
        new[] { "res://Assets/Music/E1.wav", "res://Assets/Music/E2.wav", "res://Assets/Music/E3.wav" },
        new[] { "res://Assets/Music/F1b.wav" }
    };

    private readonly RandomNumberGenerator _random = new();
    private int[][] _clipIndicesByStem = System.Array.Empty<int[]>();
    private AudioStreamInteractive _musicStream = null!;
    private AudioStreamPlayer _player = null!;
    private AudioStreamPlaybackInteractive? _playback;
    private Stem _currentStem;
    private int _currentClipIndex = -1;
    private Stem _pendingStem;
    private int _pendingClipIndex = -1;

    public LegacyMusicController()
    {
        Name = "MusicController";
    }

    public override void _Ready()
    {
        _random.Randomize();
        LoadTracksAndTransitions();
        _currentStem = _random.Randf() < 0.5f ? Stem.A : Stem.D;
        _currentClipIndex = GetRandomClipIndex(_currentStem, -1);
        _musicStream.InitialClip = _currentClipIndex;
        _player = new AudioStreamPlayer
        {
            Name = "MusicPlayer",
            Bus = LegacyAudioSettings.MusicBusName,
            Stream = _musicStream
        };
        AddChild(_player);
        GetTree().CreateTimer(1d).Timeout += StartInitialTrack;
    }

    public override void _Process(double delta)
    {
        if (_playback is null || _pendingClipIndex < 0
            || _playback.GetCurrentClipIndex() != _pendingClipIndex)
            return;

        _currentStem = _pendingStem;
        _currentClipIndex = _pendingClipIndex;
        _pendingClipIndex = -1;
        ScheduleNextClip();
    }

    private void LoadTracksAndTransitions()
    {
        int clipCount = 0;
        foreach (string[] tracks in TrackPathsByStem)
            clipCount += tracks.Length;

        _musicStream = new AudioStreamInteractive { ClipCount = clipCount };
        _clipIndicesByStem = new int[TrackPathsByStem.Length][];
        int nextClipIndex = 0;
        for (int stem = 0; stem < TrackPathsByStem.Length; stem++)
        {
            string[] paths = TrackPathsByStem[stem];
            var clipIndices = new int[paths.Length];
            for (int index = 0; index < paths.Length; index++)
            {
                AudioStream track = ResourceLoader.Load<AudioStream>(paths[index])
                    ?? throw new System.InvalidOperationException($"Could not load legacy music track '{paths[index]}'.");
                clipIndices[index] = nextClipIndex;
                _musicStream.SetClipStream(nextClipIndex, track);
                nextClipIndex++;
            }
            _clipIndicesByStem[stem] = clipIndices;
        }

        AddTransitions(Stem.A, Stem.A);
        AddTransitions(Stem.A, Stem.B);
        AddTransitions(Stem.B, Stem.A);
        AddTransitions(Stem.B, Stem.B);
        AddTransitions(Stem.B, Stem.C);
        AddTransitions(Stem.C, Stem.B);
        AddTransitions(Stem.C, Stem.D);
        AddTransitions(Stem.D, Stem.E);
        AddTransitions(Stem.E, Stem.E);
        AddTransitions(Stem.E, Stem.F);
        AddTransitions(Stem.F, Stem.A);
    }

    private void AddTransitions(Stem fromStem, Stem toStem)
    {
        int[] fromClips = _clipIndicesByStem[(int)fromStem];
        int[] toClips = _clipIndicesByStem[(int)toStem];
        foreach (int fromClip in fromClips)
        {
            foreach (int toClip in toClips)
            {
                if (fromClip == toClip)
                    continue;
                _musicStream.AddTransition(
                    fromClip,
                    toClip,
                    AudioStreamInteractive.TransitionFromTime.End,
                    AudioStreamInteractive.TransitionToTime.Start,
                    AudioStreamInteractive.FadeMode.Disabled,
                    0f);
            }
        }
    }

    private void StartInitialTrack()
    {
        if (!IsInsideTree())
            return;
        _player.Play();
        _playback = _player.GetStreamPlayback() as AudioStreamPlaybackInteractive
            ?? throw new System.InvalidOperationException("Godot did not create an interactive music playback.");
        ScheduleNextClip();
    }

    private void ScheduleNextClip()
    {
        if (_playback is null || _pendingClipIndex >= 0)
            return;

        _pendingStem = ChooseNextStem();
        int previousClip = _pendingStem == _currentStem ? _currentClipIndex : -1;
        _pendingClipIndex = GetRandomClipIndex(_pendingStem, previousClip);
        _playback.SwitchToClip(_pendingClipIndex);
    }

    private Stem ChooseNextStem()
    {
        switch (_currentStem)
        {
            case Stem.A:
                return _random.Randf() < 0.25f ? Stem.A : Stem.B;
            case Stem.B:
                float roll = _random.Randf();
                return roll < 0.25f ? Stem.A : roll < 0.75f ? Stem.B : Stem.C;
            case Stem.C:
                return _random.Randf() < 0.25f ? Stem.B : Stem.D;
            case Stem.D:
                return Stem.E;
            case Stem.E:
                return _random.Randf() < 0.75f ? Stem.E : Stem.F;
            case Stem.F:
                return Stem.A;
            default:
                throw new System.InvalidOperationException($"Unknown legacy music stem '{_currentStem}'.");
        }
    }

    private int GetRandomClipIndex(Stem stem, int previousClipIndex)
    {
        int[] clips = _clipIndicesByStem[(int)stem];
        int index = _random.RandiRange(0, clips.Length - 1);
        if (clips.Length > 1 && clips[index] == previousClipIndex)
            index = (index + 1) % clips.Length;
        return clips[index];
    }
}
