using NAudio.CoreAudioApi;

namespace FreeSoundPad.Audio;

public sealed class AudioDeviceInfo
{
    public AudioDeviceInfo(string id, string name, bool isDefault, bool isVirtual, string? hint = null)
    {
        Id = id;
        Name = name;
        IsDefault = isDefault;
        IsVirtual = isVirtual;
        Hint = hint;
    }

    public string Id { get; }
    public string Name { get; }
    public bool IsDefault { get; }
    public bool IsVirtual { get; }
    public string? Hint { get; }

    public string DisplayName => IsVirtual ? $"{Name}  ·  виртуальный" : Name;

    public override string ToString() => DisplayName;
}

public static class AudioDeviceService
{
    private static readonly string[] VirtualKeywords =
    {
        "cable input", "cable output", "vb-audio", "voicemeeter", "virtual audio cable",
        "line 1 (virtual", "steam streaming", "pico streaming", "pico streaming",
        "virtual", "loopback", "spacedesk", "blackhole", "vb-cable",
    };

    public static IReadOnlyList<AudioDeviceInfo> GetRenderDevices() => Enumerate(DataFlow.Render);

    public static IReadOnlyList<AudioDeviceInfo> GetCaptureDevices() => Enumerate(DataFlow.Capture);

    public static AudioDeviceInfo? GetDefaultRender() => Enumerate(DataFlow.Render).FirstOrDefault(d => d.IsDefault);

    public static AudioDeviceInfo? GetDefaultCapture() => Enumerate(DataFlow.Capture).FirstOrDefault(d => d.IsDefault);

    public static bool IsVirtualDevice(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var lowered = name.ToLowerInvariant();
        return VirtualKeywords.Any(lowered.Contains);
    }

    /// <summary>True for devices that actually loop audio back as a virtual microphone.</summary>
    public static bool IsLoopbackCable(string name) => Score(name) >= 30;

    /// <summary>Best guess for a loopback-style virtual cable that can be used as a virtual microphone.</summary>
    public static AudioDeviceInfo? SuggestVirtualOutput(IReadOnlyList<AudioDeviceInfo> renderDevices)
    {
        return renderDevices
            .Where(d => d.IsVirtual && Score(d.Name) >= 30)
            .OrderByDescending(d => Score(d.Name))
            .FirstOrDefault();
    }

    private static int Score(string name)
    {
        var lowered = name.ToLowerInvariant();
        var score = 0;
        if (lowered.Contains("cable input")) score += 100;
        if (lowered.Contains("vb-audio")) score += 50;
        if (lowered.Contains("voicemeeter")) score += 40;
        if (lowered.Contains("virtual audio cable")) score += 40;
        if (lowered.Contains("steam streaming speakers")) score += 20;
        if (lowered.Contains("pico streaming speaker")) score += 15;
        if (lowered.Contains("virtual")) score += 5;
        return score;
    }

    private static List<AudioDeviceInfo> Enumerate(DataFlow flow)
    {
        var result = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();

        string? defaultId = null;
        try
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            defaultId = defaultDevice.ID;
        }
        catch
        {
            // No default endpoint — keep going with an empty id.
        }

        try
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    var id = device.ID;
                    var name = device.FriendlyName;
                    result.Add(new AudioDeviceInfo(id, name, id == defaultId, IsVirtualDevice(name)));
                }
            }
        }
        catch
        {
            // Ignore enumeration failures (device service can be in a transient state).
        }

        return result
            .OrderByDescending(d => d.IsDefault)
            .ThenByDescending(d => d.IsVirtual)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
