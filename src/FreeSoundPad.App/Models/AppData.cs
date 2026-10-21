namespace FreeSoundPad.App.Models;

public sealed class AppData
{
    public List<PadData> Pads { get; set; } = new();
    public List<BookmarkData> Bookmarks { get; set; } = new();
    public SettingsData Settings { get; set; } = new();
}

public sealed class PadData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string File { get; set; } = "";
    public string Category { get; set; } = "Разное";
    public string Color { get; set; } = "FF6B6B";
    public uint HotkeyModifiers { get; set; }
    public uint HotkeyVirtualKey { get; set; }
    /// <summary>Numpad digit hotkey fires only while Num * is held.</summary>
    public bool HotkeyPrefix { get; set; }
    /// <summary>Prefix key: 0x6A for Num *, 0x6F/0xBF for /.</summary>
    public uint HotkeyPrefixVirtualKey { get; set; }
    public float Volume { get; set; } = 1f;
    public bool Loop { get; set; }
    public bool Favorite { get; set; }
    public int Order { get; set; }
    public float DurationSeconds { get; set; }
    public float[] Peaks { get; set; } = Array.Empty<float>();
}

public sealed class BookmarkData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Color { get; set; } = "4CC9F0";
}

public sealed class HotkeyData
{
    public uint Modifiers { get; set; }
    public uint VirtualKey { get; set; }
    /// <summary>Numpad digit requires the Num * prefix.</summary>
    public bool NumpadPrefix { get; set; }
    /// <summary>0x6A for Num *, 0x6F/0xBF for /.</summary>
    public uint NumpadPrefixVirtualKey { get; set; }

    public bool IsSet => VirtualKey != 0;

    public static HotkeyData Empty => new();

    public HotkeyData Clone() => new()
    {
        Modifiers = Modifiers,
        VirtualKey = VirtualKey,
        NumpadPrefix = NumpadPrefix,
        NumpadPrefixVirtualKey = NumpadPrefixVirtualKey,
    };
}

public sealed class SettingsData
{
    public string? OutputDeviceId { get; set; }
    public string? MicDeviceId { get; set; }
    public string? MonitorDeviceId { get; set; }
    public bool MonitorEnabled { get; set; }
    public bool MicPassthrough { get; set; } = true;
    public bool BrowserSend { get; set; } = true;
    public bool BrowserMonitor { get; set; }
    public bool BrowserCaptureEnabled { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool AutoStart { get; set; }

    public float MasterGain { get; set; } = 0.9f;
    public float MicGain { get; set; } = 0.8f;
    public float PadGain { get; set; } = 0.9f;
    public float BrowserGain { get; set; } = 0.8f;
    public float MonitorGain { get; set; } = 0.8f;

    public double WindowLeft { get; set; } = -1;
    public double WindowTop { get; set; } = -1;
    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 900;
    public bool WindowMaximized { get; set; }

    public double OverlayLeft { get; set; } = -1;
    public double OverlayTop { get; set; } = -1;
    public bool OverlayCompact { get; set; }
    public bool BrowserMuted { get; set; }
    /// <summary>Right-hand mixer / capture side panel is visible.</summary>
    public bool MixerVisible { get; set; } = true;
    /// <summary>Plain Numpad pad hotkeys require holding Numpad *.</summary>
    public bool PadHotkeyPrefix { get; set; }

    public string HomeUrl { get; set; } = "https://music.youtube.com";
    public string? LastUrl { get; set; }

    /// <summary>Phone/tablet stream-deck remote on the local network.</summary>
    public bool WebServerEnabled { get; set; } = true;
    public int WebServerPort { get; set; } = 8765;
    public string? WebServerToken { get; set; }

    public HotkeyData StopAllHotkey { get; set; } = new() { Modifiers = 0x3, VirtualKey = 0x58 };      // Ctrl+Alt+X
    public HotkeyData MicToggleHotkey { get; set; } = new() { Modifiers = 0x3, VirtualKey = 0x4D };     // Ctrl+Alt+M
    public HotkeyData BrowserSendHotkey { get; set; } = new() { Modifiers = 0x3, VirtualKey = 0x42 };   // Ctrl+Alt+B
    public HotkeyData MonitorHotkey { get; set; } = new() { Modifiers = 0x3, VirtualKey = 0x48 };      // Ctrl+Alt+H
    public HotkeyData OverlayHotkey { get; set; } = new() { Modifiers = 0x3, VirtualKey = 0x4F };      // Ctrl+Alt+O
    public HotkeyData ShowWindowHotkey { get; set; } = new() { Modifiers = 0x3, VirtualKey = 0x46 };   // Ctrl+Alt+F
}
