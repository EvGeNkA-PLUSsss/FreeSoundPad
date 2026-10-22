using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSoundPad.App.Models;
using FreeSoundPad.App.Services;

namespace FreeSoundPad.App.ViewModels;

public sealed partial class PadItem : ObservableObject
{
    public static readonly string[] Palette =
    {
        "FF6B6B", "FFB454", "A3E635", "4ADE80", "2DD4BF", "38BDF8",
        "818CF8", "C084FC", "F472B6", "FB7185", "FACC15", "60A5FA",
    };

    private readonly MainViewModel _owner;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _category = "Разное";
    [ObservableProperty] private string _colorHex = "FF6B6B";
    [ObservableProperty] private double _volume = 1.0;
    [ObservableProperty] private bool _loop;
    [ObservableProperty] private bool _favorite;
    [ObservableProperty] private uint _hotkeyModifiers;
    [ObservableProperty] private uint _hotkeyVirtualKey;
    [ObservableProperty] private bool _hotkeyPrefix;
    [ObservableProperty] private uint _hotkeyPrefixVirtualKey;
    private List<uint> _hotkeyKeys = new();
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private double _durationSeconds;

    public PadItem(MainViewModel owner, PadData data)
    {
        _owner = owner;
        Id = string.IsNullOrWhiteSpace(data.Id) ? Guid.NewGuid().ToString("N") : data.Id;
        _title = string.IsNullOrWhiteSpace(data.Title) ? Path.GetFileNameWithoutExtension(data.File) : data.Title;
        _category = string.IsNullOrWhiteSpace(data.Category) ? "Разное" : data.Category;
        _colorHex = string.IsNullOrWhiteSpace(data.Color) ? Palette[0] : data.Color;
        _volume = Math.Clamp(data.Volume, 0f, 1f);
        _loop = data.Loop;
        _favorite = data.Favorite;
        _hotkeyModifiers = data.HotkeyModifiers;
        _hotkeyVirtualKey = data.HotkeyVirtualKey;
        _hotkeyPrefix = data.HotkeyPrefix;
        _hotkeyPrefixVirtualKey = data.HotkeyPrefixVirtualKey;
        _hotkeyKeys = data.HotkeyKeys is { Count: > 0 }
            ? HotkeyKeyUtil.NormalizeList(data.HotkeyKeys)
            : HotkeyKeyUtil.FromLegacy(data.HotkeyModifiers, data.HotkeyVirtualKey, data.HotkeyPrefix, data.HotkeyPrefixVirtualKey);
        _durationSeconds = data.DurationSeconds;
        FilePath = ResolveFile(data.File);
        Peaks = data.Peaks is { Length: > 0 } ? data.Peaks : PlaceholderPeaks();
        Order = data.Order;

        PlayCommand = new RelayCommand(() => _owner.PlayPad(this));
        StopCommand = new RelayCommand(() => _owner.StopPad(this));
        ToggleFavoriteCommand = new RelayCommand(() => _owner.ToggleFavorite(this));
        ToggleLoopCommand = new RelayCommand(() => _owner.TogglePadLoop(this));
        RenameCommand = new RelayCommand(() => _owner.RenamePad(this));
        HotkeyCommand = new RelayCommand(() => _owner.ChangePadHotkey(this));
        CycleColorCommand = new RelayCommand(() => _owner.CyclePadColor(this));
        RemoveCommand = new RelayCommand(() => _owner.RemovePad(this));
        OpenLocationCommand = new RelayCommand(() => _owner.OpenPadLocation(this));

        RefreshBrushes();
    }

    public string Id { get; }
    public string FilePath { get; }
    public float[] Peaks { get; }
    public int Order { get; set; }

    public string FileName => Path.GetFileName(FilePath);

    public string DurationText => AudioClipFormat(DurationSeconds);

    public List<uint> HotkeyKeys
    {
        get => _hotkeyKeys;
        set
        {
            _hotkeyKeys = HotkeyKeyUtil.NormalizeList(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveHotkeyKeys));
            OnPropertyChanged(nameof(HotkeyText));
            OnPropertyChanged(nameof(HasHotkey));
            OnPropertyChanged(nameof(ShortKey));
        }
    }

    public IReadOnlyList<uint> EffectiveHotkeyKeys => HotkeyKeys is { Count: > 0 }
        ? HotkeyKeys
        : HotkeyKeyUtil.FromLegacy(HotkeyModifiers, HotkeyVirtualKey, HotkeyPrefix, HotkeyPrefixVirtualKey);

    public HotkeyData Hotkey => new() { Keys = EffectiveHotkeyKeys.ToList() };

    public string HotkeyText => HotkeyFormatter.FormatKeys(EffectiveHotkeyKeys);

    public string ShortKey
    {
        get
        {
            var keys = EffectiveHotkeyKeys;
            if (keys.Count == 0) return "";

            var parts = keys.Select(ShortName).ToArray();
            return parts.Length <= 2 ? string.Concat(parts) : string.Join("+", parts);
        }
    }

    private static string ShortName(uint virtualKey)
    {
        var key = HotkeyKeyUtil.Normalize(virtualKey);
        return key switch
        {
            HotkeyKeyUtil.VkControl => "C",
            HotkeyKeyUtil.VkAlt => "A",
            HotkeyKeyUtil.VkShift => "S",
            HotkeyKeyUtil.VkLeftWin => "W",
            >= 0x60 and <= 0x69 => "N" + (key - 0x60),
            _ => HotkeyFormatter.KeyName(key).Replace("Num ", "", StringComparison.Ordinal),
        };
    }

    public bool HasHotkey => EffectiveHotkeyKeys.Count > 0;

    public string FavoriteGlyph => Favorite ? "\uE735" : "\uE734";

    public string CategoryDisplay => Category;

    public IRelayCommand PlayCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IRelayCommand ToggleFavoriteCommand { get; }
    public IRelayCommand ToggleLoopCommand { get; }
    public IRelayCommand RenameCommand { get; }
    public IRelayCommand HotkeyCommand { get; }
    public IRelayCommand CycleColorCommand { get; }
    public IRelayCommand RemoveCommand { get; }
    public IRelayCommand OpenLocationCommand { get; }

    public Brush AccentBrush { get; private set; } = Brushes.Gray;
    public Brush WaveBrush { get; private set; } = Brushes.Gray;
    public Brush BorderBrush { get; private set; } = Brushes.Transparent;
    public Brush PadBackground { get; private set; } = Brushes.Transparent;

    partial void OnColorHexChanged(string value) => RefreshBrushes();

    partial void OnFavoriteChanged(bool value) => OnPropertyChanged(nameof(FavoriteGlyph));

    partial void OnHotkeyVirtualKeyChanged(uint value)
    {
        OnPropertyChanged(nameof(EffectiveHotkeyKeys));
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(HasHotkey));
        OnPropertyChanged(nameof(ShortKey));
    }

    partial void OnHotkeyModifiersChanged(uint value)
    {
        OnPropertyChanged(nameof(EffectiveHotkeyKeys));
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(ShortKey));
    }

    partial void OnHotkeyPrefixChanged(bool value)
    {
        OnPropertyChanged(nameof(EffectiveHotkeyKeys));
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(ShortKey));
    }

    partial void OnHotkeyPrefixVirtualKeyChanged(uint value)
    {
        OnPropertyChanged(nameof(EffectiveHotkeyKeys));
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(ShortKey));
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(FileName));

    private void RefreshBrushes()
    {
        var color = ParseColor(ColorHex);
        var accent = new SolidColorBrush(color);
        accent.Freeze();
        AccentBrush = accent;

        var border = new SolidColorBrush(Color.FromArgb(70, color.R, color.G, color.B));
        border.Freeze();
        BorderBrush = border;

        var wave = new SolidColorBrush(Color.FromArgb(170, color.R, color.G, color.B));
        wave.Freeze();
        WaveBrush = wave;

        var background = new LinearGradientBrush
        {
            StartPoint = new Point(0.05, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(60, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(14, color.R, color.G, color.B), 0.6),
                new GradientStop(Color.FromRgb(11, 15, 22), 1),
            },
        };
        background.Freeze();
        PadBackground = background;

        OnPropertyChanged(nameof(AccentBrush));
        OnPropertyChanged(nameof(WaveBrush));
        OnPropertyChanged(nameof(BorderBrush));
        OnPropertyChanged(nameof(PadBackground));
    }

    public PadData ToData() => new()
    {
        Id = Id,
        Title = Title,
        File = FilePath,
        Category = Category,
        Color = ColorHex,
        HotkeyKeys = EffectiveHotkeyKeys.ToList(),
        HotkeyModifiers = 0,
        HotkeyVirtualKey = 0,
        HotkeyPrefix = false,
        HotkeyPrefixVirtualKey = 0,
        Volume = (float)Volume,
        Loop = Loop,
        Favorite = Favorite,
        Order = Order,
        DurationSeconds = (float)DurationSeconds,
        Peaks = Peaks,
    };

    private static string ResolveFile(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return "";
        if (Path.IsPathRooted(file)) return file;
        return Path.Combine(AppPaths.SoundsDirectory, file);
    }

    private static float[] PlaceholderPeaks()
    {
        var peaks = new float[64];
        for (var i = 0; i < peaks.Length; i++) peaks[i] = 0.25f + 0.2f * (i % 3);
        return peaks;
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString("#" + hex.TrimStart('#'));
        }
        catch
        {
            return Color.FromRgb(255, 107, 107);
        }
    }

    private static string AudioClipFormat(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
    }
}
