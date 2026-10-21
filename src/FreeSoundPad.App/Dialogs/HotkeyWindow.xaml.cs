using System.Windows;
using System.Windows.Input;
using FreeSoundPad.App.Models;
using FreeSoundPad.App.Services;

namespace FreeSoundPad.App.Dialogs;

public partial class HotkeyWindow : Window
{
    private bool _captureSpecial;
    private bool _awaitingConfirm;
    private bool _prefixArmed;
    private uint _prefixVirtualKey;
    private uint _pendingModifiers;
    private uint _pendingVirtualKey;

    public HotkeyWindow(uint modifiers, uint virtualKey, bool numpadPrefix = false, uint prefixVirtualKey = 0)
    {
        InitializeComponent();
        _prefixVirtualKey = prefixVirtualKey;
        PreviewText.Text = virtualKey == 0
            ? "не назначена"
            : HotkeyFormatter.Format(modifiers, virtualKey, numpadPrefix, prefixVirtualKey);
        Loaded += (_, _) => Focus();
    }

    public HotkeyData? Result { get; private set; }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (_captureSpecial)
        {
            var specialVirtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (specialVirtualKey == 0) return;

            Result = new HotkeyData
            {
                Modifiers = ToNativeModifiers(Keyboard.Modifiers),
                VirtualKey = specialVirtualKey,
            };
            DialogResult = true;
            Close();
            return;
        }

        if (key == Key.Escape)
        {
            DialogResult = false;
            Close();
            return;
        }

        if (key is Key.Back or Key.Delete)
        {
            Result = null;
            DialogResult = true;
            Close();
            return;
        }

        if (key == Key.Enter && _awaitingConfirm)
        {
            Result = new HotkeyData { Modifiers = _pendingModifiers, VirtualKey = _pendingVirtualKey };
            DialogResult = true;
            Close();
            return;
        }

        if (IsModifierKey(key))
        {
            var mods = ToNativeModifiers(Keyboard.Modifiers);
            _awaitingConfirm = false;
            _prefixArmed = false;
            WarningText.Visibility = Visibility.Collapsed;
            PreviewText.Text = mods == 0 ? "…" : HotkeyFormatter.Format(mods, 0) is { Length: > 0 } text ? text : "…";
            HintText.Text = mods == 0 ? "нажмите комбинацию" : "теперь нажмите клавишу";
            return;
        }

        var modifiers = ToNativeModifiers(Keyboard.Modifiers);
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return;

        if ((modifiers & ~HotkeyManager.ModShift) == 0 && HotkeyManager.IsPrefixKey(virtualKey))
        {
            if (_prefixArmed && _prefixVirtualKey == virtualKey)
            {
                // Second press of the same prefix key assigns it as a standalone hotkey.
                Result = new HotkeyData { Modifiers = 0, VirtualKey = virtualKey };
                DialogResult = true;
                Close();
                return;
            }

            _awaitingConfirm = false;
            _prefixArmed = true;
            _prefixVirtualKey = virtualKey;
            var prefix = HotkeyFormatter.PrefixSymbol(virtualKey);
            PreviewText.Text = "Num " + prefix + " + …";
            HintText.Text = "теперь нажми Num 0–9; ещё раз " + prefix + " — назначить сам " + prefix;
            WarningText.Text = $"Зажми Num {prefix} и нажми цифру нумпада — пад сработает только от {prefix} + Num.";
            WarningText.Visibility = Visibility.Visible;
            return;
        }

        if (_prefixArmed)
        {
            if (virtualKey is >= 0x60 and <= 0x69)
            {
                Result = new HotkeyData
                {
                    Modifiers = 0,
                    VirtualKey = virtualKey,
                    NumpadPrefix = true,
                    NumpadPrefixVirtualKey = _prefixVirtualKey,
                };
                DialogResult = true;
                Close();
                return;
            }

            WarningText.Text = $"После Num {HotkeyFormatter.PrefixSymbol(_prefixVirtualKey)} нажми цифру нумпада 0–9.";
            WarningText.Visibility = Visibility.Visible;
            return;
        }

        // A bare letter or symbol becomes a global hotkey and shadows normal typing.
        // Ask for Enter once instead of silently stealing the key everywhere.
        if (modifiers == 0 && !IsSafeBareKey(key))
        {
            _awaitingConfirm = true;
            _pendingModifiers = 0;
            _pendingVirtualKey = virtualKey;
            PreviewText.Text = HotkeyFormatter.Format(0, virtualKey);
            HintText.Text = "Enter — подтвердить";
            WarningText.Text = "Без модификатора клавиша будет перехватываться во всех приложениях. Enter — подтвердить, Esc — отмена.";
            WarningText.Visibility = Visibility.Visible;
            return;
        }

        Result = new HotkeyData { Modifiers = modifiers, VirtualKey = virtualKey };
        DialogResult = true;
        Close();
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static bool IsSafeBareKey(Key key)
    {
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return true;
        if (key is >= Key.F1 and <= Key.F24) return true;
        return false;
    }

    private static uint ToNativeModifiers(ModifierKeys modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= HotkeyManager.ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= HotkeyManager.ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= HotkeyManager.ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= HotkeyManager.ModWin;
        return result;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = true;
        Close();
    }

    private void OnCaptureSpecial(object sender, RoutedEventArgs e)
    {
        _captureSpecial = true;
        _awaitingConfirm = false;
        _prefixArmed = false;
        PreviewText.Text = "…";
        HintText.Text = "нажми служебную клавишу";
        WarningText.Text = "Режим служебных клавиш: нажми Esc, Backspace, Delete, Enter или любую другую — она назначится. Для отмены нажми «Отмена».";
        WarningText.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
