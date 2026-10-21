using System.Text.Json;
using System.Threading;
using System.Windows;
using FreeSoundPad.App.Services;
using FreeSoundPad.App.ViewModels;

namespace FreeSoundPad.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.Ensure();
        Log.Info("FreeSoundPad starting");

        _instanceMutex = new Mutex(initiallyOwned: true, "FreeSoundPad.SingleInstance", out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show("FreeSoundPad уже запущен. Найдите его в трее.", "FreeSoundPad",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled dispatcher exception", args.Exception);
            MessageBox.Show($"Произошла ошибка: {args.Exception.Message}", "FreeSoundPad",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                Log.Error("Unhandled domain exception", exception);
            }
        };

        SessionEnding += (_, _) =>
        {
            if (MainWindow?.DataContext is MainViewModel viewModel)
            {
                viewModel.PrepareSystemShutdown();
            }
        };

        var window = new MainWindow();
        MainWindow = window;

        if (e.Args.Length > 0)
        {
            window.Loaded += async (_, _) =>
            {
                if (window.DataContext is MainViewModel viewModel)
                {
                    await RunStartupArgumentsAsync(viewModel, e.Args);
                }
            };
        }

        window.Show();
    }

    private static async Task RunStartupArgumentsAsync(MainViewModel viewModel, string[] args)
    {
        try
        {
            if (args.Contains("--selftest"))
            {
                await RunSelfTestAsync(viewModel);
                return;
            }

            if (args.Contains("--mute")) viewModel.SetBrowserMuted(true);

            if (args.Contains("--tab"))
            {
                var index = Array.IndexOf(args, "--tab");
                if (index >= 0 && index + 1 < args.Length && args[index + 1].Equals("browser", StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.ActiveTab = AppTab.Browser;
                }
            }

            if (args.Contains("--url"))
            {
                var index = Array.IndexOf(args, "--url");
                if (index >= 0 && index + 1 < args.Length)
                {
                    for (var attempt = 0; attempt < 150 && !viewModel.IsBrowserReady; attempt++)
                    {
                        await Task.Delay(200);
                    }

                    if (viewModel.IsBrowserReady)
                    {
                        viewModel.NavigateBrowser(args[index + 1]);
                    }
                }
            }

            if (args.Contains("--settings")) viewModel.OpenSettingsCommand.Execute(null);
            if (args.Contains("--overlay")) viewModel.ToggleOverlayCommand.Execute(null);
        }
        catch (Exception ex)
        {
            Log.Error("Startup arguments failed", ex);
        }
    }

    private static async Task RunSelfTestAsync(MainViewModel viewModel)
    {
        var results = new Dictionary<string, object?>();

        try
        {
            for (var attempt = 0; attempt < 150 && !viewModel.IsBrowserReady; attempt++)
            {
                await Task.Delay(200);
            }

            results["browserReady"] = viewModel.IsBrowserReady;
            results["outputDevice"] = viewModel.OutputDeviceName;

            var page = Path.Combine(Path.GetTempPath(), "freesoundpad-selftest.html");
            await File.WriteAllTextAsync(page, """
                <!doctype html>
                <html><head><meta charset="utf-8"><title>FreeSoundPad selftest tone</title></head>
                <body style="background:#06080C;color:#E8EEF9;font-family:Segoe UI">
                <h2>FreeSoundPad self-test tone</h2>
                <script>
                  const ctx = new AudioContext();
                  const osc = ctx.createOscillator();
                  const gain = ctx.createGain();
                  gain.gain.value = 0.25;
                  osc.frequency.value = 440;
                  osc.connect(gain).connect(ctx.destination);
                  osc.start();
                  ctx.resume();
                </script>
                </body></html>
                """);

            viewModel.ActiveTab = AppTab.Browser;
            viewModel.SetBrowserMuted(false);
            await Task.Delay(700);
            viewModel.NavigateBrowser(new Uri(page).AbsoluteUri);

            await Task.Delay(3500);
            results["captureActive"] = viewModel.BrowserCaptureActive;
            results["captureStatus"] = viewModel.BrowserStatusText;
            results["captureDetail"] = viewModel.BrowserCaptureDetail;
            results["processId"] = viewModel.BrowserProcessId.ToString();

            double webPeak = 0;
            for (var i = 0; i < 40; i++)
            {
                webPeak = Math.Max(webPeak, viewModel.WebLevel);
                await Task.Delay(100);
            }

            results["browserPeakPercent"] = Math.Round(webPeak, 1);

            // Browser audio must keep playing (and being captured) when the user switches to the sounds tab.
            viewModel.ActiveTab = AppTab.Sounds;
            await Task.Delay(1200);
            double hiddenPeak = 0;
            for (var i = 0; i < 30; i++)
            {
                hiddenPeak = Math.Max(hiddenPeak, viewModel.WebLevel);
                await Task.Delay(100);
            }

            results["hiddenBrowserPeakPercent"] = Math.Round(hiddenPeak, 1);
            results["captureActiveWhenHidden"] = viewModel.BrowserCaptureActive;

            // Does muting the WebView2 (so the site is not heard directly) keep process loopback capture alive?
            viewModel.SetBrowserMuted(true);
            await Task.Delay(1000);
            double mutedPeak = 0;
            for (var i = 0; i < 15; i++)
            {
                mutedPeak = Math.Max(mutedPeak, viewModel.WebLevel);
                await Task.Delay(100);
            }

            results["mutedBrowserPeakPercent"] = Math.Round(mutedPeak, 1);
            viewModel.SetBrowserMuted(false);
            await Task.Delay(500);

            // Alternative: mute the WebView2 audio sessions (endpoint mute) instead of CoreWebView2.IsMuted.
            viewModel.SetBrowserSessionsMuted(true);
            await Task.Delay(1000);
            double sessionMutedPeak = 0;
            for (var i = 0; i < 15; i++)
            {
                sessionMutedPeak = Math.Max(sessionMutedPeak, viewModel.WebLevel);
                await Task.Delay(100);
            }

            results["sessionMutedBrowserPeakPercent"] = Math.Round(sessionMutedPeak, 1);
            viewModel.SetBrowserSessionsMuted(false);

            // Quiet local playback, loud capture? Test whether session volume scales the process-loopback signal.
            viewModel.SetBrowserSessionsVolume(0.02f);
            await Task.Delay(1000);
            double quietPeak = 0;
            for (var i = 0; i < 15; i++)
            {
                quietPeak = Math.Max(quietPeak, viewModel.WebLevel);
                await Task.Delay(100);
            }

            results["quietBrowserPeakPercent"] = Math.Round(quietPeak, 1);
            viewModel.SetBrowserSessionsVolume(1f);
            await Task.Delay(500);

            var pad = viewModel.Pads.FirstOrDefault();
            if (pad != null)
            {
                viewModel.PlayPad(pad);
                double padPeak = 0;
                double outputPeak = 0;
                for (var i = 0; i < 30; i++)
                {
                    padPeak = Math.Max(padPeak, viewModel.PadLevel);
                    outputPeak = Math.Max(outputPeak, viewModel.OutputLevel);
                    await Task.Delay(100);
                }

                results["padPeakPercent"] = Math.Round(padPeak, 1);
                results["outputPeakPercent"] = Math.Round(outputPeak, 1);
                viewModel.StopEverything();
            }

            results["ok"] = webPeak > 2 && viewModel.BrowserCaptureActive && hiddenPeak > 2;
        }
        catch (Exception ex)
        {
            results["error"] = ex.ToString();
        }

        var path = Path.Combine(Path.GetTempPath(), "freesoundpad-selftest.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(results,
            new JsonSerializerOptions { WriteIndented = true }));
        Log.Info($"Self-test finished: {path}");
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            (MainWindow?.DataContext as MainViewModel)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Dispose on exit failed", ex);
        }

        try
        {
            if (_ownsMutex) _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch
        {
            // ignore
        }

        base.OnExit(e);
    }
}
