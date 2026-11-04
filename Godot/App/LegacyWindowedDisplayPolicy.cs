using System.Collections.Generic;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private static readonly Vector2I[] WindowedSizePresets =
    {
        new(640, 360),
        new(800, 450),
        new(960, 540),
        new(1024, 576),
        new(1280, 720),
        new(1366, 768),
        new(1600, 900),
        new(1920, 1080),
        new(2560, 1440)
    };

    private Vector2I _windowedSize = new(1280, 720);
    private bool _applyingDisplayChange;
    private Godot.Timer? _windowedSizeSaveTimer;

    private void InitializeWindowedDisplayTracking()
    {
        Window window = GetWindow();
        window.SizeChanged += OnMainWindowSizeChanged;
        _windowedSizeSaveTimer = new Godot.Timer
        {
            Name = "WindowedSizeSaveTimer",
            OneShot = true,
            WaitTime = 0.5
        };
        _windowedSizeSaveTimer.Timeout += SaveEditorPreferences;
        AddChild(_windowedSizeSaveTimer);
    }

    private void CleanupWindowedDisplayTracking()
    {
        Window window = GetWindow();
        window.SizeChanged -= OnMainWindowSizeChanged;
        if (_windowedSizeSaveTimer is null)
            return;
        if (!_windowedSizeSaveTimer.IsStopped())
        {
            _windowedSizeSaveTimer.Stop();
            SaveEditorPreferences();
        }
        _windowedSizeSaveTimer.Timeout -= SaveEditorPreferences;
        _windowedSizeSaveTimer = null;
    }

    private static Vector2I[] GetWindowedSizePresets(Window window)
    {
        Vector2I available = DisplayServer.ScreenGetUsableRect(window.CurrentScreen).Size;
        var presets = new List<Vector2I>(WindowedSizePresets.Length);
        foreach (Vector2I preset in WindowedSizePresets)
        {
            if (preset.X <= available.X && preset.Y <= available.Y)
                presets.Add(preset);
        }
        return presets.ToArray();
    }

    private void RestoreWindowedSize()
    {
        Window window = GetWindow();
        _windowedSize = ClampWindowedSize(_windowedSize, DisplayServer.ScreenGetUsableRect(window.CurrentScreen));
        bool wasApplyingDisplayChange = _applyingDisplayChange;
        _applyingDisplayChange = true;
        try
        {
            window.Mode = Window.ModeEnum.Windowed;
            window.Size = _windowedSize;
            CenterWindow(window, _windowedSize);
        }
        finally
        {
            _applyingDisplayChange = wasApplyingDisplayChange;
        }
    }

    private void SetWindowedSizePreference(Vector2I size)
    {
        if (_fullscreen)
            return;
        Window window = GetWindow();
        _windowedSize = ClampWindowedSize(size, DisplayServer.ScreenGetUsableRect(window.CurrentScreen));
        bool wasApplyingDisplayChange = _applyingDisplayChange;
        _applyingDisplayChange = true;
        try
        {
            window.Mode = Window.ModeEnum.Windowed;
            window.Size = _windowedSize;
            CenterWindow(window, _windowedSize);
        }
        finally
        {
            _applyingDisplayChange = wasApplyingDisplayChange;
        }
        RefreshControlsDisplayState();
        SaveEditorPreferences();
    }

    private void OnMainWindowSizeChanged()
    {
        Window window = GetWindow();
        if (!_applyingDisplayChange && !_fullscreen && !IsFullscreenMode(window.Mode))
        {
            _windowedSize = ClampWindowedSize(window.Size, DisplayServer.ScreenGetUsableRect(window.CurrentScreen));
            if (_windowedSize != window.Size)
            {
                bool wasApplyingDisplayChange = _applyingDisplayChange;
                _applyingDisplayChange = true;
                try
                {
                    window.Size = _windowedSize;
                }
                finally
                {
                    _applyingDisplayChange = wasApplyingDisplayChange;
                }
            }
            _windowedSizeSaveTimer?.Start();
        }
        RefreshControlsDisplayState();
    }

    private void RefreshControlsDisplayState()
    {
        if (_controlsWindow is null || !IsInstanceValid(_controlsWindow))
            return;
        Window window = GetWindow();
        _controlsWindow.RefreshDisplayState(_windowedSize, window.Size, _fullscreen);
    }

    private static Vector2I ClampWindowedSize(Vector2I size, Rect2I usableRect)
    {
        int maximumWidth = System.Math.Max(1, usableRect.Size.X);
        int maximumHeight = System.Math.Max(1, usableRect.Size.Y);
        int minimumWidth = System.Math.Min(640, maximumWidth);
        int minimumHeight = System.Math.Min(360, maximumHeight);
        return new Vector2I(
            System.Math.Clamp(size.X, minimumWidth, maximumWidth),
            System.Math.Clamp(size.Y, minimumHeight, maximumHeight));
    }

    private static void CenterWindow(Window window, Vector2I size)
    {
        Rect2I usableRect = DisplayServer.ScreenGetUsableRect(window.CurrentScreen);
        window.Position = usableRect.Position + new Vector2I(
            (usableRect.Size.X - size.X) / 2,
            (usableRect.Size.Y - size.Y) / 2);
    }

    private static int ReadDisplayDimension(ConfigFile settings, string key, int fallback)
    {
        double value = settings.GetValue("display", key, fallback).AsDouble();
        return double.IsFinite(value) && value >= 1d && value <= 16384d ? (int)value : fallback;
    }
}
