using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSoundPad.App.Models;

namespace FreeSoundPad.App.ViewModels;

public sealed partial class BookmarkItem : ObservableObject
{
    private readonly MainViewModel _owner;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _colorHex = "4CC9F0";

    public BookmarkItem(MainViewModel owner, BookmarkData data)
    {
        _owner = owner;
        Id = string.IsNullOrWhiteSpace(data.Id) ? Guid.NewGuid().ToString("N") : data.Id;
        _title = data.Title;
        _url = data.Url;
        _colorHex = data.Color;
        NavigateCommand = new RelayCommand(() => _owner.NavigateBookmark(this));
        RemoveCommand = new RelayCommand(() => _owner.RemoveBookmark(this));
        RefreshBrushes();
    }

    public string Id { get; }

    public string Initial
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title.Trim()[..1].ToUpperInvariant();
            return "?";
        }
    }

    public string Host
    {
        get
        {
            try
            {
                return new Uri(Url).Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return Url;
            }
        }
    }

    public Brush AccentBrush { get; private set; } = Brushes.Gray;
    public Brush SoftBrush { get; private set; } = Brushes.Gray;

    public IRelayCommand NavigateCommand { get; }
    public IRelayCommand RemoveCommand { get; }

    partial void OnColorHexChanged(string value) => RefreshBrushes();

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(Initial));

    public BookmarkData ToData() => new()
    {
        Id = Id,
        Title = Title,
        Url = Url,
        Color = ColorHex,
    };

    private void RefreshBrushes()
    {
        var color = ParseColor(ColorHex);
        var accent = new SolidColorBrush(color);
        accent.Freeze();
        AccentBrush = accent;

        var soft = new SolidColorBrush(Color.FromArgb(36, color.R, color.G, color.B));
        soft.Freeze();
        SoftBrush = soft;

        OnPropertyChanged(nameof(AccentBrush));
        OnPropertyChanged(nameof(SoftBrush));
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString("#" + hex.TrimStart('#'));
        }
        catch
        {
            return Color.FromRgb(76, 201, 240);
        }
    }
}

public sealed class CategoryItem
{
    public CategoryItem(string name, int count)
    {
        Name = name;
        Count = count;
    }

    public string Name { get; }
    public int Count { get; }
    public string Display => Count > 0 ? $"{Name}  {Count}" : Name;
}
