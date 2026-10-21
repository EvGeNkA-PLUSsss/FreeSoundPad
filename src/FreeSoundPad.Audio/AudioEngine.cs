using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FreeSoundPad.Audio;

/// <summary>
/// Owns the whole audio path: pads + microphone + browser into a virtual output (cable),
/// with an optional monitor output for headphones.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enumerator = new();

    private readonly SampleRingBuffer _micBus = new(SampleRate * Channels * 3);
    private readonly SampleRingBuffer _browserBus = new(SampleRate * Channels * 2);
    private readonly SampleRingBuffer _monitorBrowserBus = new(SampleRate * Channels * 2);
    private readonly PadMixer _padMixer = new();
    private readonly PadMixer _monitorPadMixer = new();

    private readonly MixProvider _mainProvider;
    private readonly MixProvider _monitorProvider;

    private WasapiOut? _output;
    private WasapiOut? _monitorOutput;
    private BufferedWaveProvider? _mainBuffer;
    private BufferedWaveProvider? _monitorBuffer;
    private Thread? _pumpThread;
    private volatile bool _pumpRunning;
    private MicCapture? _micCapture;
    private ProcessLoopbackCapture? _browserCapture;
    private AudioClip? _testTone;

    private const int PumpBlockFrames = 480; // 10 ms at 48 kHz
    private const int PumpTargetMilliseconds = 150;
    private const int PumpBufferMilliseconds = 500;

    private string? _outputDeviceId;
    private string? _micDeviceId;
    private string? _monitorDeviceId;
    private bool _monitorEnabled;

    private float _masterGain = 0.9f;
    private float _micGain = 0.8f;
    private float _padGain = 0.9f;
    private float _browserGain = 0.8f;
    private float _monitorGain = 0.8f;

    public AudioEngine()
    {
        _mainProvider = new MixProvider(this, isMonitor: false);
        _monitorProvider = new MixProvider(this, isMonitor: true);
    }

    public LevelMeter OutputMeter { get; } = new();
    public LevelMeter MonitorMeter { get; } = new();
    public LevelMeter MicMeter { get; } = new();
    public LevelMeter PadMeter { get; } = new();
    public LevelMeter BrowserMeter { get; } = new();

    public bool IsRunning => _output != null;

    /// <summary>Peak reported by the output endpoint itself (-1 when unavailable).</summary>
    public float OutputEndpointPeak
    {
        get
        {
            if (_outputDeviceId == null) return -1f;
            try
            {
                using var device = _enumerator.GetDevice(_outputDeviceId);
                return device.AudioMeterInformation.MasterPeakValue;
            }
            catch
            {
                return -1f;
            }
        }
    }
    public bool MonitorEnabled => _monitorEnabled;
    public string? OutputDeviceId => _outputDeviceId;
    public string? MicDeviceId => _micDeviceId;
    public string? MonitorDeviceId => _monitorDeviceId;

    public bool MicPassthrough { get; set; } = true;
    public bool BrowserSend { get; set; } = true;
    public bool BrowserMonitor { get; set; }

    /// <summary>Digital boost for the captured browser bus (used when the site is kept quiet locally).</summary>
    public float BrowserCaptureBoost { get; set; } = 1f;

    public bool BrowserCaptureActive => _browserCapture?.IsRunning == true;
    public uint BrowserProcessId => _browserCapture?.ProcessId ?? 0;
    public string? BrowserCaptureError => _browserCapture?.LastError;
    public bool MonitorOutputActive => _monitorOutput != null;

    public float MasterGain
    {
        get => _masterGain;
        set => _masterGain = Math.Clamp(value, 0f, 2f);
    }

    public float MicGain
    {
        get => _micGain;
        set => _micGain = Math.Clamp(value, 0f, 2f);
    }

    public float PadGain
    {
        get => _padGain;
        set => _padGain = Math.Clamp(value, 0f, 2f);
    }

    public float BrowserGain
    {
        get => _browserGain;
        set => _browserGain = Math.Clamp(value, 0f, 2f);
    }

    public float MonitorGain
    {
        get => _monitorGain;
        set => _monitorGain = Math.Clamp(value, 0f, 2f);
    }

    public event EventHandler<string>? Error;
    public event EventHandler? StateChanged;
    public event EventHandler<string>? BrowserCaptureStopped;

    // ---------------------------------------------------------------- output

    /// <summary>
    /// Mute/unmute every audio session of processes with the given name.
    /// Used to silence the site locally while process-loopback capture keeps running.
    /// </summary>
    public static void SetProcessSessionsMuted(string processName, bool mute)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        try
                        {
                            var process = System.Diagnostics.Process.GetProcessById((int)session.GetProcessID);
                            if (!process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)) continue;
                            session.SimpleAudioVolume.Mute = mute;
                        }
                        catch
                        {
                            // process exited / session unavailable
                        }
                    }
                }
                catch
                {
                    // endpoint unavailable
                }
            }
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Set the volume of every audio session of processes with the given name.</summary>
    public static void SetProcessSessionsVolume(string processName, float volume)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        try
                        {
                            var process = System.Diagnostics.Process.GetProcessById((int)session.GetProcessID);
                            if (!process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)) continue;
                            session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0f, 1f);
                        }
                        catch
                        {
                            // process exited / session unavailable
                        }
                    }
                }
                catch
                {
                    // endpoint unavailable
                }
            }
        }
        catch
        {
            // best effort
        }
    }

    public bool Start(string outputDeviceId)
    {
        lock (_gate)
        {
            StopOutputLocked();

            if (!OpenOutputLocked(outputDeviceId)) return false;

            if (_monitorEnabled && !string.IsNullOrEmpty(_monitorDeviceId))
            {
                OpenMonitorLocked(_monitorDeviceId!);
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
    }

    public bool SetOutputDevice(string outputDeviceId)
    {
        lock (_gate)
        {
            var wasRunning = _output != null;
            StopOutputLocked();
            _outputDeviceId = outputDeviceId;

            if (!wasRunning)
            {
                if (_monitorEnabled)
                {
                    OpenMonitorLockedIfNeeded();
                }

                return true;
            }

            return OpenOutputLocked(outputDeviceId);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopOutputLocked();
            StopMonitorLocked();
            StopMicLocked();
            StopBrowserCaptureLocked();
        }

        ResetMeters();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool OpenOutputLocked(string deviceId)
    {
        try
        {
            var device = _enumerator.GetDevice(deviceId);
            var buffer = CreateRenderBuffer();
            _mainBuffer = buffer;
            PrefillRenderBuffer(buffer, _mainProvider);
            var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, 30);
            output.Init(buffer);
            output.Play();
            _output = output;
            _outputDeviceId = deviceId;
            StartPumpLocked();

            if (_monitorEnabled)
            {
                OpenMonitorLockedIfNeeded();
            }

            return true;
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Не удалось открыть устройство вывода: {ex.Message}");
            return false;
        }
    }

    private static BufferedWaveProvider CreateRenderBuffer()
    {
        return new BufferedWaveProvider(new WaveFormat(SampleRate, 16, Channels))
        {
            BufferDuration = TimeSpan.FromMilliseconds(PumpBufferMilliseconds),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
    }

    /// <summary>
    /// Fill the render buffer before playback starts. Without this the first WASAPI read
    /// arrives before the pump has produced data and BufferedWaveProvider pads it with silence.
    /// </summary>
    private static void PrefillRenderBuffer(BufferedWaveProvider buffer, MixProvider provider)
    {
        var mix = new float[PumpBlockFrames * Channels];
        var bytes = new byte[PumpBlockFrames * Channels * 2];
        var target = TimeSpan.FromMilliseconds(PumpTargetMilliseconds);

        while (buffer.BufferedDuration < target)
        {
            provider.Read(mix, 0, mix.Length);
            ConvertToPcm16(mix, bytes, mix.Length);
            buffer.AddSamples(bytes, 0, bytes.Length);
        }
    }

    private void StartPumpLocked()
    {
        if (_pumpThread != null) return;

        _pumpRunning = true;
        _pumpThread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "FreeSoundPad audio pump",
        };
        _pumpThread.Start();
    }

    private void StopPumpLocked()
    {
        _pumpRunning = false;
        var thread = _pumpThread;
        _pumpThread = null;
        try
        {
            thread?.Join(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // ignore
        }
    }

    private void PumpLoop()
    {
        var mix = new float[PumpBlockFrames * Channels];
        var bytes = new byte[PumpBlockFrames * Channels * 2];
        var target = TimeSpan.FromMilliseconds(PumpTargetMilliseconds);

        while (_pumpRunning)
        {
            var produced = false;

            var main = _mainBuffer;
            if (main != null && main.BufferedDuration < target)
            {
                for (var block = 0; block < 6 && main.BufferedDuration < target; block++)
                {
                    _mainProvider.Read(mix, 0, mix.Length);
                    ConvertToPcm16(mix, bytes, mix.Length);
                    main.AddSamples(bytes, 0, bytes.Length);
                }
                produced = true;
            }

            var monitor = _monitorBuffer;
            if (monitor != null && monitor.BufferedDuration < target)
            {
                for (var block = 0; block < 6 && monitor.BufferedDuration < target; block++)
                {
                    _monitorProvider.Read(mix, 0, mix.Length);
                    ConvertToPcm16(mix, bytes, mix.Length);
                    monitor.AddSamples(bytes, 0, bytes.Length);
                }
                produced = true;
            }

            Thread.Sleep(produced ? 2 : 4);
        }
    }

    private static void ConvertToPcm16(float[] source, byte[] destination, int samples)
    {
        for (var i = 0; i < samples; i++)
        {
            var value = (int)MathF.Round(Math.Clamp(source[i], -1f, 1f) * 32767f);
            destination[i * 2] = (byte)(value & 0xFF);
            destination[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
    }

    private void StopOutputLocked()
    {
        var output = _output;
        _output = null;
        _mainBuffer = null;

        if (output == null)
        {
            if (_monitorBuffer == null) StopPumpLocked();
            return;
        }

        try
        {
            output.Stop();
        }
        catch
        {
            // ignore
        }

        output.Dispose();
        OutputMeter.Reset();

        if (_monitorBuffer == null) StopPumpLocked();
    }

    // ------------------------------------------------------------- monitoring

    public void SetMonitor(string? deviceId, bool enabled)
    {
        lock (_gate)
        {
            _monitorEnabled = enabled;
            _monitorDeviceId = deviceId;
            StopMonitorLocked();
            _monitorBrowserBus.Clear();

            if (enabled && !string.IsNullOrEmpty(deviceId)) OpenMonitorLockedIfNeeded();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OpenMonitorLocked(string deviceId)
    {
        try
        {
            var device = _enumerator.GetDevice(deviceId);
            var buffer = CreateRenderBuffer();
            _monitorBuffer = buffer;
            PrefillRenderBuffer(buffer, _monitorProvider);
            var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, 40);
            output.Init(buffer);
            output.Play();
            _monitorOutput = output;
            _monitorDeviceId = deviceId;
            StartPumpLocked();
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Не удалось открыть устройство мониторинга: {ex.Message}");
        }
    }

    private void OpenMonitorLockedIfNeeded()
    {
        if (_monitorOutput == null && !string.IsNullOrEmpty(_monitorDeviceId))
        {
            OpenMonitorLocked(_monitorDeviceId!);
        }
    }

    private void StopMonitorLocked()
    {
        var output = _monitorOutput;
        _monitorOutput = null;
        _monitorBuffer = null;

        if (output != null)
        {
            try
            {
                output.Stop();
            }
            catch
            {
                // ignore
            }

            output.Dispose();
            MonitorMeter.Reset();
        }

        if (_mainBuffer == null) StopPumpLocked();
    }

    // ------------------------------------------------------------- microphone

    public bool SetMicDevice(string? deviceId)
    {
        lock (_gate)
        {
            StopMicLocked();
            _micDeviceId = deviceId;
            if (string.IsNullOrEmpty(deviceId)) return true;

            try
            {
                var device = _enumerator.GetDevice(deviceId);
                var capture = new MicCapture(device, _micBus);
                capture.Start();
                if (capture.LastError != null)
                {
                    Error?.Invoke(this, $"Не удалось открыть микрофон: {capture.LastError}");
                    capture.Dispose();
                    return false;
                }

                _micCapture = capture;
                return true;
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"Не удалось открыть микрофон: {ex.Message}");
                return false;
            }
        }
    }

    private void StopMicLocked()
    {
        var capture = _micCapture;
        _micCapture = null;
        if (capture == null) return;

        try
        {
            capture.Stop();
            capture.Dispose();
        }
        catch
        {
            // ignore
        }

        _micBus.Clear();
        MicMeter.Reset();
    }

    // -------------------------------------------------------- browser capture

    public bool StartBrowserCapture(uint processId)
    {
        lock (_gate)
        {
            StopBrowserCaptureLocked();
            if (processId == 0) return false;

            _browserBus.Clear();
            _monitorBrowserBus.Clear();

            var capture = new ProcessLoopbackCapture(processId, _browserBus, _monitorBrowserBus);
            capture.CaptureStopped += OnBrowserCaptureStopped;

            if (!capture.Start())
            {
                var message = capture.LastError ?? "неизвестная ошибка";
                capture.CaptureStopped -= OnBrowserCaptureStopped;
                capture.Dispose();
                Error?.Invoke(this, message);
                return false;
            }

            _browserCapture = capture;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
    }

    public void StopBrowserCapture()
    {
        lock (_gate)
        {
            StopBrowserCaptureLocked();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StopBrowserCaptureLocked()
    {
        var capture = _browserCapture;
        _browserCapture = null;
        if (capture == null) return;

        capture.CaptureStopped -= OnBrowserCaptureStopped;
        try
        {
            capture.Dispose();
        }
        catch
        {
            // ignore
        }

        _browserBus.Clear();
        _monitorBrowserBus.Clear();
        BrowserMeter.Reset();
    }

    private void OnBrowserCaptureStopped(object? sender, string message)
    {
        bool expected;
        lock (_gate)
        {
            expected = ReferenceEquals(_browserCapture, sender);
            if (expected) _browserCapture = null;
        }

        if (expected)
        {
            BrowserCaptureStopped?.Invoke(this, message);
        }
    }

    // ------------------------------------------------------------- playback

    public void PlayClip(AudioClip clip, string tag, float gain = 1f, bool loop = false)
    {
        _padMixer.Add(new PadVoice(clip, tag, gain, loop));
        if (_monitorEnabled && _monitorOutput != null)
        {
            _monitorPadMixer.Add(new PadVoice(clip, tag, gain, loop));
        }
    }

    public void StopAll()
    {
        _padMixer.StopAll();
        _monitorPadMixer.StopAll();
    }

    public void StopTag(string tag)
    {
        _padMixer.StopTag(tag);
        _monitorPadMixer.StopTag(tag);
    }

    public bool IsPlaying(string tag) => _padMixer.AnyPlaying(tag);

    public double GetProgress(string tag) => _padMixer.Progress(tag);

    public int ActivePadVoices => _padMixer.ActiveCount;

    /// <summary>Diagnostic tap: receives exactly the main-mix samples that go to WASAPI.</summary>
    public Action<float[], int, int>? MixTap { get; set; }

    /// <summary>Renders the main mix offline (no output device). Used by diagnostics.</summary>
    public int ReadMixed(float[] buffer, int offset, int count) => _mainProvider.Read(buffer, offset, count);

    /// <summary>Renders a few blocks through the mixer without a device. Used by diagnostics/tests.</summary>
    public float RenderPreviewPeak(int blocks = 8)
    {
        var buffer = new float[480 * Channels];
        var peak = 0f;
        for (var block = 0; block < blocks; block++)
        {
            _mainProvider.Read(buffer, 0, buffer.Length);
            foreach (var sample in buffer)
            {
                var absolute = MathF.Abs(sample);
                if (absolute > peak) peak = absolute;
            }
        }

        return peak;
    }

    public void PlayTestTone()
    {
        _testTone ??= BuildTestTone();
        PlayClip(_testTone, "__test", 0.6f);
    }

    private static AudioClip BuildTestTone()
    {
        const int seconds = 2;
        var frames = SampleRate * seconds;
        var samples = new float[frames * Channels];

        for (var frame = 0; frame < frames; frame++)
        {
            var t = (double)frame / SampleRate;
            var envelope = Math.Min(1.0, Math.Min(t / 0.02, (seconds - t) / 0.08));
            var value = (float)(Math.Sin(2 * Math.PI * 440 * t) * 0.35 * envelope);
            samples[frame * 2] = value;
            samples[frame * 2 + 1] = value;
        }

        var peaks = Enumerable.Repeat(0.35f, 200).ToArray();
        return new AudioClip("__test_tone__", samples, frames, peaks);
    }

    // ---------------------------------------------------------------- meters

    public void ResetMeters()
    {
        OutputMeter.Reset();
        MonitorMeter.Reset();
        MicMeter.Reset();
        PadMeter.Reset();
        BrowserMeter.Reset();
    }

    private void EnsureMonitorRunningIfNeeded()
    {
        if (_monitorEnabled && _monitorOutput == null) OpenMonitorLockedIfNeeded();
    }

    // ---------------------------------------------------------------- engine

    private sealed class MixProvider : ISampleProvider
    {
        private readonly AudioEngine _engine;
        private readonly bool _isMonitor;
        private float[] _scratchMic = new float[1024];
        private float[] _scratchBrowser = new float[1024];

        public MixProvider(AudioEngine engine, bool isMonitor)
        {
            _engine = engine;
            _isMonitor = isMonitor;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            if (count <= 0) return 0;

            if (_scratchMic.Length < count) _scratchMic = new float[count];
            if (_scratchBrowser.Length < count) _scratchBrowser = new float[count];

            Array.Clear(buffer, offset, count);

            if (!_isMonitor)
            {
                _engine._padMixer.MixAdd(buffer, offset, count, _engine.PadGain);
                _engine.PadMeter.Update(buffer, offset, count);

                _engine._micBus.Read(_scratchMic.AsSpan(0, count));
                _engine.MicMeter.Update(_scratchMic, 0, count);
                if (_engine.MicPassthrough) AddScaled(buffer, offset, _scratchMic, count, _engine.MicGain);

                _engine._browserBus.Read(_scratchBrowser.AsSpan(0, count));
                if (_engine.BrowserCaptureBoost != 1f)
                {
                    ApplyGain(_scratchBrowser, 0, count, _engine.BrowserCaptureBoost);
                }

                _engine.BrowserMeter.Update(_scratchBrowser, 0, count);
                if (_engine.BrowserSend) AddScaled(buffer, offset, _scratchBrowser, count, _engine.BrowserGain);

                ApplyGain(buffer, offset, count, _engine.MasterGain);
                SoftLimiter.Process(buffer.AsSpan(offset, count));
                _engine.OutputMeter.Update(buffer, offset, count);
                _engine.MixTap?.Invoke(buffer, offset, count);
            }
            else
            {
                _engine._monitorPadMixer.MixAdd(buffer, offset, count, _engine.PadGain);

                _engine._monitorBrowserBus.Read(_scratchBrowser.AsSpan(0, count));
                if (_engine.BrowserCaptureBoost != 1f)
                {
                    ApplyGain(_scratchBrowser, 0, count, _engine.BrowserCaptureBoost);
                }

                if (_engine.BrowserMonitor) AddScaled(buffer, offset, _scratchBrowser, count, _engine.BrowserGain);

                ApplyGain(buffer, offset, count, _engine.MonitorGain);
                SoftLimiter.Process(buffer.AsSpan(offset, count));
                _engine.MonitorMeter.Update(buffer, offset, count);
            }

            return count;
        }

        private static void AddScaled(float[] destination, int offset, float[] source, int count, float gain)
        {
            for (var i = 0; i < count; i++)
            {
                destination[offset + i] += source[i] * gain;
            }
        }

        private static void ApplyGain(float[] buffer, int offset, int count, float gain)
        {
            if (Math.Abs(gain - 1f) < 0.0001f) return;
            for (var i = 0; i < count; i++)
            {
                buffer[offset + i] *= gain;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            Stop();
        }
        catch
        {
            // ignore
        }

        _enumerator.Dispose();
    }
}
