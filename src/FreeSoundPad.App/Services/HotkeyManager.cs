using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FreeSoundPad.App.Services;

public static class HotkeyFormatter
{
    /// <summary>When true, plain Numpad digits are displayed as "Num * + Num N".</summary>
    public static bool PadPrefix { get; set; }

    public static string Format(uint modifiers, uint virtualKey, bool numpadPrefix = false, uint prefixVirtualKey = 0)
    {
        if (virtualKey == 0) return "";

        var parts = new List<string>();
        if ((modifiers & HotkeyManager.ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & HotkeyManager.ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & HotkeyManager.ModShift) != 0) parts.Add("Shift");
        if ((modifiers & HotkeyManager.ModWin) != 0) parts.Add("Win");
        parts.Add(KeyName(virtualKey));
        var text = string.Join(" + ", parts);

        if ((numpadPrefix || PadPrefix) && modifiers == 0 && virtualKey is >= 0x60 and <= 0x69)
        {
            return "Num " + PrefixSymbol(prefixVirtualKey) + " + " + text;
        }

        return text;
    }

    public static string PrefixSymbol(uint prefixVirtualKey) => prefixVirtualKey switch
    {
        HotkeyManager.VkDivide or HotkeyManager.VkOemQuestion => "/",
        _ => "*",
    };

    public static string KeyName(uint virtualKey)
    {
        var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            return "Num " + (key - Key.NumPad0);
        }

        if (key >= Key.D0 && key <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString();
        }

        return key switch
        {
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            Key.Space => "Space",
            Key.Return => "Enter",
            Key.Escape => "Esc",
            Key.Multiply => "*",
            Key.Divide => "/",
            Key.Add => "+",
            Key.Subtract => "-",
            Key.Decimal => ".",
            _ => key.ToString(),
        };
    }
}

public sealed class HotkeyManager : IDisposable
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;
    public const uint VkMultiply = 0x6A;
    public const uint VkDivide = 0x6F;
    public const uint VkOemQuestion = 0xBF;

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private readonly IntPtr _windowHandle;
    private readonly HwndSource? _source;
    private readonly Dictionary<int, Action> _actions = new();
    private readonly Dictionary<int, string> _descriptions = new();
    private readonly Dictionary<long, Action> _prefixActions = new();
    private readonly Dictionary<long, Action> _hookedActions = new();
    private readonly HashSet<uint> _heldPrefixKeys = new();
    private readonly HashSet<uint> _suppressedKeys = new();
    private readonly HashSet<uint> _hookedDownKeys = new();
    private readonly HashSet<uint> _hookedSuppressedKeys = new();
    private readonly LowLevelKeyboardProc _keyboardProc;
    private IntPtr _keyboardHook;
    private int _nextId = 1;

    public HotkeyManager(Window window)
    {
        _windowHandle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WndProc);

        // Track the Numpad * key so pads can require "* + Num N" instead of bare Num N.
        _keyboardProc = KeyboardHook;
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, GetModuleHandle(null), 0);
    }

    /// <summary>True while one of the Numpad prefix keys (* or /) is held.</summary>
    public bool PrefixHeld => _heldPrefixKeys.Count > 0;

    private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var message = wParam.ToInt32();
            var isDown = message is WmKeyDown or WmSysKeyDown;
            var isUp = message is WmKeyUp or WmSysKeyUp;

            if (IsPrefixKey(info.vkCode))
            {
                if (isDown) _heldPrefixKeys.Add(info.vkCode);
                else if (isUp) _heldPrefixKeys.Remove(info.vkCode);
                // Fall through: * and / may also be assigned as standalone hotkeys.
            }

            // Numpad digits are not registered as global hotkeys (that would steal them
            // from games). They trigger a pad only while * or / is held.
            if (info.vkCode is >= 0x60 and <= 0x69)
            {
                if (isUp && _suppressedKeys.Remove(info.vkCode))
                {
                    return (IntPtr)1;
                }

                if (isDown)
                {
                    foreach (var prefixKey in _heldPrefixKeys)
                    {
                        if (!_prefixActions.TryGetValue(PrefixActionKey(prefixKey, info.vkCode), out var prefixAction))
                            continue;

                        _suppressedKeys.Add(info.vkCode);
                        try
                        {
                            prefixAction();
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Prefix hotkey action failed", ex);
                        }

                        return (IntPtr)1;
                    }
                }
            }

            // Keys that RegisterHotKey refuses (PrintScreen, media, some layouts) are
            // matched directly in the same global hook. This is also the path for all
            // pad hotkeys, so any keyboard key can be assigned.
            if (isUp)
            {
                _hookedDownKeys.Remove(info.vkCode);
                if (_hookedSuppressedKeys.Remove(info.vkCode)) return (IntPtr)1;
            }
            else if (isDown)
            {
                var modifiers = NativeModifiers();
                if (_hookedActions.TryGetValue(HookActionKey(modifiers, info.vkCode), out var hookedAction))
                {
                    var firstPress = _hookedDownKeys.Add(info.vkCode);
                    _hookedSuppressedKeys.Add(info.vkCode);

                    if (firstPress)
                    {
                        try
                        {
                            hookedAction();
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Hooked hotkey action failed", ex);
                        }
                    }

                    return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static long PrefixActionKey(uint prefixKey, uint virtualKey) => ((long)prefixKey << 32) | virtualKey;

    private static long HookActionKey(uint modifiers, uint virtualKey) => ((long)modifiers << 32) | virtualKey;

    private static uint NativeModifiers()
    {
        uint modifiers = 0;
        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) modifiers |= ModControl;
        if ((GetAsyncKeyState(0x12) & 0x8000) != 0) modifiers |= ModAlt;
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) modifiers |= ModShift;
        if ((GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0)
            modifiers |= ModWin;
        return modifiers;
    }

    public static bool IsPrefixKey(uint virtualKey) => virtualKey is VkMultiply or VkDivide or VkOemQuestion;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public List<string> RegistrationFailures { get; } = new();

    public bool Register(uint modifiers, uint virtualKey, string description, Action action)
    {
        if (virtualKey == 0) return true;
        if (_source == null) return false;

        var id = _nextId++;
        var ok = RegisterHotKey(_windowHandle, id, modifiers | ModNoRepeat, virtualKey);
        if (!ok)
        {
            RegistrationFailures.Add(HotkeyFormatter.Format(modifiers, virtualKey));
            Log.Info($"Hotkey conflict: {HotkeyFormatter.Format(modifiers, virtualKey)} ({description})");
            return false;
        }

        _actions[id] = action;
        _descriptions[id] = description;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error("Hotkey action failed", ex);
            }

            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>Registers a Numpad digit that fires only while the given prefix key is held.</summary>
    public void RegisterPrefixAction(uint prefixVirtualKey, uint virtualKey, Action action)
    {
        _prefixActions[PrefixActionKey(prefixVirtualKey, virtualKey)] = action;
    }

    /// <summary>Registers a key through the global low-level hook (works for any key).</summary>
    public bool RegisterHooked(uint modifiers, uint virtualKey, string description, Action action)
    {
        if (virtualKey == 0) return true;

        var key = HookActionKey(modifiers, virtualKey);
        if (_hookedActions.ContainsKey(key))
        {
            Log.Info($"Hooked hotkey conflict: {HotkeyFormatter.Format(modifiers, virtualKey)} ({description})");
            return false;
        }

        _hookedActions[key] = action;
        return true;
    }

    public bool IsHooked(uint modifiers, uint virtualKey) =>
        _hookedActions.ContainsKey(HookActionKey(modifiers, virtualKey));

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys)
        {
            try
            {
                UnregisterHotKey(_windowHandle, id);
            }
            catch
            {
                // ignore
            }
        }

        _actions.Clear();
        _descriptions.Clear();
        _prefixActions.Clear();
        _hookedActions.Clear();
        _heldPrefixKeys.Clear();
        _suppressedKeys.Clear();
        _hookedDownKeys.Clear();
        _hookedSuppressedKeys.Clear();
        RegistrationFailures.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
