using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSoundPad.App.Models;
using FreeSoundPad.App.Services;
using FreeSoundPad.Audio;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FreeSoundPad.App.ViewModels;

public sealed partial class MainViewModel
{
    private static readonly string[] BookmarkColors = { "FF6B6B", "FFB454", "3FE0B0", "4CC9F0", "9B8CFF", "F472B6" };

    private WebView2? _webView;
    private bool _browserInitStarted;
    private bool _captureRestartRunning;

    [ObservableProperty] private bool _isBrowserReady;
    [ObservableProperty] private bool _browserUnavailable;
    [ObservableProperty] private string _browserUrl = "";
    [ObservableProperty] private string _browserTitle = "";
    [ObservableProperty] private bool _browserCaptureActive;
    [ObservableProperty] private uint _browserProcessId;
    [ObservableProperty] private string _browserProcessText = "—";
    [ObservableProperty] private string _browserStatusText = "Захват не запущен";
    [ObservableProperty] private string _browserCaptureDetail = "";
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;

    public string BrowserHomeUrl => _data.Settings.HomeUrl;

    private async void AttachBrowserCore(WebView2 webView)
    {
        if (_browserInitStarted) return;
        _browserInitStarted = true;
        _webView = webView;

        try
        {
            var runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
            Log.Info($"WebView2 runtime: {runtimeVersion}");

            AppPaths.Ensure();
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDirectory, options);
            await webView.EnsureCoreWebView2Async(environment);

            var core = webView.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsPasswordAutosaveEnabled = true;
            core.IsMuted = BrowserMuted;

            core.SourceChanged += OnBrowserSourceChanged;
            core.NavigationCompleted += OnBrowserNavigationCompleted;
            core.DocumentTitleChanged += OnBrowserTitleChanged;
            core.NewWindowRequested += OnBrowserNewWindowRequested;
            core.ProcessFailed += OnBrowserProcessFailed;
            core.ContextMenuRequested += OnBrowserContextMenuRequested;

            IsBrowserReady = true;
            BrowserUnavailable = false;

            var startUrl = _data.Settings.LastUrl;
            if (!string.IsNullOrWhiteSpace(startUrl) && startUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                BrowserUrl = startUrl;
                webView.Source = new Uri(startUrl);
            }
            else
            {
                ShowStartPage();
            }

            StartBrowserCaptureIfReady();
        }
        catch (Exception ex)
        {
            BrowserUnavailable = true;
            IsBrowserReady = false;
            BrowserStatusText = "Браузер недоступен";
            BrowserCaptureDetail = ex.Message;
            Log.Error("WebView2 init failed", ex);
            SetStatus("WebView2 Runtime не найден или не запустился — браузер недоступен");
        }
    }

    private void OnBrowserSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (_webView?.CoreWebView2 == null) return;
        try
        {
            var source = _webView.CoreWebView2.Source;
            BrowserUrl = string.IsNullOrWhiteSpace(source) || source == "about:blank" ? "freesoundpad://start" : source;
            CanGoBack = _webView.CoreWebView2.CanGoBack;
            CanGoForward = _webView.CoreWebView2.CanGoForward;
            if (BrowserUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) _data.Settings.LastUrl = BrowserUrl;
        }
        catch
        {
            // WebView may be in a transient state.
        }
    }

    private void OnBrowserNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_webView?.CoreWebView2 == null) return;
        try
        {
            var source = _webView.CoreWebView2.Source;
            BrowserUrl = string.IsNullOrWhiteSpace(source) || source == "about:blank" ? "freesoundpad://start" : source;
            BrowserTitle = _webView.CoreWebView2.DocumentTitle;
            CanGoBack = _webView.CoreWebView2.CanGoBack;
            CanGoForward = _webView.CoreWebView2.CanGoForward;
            if (BrowserUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) _data.Settings.LastUrl = BrowserUrl;
        }
        catch
        {
            // ignore
        }

        if (!BrowserCaptureActive) StartBrowserCaptureIfReady();
    }

    private void OnBrowserTitleChanged(object? sender, object e)
    {
        if (_webView?.CoreWebView2 == null) return;
        try
        {
            BrowserTitle = _webView.CoreWebView2.DocumentTitle;
        }
        catch
        {
            // ignore
        }
    }

    private void OnBrowserNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        NavigateBrowser(e.Uri);
    }

    private void OnBrowserProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        BrowserCaptureActive = false;
        BrowserStatusText = "Браузерный процесс перезапускается…";
        Log.Info($"WebView2 process failed: {e.ProcessFailedKind}");
        ScheduleCaptureRestart();
    }

    private bool StartBrowserCaptureIfReady()
    {
        if (!BrowserCaptureEnabled || _isShuttingDown) return false;
        if (_webView?.CoreWebView2 == null) return false;

        try
        {
            var pid = _webView.CoreWebView2.BrowserProcessId;
            if (pid == 0) return false;

            BrowserProcessId = pid;
            BrowserProcessText = $"PID {pid}";

            if (_engine.StartBrowserCapture(pid))
            {
                BrowserCaptureActive = true;
                BrowserStatusText = "Захват активен";
                BrowserCaptureDetail = $"msedgewebview2.exe · PID {pid}\nдерево процессов · WASAPI loopback";
                return true;
            }

            BrowserCaptureActive = false;
            BrowserStatusText = "Не удалось запустить захват";
            BrowserCaptureDetail = _engine.BrowserCaptureError ?? "";
            ScheduleCaptureRestart();
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Start browser capture failed", ex);
            BrowserStatusText = "Ошибка захвата";
            BrowserCaptureDetail = ex.Message;
            return false;
        }
    }

    private async void ScheduleCaptureRestart()
    {
        if (_captureRestartRunning || _isShuttingDown) return;
        _captureRestartRunning = true;

        try
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(1200);
                if (_isShuttingDown || !BrowserCaptureEnabled) return;
                if (_engine.BrowserCaptureActive) return;

                CoreWebView2? core = null;
                try
                {
                    core = _webView?.CoreWebView2;
                }
                catch
                {
                    // WebView may be disposing.
                }

                if (core == null) continue;
                var pid = core.BrowserProcessId;
                if (pid == 0) continue;

                BrowserProcessId = pid;
                BrowserProcessText = $"PID {pid}";
                if (_engine.StartBrowserCapture(pid))
                {
                    BrowserCaptureActive = true;
                    BrowserStatusText = "Захват активен";
                    BrowserCaptureDetail = $"msedgewebview2.exe · PID {pid}\nдерево процессов · WASAPI loopback";
                    return;
                }
            }

            BrowserStatusText = "Захват не возобновился";
            BrowserCaptureActive = false;
        }
        finally
        {
            _captureRestartRunning = false;
        }
    }

    private void OnBrowserCaptureStoppedInternal(string message)
    {
        if (_isShuttingDown) return;

        BrowserCaptureActive = false;
        BrowserProcessText = "—";
        BrowserStatusText = "Захват остановлен";
        BrowserCaptureDetail = message;
        if (BrowserCaptureEnabled) ScheduleCaptureRestart();
    }

    [RelayCommand]
    private void RestartBrowserCapture()
    {
        _engine.StopBrowserCapture();
        BrowserCaptureActive = false;
        if (StartBrowserCaptureIfReady())
        {
            SetStatus("Захват звука браузера перезапущен");
        }
        else
        {
            SetStatus("Не удалось перезапустить захват браузера");
        }
    }

    [RelayCommand]
    private void NavigateAddress()
    {
        var input = BrowserUrl?.Trim();
        if (string.IsNullOrEmpty(input)) return;

        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            NavigateBrowser(input);
            return;
        }

        if (input.Contains('.') && !input.Contains(' '))
        {
            NavigateBrowser("https://" + input);
            return;
        }

        NavigateBrowser("https://duckduckgo.com/?q=" + Uri.EscapeDataString(input));
    }

    [RelayCommand]
    private void GoBack()
    {
        if (_webView?.CoreWebView2?.CanGoBack == true) _webView.CoreWebView2.GoBack();
    }

    [RelayCommand]
    private void GoForward()
    {
        if (_webView?.CoreWebView2?.CanGoForward == true) _webView.CoreWebView2.GoForward();
    }

    [RelayCommand]
    private void ReloadPage()
    {
        try
        {
            _webView?.CoreWebView2?.Reload();
        }
        catch
        {
            // ignore
        }
    }

    [RelayCommand]
    private void GoHome() => ShowStartPage();

    public void ShowStartPage()
    {
        try
        {
            var core = _webView?.CoreWebView2;
            if (core == null) return;

            core.NavigateToString(BuildStartPageHtml());
            BrowserUrl = "freesoundpad://start";
            BrowserTitle = "FreeSoundPad — новая вкладка";
        }
        catch (Exception ex)
        {
            Log.Error("Start page failed", ex);
        }
    }

    private string BuildStartPageHtml()
    {
        var cards = new System.Text.StringBuilder();
        foreach (var bookmark in Bookmarks)
        {
            var color = "#" + bookmark.ColorHex;
            cards.Append("<a class=\"card\" href=\"")
                .Append(System.Net.WebUtility.HtmlEncode(bookmark.Url))
                .Append("\"><span class=\"dot\" style=\"background:")
                .Append(color)
                .Append("\"></span><span class=\"t\">")
                .Append(System.Net.WebUtility.HtmlEncode(bookmark.Title))
                .Append("</span><span class=\"h\">")
                .Append(System.Net.WebUtility.HtmlEncode(bookmark.Host))
                .Append("</span></a>");
        }

        return $$"""
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<style>
  *{box-sizing:border-box}
  body{margin:0;min-height:100vh;background:radial-gradient(900px 520px at 18% -12%, rgba(63,224,176,.09), transparent 60%),#06080C;color:#E8EEF9;font-family:'Segoe UI Variable Text','Segoe UI',sans-serif;display:flex;align-items:center;justify-content:center}
  .wrap{width:min(900px,90vw);padding:44px 0}
  .brand{display:flex;align-items:center;gap:12px;font-size:14px;font-weight:600;color:#AFBBD0}
  .logo{width:36px;height:36px;border-radius:11px;background:linear-gradient(150deg,#55F0C5,#1CAE87);display:grid;place-items:center;color:#03241B;font-weight:800;font-size:18px}
  h1{font-size:40px;letter-spacing:-.035em;margin:28px 0 8px}
  p.sub{color:#7F8DA3;margin:0 0 28px;font-size:14px}
  .grid{display:grid;grid-template-columns:repeat(3,1fr);gap:10px}
  .card{display:flex;flex-direction:column;gap:3px;padding:15px 16px;border-radius:12px;border:1px solid rgba(148,170,205,.12);background:#101622;text-decoration:none;color:#E8EEF9;transition:.15s}
  .card:hover{border-color:rgba(63,224,176,.42);background:#151D2B}
  .dot{width:8px;height:8px;border-radius:50%;margin-bottom:7px}
  .t{font-size:13px;font-weight:600}
  .h{font-size:10.5px;color:#7F8DA3}
  .hint{margin-top:28px;padding:12px 14px;border-radius:10px;border:1px dashed rgba(63,224,176,.22);background:rgba(63,224,176,.05);color:#8FCBB9;font-size:11.5px;line-height:1.55}
  .cap{margin-top:15px;color:#5A687E;font-size:10.5px}
</style>
</head>
<body>
  <div class="wrap">
    <div class="brand"><span class="logo">∿</span> FreeSoundPad · встроенный браузер</div>
    <h1>Куда зайдём?</h1>
    <p class="sub">Введите адрес или запрос в строке сверху — либо откройте одну из закладок.</p>
    <div class="grid">{{cards}}</div>
    <div class="hint">Звук этой вкладки захватывается отдельно от системы: системные звуки и другие приложения в микрофон не попадут.</div>
    <div class="cap">Захват включается автоматически · 48 kHz · stereo</div>
  </div>
</body>
</html>
""";
    }

    [RelayCommand]
    private void OpenExternal()
    {
        var url = _webView?.CoreWebView2?.Source ?? BrowserUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Не удалось открыть ссылку: {ex.Message}");
        }
    }

    [RelayCommand]
    private void AddCurrentBookmark()
    {
        if (_webView?.CoreWebView2 == null) return;

        string url;
        string title;
        try
        {
            url = _webView.CoreWebView2.Source;
            title = _webView.CoreWebView2.DocumentTitle;
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(url) || url == "about:blank") return;

        if (Bookmarks.Any(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("Эта страница уже в закладках");
            return;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            try
            {
                title = new Uri(url).Host;
            }
            catch
            {
                title = url;
            }
        }

        var bookmark = new BookmarkItem(this, new BookmarkData
        {
            Title = title,
            Url = url,
            Color = BookmarkColors[Bookmarks.Count % BookmarkColors.Length],
        });

        Bookmarks.Add(bookmark);
        Save();
        SetStatus($"Закладка добавлена: {title}");
    }

    public void NavigateBrowser(string url)
    {
        try
        {
            _webView?.CoreWebView2?.Navigate(url);
        }
        catch (Exception ex)
        {
            SetStatus($"Не удалось открыть страницу: {ex.Message}");
        }
    }

    public void NavigateBookmark(BookmarkItem bookmark) => NavigateBrowser(bookmark.Url);

    public void SetBrowserMuted(bool muted) => BrowserMuted = muted;

    public void SetBrowserSessionsMuted(bool muted) => AudioEngine.SetProcessSessionsMuted("msedgewebview2", muted);

    public void SetBrowserSessionsVolume(float volume) => AudioEngine.SetProcessSessionsVolume("msedgewebview2", volume);

    /// <summary>
    /// Keep the site quiet locally while it is being captured. Session volume scales the
    /// process-loopback signal, so the capture is boosted digitally to compensate.
    /// </summary>
    private void ApplyBrowserAudioMode()
    {
        var quiet = !BrowserMuted
                    && !OutputEndpointSilent
                    && BrowserCaptureEnabled
                    && _engine.BrowserCaptureActive
                    && (BrowserSend || (BrowserMonitor && CanMonitor));
        _engine.BrowserCaptureBoost = quiet ? 100f : 1f;
        AudioEngine.SetProcessSessionsVolume("msedgewebview2", quiet ? 0.01f : 1f);
        _browserQuietApplied = quiet;
    }

    private void SyncBrowserAudioMode()
    {
        var wantQuiet = !BrowserMuted
                        && !OutputEndpointSilent
                        && BrowserCaptureEnabled
                        && _engine.BrowserCaptureActive
                        && (BrowserSend || (BrowserMonitor && CanMonitor));
        if (!wantQuiet)
        {
            if (_browserQuietApplied) ApplyBrowserAudioMode();
            return;
        }

        if (WebLevel > 90 || !_browserQuietApplied)
        {
            ApplyBrowserAudioMode();
        }
    }

    private void OnBrowserContextMenuRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2ContextMenuRequestedEventArgs e)
    {
        e.Handled = true;

        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = _webView,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };

        var back = new System.Windows.Controls.MenuItem { Header = "Назад", Command = GoBackCommand, IsEnabled = CanGoBack };
        var forward = new System.Windows.Controls.MenuItem { Header = "Вперёд", Command = GoForwardCommand, IsEnabled = CanGoForward };
        var reload = new System.Windows.Controls.MenuItem { Header = "Обновить", Command = ReloadPageCommand };
        var home = new System.Windows.Controls.MenuItem { Header = "Домой", Command = GoHomeCommand };
        var bookmark = new System.Windows.Controls.MenuItem { Header = "Добавить в закладки", Command = AddCurrentBookmarkCommand };
        var external = new System.Windows.Controls.MenuItem { Header = "Открыть в системном браузере", Command = OpenExternalCommand };
        var copy = new System.Windows.Controls.MenuItem { Header = "Копировать адрес" };
        copy.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(BrowserUrl);
            }
            catch (Exception ex)
            {
                Log.Info($"Clipboard failed: {ex.Message}");
            }
        };

        menu.Items.Add(back);
        menu.Items.Add(forward);
        menu.Items.Add(reload);
        menu.Items.Add(home);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(bookmark);
        menu.Items.Add(copy);
        menu.Items.Add(external);

        menu.IsOpen = true;
    }

    partial void OnBrowserMutedChanged(bool value)
    {
        try
        {
            if (_webView?.CoreWebView2 != null) _webView.CoreWebView2.IsMuted = value;
        }
        catch (Exception ex)
        {
            Log.Info($"Browser mute failed: {ex.Message}");
        }
    }

    public void RemoveBookmark(BookmarkItem bookmark)
    {
        Bookmarks.Remove(bookmark);
        Save();
        SetStatus($"Закладка «{bookmark.Title}» удалена");
    }
}
