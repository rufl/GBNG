using Godot;

namespace GBNG.Behaviors;

internal static class LegacyInputKeys
{
    internal static readonly string[] ScriptableNames =
    [
        "1", "2", "3", "4", "5", "6", "7", "8", "9", "0",
        "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
        "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
        "space", "enter", "return", "backspace", "up", "down", "left", "right",
        "[1]", "[2]", "[3]", "[4]", "[5]", "[6]", "[7]", "[8]", "[9]", "[0]",
        "mouse 0", "@jump", "@pri", "@sec"
    ];

    internal static bool IsHeld(string keyName, bool mouseIsDown, bool secondaryActionDown)
    {
        if (keyName == "mouse 0")
            return mouseIsDown;
        if (keyName == "@jump")
            return Input.IsKeyPressed(Key.Space);
        if (keyName == "@pri")
            return mouseIsDown;
        if (keyName == "@sec")
            return secondaryActionDown;
        if (keyName.Length == 1 && char.IsAsciiLetter(keyName[0]))
            return Input.IsKeyPressed((Key)char.ToUpperInvariant(keyName[0]));
        if (keyName.Length == 1 && char.IsAsciiDigit(keyName[0]))
            return Input.IsKeyPressed((Key)((int)Key.Key0 + keyName[0] - '0'));
        if (keyName.Length == 3 && keyName[0] == '[' && keyName[2] == ']')
            return Input.IsKeyPressed((Key)((int)Key.Kp0 + keyName[1] - '0'));
        return keyName switch
        {
            "space" => Input.IsKeyPressed(Key.Space),
            "enter" or "return" => Input.IsKeyPressed(Key.Enter),
            "backspace" => Input.IsKeyPressed(Key.Backspace),
            "up" => Input.IsKeyPressed(Key.Up),
            "down" => Input.IsKeyPressed(Key.Down),
            "left" => Input.IsKeyPressed(Key.Left),
            "right" => Input.IsKeyPressed(Key.Right),
            _ => false
        };
    }
}
