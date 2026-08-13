using System.Text.Json.Serialization;

namespace HotkeyAI.Core.Dsl;

/// <summary>What to do when an action fails or its postcondition is not met.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OnErrorBehaviour>))]
public enum OnErrorBehaviour
{
    [JsonStringEnumMemberName("abort")] Abort,
    [JsonStringEnumMemberName("continue")] Continue,
}

/// <summary>Declared type of an automation variable.</summary>
/// <remarks>CA1720 fires on <c>Integer</c>, but these members name DSL types, which is
/// exactly what the rule exists to prevent for ordinary identifiers and precisely what is
/// wanted here. The wire names come from the schema and cannot be renamed anyway.</remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1720:Identifier contains type name",
    Justification = "Enum members name DSL variable types; the names mirror the schema.")]
[JsonConverter(typeof(JsonStringEnumConverter<VariableType>))]
public enum VariableType
{
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("path")] Path,
    [JsonStringEnumMemberName("pathList")] PathList,
    [JsonStringEnumMemberName("textList")] TextList,
    [JsonStringEnumMemberName("boolean")] Boolean,
    [JsonStringEnumMemberName("integer")] Integer,
}

/// <summary>Named window layout position. Named rather than pixel coordinates so a plan
/// survives different screen sizes and DPI settings.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WindowPosition>))]
public enum WindowPosition
{
    [JsonStringEnumMemberName("left_half")] LeftHalf,
    [JsonStringEnumMemberName("right_half")] RightHalf,
    [JsonStringEnumMemberName("top_half")] TopHalf,
    [JsonStringEnumMemberName("bottom_half")] BottomHalf,
    [JsonStringEnumMemberName("maximized")] Maximized,
    [JsonStringEnumMemberName("centered")] Centered,
    [JsonStringEnumMemberName("top_left_quarter")] TopLeftQuarter,
    [JsonStringEnumMemberName("top_right_quarter")] TopRightQuarter,
    [JsonStringEnumMemberName("bottom_left_quarter")] BottomLeftQuarter,
    [JsonStringEnumMemberName("bottom_right_quarter")] BottomRightQuarter,
}

/// <summary>System-wide multimedia or browser command, broadcast via WM_APPCOMMAND.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppCommand>))]
public enum AppCommand
{
    [JsonStringEnumMemberName("media_play_pause")] MediaPlayPause,
    [JsonStringEnumMemberName("media_next_track")] MediaNextTrack,
    [JsonStringEnumMemberName("media_previous_track")] MediaPreviousTrack,
    [JsonStringEnumMemberName("media_stop")] MediaStop,
    [JsonStringEnumMemberName("volume_up")] VolumeUp,
    [JsonStringEnumMemberName("volume_down")] VolumeDown,
    [JsonStringEnumMemberName("volume_mute")] VolumeMute,
    [JsonStringEnumMemberName("browser_back")] BrowserBack,
    [JsonStringEnumMemberName("browser_forward")] BrowserForward,
    [JsonStringEnumMemberName("browser_refresh")] BrowserRefresh,
}

/// <summary>Visual severity of a toast notification.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NotifyLevel>))]
public enum NotifyLevel
{
    [JsonStringEnumMemberName("info")] Info,
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("error")] Error,
}

/// <summary>A single key. Modifiers are Ctrl, Alt, Shift, Win; everything else is a
/// non-modifier key, of which a chord may contain exactly one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KeyName>))]
public enum KeyName
{
    [JsonStringEnumMemberName("CTRL")] Ctrl,
    [JsonStringEnumMemberName("ALT")] Alt,
    [JsonStringEnumMemberName("SHIFT")] Shift,
    [JsonStringEnumMemberName("WIN")] Win,

    [JsonStringEnumMemberName("A")] A, [JsonStringEnumMemberName("B")] B,
    [JsonStringEnumMemberName("C")] C, [JsonStringEnumMemberName("D")] D,
    [JsonStringEnumMemberName("E")] E, [JsonStringEnumMemberName("F")] F,
    [JsonStringEnumMemberName("G")] G, [JsonStringEnumMemberName("H")] H,
    [JsonStringEnumMemberName("I")] I, [JsonStringEnumMemberName("J")] J,
    [JsonStringEnumMemberName("K")] K, [JsonStringEnumMemberName("L")] L,
    [JsonStringEnumMemberName("M")] M, [JsonStringEnumMemberName("N")] N,
    [JsonStringEnumMemberName("O")] O, [JsonStringEnumMemberName("P")] P,
    [JsonStringEnumMemberName("Q")] Q, [JsonStringEnumMemberName("R")] R,
    [JsonStringEnumMemberName("S")] S, [JsonStringEnumMemberName("T")] T,
    [JsonStringEnumMemberName("U")] U, [JsonStringEnumMemberName("V")] V,
    [JsonStringEnumMemberName("W")] W, [JsonStringEnumMemberName("X")] X,
    [JsonStringEnumMemberName("Y")] Y, [JsonStringEnumMemberName("Z")] Z,

    [JsonStringEnumMemberName("D0")] D0, [JsonStringEnumMemberName("D1")] D1,
    [JsonStringEnumMemberName("D2")] D2, [JsonStringEnumMemberName("D3")] D3,
    [JsonStringEnumMemberName("D4")] D4, [JsonStringEnumMemberName("D5")] D5,
    [JsonStringEnumMemberName("D6")] D6, [JsonStringEnumMemberName("D7")] D7,
    [JsonStringEnumMemberName("D8")] D8, [JsonStringEnumMemberName("D9")] D9,

    [JsonStringEnumMemberName("F1")] F1, [JsonStringEnumMemberName("F2")] F2,
    [JsonStringEnumMemberName("F3")] F3, [JsonStringEnumMemberName("F4")] F4,
    [JsonStringEnumMemberName("F5")] F5, [JsonStringEnumMemberName("F6")] F6,
    [JsonStringEnumMemberName("F7")] F7, [JsonStringEnumMemberName("F8")] F8,
    [JsonStringEnumMemberName("F9")] F9, [JsonStringEnumMemberName("F10")] F10,
    [JsonStringEnumMemberName("F11")] F11, [JsonStringEnumMemberName("F12")] F12,

    [JsonStringEnumMemberName("SPACE")] Space,
    [JsonStringEnumMemberName("ENTER")] Enter,
    [JsonStringEnumMemberName("TAB")] Tab,
    [JsonStringEnumMemberName("ESC")] Esc,
    [JsonStringEnumMemberName("BACKSPACE")] Backspace,
    [JsonStringEnumMemberName("DELETE")] Delete,
    [JsonStringEnumMemberName("INSERT")] Insert,
    [JsonStringEnumMemberName("HOME")] Home,
    [JsonStringEnumMemberName("END")] End,
    [JsonStringEnumMemberName("PAGEUP")] PageUp,
    [JsonStringEnumMemberName("PAGEDOWN")] PageDown,
    [JsonStringEnumMemberName("LEFT")] Left,
    [JsonStringEnumMemberName("RIGHT")] Right,
    [JsonStringEnumMemberName("UP")] Up,
    [JsonStringEnumMemberName("DOWN")] Down,

    [JsonStringEnumMemberName("OEM_COMMA")] OemComma,
    [JsonStringEnumMemberName("OEM_PERIOD")] OemPeriod,
    [JsonStringEnumMemberName("OEM_MINUS")] OemMinus,
    [JsonStringEnumMemberName("OEM_PLUS")] OemPlus,
    [JsonStringEnumMemberName("OEM_1")] Oem1,
    [JsonStringEnumMemberName("OEM_2")] Oem2,
    [JsonStringEnumMemberName("OEM_3")] Oem3,
    [JsonStringEnumMemberName("OEM_4")] Oem4,
    [JsonStringEnumMemberName("OEM_5")] Oem5,
    [JsonStringEnumMemberName("OEM_6")] Oem6,
    [JsonStringEnumMemberName("OEM_7")] Oem7,
}

/// <summary>The four modifier keys, for chord validation.</summary>
public static class Keys
{
    public static readonly IReadOnlySet<KeyName> Modifiers =
        new HashSet<KeyName> { KeyName.Ctrl, KeyName.Alt, KeyName.Shift, KeyName.Win };

    public static bool IsModifier(KeyName key) => Modifiers.Contains(key);
}
