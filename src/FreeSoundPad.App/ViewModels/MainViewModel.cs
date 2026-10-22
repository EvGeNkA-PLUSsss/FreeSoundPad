using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSoundPad.App.Dialogs;
using FreeSoundPad.App.Models;
using FreeSoundPad.App.Services;
using FreeSoundPad.Audio;
using Microsoft.Win32;

namespace FreeSoundPad.App.ViewModels;

public enum AppTab
{
    Sounds,
    Browser,
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public const string SupportedFilter = "Аудиофайлы|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac|MP3|*.mp3|Все файлы|*.*";

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".aac", ".wma", ".flac",
    };

    private readonly Dispatcher _dispatcher;
    private readonly AudioEngine _engine = new();
    private readonly Dictionary<string, AudioClip> _clipCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<AudioClip>> _clipLoads = new(StringComparer.OrdinalIgnoreCase);

    private AppData _data = new();
    private Window? _window;
    private HotkeyManager? _hotkeyManager;
    private TrayService? _tray;
    private OverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _meterTimer;
    private DispatcherTimer? _statusTimer;
    private WebRemoteServer? _webServer;
    private bool _suppress;
    private bool _initialized;
    private bool _isShuttingDown;
    private bool _trayHintShown;

    [ObservableProperty] private AppTab _activeTab = AppTab.Sounds;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string? _selectedCategory = "Все";
    [ObservableProperty] private int _sortIndex;
    [ObservableProperty] private string _statusText = "Готово";
    [ObservableProperty] private string _nowPlayingTitle = "Тишина";
    [ObservableProperty] private string _nowPlayingMeta = "Выберите звук или нажмите хоткей";
    [ObservableProperty] private string _nowPlayingTime = "";
    [ObservableProperty] private double _nowPlayingProgress;
    [ObservableProperty] private bool _isNowPlaying;

    [ObservableProperty] private double _micLevel;
    [ObservableProperty] private double _padLevel;
    [ObservableProperty] private double _webLevel;
    [ObservableProperty] private double _outputLevel;
    [ObservableProperty] private double _monitorLevel;

    [ObservableProperty] private double _masterGain = 90;
    [ObservableProperty] private double _micGain = 80;
    [ObservableProperty] private double _padGain = 90;
    [ObservableProperty] private double _webGain = 80;
    [ObservableProperty] private double _monitorGain = 80;

    [ObservableProperty] private bool _micPassthrough = true;
    [ObservableProperty] private bool _browserSend = true;
    [ObservableProperty] private bool _browserMonitor;
    [ObservableProperty] private bool _monitorEnabled;
    [ObservableProperty] private bool _browserCaptureEnabled = true;
    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private bool _closeToTray = true;
    [ObservableProperty] private bool _autoStart;

    [ObservableProperty] private AudioDeviceInfo? _selectedOutputDevice;
    [ObservableProperty] private AudioDeviceInfo? _selectedMicDevice;
    [ObservableProperty] private AudioDeviceInfo? _selectedMonitorDevice;

    [ObservableProperty] private bool _cableDetected;
    [ObservableProperty] private string _cableHint = "";
    [ObservableProperty] private string _outputDeviceName = "Не выбрано";
    [ObservableProperty] private string _outputHint = "";
    [ObservableProperty] private string _hotkeyWarning = "";

    [ObservableProperty] private bool _overlayVisible;
    [ObservableProperty] private bool _overlayCompact;
    [ObservableProperty] private bool _outputIsCable;
    [ObservableProperty] private bool _browserMuted;
    [ObservableProperty] private bool _mixerVisible = true;
    [ObservableProperty] private bool _outputEndpointSilent;
    [ObservableProperty] private bool _hasMorePads;
    [ObservableProperty] private string _showMoreText = "";
    [ObservableProperty] private bool _webServerEnabled = true;
    [ObservableProperty] private int _webServerPort = 8765;
    [ObservableProperty] private string _webServerToken = "";
    [ObservableProperty] private string _webServerUrl = "";
    [ObservableProperty] private string _webServerStatus = "Выключен";
    private bool _browserQuietApplied;
    private int _silentOutputTicks;
    private PadItem? _currentPad;
    private int _playRequest;
    private int _visibleLimit = 60;
    private DispatcherTimer? _searchTimer;
    private DispatcherTimer? _libraryTimer;

    public ObservableCollection<PadItem> VisiblePads { get; } = new();

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        _searchTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(160),
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyFilters();
        };

        _libraryTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        _libraryTimer.Tick += (_, _) =>
        {
            _libraryTimer.Stop();
            RefreshCategories();
            RebuildQuickPads();
            ApplyFilters();
        };

        Pads.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PadsCountText));
            OnPropertyChanged(nameof(PadHotkeysText));
            _libraryTimer.Stop();
            _libraryTimer.Start();
        };

        PadsView = CollectionViewSource.GetDefaultView(Pads);
        PadsView.Filter = FilterPad;

        _engine.Error += (_, message) => Dispatch(() =>
        {
            Log.Error(message);
            SetStatus(message);
        });

        _engine.BrowserCaptureStopped += (_, message) => Dispatch(() => OnBrowserCaptureStoppedInternal(message));

        LoadData();
    }

    public ObservableCollection<PadItem> Pads { get; } = new();
    public ObservableCollection<PadItem> QuickPads { get; } = new();
    public ObservableCollection<BookmarkItem> Bookmarks { get; } = new();
    public ObservableCollection<CategoryItem> Categories { get; } = new();
    public ObservableCollection<AudioDeviceInfo> RenderDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> CaptureDevices { get; } = new();

    public ICollectionView PadsView { get; }

    public AudioEngine Engine => _engine;

    public SettingsData Settings => _data.Settings;

    public PadItem? CurrentPad => _currentPad;

    public bool IsShuttingDown => _isShuttingDown;

    public string PadsCountText => Pads.Count switch
    {
        0 => "пусто",
        1 => "1 звук",
        _ when Pads.Count < 5 => $"{Pads.Count} звука",
        _ => $"{Pads.Count} звуков",
    };

    public string PadHotkeysText
    {
        get
        {
            var assigned = Pads.Count(p => p.HasHotkey);
            return Pads.Count == 0
                ? "Падов пока нет"
                : $"Назначено {assigned} из {Pads.Count}";
        }
    }
    public string FooterMeta => "48 kHz · стерео · буфер 30 мс";

    public bool IsSoundsTab
    {
        get => ActiveTab == AppTab.Sounds;
        set
        {
            if (value) ActiveTab = AppTab.Sounds;
        }
    }

    public bool IsBrowserTab
    {
        get => ActiveTab == AppTab.Browser;
        set
        {
            if (value) ActiveTab = AppTab.Browser;
        }
    }

    // ------------------------------------------------------------- lifecycle

    public void Initialize(Window window)
    {
        if (_initialized) return;
        _initialized = true;
        _window = window;

        _hotkeyManager = new HotkeyManager(window);

        RefreshDevices();
        ApplyAudioSettings();
        StartAudio();
        AudioEngine.SetProcessSessionsVolume("msedgewebview2", 1f);

        CreateTray();
        RegisterHotkeys();
        InitializeWebServer();

        _meterTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _meterTimer.Tick += OnMeterTick;
        _meterTimer.Start();

        SetStatus(CableDetected
            ? "Готово · виртуальный кабель найден"
            : "Виртуальный кабель не найден — откройте настройки");

        UpdateTrayState();
    }

    public void AttachBrowser(Microsoft.Web.WebView2.Wpf.WebView2 webView)
    {
        AttachBrowserCore(webView);
    }

    public void SaveWindowState(Window window)
    {
        var settings = _data.Settings;
        if (window.WindowState == WindowState.Maximized)
        {
            settings.WindowMaximized = true;
        }
        else if (window.WindowState == WindowState.Normal)
        {
            settings.WindowMaximized = false;
            settings.WindowLeft = window.Left;
            settings.WindowTop = window.Top;
            settings.WindowWidth = window.Width;
            settings.WindowHeight = window.Height;
        }
    }

    public void RequestExit()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;
        try
        {
            if (_window != null) SaveWindowState(_window);
            Save();
        }
        catch (Exception ex)
        {
            Log.Error("Save before exit failed", ex);
        }

        _window?.Close();
        Application.Current.Shutdown();
    }

    /// <summary>Called when Windows is shutting down: never block the shutdown with tray behavior.</summary>
    public void PrepareSystemShutdown()
    {
        _isShuttingDown = true;
        try
        {
            if (_window != null) SaveWindowState(_window);
            Save();
        }
        catch (Exception ex)
        {
            Log.Error("Save before system shutdown failed", ex);
        }
    }

    public void ShowMainWindow()
    {
        if (_window == null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    public void OnMinimized()
    {
        if (!MinimizeToTray || _window == null) return;
        _window.Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray?.ShowBalloon("FreeSoundPad свёрнут в трей",
                "Приложение продолжает работать. Глобальные хоткеи активны.");
        }
    }

    public void OnClosing(Window window)
    {
        SaveWindowState(window);
    }

    public bool ShouldHideOnClose()
    {
        if (_isShuttingDown || !CloseToTray) return false;
        if (_window == null) return false;
        _window.Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray?.ShowBalloon("FreeSoundPad остался в трее",
                "Закройте через меню трея, если нужно выключить приложение.");
        }

        return true;
    }

    public void OnOverlayHidden()
    {
        if (_overlay != null) SaveOverlayPosition();
        OverlayVisible = false;
        UpdateTrayState();
    }

    public void Save()
    {
        try
        {
            _data.Pads = Pads.Select(p => p.ToData()).ToList();
            _data.Bookmarks = Bookmarks.Select(b => b.ToData()).ToList();

            var settings = _data.Settings;
            settings.OutputDeviceId = SelectedOutputDevice?.Id;
            settings.MicDeviceId = SelectedMicDevice?.Id;
            settings.MonitorDeviceId = SelectedMonitorDevice?.Id;
            settings.MonitorEnabled = MonitorEnabled;
            settings.MicPassthrough = MicPassthrough;
            settings.BrowserSend = BrowserSend;
            settings.BrowserMonitor = BrowserMonitor;
            settings.BrowserCaptureEnabled = BrowserCaptureEnabled;
            settings.MinimizeToTray = MinimizeToTray;
            settings.CloseToTray = CloseToTray;
            settings.AutoStart = AutoStart;
            settings.MasterGain = (float)(MasterGain / 100);
            settings.MicGain = (float)(MicGain / 100);
            settings.PadGain = (float)(PadGain / 100);
            settings.BrowserGain = (float)(WebGain / 100);
            settings.MonitorGain = (float)(MonitorGain / 100);
            settings.OverlayCompact = OverlayCompact;
            settings.BrowserMuted = BrowserMuted;
            settings.MixerVisible = MixerVisible;
            settings.WebServerEnabled = WebServerEnabled;
            settings.WebServerPort = WebServerPort;
            settings.WebServerToken = WebServerToken;

            ConfigStore.Save(_data);
        }
        catch (Exception ex)
        {
            Log.Error("Save failed", ex);
        }
    }

    public void Dispose()
    {
        try
        {
            Save();
        }
        catch
        {
            // ignore
        }

        _meterTimer?.Stop();
        _statusTimer?.Stop();
        try
        {
            AudioEngine.SetProcessSessionsVolume("msedgewebview2", 1f);
            AudioEngine.SetProcessSessionsMuted("msedgewebview2", false);
        }
        catch
        {
            // ignore
        }

        _hotkeyManager?.Dispose();
        _webServer?.Dispose();
        _tray?.Dispose();
        _engine.Dispose();

        try
        {
            _overlay?.Close();
        }
        catch
        {
            // ignore
        }

        try
        {
            _settingsWindow?.Close();
        }
        catch
        {
            // ignore
        }
    }

    // -------------------------------------------------------------- data load

    private void LoadData()
    {
        _suppress = true;
        _data = ConfigStore.Load();

        foreach (var pad in _data.Pads.OrderBy(p => p.Order))
        {
            Pads.Add(new PadItem(this, pad));
        }

        foreach (var bookmark in _data.Bookmarks)
        {
            Bookmarks.Add(new BookmarkItem(this, bookmark));
        }

        var settings = _data.Settings;
        HotkeyFormatter.PadPrefix = settings.PadHotkeyPrefix;
        MasterGain = settings.MasterGain * 100;
        MicGain = settings.MicGain * 100;
        PadGain = settings.PadGain * 100;
        WebGain = settings.BrowserGain * 100;
        MonitorGain = settings.MonitorGain * 100;
        MicPassthrough = settings.MicPassthrough;
        BrowserSend = settings.BrowserSend;
        BrowserMonitor = settings.BrowserMonitor;
        MonitorEnabled = settings.MonitorEnabled;
        BrowserCaptureEnabled = settings.BrowserCaptureEnabled;
        MinimizeToTray = settings.MinimizeToTray;
        CloseToTray = settings.CloseToTray;
        AutoStart = settings.AutoStart;
        OverlayCompact = settings.OverlayCompact;
        BrowserMuted = settings.BrowserMuted;
        MixerVisible = settings.MixerVisible;
        WebServerEnabled = settings.WebServerEnabled;
        WebServerPort = settings.WebServerPort is >= 1024 and <= 65535 ? settings.WebServerPort : 8765;
        WebServerToken = settings.WebServerToken ?? "";

        _suppress = false;

        ApplySort();
        RefreshCategories();
        RebuildQuickPads();
        RefreshVisiblePads();
        OnPropertyChanged(nameof(PadsCountText));
    }

    // ------------------------------------------------------------- audio setup

    public void RefreshDevices()
    {
        _suppress = true;

        var render = AudioDeviceService.GetRenderDevices();
        var capture = AudioDeviceService.GetCaptureDevices();

        RenderDevices.Clear();
        foreach (var device in render) RenderDevices.Add(device);

        CaptureDevices.Clear();
        foreach (var device in capture) CaptureDevices.Add(device);

        var settings = _data.Settings;
        var suggestedCable = AudioDeviceService.SuggestVirtualOutput(render);

        SelectedOutputDevice = render.FirstOrDefault(d => d.Id == settings.OutputDeviceId)
                               ?? suggestedCable
                               ?? render.FirstOrDefault(d => d.IsDefault)
                               ?? render.FirstOrDefault();

        SelectedMicDevice = capture.FirstOrDefault(d => d.Id == settings.MicDeviceId)
                            ?? capture.FirstOrDefault(d => d.IsDefault && !d.IsVirtual)
                            ?? capture.FirstOrDefault(d => !d.IsVirtual)
                            ?? capture.FirstOrDefault();

        SelectedMonitorDevice = render.FirstOrDefault(d => d.Id == settings.MonitorDeviceId)
                                ?? render.FirstOrDefault(d => d.IsDefault && d.Id != SelectedOutputDevice?.Id)
                                ?? render.FirstOrDefault(d => !d.IsVirtual
                                                              && d.Id != SelectedOutputDevice?.Id
                                                              && !d.Name.Contains("fifine", StringComparison.OrdinalIgnoreCase))
                                ?? render.FirstOrDefault(d => d.Id != SelectedOutputDevice?.Id);

        CableDetected = suggestedCable != null;
        CableHint = suggestedCable != null
            ? $"Найден виртуальный кабель: {suggestedCable.Name}"
            : "Виртуальный кабель не найден. Установите VB-CABLE, чтобы собеседники слышали звуки.";

        UpdateOutputInfo();
        OnPropertyChanged(nameof(CanMonitor));
        _suppress = false;
    }

    private void StartAudio()
    {
        if (SelectedOutputDevice == null)
        {
            SetStatus("Не найдено устройство вывода");
            return;
        }

        if (!_engine.Start(SelectedOutputDevice.Id))
        {
            SetStatus("Не удалось запустить аудиовыход — проверьте настройки");
            return;
        }

        _engine.SetMicDevice(SelectedMicDevice?.Id);
        if (MonitorEnabled && SelectedMonitorDevice != null)
        {
            _engine.SetMonitor(SelectedMonitorDevice.Id, true);
        }
    }

    private void ApplyAudioSettings()
    {
        _engine.MicPassthrough = MicPassthrough;
        _engine.BrowserSend = BrowserSend;
        _engine.BrowserMonitor = BrowserMonitor;
        _engine.MasterGain = (float)(MasterGain / 100);
        _engine.MicGain = (float)(MicGain / 100);
        _engine.PadGain = (float)(PadGain / 100);
        _engine.BrowserGain = (float)(WebGain / 100);
        _engine.MonitorGain = (float)(MonitorGain / 100);
    }

    private void UpdateOutputInfo()
    {
        var device = SelectedOutputDevice;
        OutputDeviceName = device?.Name ?? "Не выбрано";
        OutputIsCable = device != null && AudioDeviceService.IsLoopbackCable(device.Name);
        OutputHint = device == null
            ? "Выберите устройство вывода в настройках"
            : OutputIsCable
                ? "В Discord выбери микрофон CABLE Output — услышат всё, что идёт в эфир."
                : "Локальный режим: звук слышен в этом устройстве. Для микрофона нужен виртуальный кабель (VB-CABLE).";

        OnPropertyChanged(nameof(CanMonitor));
        if (!CanMonitor && MonitorEnabled) MonitorEnabled = false;
    }

    /// <summary>Monitor makes sense only when it routes audio to a different device than the main output.</summary>
    public bool CanMonitor => OutputIsCable
                              || (SelectedOutputDevice != null
                                  && SelectedMonitorDevice != null
                                  && SelectedMonitorDevice.Id != SelectedOutputDevice.Id);

    // -------------------------------------------------------------- commands

    [RelayCommand]
    private void SetTab(string? tab) => ActiveTab = string.Equals(tab, "browser", StringComparison.OrdinalIgnoreCase)
        ? AppTab.Browser
        : AppTab.Sounds;

    [RelayCommand]
    private void SetCategory(string? category)
    {
        SelectedCategory = string.IsNullOrWhiteSpace(category) ? "Все" : category;
    }

    [RelayCommand]
    private void ToggleFavoriteAll() => SelectedCategory = "Избранное";

    [RelayCommand]
    private void StopAll() => StopEverything();

    [RelayCommand]
    private void ToggleMic() => MicPassthrough = !MicPassthrough;

    [RelayCommand]
    private void ToggleBrowserSend() => BrowserSend = !BrowserSend;

    [RelayCommand]
    private void ToggleMonitor() => MonitorEnabled = !MonitorEnabled;

    [RelayCommand]
    private void ToggleOverlay()    {
        if (_overlay == null)
        {
            _overlay = new OverlayWindow(this);
            var settings = _data.Settings;
            if (settings.OverlayLeft >= 0 && settings.OverlayTop >= 0)
            {
                _overlay.WindowStartupLocation = WindowStartupLocation.Manual;
                _overlay.Left = settings.OverlayLeft;
                _overlay.Top = settings.OverlayTop;
            }
            else
            {
                var area = SystemParameters.WorkArea;
                _overlay.WindowStartupLocation = WindowStartupLocation.Manual;
                _overlay.Left = area.Right - 640;
                _overlay.Top = area.Bottom - 220;
            }
        }

        if (_overlay.IsVisible)
        {
            SaveOverlayPosition();
            _overlay.Hide();
            OverlayVisible = false;
        }
        else
        {
            _overlay.Show();
            _overlay.Activate();
            OverlayVisible = true;
        }

        UpdateTrayState();
    }

    private void SaveOverlayPosition()
    {
        if (_overlay == null) return;
        var settings = _data.Settings;
        if (_overlay.WindowState == WindowState.Normal)
        {
            settings.OverlayLeft = _overlay.Left;
            settings.OverlayTop = _overlay.Top;
        }

        SettingsDataStoreSave();
    }

    private void SettingsDataStoreSave()
    {
        try
        {
            _data.Settings.OutputDeviceId = SelectedOutputDevice?.Id;
            _data.Settings.MicDeviceId = SelectedMicDevice?.Id;
            _data.Settings.MonitorDeviceId = SelectedMonitorDevice?.Id;
            ConfigStore.Save(_data);
        }
        catch (Exception ex)
        {
            Log.Error("Save overlay state failed", ex);
        }
    }

    [RelayCommand]
    private void ToggleOverlaySize()
    {
        OverlayCompact = !OverlayCompact;
        Save();
    }

    [RelayCommand]
    private void ToggleBrowserMute()
    {
        BrowserMuted = !BrowserMuted;
        Save();
        SetStatus(BrowserMuted ? "Звук браузера выключен" : "Звук браузера включён");
    }

    [RelayCommand]
    private void ToggleMixer()
    {
        MixerVisible = !MixerVisible;
        Save();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (_window != null && !_window.IsVisible) ShowMainWindow();

        if (_settingsWindow == null)
        {
            var overlayWasVisible = OverlayVisible;
            OverlayVisible = false;
            _hotkeyManager?.UnregisterAll();

            _settingsWindow = new SettingsWindow
            {
                DataContext = this,
                Owner = _window,
            };
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                RegisterHotkeys();
                if (overlayWasVisible) OverlayVisible = true;
                Save();
            };

            // Modal: the main window must not be usable while settings are open.
            try
            {
                _settingsWindow.ShowDialog();
            }
            catch
            {
                _settingsWindow = null;
                RegisterHotkeys();
                if (overlayWasVisible) OverlayVisible = true;
                throw;
            }
        }
        else
        {
            _settingsWindow.Activate();
        }
    }

    [RelayCommand]
    private void RefreshDevicesCommand()
    {
        RefreshDevices();
        if (!_engine.IsRunning) StartAudio();
        SetStatus("Список устройств обновлён");
    }

    [RelayCommand]
    private void TestRoute()
    {
        _engine.PlayTestTone();
        SetStatus("Тестовый сигнал отправлен на выход");
    }

    [RelayCommand]
    private void OpenSoundsFolder()
    {
        try
        {
            AppPaths.Ensure();
            Process.Start(new ProcessStartInfo(AppPaths.SoundsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Не удалось открыть папку: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenCableWebsite()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Не удалось открыть сайт: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenRepo()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.Root) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    [RelayCommand]
    private void ShowWindow() => ShowMainWindow();

    [RelayCommand]
    private void ResetMixer()
    {
        MicGain = 80;
        PadGain = 90;
        WebGain = 80;
        MasterGain = 90;
        MonitorGain = 80;
        SetStatus("Громкости сброшены");
    }

    // ------------------------------------------------------------- import/play

    [RelayCommand]
    private async Task ImportSoundsAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Добавить звуки",
            Filter = SupportedFilter,
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true) return;
        await ImportFilesAsync(dialog.FileNames);
    }

    public async Task ImportFilesAsync(IEnumerable<string> files)
    {
        var list = files.Where(f => SupportedExtensions.Contains(Path.GetExtension(f))).Distinct().ToList();
        if (list.Count == 0)
        {
            SetStatus("Поддерживаются mp3, wav, m4a, aac, wma и flac");
            return;
        }

        var added = 0;
        var failed = new List<string>();
        var total = list.Count;

        foreach (var file in list)
        {
            try
            {
                SetStatus($"Импорт {added + failed.Count + 1} из {total}: {Path.GetFileName(file)}");
                var stored = CopyToLibrary(file);
                var clip = await Task.Run(() => AudioClipLoader.Load(stored));
                _clipCache[stored] = clip;

                var pad = new PadItem(this, new PadData
                {
                    Title = Path.GetFileNameWithoutExtension(stored),
                    File = stored,
                    Category = "Разное",
                    Color = PadItem.Palette[Pads.Count % PadItem.Palette.Length],
                    Order = NextOrder(),
                    DurationSeconds = (float)clip.Duration.TotalSeconds,
                    Peaks = clip.Peaks,
                });

                Pads.Add(pad);
                added++;
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(file)}: {ex.Message}");
                Log.Error($"Import failed: {file}", ex);
            }
        }

        RefreshCategories();
        RebuildQuickPads();
        PadsView.Refresh();
        RefreshVisiblePads();
        RegisterHotkeys();
        Save();

        if (failed.Count == 0)
        {
            SetStatus($"Добавлено звуков: {added}");
        }
        else
        {
            SetStatus($"Добавлено: {added}. Ошибки: {string.Join("; ", failed.Take(3))}");
        }
    }

    public void PlayPad(PadItem pad)
    {
        _ = PlayPadInternalAsync(pad);
    }

    private async Task PlayPadInternalAsync(PadItem pad)
    {
        var request = ++_playRequest;
        StopCurrentPad();

        try
        {
            var clip = await GetClipAsync(pad.FilePath);
            if (request != _playRequest) return;

            _engine.StopAll();
            _engine.PlayClip(clip, pad.Id, (float)pad.Volume, pad.Loop);
            _currentPad = pad;
            pad.IsPlaying = true;
            UpdateNowPlaying();
        }
        catch (Exception ex)
        {
            SetStatus($"Не удалось воспроизвести «{pad.Title}»: {ex.Message}");
            Log.Error($"Play failed: {pad.FilePath}", ex);
        }
    }

    private void StopCurrentPad()
    {
        var current = _currentPad;
        _currentPad = null;
        if (current == null) return;

        _engine.StopTag(current.Id);
        current.IsPlaying = false;
        current.Progress = 0;
    }

    public void StopPad(PadItem pad)
    {
        _engine.StopTag(pad.Id);
        if (ReferenceEquals(_currentPad, pad)) _currentPad = null;
        pad.IsPlaying = false;
        pad.Progress = 0;
        UpdateNowPlaying();
    }

    public void StopEverything()
    {
        _playRequest++;
        _engine.StopAll();
        StopCurrentPad();
        _engine.StopAll();

        IsNowPlaying = false;
        NowPlayingTitle = "Тишина";
        NowPlayingMeta = "Выберите звук или нажмите хоткей";
        NowPlayingTime = "";
        NowPlayingProgress = 0;
        SetStatus("Все звуки остановлены");
    }

    public void ToggleFavorite(PadItem pad)
    {
        pad.Favorite = !pad.Favorite;
        RefreshCategories();
        PadsView.Refresh();
        RefreshVisiblePads();
        RebuildQuickPads();
        Save();
        SetStatus(pad.Favorite ? $"«{pad.Title}» в избранном" : $"«{pad.Title}» убрано из избранного");
    }

    public void TogglePadLoop(PadItem pad)
    {
        pad.Loop = !pad.Loop;
        Save();
        SetStatus(pad.Loop ? $"«{pad.Title}» зациклен" : $"«{pad.Title}» играет один раз");
    }

    public void RenamePad(PadItem pad)
    {
        var dialog = new TextInputWindow("Переименовать звук", "Название", pad.Title)
        {
            Owner = _window,
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Value))
        {
            pad.Title = dialog.Value.Trim();
            Save();
            SetStatus("Название обновлено");
        }
    }

    public void ChangePadHotkey(PadItem pad)
    {
        // Global hotkeys would swallow the very key the dialog is trying to capture,
        // including an existing Numpad 0 assignment. Suspend them while editing.
        _hotkeyManager?.UnregisterAll();

        try
        {
            var dialog = new HotkeyWindow(pad.Hotkey, keys => FindHotkeyConflict(keys, pad, null))
            {
                Owner = _window,
            };

            if (dialog.ShowDialog() != true) return;

            if (dialog.Result == null)
            {
                pad.HotkeyKeys = new List<uint>();
                pad.HotkeyModifiers = 0;
                pad.HotkeyVirtualKey = 0;
                pad.HotkeyPrefix = false;
                pad.HotkeyPrefixVirtualKey = 0;
            }
            else
            {
                pad.HotkeyKeys = dialog.Result.GetEffectiveKeys();
                pad.HotkeyModifiers = 0;
                pad.HotkeyVirtualKey = 0;
                pad.HotkeyPrefix = false;
                pad.HotkeyPrefixVirtualKey = 0;
            }

            Save();
            OnPropertyChanged(nameof(PadHotkeysText));
            SetStatus(pad.HotkeyText is { Length: > 0 } ? $"Хоткей: {pad.HotkeyText}" : "Хоткей снят");
        }
        finally
        {
            RegisterHotkeys();
        }
    }

    [RelayCommand]
    private void ClearAllPadHotkeys()
    {
        if (Pads.Count == 0)
        {
            SetStatus("В библиотеке нет падов");
            return;
        }

        var assigned = Pads.Count(p => p.HasHotkey);
        if (assigned == 0)
        {
            SetStatus("У падов уже нет биндов");
            return;
        }

        var owner = _settingsWindow ?? _window;
        var message = $"Снять горячие клавиши со всех падов?\n\nНазначено: {assigned} из {Pads.Count}. Сами звуки останутся на месте.";
        var result = owner == null
            ? MessageBox.Show(message, "Снять все бинды", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : MessageBox.Show(owner, message, "Снять все бинды", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        foreach (var pad in Pads)
        {
            pad.HotkeyKeys = new List<uint>();
            pad.HotkeyModifiers = 0;
            pad.HotkeyVirtualKey = 0;
            pad.HotkeyPrefix = false;
            pad.HotkeyPrefixVirtualKey = 0;
        }

        _data.Settings.PadHotkeyPrefix = false;
        HotkeyFormatter.PadPrefix = false;
        Save();
        RegisterHotkeys();
        OnPropertyChanged(nameof(PadHotkeysText));
        SetStatus($"Бинды сняты со всех падов ({assigned})");
    }
    public void CyclePadColor(PadItem pad)
    {
        var index = Array.IndexOf(PadItem.Palette, pad.ColorHex);
        pad.ColorHex = PadItem.Palette[(index + 1 + PadItem.Palette.Length) % PadItem.Palette.Length];
        Save();
    }

    public void RemovePad(PadItem pad)
    {
        var result = MessageBox.Show(_window!, $"Убрать «{pad.Title}» из библиотеки?\nФайл останется в папке Sounds.",
            "FreeSoundPad", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result != MessageBoxResult.OK) return;

        _engine.StopTag(pad.Id);
        Pads.Remove(pad);
        PadsView.Refresh();
        RefreshVisiblePads();
        RefreshCategories();
        RebuildQuickPads();
        RegisterHotkeys();
        Save();
        SetStatus($"«{pad.Title}» удалён из библиотеки");
    }

    public void OpenPadLocation(PadItem pad)
    {
        try
        {
            if (!File.Exists(pad.FilePath))
            {
                SetStatus("Файл не найден");
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{pad.FilePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Не удалось открыть папку: {ex.Message}");
        }
    }

    private async Task<AudioClip> GetClipAsync(string path)
    {
        if (_clipCache.TryGetValue(path, out var cached)) return cached;
        if (_clipLoads.TryGetValue(path, out var running)) return await running;

        var task = Task.Run(() => AudioClipLoader.Load(path));
        _clipLoads[path] = task;
        try
        {
            var clip = await task;
            _clipCache[path] = clip;
            return clip;
        }
        finally
        {
            _clipLoads.Remove(path);
        }
    }

    private static string CopyToLibrary(string source)
    {
        AppPaths.Ensure();
        var full = Path.GetFullPath(source);
        if (full.StartsWith(Path.GetFullPath(AppPaths.SoundsDirectory), StringComparison.OrdinalIgnoreCase))
        {
            return full;
        }

        var baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(full));
        var extension = Path.GetExtension(full);
        var candidate = Path.Combine(AppPaths.SoundsDirectory, baseName + extension);
        var index = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(AppPaths.SoundsDirectory, $"{baseName} ({index++}){extension}");
        }

        File.Copy(full, candidate);
        return candidate;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        name = name.Trim();
        return string.IsNullOrWhiteSpace(name) ? "sound" : name;
    }

    private int NextOrder()
    {
        return Pads.Count == 0 ? 0 : Pads.Max(p => p.Order) + 1;
    }

    // ------------------------------------------------------------- filtering

    private bool FilterPad(object obj)
    {
        if (obj is not PadItem pad) return false;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var query = SearchText.Trim();
            var matches = pad.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                          || pad.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                          || (pad.HotkeyText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
            if (!matches) return false;
        }

        if (string.Equals(SelectedCategory, "Избранное", StringComparison.OrdinalIgnoreCase))
        {
            return pad.Favorite;
        }

        if (!string.IsNullOrEmpty(SelectedCategory) && SelectedCategory != "Все")
        {
            return string.Equals(pad.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private void ApplySort()
    {
        PadsView.SortDescriptions.Clear();
        if (SortIndex == 1)
        {
            PadsView.SortDescriptions.Add(new SortDescription(nameof(PadItem.Title), ListSortDirection.Ascending));
        }
        else
        {
            PadsView.SortDescriptions.Add(new SortDescription(nameof(PadItem.Order), ListSortDirection.Descending));
        }

        PadsView.Refresh();
        ResetVisiblePage();
        RefreshVisiblePads();
    }

    private void ResetVisiblePage() => _visibleLimit = 60;

    private void RefreshVisiblePads()
    {
        if (PadsView == null) return;

        var all = PadsView.Cast<PadItem>().ToList();
        var page = all.Take(_visibleLimit).ToList();

        if (VisiblePads.Count != page.Count || !VisiblePads.SequenceEqual(page))
        {
            VisiblePads.Clear();
            foreach (var pad in page) VisiblePads.Add(pad);
        }

        HasMorePads = all.Count > page.Count;
        ShowMoreText = HasMorePads
            ? $"Показать ещё · {page.Count} из {all.Count}"
            : "";
        OnPropertyChanged(nameof(IsFilterEmpty));
    }

    public bool IsFilterEmpty => VisiblePads.Count == 0 && Pads.Count > 0;

    [RelayCommand]
    private void ShowMorePads()
    {
        _visibleLimit += 60;
        RefreshVisiblePads();
    }

    private void ApplyFilters()
    {
        ResetVisiblePage();
        PadsView.Refresh();
        RefreshVisiblePads();
    }
    private void RefreshCategories()
    {
        var previous = SelectedCategory;
        var wasSuppressed = _suppress;
        _suppress = true;

        Categories.Clear();
        Categories.Add(new CategoryItem("Все", Pads.Count));
        Categories.Add(new CategoryItem("Избранное", Pads.Count(p => p.Favorite)));

        foreach (var group in Pads.GroupBy(p => p.Category).OrderBy(g => g.Key))
        {
            Categories.Add(new CategoryItem(group.Key, group.Count()));
        }

        _suppress = wasSuppressed;

        if (previous != null && Categories.Any(c => c.Name == previous))
        {
            if (!string.Equals(SelectedCategory, previous, StringComparison.Ordinal))
            {
                SelectedCategory = previous;
            }
        }
        else
        {
            SelectedCategory = "Все";
        }
    }

    private void RebuildQuickPads()
    {
        var wanted = Pads
            .OrderByDescending(p => p.Favorite)
            .ThenBy(p => p.Order)
            .Take(10)
            .ToList();

        QuickPads.Clear();
        foreach (var pad in wanted) QuickPads.Add(pad);
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchTimer?.Stop();
        _searchTimer?.Start();
    }

    partial void OnSelectedCategoryChanged(string? value) => ApplyFilters();

    partial void OnSortIndexChanged(int value) => ApplySort();

    partial void OnActiveTabChanged(AppTab value)
    {
        OnPropertyChanged(nameof(IsSoundsTab));
        OnPropertyChanged(nameof(IsBrowserTab));
    }

    // ------------------------------------------------------------- engine props

    partial void OnSelectedOutputDeviceChanged(AudioDeviceInfo? value)
    {
        if (_suppress || value == null) return;
        _data.Settings.OutputDeviceId = value.Id;
        UpdateOutputInfo();
        OnPropertyChanged(nameof(CanMonitor));

        if (!_engine.SetOutputDevice(value.Id) && _engine.IsRunning)
        {
            SetStatus("Не удалось переключить устройство вывода");
        }
        else if (!_engine.IsRunning)
        {
            StartAudio();
        }

        Save();
    }

    partial void OnSelectedMicDeviceChanged(AudioDeviceInfo? value)
    {
        if (_suppress) return;
        _data.Settings.MicDeviceId = value?.Id;
        _engine.SetMicDevice(value?.Id);
        Save();
    }

    partial void OnSelectedMonitorDeviceChanged(AudioDeviceInfo? value)
    {
        if (_suppress) return;
        _data.Settings.MonitorDeviceId = value?.Id;
        OnPropertyChanged(nameof(CanMonitor));
        if (!CanMonitor && MonitorEnabled) MonitorEnabled = false;
        if (MonitorEnabled) _engine.SetMonitor(value?.Id, true);
        Save();
    }

    partial void OnMasterGainChanged(double value) => _engine.MasterGain = (float)(value / 100);

    partial void OnMicGainChanged(double value) => _engine.MicGain = (float)(value / 100);

    partial void OnPadGainChanged(double value) => _engine.PadGain = (float)(value / 100);

    partial void OnWebGainChanged(double value) => _engine.BrowserGain = (float)(value / 100);

    partial void OnMonitorGainChanged(double value) => _engine.MonitorGain = (float)(value / 100);

    partial void OnMicPassthroughChanged(bool value)
    {
        if (_suppress) return;
        _engine.MicPassthrough = value;
        UpdateTrayState();
    }

    partial void OnBrowserSendChanged(bool value)
    {
        if (_suppress) return;
        _engine.BrowserSend = value;
        ApplyBrowserAudioMode();
        UpdateTrayState();
    }

    partial void OnBrowserMonitorChanged(bool value)
    {
        if (_suppress) return;
        _engine.BrowserMonitor = value;
        ApplyBrowserAudioMode();
    }

    partial void OnMonitorEnabledChanged(bool value)
    {
        if (_suppress) return;
        _data.Settings.MonitorEnabled = value;
        _engine.SetMonitor(SelectedMonitorDevice?.Id, value);
        UpdateTrayState();
        Save();
    }

    partial void OnBrowserCaptureEnabledChanged(bool value)
    {
        if (_suppress) return;
        _data.Settings.BrowserCaptureEnabled = value;
        if (value)
        {
            StartBrowserCaptureIfReady();
        }
        else
        {
            _engine.StopBrowserCapture();
            BrowserCaptureActive = false;
        }

        Save();
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (_suppress) return;
        _data.Settings.MinimizeToTray = value;
        Save();
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_suppress) return;
        _data.Settings.CloseToTray = value;
        Save();
    }

    partial void OnAutoStartChanged(bool value)
    {
        if (_suppress) return;
        _data.Settings.AutoStart = value;
        AutoStartService.SetEnabled(value);
        Save();
    }

    // -------------------------------------------------------------- web remote

    private void InitializeWebServer()
    {
        if (!WebServerEnabled)
        {
            WebServerStatus = "Выключен";
            WebServerUrl = "";
            return;
        }

        var generated = EnsureWebToken();
        StartWebServer();
        if (generated) Save();
    }

    private bool EnsureWebToken()
    {
        if (!string.IsNullOrWhiteSpace(WebServerToken)) return false;

        WebServerToken = GenerateWebToken();
        _data.Settings.WebServerToken = WebServerToken;
        return true;
    }

    private static string GenerateWebToken()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> characters = stackalloc char[8];
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = alphabet[bytes[index] % alphabet.Length];
        }

        return new string(characters);
    }

    private void StartWebServer()
    {
        StopWebServer();

        if (!WebServerEnabled)
        {
            WebServerStatus = "Выключен";
            WebServerUrl = "";
            return;
        }

        if (WebServerPort is < 1024 or > 65535)
        {
            WebServerStatus = "Порт должен быть 1024–65535";
            WebServerUrl = "";
            return;
        }

        EnsureWebToken();

        try
        {
            var server = new WebRemoteServer(this, _dispatcher, WebServerPort, WebServerToken);
            server.Start();
            _webServer = server;
            WebServerUrl = server.Url;
            WebServerStatus = "Работает";
        }
        catch (Exception ex)
        {
            WebServerUrl = "";
            WebServerStatus = ex is System.Net.Sockets.SocketException
                ? $"Порт {WebServerPort} занят или недоступен"
                : $"Ошибка: {ex.Message}";
            Log.Error("Web server start failed", ex);
            StopWebServer();
        }
    }

    private void StopWebServer()
    {
        try
        {
            _webServer?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Info($"Web server stop: {ex.Message}");
        }

        _webServer = null;
    }

    partial void OnWebServerEnabledChanged(bool value)
    {
        if (_suppress) return;
        _data.Settings.WebServerEnabled = value;
        if (value)
        {
            EnsureWebToken();
            StartWebServer();
        }
        else
        {
            StopWebServer();
            WebServerStatus = "Выключен";
            WebServerUrl = "";
        }

        Save();
    }

    partial void OnWebServerPortChanged(int value)
    {
        if (_suppress) return;
        _data.Settings.WebServerPort = value;
        if (WebServerEnabled) StartWebServer();
        Save();
    }

    [RelayCommand]
    private void CopyWebServerUrl()
    {
        if (string.IsNullOrWhiteSpace(WebServerUrl))
        {
            SetStatus("Сначала включите веб-пульт");
            return;
        }

        try
        {
            Clipboard.SetText(WebServerUrl);
            SetStatus("Адрес веб-пульта скопирован");
        }
        catch (Exception ex)
        {
            SetStatus($"Не удалось скопировать: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CopyWebServerToken()
    {
        EnsureWebToken();
        Save();
        try
        {
            Clipboard.SetText(WebServerToken);
            SetStatus("Код веб-пульта скопирован");
        }
        catch (Exception ex)
        {
            SetStatus($"Не удалось скопировать: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenWebServer()
    {
        if (!WebServerEnabled || string.IsNullOrWhiteSpace(WebServerUrl))
        {
            SetStatus("Сначала включите веб-пульт");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(WebServerUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Не удалось открыть браузер: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- hotkeys

    private void RegisterHotkeys()
    {
        if (_hotkeyManager == null) return;

        _hotkeyManager.UnregisterAll();
        var failures = new List<string>();

        foreach (var pad in Pads.Where(p => p.HasHotkey))
        {
            var captured = pad;
            var keys = captured.EffectiveHotkeyKeys;
            if (!_hotkeyManager.RegisterHookedChord(keys, "pad:" + captured.Id,
                    () => Dispatch(() => PlayPad(captured))))
            {
                failures.Add($"{captured.HotkeyText} · {captured.Title}");
            }
        }

        var settings = _data.Settings;
        RegisterCommandHotkey(settings.StopAllHotkey, "Стоп всё", StopEverything, failures);
        RegisterCommandHotkey(settings.MicToggleHotkey, "Микрофон", ToggleMic, failures);
        RegisterCommandHotkey(settings.BrowserSendHotkey, "Браузер", ToggleBrowserSend, failures);
        RegisterCommandHotkey(settings.MonitorHotkey, "Мониторинг", ToggleMonitor, failures);
        RegisterCommandHotkey(settings.OverlayHotkey, "Оверлей", ToggleOverlay, failures);
        RegisterCommandHotkey(settings.ShowWindowHotkey, "Показать окно", ShowMainWindow, failures);

        HotkeyWarning = failures.Count == 0 ? "" : "Заняты: " + string.Join(", ", failures.Distinct());
        if (failures.Count > 0) Log.Info("Hotkey conflicts: " + HotkeyWarning);
    }

    private void RegisterCommandHotkey(HotkeyData hotkey, string description, Action action, List<string> failures)
    {
        if (!hotkey.IsSet || _hotkeyManager == null) return;

        var keys = hotkey.GetEffectiveKeys();
        if (!_hotkeyManager.RegisterHookedChord(keys, description, () => Dispatch(action)))
        {
            failures.Add($"{HotkeyFormatter.FormatKeys(keys)} · {description}");
        }
    }

    private string? FindHotkeyConflict(IReadOnlyList<uint> keys, PadItem? exceptPad, HotkeyData? exceptCommand)
    {
        var requested = HotkeyKeyUtil.NormalizeList(keys);
        if (requested.Count == 0) return null;

        foreach (var pad in Pads)
        {
            if (ReferenceEquals(pad, exceptPad) || !pad.HasHotkey) continue;
            if (HotkeyKeyUtil.IsDuplicateOrPrefix(requested, pad.EffectiveHotkeyKeys))
            {
                return $"Комбинация уже используется: «{pad.Title}» ({pad.HotkeyText}).";
            }
        }

        var settings = _data.Settings;
        foreach (var (hotkey, description) in new[]
                 {
                     (settings.StopAllHotkey, "Стоп всё"),
                     (settings.MicToggleHotkey, "Микрофон"),
                     (settings.BrowserSendHotkey, "Браузер"),
                     (settings.MonitorHotkey, "Мониторинг"),
                     (settings.OverlayHotkey, "Оверлей"),
                     (settings.ShowWindowHotkey, "Показывать окно"),
                 })
        {
            if (ReferenceEquals(hotkey, exceptCommand) || !hotkey.IsSet) continue;
            var other = hotkey.GetEffectiveKeys();
            if (HotkeyKeyUtil.IsDuplicateOrPrefix(requested, other))
            {
                return $"Комбинация уже используется: {description} ({HotkeyFormatter.FormatKeys(other)}).";
            }
        }

        return null;
    }

    [RelayCommand]
    private void ChangeCommandHotkey(string? command)
    {
        var settings = _data.Settings;
        var hotkey = command switch
        {
            "stopAll" => settings.StopAllHotkey,
            "mic" => settings.MicToggleHotkey,
            "browser" => settings.BrowserSendHotkey,
            "monitor" => settings.MonitorHotkey,
            "overlay" => settings.OverlayHotkey,
            "show" => settings.ShowWindowHotkey,
            _ => null,
        };

        if (hotkey == null) return;

        // Global hotkeys would consume the keys being captured in the dialog.
        _hotkeyManager?.UnregisterAll();
        try
        {
            var dialog = new HotkeyWindow(hotkey.Clone(), keys => FindHotkeyConflict(keys, null, hotkey))
            {
                Owner = _settingsWindow ?? _window,
            };

            if (dialog.ShowDialog() != true) return;
            hotkey.Keys = dialog.Result?.GetEffectiveKeys() ?? new List<uint>();
            hotkey.Modifiers = 0;
            hotkey.VirtualKey = 0;
            hotkey.NumpadPrefix = false;
            hotkey.NumpadPrefixVirtualKey = 0;
            Save();
            RegisterHotkeys();
        }
        finally
        {
            RegisterHotkeys();
        }

        OnPropertyChanged(nameof(StopAllHotkeyText));
        OnPropertyChanged(nameof(MicHotkeyText));
        OnPropertyChanged(nameof(BrowserHotkeyText));
        OnPropertyChanged(nameof(MonitorHotkeyText));
        OnPropertyChanged(nameof(OverlayHotkeyText));
        OnPropertyChanged(nameof(ShowWindowHotkeyText));
    }

    public string StopAllHotkeyText => HotkeyFormatter.FormatKeys(_data.Settings.StopAllHotkey.GetEffectiveKeys());
    public string MicHotkeyText => HotkeyFormatter.FormatKeys(_data.Settings.MicToggleHotkey.GetEffectiveKeys());
    public string BrowserHotkeyText => HotkeyFormatter.FormatKeys(_data.Settings.BrowserSendHotkey.GetEffectiveKeys());
    public string MonitorHotkeyText => HotkeyFormatter.FormatKeys(_data.Settings.MonitorHotkey.GetEffectiveKeys());
    public string OverlayHotkeyText => HotkeyFormatter.FormatKeys(_data.Settings.OverlayHotkey.GetEffectiveKeys());
    public string ShowWindowHotkeyText => HotkeyFormatter.FormatKeys(_data.Settings.ShowWindowHotkey.GetEffectiveKeys());

    // ------------------------------------------------------------------ tray

    private void CreateTray()
    {
        try
        {
            _tray = new TrayService(AppIconFactory.CreateTrayIcon());
            _tray.OpenRequested += () => Dispatch(ShowMainWindow);
            _tray.OverlayRequested += () => Dispatch(ToggleOverlay);
            _tray.StopAllRequested += () => Dispatch(StopEverything);
            _tray.MicToggleRequested += () => Dispatch(ToggleMic);
            _tray.BrowserToggleRequested += () => Dispatch(ToggleBrowserSend);
            _tray.MonitorToggleRequested += () => Dispatch(ToggleMonitor);
            _tray.ExitRequested += () => Dispatch(RequestExit);
            UpdateTrayState();
        }
        catch (Exception ex)
        {
            Log.Error("Tray init failed", ex);
        }
    }

    private void UpdateTrayState()
    {
        _tray?.UpdateState(MicPassthrough, BrowserSend, MonitorEnabled, OverlayVisible);
    }

    // ------------------------------------------------------------------ timer

    private void OnMeterTick(object? sender, EventArgs e)
    {
        MicLevel = _engine.MicMeter.Peak * 100;
        PadLevel = _engine.PadMeter.Peak * 100;
        WebLevel = _engine.BrowserMeter.Peak * 100;
        OutputLevel = _engine.OutputMeter.Peak * 100;
        MonitorLevel = _engine.MonitorMeter.Peak * 100;

        UpdateNowPlaying();

        SyncBrowserAudioMode();
        TrackOutputEndpoint();

        var captureActive = _engine.BrowserCaptureActive;
        if (BrowserCaptureActive != captureActive) BrowserCaptureActive = captureActive;
    }

    /// <summary>
    /// A virtual cable can be installed but not pass audio until Windows/driver restart.
    /// Detect that the endpoint stays silent while the mixer is clearly producing sound.
    /// </summary>
    private void TrackOutputEndpoint()
    {
        if (!OutputIsCable || OutputLevel < 2)
        {
            _silentOutputTicks = 0;
            return;
        }

        var endpointPeak = _engine.OutputEndpointPeak;
        if (endpointPeak < 0)
        {
            return;
        }

        if (endpointPeak < 0.002f)
        {
            _silentOutputTicks++;
            if (_silentOutputTicks >= 20)
            {
                OutputEndpointSilent = true;
                if (_browserQuietApplied) ApplyBrowserAudioMode();
            }
        }
        else
        {
            _silentOutputTicks = 0;
            OutputEndpointSilent = false;
        }
    }

    private void UpdateNowPlaying()
    {
        var current = _currentPad;
        if (current != null && _engine.IsPlaying(current.Id))
        {
            current.Progress = _engine.GetProgress(current.Id);
            IsNowPlaying = true;
            NowPlayingTitle = current.Title;
            NowPlayingProgress = current.Progress * 100;
            var total = TimeSpan.FromSeconds(current.DurationSeconds);
            var position = TimeSpan.FromSeconds(current.DurationSeconds * current.Progress);
            NowPlayingTime = $"{FormatTime(position)} / {FormatTime(total)}";
            NowPlayingMeta = current.Loop
                ? "PAD · зациклено"
                : current.HotkeyText is { Length: > 0 }
                    ? $"PAD · хоткей {current.HotkeyText}"
                    : "PAD · без хоткея";
            return;
        }

        if (current != null)
        {
            current.IsPlaying = false;
            current.Progress = 0;
            _currentPad = null;
        }

        if (IsNowPlaying)
        {
            IsNowPlaying = false;
            NowPlayingTitle = "Тишина";
            NowPlayingMeta = "Выберите звук или нажмите хоткей";
            NowPlayingTime = "";
            NowPlayingProgress = 0;
        }
    }

    private static string FormatTime(TimeSpan time)
    {
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    // --------------------------------------------------------------- helpers

    private void SetStatus(string text)
    {
        StatusText = text;
        _statusTimer?.Stop();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer?.Stop();
            StatusText = "Готово";
        };
        _statusTimer.Start();
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    public void HandleBrowserCaptureStopped(string message) => OnBrowserCaptureStoppedInternal(message);
}
