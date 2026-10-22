namespace FreeSoundPad.App.Models;

/// <summary>
/// Normalises physical key codes into one representation that can be compared,
/// displayed and matched by the global keyboard hook.
/// </summary>
public static class HotkeyKeyUtil
{
    public const uint VkShift = 0x10;
    public const uint VkControl = 0x11;
    public const uint VkAlt = 0x12;
    public const uint VkLeftWin = 0x5B;
    public const uint VkRightWin = 0x5C;

    /// <summary>Left/right modifier variants become their generic virtual-key code.</summary>
    public static uint Normalize(uint virtualKey) => virtualKey switch
    {
        0xA0 or 0xA1 => VkShift,
        0xA2 or 0xA3 => VkControl,
        0xA4 or 0xA5 => VkAlt,
        VkRightWin => VkLeftWin,
        _ => virtualKey,
    };

    public static bool IsModifier(uint virtualKey)
    {
        var key = Normalize(virtualKey);
        return key is VkShift or VkControl or VkAlt or VkLeftWin;
    }

    public static List<uint> NormalizeList(IEnumerable<uint>? keys)
    {
        if (keys == null) return new List<uint>();

        var result = new List<uint>();
        foreach (var key in keys)
        {
            var normalized = Normalize(key);
            if (normalized == 0 || result.Contains(normalized)) continue;
            result.Add(normalized);
        }

        return result;
    }

    /// <summary>
    /// Builds a key list from the old modifiers + virtual key representation,
    /// including the legacy Numpad prefix.
    /// </summary>
    public static List<uint> FromLegacy(
        uint modifiers,
        uint virtualKey,
        bool numpadPrefix = false,
        uint prefixVirtualKey = 0)
    {
        var keys = new List<uint>();
        if (numpadPrefix && prefixVirtualKey != 0) keys.Add(Normalize(prefixVirtualKey));
        if ((modifiers & Services.HotkeyManager.ModControl) != 0) keys.Add(VkControl);
        if ((modifiers & Services.HotkeyManager.ModAlt) != 0) keys.Add(VkAlt);
        if ((modifiers & Services.HotkeyManager.ModShift) != 0) keys.Add(VkShift);
        if ((modifiers & Services.HotkeyManager.ModWin) != 0) keys.Add(VkLeftWin);
        if (virtualKey != 0) keys.Add(Normalize(virtualKey));
        return NormalizeList(keys);
    }

    /// <summary>Order-independent identity used for conflict checks.</summary>
    public static string Signature(IEnumerable<uint>? keys)
    {
        var normalized = NormalizeList(keys);
        normalized.Sort();
        return string.Join(",", normalized);
    }

    /// <summary>
    /// Exact duplicate or a chord that contains another chord (A vs A+B).
    /// Sharing only modifiers does not count as an overlap.
    /// </summary>
    public static bool IsDuplicateOrPrefix(IReadOnlyList<uint> left, IReadOnlyList<uint> right)
    {
        var a = NormalizeList(left);
        var b = NormalizeList(right);
        if (a.Count == 0 || b.Count == 0) return false;

        if (a.Count == b.Count) return Signature(a) == Signature(b);
        var shorter = a.Count < b.Count ? a : b;
        var longer = a.Count < b.Count ? b : a;
        return shorter.All(longer.Contains);
    }
}
