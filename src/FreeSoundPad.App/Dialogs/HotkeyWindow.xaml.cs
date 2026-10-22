using System.Windows;
using System.Windows.Input;
using FreeSoundPad.App.Models;
using FreeSoundPad.App.Services;

namespace FreeSoundPad.App.Dialogs;

public partial class HotkeyWindow : Window
{
    private readonly Func<IReadOnlyList<uint>, string?>? _validate;
    private readonly List<uint> _pressedKeys = new();
    private readonly List<uint> _gestureKeys = new();
    private bool _captureSpecial;
    private bool _awaitingConfirm;
    private bool _closing;
    private List<uint>? _pendingKeys;

    public HotkeyWindow(HotkeyData? hotkey = null, Func<IReadOnlyList<uint>, string?>? validate = null)
    {
        InitializeComponent();
        _validate = validate;

        var keys = hotkey?.GetEffectiveKeys() ?? new List<uint>();
        PreviewText.Text = keys.Count == 0 ? "не назначена" : HotkeyFormatter.FormatKeys(keys);
        Loaded += (_, _) => Focus();
    }

    public HotkeyData? Result { get; private set; }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (_closing) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var virtualKey = HotkeyKeyUtil.Normalize((uint)KeyInterop.VirtualKeyFromKey(key));

        if (_captureSpecial)
        {
            if (virtualKey == 0) return;
            var special = new HotkeyData { Keys = new List<uint> { virtualKey } };
            TryAccept(special, isSpecial: true);
            return;
        }

        if (key == Key.Escape)
        {
            Cancel();
            return;
        }

        if (key is Key.Back or Key.Delete)
        {
            Result = null;
            DialogResult = true;
            Close();
            return;
        }

        if (key == Key.Enter && _awaitingConfirm && _pendingKeys != null)
        {
            var pending = new HotkeyData { Keys = _pendingKeys.ToList() };
            if (TryAccept(pending, isSpecial: false))
            {
                _pendingKeys = null;
                _awaitingConfirm = false;
            }
            return;
        }

        if (virtualKey == 0) return;

        // Starting a new gesture after the bare-key confirmation prompt replaces it.
        if (_awaitingConfirm)
        {
            _awaitingConfirm = false;
            _pendingKeys = null;
        }

        if (_pressedKeys.Count == 0)
        {
            _gestureKeys.Clear();
        }

        if (e.IsRepeat) return;
        if (!_pressedKeys.Contains(virtualKey)) _pressedKeys.Add(virtualKey);
        if (!_gestureKeys.Contains(virtualKey)) _gestureKeys.Add(virtualKey);

        WarningText.Visibility = Visibility.Collapsed;
        PreviewText.Text = HotkeyFormatter.FormatKeys(_gestureKeys);
        HintText.Text = "отпусти клавиши, чтобы назначить";
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (_closing || _captureSpecial) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var virtualKey = HotkeyKeyUtil.Normalize((uint)KeyInterop.VirtualKeyFromKey(key));
        if (virtualKey == 0) return;

        _pressedKeys.Remove(virtualKey);
        if (_pressedKeys.Count > 0) return;
        if (_gestureKeys.Count == 0) return;

        var candidate = HotkeyKeyUtil.NormalizeList(_gestureKeys);
        _gestureKeys.Clear();

        if (candidate.Count == 0) return;
        if (candidate.All(HotkeyKeyUtil.IsModifier))
        {
            ShowWarning("Добавь к модификатору ещё одну клавишу — одна Ctrl/Alt/Shift/Win не считается комбинацией.");
            PreviewText.Text = "не назначена";
            HintText.Text = "нажмите комбинацию";
            return;
        }

        var text = HotkeyFormatter.FormatKeys(candidate);
        if (candidate.Count == 1 && !IsSafeBareKey((Key)KeyInterop.KeyFromVirtualKey((int)candidate[0])))
        {
            _pendingKeys = candidate;
            _awaitingConfirm = true;
            PreviewText.Text = text;
            HintText.Text = "Enter — подтвердить";
            ShowWarning("Без модификатора эта клавиша будет перехватываться во всех приложениях. Enter — подтвердить, Esc — отмена.");
            return;
        }

        TryAccept(new HotkeyData { Keys = candidate }, isSpecial: false);
    }

    private bool TryAccept(HotkeyData result, bool isSpecial)
    {
        var keys = result.GetEffectiveKeys();
        if (keys.Count == 0) return false;

        if (keys.All(HotkeyKeyUtil.IsModifier))
        {
            ShowWarning("Одни модификаторы не считаются комбинацией. Добавь обычную клавишу.");
            return false;
        }

        var conflict = _validate?.Invoke(keys);
        if (!string.IsNullOrWhiteSpace(conflict))
        {
            ShowWarning(conflict);
            _awaitingConfirm = false;
            _pendingKeys = null;
            if (!isSpecial)
            {
                PreviewText.Text = HotkeyFormatter.FormatKeys(keys);
                HintText.Text = "попробуй другую комбинацию";
            }
            return false;
        }

        Result = result;
        _closing = true;
        DialogResult = true;
        Close();
        return true;
    }

    private void ShowWarning(string message)
    {
        WarningText.Text = message;
        WarningText.Visibility = Visibility.Visible;
    }

    private static bool IsSafeBareKey(Key key)
    {
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return true;
        if (key is >= Key.F1 and <= Key.F24) return true;
        return false;
    }

    private void Cancel()
    {
        _closing = true;
        DialogResult = false;
        Close();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Result = null;
        _closing = true;
        DialogResult = true;
        Close();
    }

    private void OnCaptureSpecial(object sender, RoutedEventArgs e)
    {
        _captureSpecial = true;
        _awaitingConfirm = false;
        _pendingKeys = null;
        _pressedKeys.Clear();
        _gestureKeys.Clear();
        PreviewText.Text = "…";
        HintText.Text = "нажми служебную клавишу";
        ShowWarning("Режим служебных клавиш: нажми Esc, Backspace, Delete, Enter или любую другую — она назначится. Для отмены нажми «Отмена».");
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Cancel();

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
