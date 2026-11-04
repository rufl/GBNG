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
    private AudioStream[][] _tracksByStem = System.Array.Empty<AudioStream[]>();
    private AudioStreamPlayer[] _players = System.Array.Empty<AudioStreamPlayer>();
    private Stem _lastStem;
    private AudioStream? _lastClip;
    private int _activePlayer;

    public LegacyMusicController()
    {
        Name = "MusicController";
    }

    public override void _Ready()
    {
        _random.Randomize();
        LoadTracks();
        CreatePlayers();

        _lastStem = _random.Randf() < 0.5f ? Stem.A : Stem.D;
        _lastClip = GetRandomClip(_lastStem, null);
        _players[_activePlayer].Stream = _lastClip;
        GetTree().CreateTimer(1d).Timeout += StartInitialTrack;
    }

    private void LoadTracks()
    {
        _tracksByStem = new AudioStream[TrackPathsByStem.Length][];
        for (int stem = 0; stem < TrackPathsByStem.Length; stem++)
        {
            string[] paths = TrackPathsByStem[stem];
            var tracks = new AudioStream[paths.Length];
            for (int index = 0; index < paths.Length; index++)
                tracks[index] = ResourceLoader.Load<AudioStream>(paths[index])
                    ?? throw new System.InvalidOperationException($"Could not load legacy music track '{paths[index]}'.");
            _tracksByStem[stem] = tracks;
        }
    }

    private void CreatePlayers()
    {
        _players = new AudioStreamPlayer[2];
        for (int index = 0; index < _players.Length; index++)
        {
            var player = new AudioStreamPlayer
            {
                Name = index == 0 ? "MusicPlayerA" : "MusicPlayerB",
                Bus = LegacyAudioSettings.MusicBusName
            };
            player.Finished += OnTrackFinished;
            AddChild(player);
            _players[index] = player;
        }
    }

    private void StartInitialTrack()
    {
        if (IsInsideTree())
            _players[_activePlayer].Play();
    }

    private void OnTrackFinished()
    {
        if (!IsInsideTree())
            return;

        Stem nextStem = ChooseNextStem();
        AudioStream? previousClip = nextStem == _lastStem ? _lastClip : null;
        AudioStream nextClip = GetRandomClip(nextStem, previousClip);
        _activePlayer = 1 - _activePlayer;
        _players[_activePlayer].Stream = nextClip;
        _players[_activePlayer].Play();
        _lastStem = nextStem;
        _lastClip = nextClip;
    }

    private Stem ChooseNextStem()
    {
        switch (_lastStem)
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
                throw new System.InvalidOperationException($"Unknown legacy music stem '{_lastStem}'.");
        }
    }

    private AudioStream GetRandomClip(Stem stem, AudioStream? previousClip)
    {
        AudioStream[] clips = _tracksByStem[(int)stem];
        int index = _random.RandiRange(0, clips.Length - 1);
        if (clips.Length > 1 && ReferenceEquals(clips[index], previousClip))
            index = (index + 1) % clips.Length;
        return clips[index];
    }
}
