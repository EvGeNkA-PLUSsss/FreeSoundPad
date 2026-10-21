using FreeSoundPad.Audio;
using NAudio.Wave;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    switch (args[0])
    {
        case "loopback":
            return LoopbackTest(args);
        case "play":
            return await PlayTest(args);
        case "directplay":
            return await DirectPlayTest(args);
        case "sysloop":
            return await SystemLoopbackTest(args);
        case "capdev":
            return await CaptureDeviceTest(args);
        case "tone":
            return await ToneTest(args);
        case "offline":
            return OfflineRender(args);
        case "clipplay":
            return await ClipPlayTest(args);
        case "playmonitor":
            return await PlayMonitorTest(args);
        case "devices":
            PrintDevices();
            return 0;
        case "sessions":
            PrintSessions();
            return 0;
        case "volumes":
            PrintVolumes();
            return 0;
        default:
            PrintUsage();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED: {ex}");
    return 2;
}

static void PrintVolumes()
{
    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    foreach (var flow in new[] { NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DataFlow.Capture })
    {
        Console.WriteLine($"=== {flow} ===");
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, NAudio.CoreAudioApi.DeviceState.Active))
        {
            try
            {
                Console.WriteLine($"  {device.FriendlyName}: volume={device.AudioEndpointVolume.MasterVolumeLevelScalar:F2} mute={device.AudioEndpointVolume.Mute}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {device.FriendlyName}: {ex.Message}");
            }
        }
    }
}

static void PrintSessions()
{
    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    foreach (var device in enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
    {
        Console.WriteLine($"=== {device.FriendlyName} ===");
        try
        {
            var sessions = device.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                var volume = -1f;
                var muted = false;
                try
                {
                    volume = session.SimpleAudioVolume.Volume;
                    muted = session.SimpleAudioVolume.Mute;
                }
                catch
                {
                    // ignore
                }

                Console.WriteLine($"  pid={session.GetProcessID} state={session.State} volume={volume:F2} muted={muted} name={session.DisplayName}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  err: " + ex.Message);
        }
    }
}

static void PrintDevices()
{
    Console.WriteLine("=== ВЫХОД (render) ===");
    foreach (var device in AudioDeviceService.GetRenderDevices())
    {
        Console.WriteLine($"{device.DisplayName,-60} default={device.IsDefault,-5} virtual={device.IsVirtual,-5} {device.Id}");
    }

    Console.WriteLine();
    Console.WriteLine("=== ВХОД (capture) ===");
    foreach (var device in AudioDeviceService.GetCaptureDevices())
    {
        Console.WriteLine($"{device.DisplayName,-60} default={device.IsDefault,-5} virtual={device.IsVirtual,-5} {device.Id}");
    }
}

static void PrintUsage()
{
    Console.WriteLine("""
        FreeSoundPad.AudioProbe — консольный стенд для проверки аудио.

          loopback <pid> <seconds> <out.wav>   захватить звук процесса (process loopback) в WAV
          play <device-substring> <file.mp3>   сыграть файл в выбранное устройство вывода
          devices                              список устройств
        """);
}

static int LoopbackTest(string[] args)
{
    if (args.Length < 4)
    {
        PrintUsage();
        return 1;
    }

    var pid = uint.Parse(args[1]);
    var seconds = int.Parse(args[2]);
    var output = Path.GetFullPath(args[3]);
    var bus = new SampleRingBuffer(AudioClip.SampleRate * AudioClip.Channels * 2 * 4);
    using var capture = new ProcessLoopbackCapture(pid, bus);
    capture.CaptureStopped += (_, message) => Console.WriteLine($"capture stopped: {message}");

    Console.WriteLine($"capturing PID {pid} for {seconds}s -> {output}");
    if (!capture.Start())
    {
        Console.Error.WriteLine($"start failed: {capture.LastError}");
        return 3;
    }

    var samples = new List<float>(AudioClip.SampleRate * seconds * AudioClip.Channels);
    var block = new float[4800 * AudioClip.Channels];
    var started = DateTime.UtcNow;
    var deadline = started.AddSeconds(seconds);

    while (DateTime.UtcNow < deadline)
    {
        Thread.Sleep(10);
        while (bus.Available > 0)
        {
            var read = bus.Read(block.AsSpan());
            if (read <= 0) break;
            samples.AddRange(block.AsSpan(0, read));
        }
    }

    capture.Stop();

    var totalFrames = samples.Count / AudioClip.Channels;
    Console.WriteLine($"captured {totalFrames} frames ({totalFrames / (double)AudioClip.SampleRate:F2}s) in {(DateTime.UtcNow - started).TotalSeconds:F2}s realtime");

    WriteWav(output, samples, AudioClip.SampleRate, AudioClip.Channels);
    Console.WriteLine($"written {new FileInfo(output).Length} bytes");
    return 0;
}

static async Task<int> SystemLoopbackTest(string[] args)
{
    if (args.Length < 4)
    {
        PrintUsage();
        return 1;
    }

    var deviceSubstring = args[1];
    var seconds = int.Parse(args[2]);
    var output = Path.GetFullPath(args[3]);

    var deviceInfo = AudioDeviceService.GetRenderDevices()
        .FirstOrDefault(d => d.Name.Contains(deviceSubstring, StringComparison.OrdinalIgnoreCase));
    if (deviceInfo == null)
    {
        Console.Error.WriteLine("device not found");
        return 4;
    }

    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var device = enumerator.GetDevice(deviceInfo.Id);
    using var capture = new NAudio.Wave.WasapiLoopbackCapture(device);
    using var writer = new NAudio.Wave.WaveFileWriter(output, capture.WaveFormat);
    capture.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);

    Console.WriteLine($"system loopback {deviceInfo.Name}, format {capture.WaveFormat}");
    capture.StartRecording();
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    capture.StopRecording();
    writer.Flush();
    Console.WriteLine($"written {new FileInfo(output).Length} bytes");
    return 0;
}

static async Task<int> CaptureDeviceTest(string[] args)
{
    if (args.Length < 4 || !int.TryParse(args[2], out var seconds))
    {
        PrintUsage();
        return 1;
    }

    var deviceInfo = AudioDeviceService.GetCaptureDevices()
        .FirstOrDefault(d => d.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase));
    if (deviceInfo == null)
    {
        Console.Error.WriteLine("capture device not found");
        return 4;
    }

    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var device = enumerator.GetDevice(deviceInfo.Id);
    using var capture = new NAudio.CoreAudioApi.WasapiCapture(device);
    using var writer = new NAudio.Wave.WaveFileWriter(Path.GetFullPath(args[3]), capture.WaveFormat);
    capture.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);

    Console.WriteLine($"capture {deviceInfo.Name}, format {capture.WaveFormat}");
    capture.StartRecording();
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    capture.StopRecording();
    writer.Flush();
    Console.WriteLine($"written {new FileInfo(args[3]).Length} bytes");
    return 0;
}

static int OfflineRender(string[] args)
{
    if (args.Length < 3)
    {
        PrintUsage();
        return 1;
    }

    var file = Path.GetFullPath(args[1]);
    var output = Path.GetFullPath(args[2]);
    var clip = AudioClipLoader.Load(file);

    using var engine = new AudioEngine();
    engine.PlayClip(clip, "probe");

    using var writer = new WaveFileWriter(output, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
    var buffer = new float[480 * 2];
    var totalFrames = clip.Frames + 48000;
    var framesWritten = 0;
    var first = true;
    while (framesWritten < totalFrames)
    {
        var framesToRead = first ? 1440 : 480;
        first = false;
        var samples = framesToRead * 2;
        if (buffer.Length < samples) buffer = new float[samples];
        engine.ReadMixed(buffer, 0, samples);
        var bytes = new byte[samples * 4];
        Buffer.BlockCopy(buffer, 0, bytes, 0, bytes.Length);
        writer.Write(bytes, 0, bytes.Length);
        framesWritten += framesToRead;
    }

    writer.Flush();
    Console.WriteLine($"offline render written: {output}");
    return 0;
}

static async Task<int> PlayMonitorTest(string[] args)
{
    if (args.Length < 4)
    {
        PrintUsage();
        return 1;
    }

    var devices = AudioDeviceService.GetRenderDevices();
    var output = devices.FirstOrDefault(d => d.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase));
    var monitor = devices.FirstOrDefault(d => d.Name.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    if (output == null || monitor == null)
    {
        Console.Error.WriteLine("device not found");
        return 4;
    }

    var clip = AudioClipLoader.Load(Path.GetFullPath(args[3]));
    using var engine = new AudioEngine();
    engine.Error += (_, message) => Console.Error.WriteLine("engine error: " + message);
    engine.SetMonitor(monitor.Id, true);
    if (!engine.Start(output.Id)) return 5;
    engine.PlayClip(clip, "probe");
    await Task.Delay(clip.Duration + TimeSpan.FromSeconds(1));
    return 0;
}

static async Task<int> ClipPlayTest(string[] args)
{
    if (args.Length < 3)
    {
        PrintUsage();
        return 1;
    }

    var deviceSubstring = args[1];
    var file = Path.GetFullPath(args[2]);
    var deviceInfo = AudioDeviceService.GetRenderDevices()
        .FirstOrDefault(d => d.Name.Contains(deviceSubstring, StringComparison.OrdinalIgnoreCase));
    if (deviceInfo == null)
    {
        Console.Error.WriteLine("device not found");
        return 4;
    }

    var clip = AudioClipLoader.Load(file);
    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var mmDevice = enumerator.GetDevice(deviceInfo.Id);
    var provider = new ClipSampleProvider(clip, 0.81f);
    using var output = new WasapiOut(mmDevice, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 30);
    output.Init(provider);
    output.Play();
    Console.WriteLine($"clip play {file} -> {deviceInfo.Name}");
    await Task.Delay(clip.Duration + TimeSpan.FromMilliseconds(500));
    return 0;
}

static async Task<int> ToneTest(string[] args)
{
    if (args.Length < 3)
    {
        PrintUsage();
        return 1;
    }

    var deviceSubstring = args[1];
    var seconds = double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
    var amplitude = args.Length > 3
        ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)
        : 0.1;

    var deviceInfo = AudioDeviceService.GetRenderDevices()
        .FirstOrDefault(d => d.Name.Contains(deviceSubstring, StringComparison.OrdinalIgnoreCase));
    if (deviceInfo == null)
    {
        Console.Error.WriteLine("device not found");
        return 4;
    }

    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var mmDevice = enumerator.GetDevice(deviceInfo.Id);
    var generator = new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2)
    {
        Frequency = 1000,
        Gain = amplitude,
        Type = NAudio.Wave.SampleProviders.SignalGeneratorType.Sin,
    };

    using var output = new NAudio.Wave.WasapiOut(mmDevice, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 30);
    NAudio.Wave.ISampleProvider provider = generator;
    output.Init(provider);
    output.Play();
    Console.WriteLine($"tone 1000 Hz amplitude {amplitude} -> {deviceInfo.Name}");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    return 0;
}

static async Task<int> DirectPlayTest(string[] args)
{
    if (args.Length < 3)
    {
        PrintUsage();
        return 1;
    }

    var deviceSubstring = args[1];
    var file = Path.GetFullPath(args[2]);

    var device = AudioDeviceService.GetRenderDevices()
        .FirstOrDefault(d => d.Name.Contains(deviceSubstring, StringComparison.OrdinalIgnoreCase));
    if (device == null)
    {
        Console.Error.WriteLine("device not found");
        return 4;
    }

    using var reader = new NAudio.Wave.MediaFoundationReader(file);
    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var mmDevice = enumerator.GetDevice(device.Id);
    using var output = new NAudio.Wave.WasapiOut(
        mmDevice,
        NAudio.CoreAudioApi.AudioClientShareMode.Shared,
        true,
        30);
    output.Init(reader);
    output.Play();
    Console.WriteLine($"direct play {file} -> {device.Name}, {reader.TotalTime.TotalSeconds:F2}s");
    await Task.Delay(reader.TotalTime + TimeSpan.FromMilliseconds(500));
    return 0;
}

static async Task<int> PlayTest(string[] args)
{
    if (args.Length < 3)
    {
        PrintUsage();
        return 1;
    }

    var deviceSubstring = args[1];
    var file = Path.GetFullPath(args[2]);

    var devices = AudioDeviceService.GetRenderDevices();
    var device = devices.FirstOrDefault(d =>
        d.Name.Contains(deviceSubstring, StringComparison.OrdinalIgnoreCase));
    if (device == null)
    {
        Console.Error.WriteLine("device not found. available:");
        foreach (var d in devices) Console.Error.WriteLine($"  {d.Name}  [{d.Id}]");
        return 4;
    }

    var clip = AudioClipLoader.Load(file);
    var clipPeak = 0f;
    foreach (var sample in clip.Samples)
    {
        var absolute = MathF.Abs(sample);
        if (absolute > clipPeak) clipPeak = absolute;
    }

    Console.WriteLine($"decoded: {clip.Frames} frames ({clip.Duration.TotalSeconds:F2}s), peak={clipPeak:F4}");

    using var engine = new AudioEngine();
    engine.Error += (_, message) => Console.Error.WriteLine("engine error: " + message);

    var tapPath = Environment.GetEnvironmentVariable("FSP_TAP");
    NAudio.Wave.WaveFileWriter? tapWriter = null;

    engine.PlayClip(clip, "probe");
    Console.WriteLine($"mixer preview peak: {engine.RenderPreviewPeak():F4} (padGain={engine.PadGain:F2}, masterGain={engine.MasterGain:F2})");
    engine.StopAll();

    if (!string.IsNullOrWhiteSpace(tapPath))
    {
        tapWriter = new NAudio.Wave.WaveFileWriter(tapPath, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        var first = true;
        var callCount = 0;
        engine.MixTap = (data, offset, count) =>
        {
            callCount++;
            if (callCount <= 12)
            {
                Console.WriteLine($"tap call {callCount}: thread={Environment.CurrentManagedThreadId} offset={offset} count={count} bufferLen={data.Length}");
            }
            if (first)
            {
                first = false;
                var blockPeak = 0f;
                for (var i = 0; i < count; i++) blockPeak = MathF.Max(blockPeak, MathF.Abs(data[offset + i]));
                Console.WriteLine($"tap first block: count={count} peak={blockPeak:F4} master={engine.MasterGain:F2} pad={engine.PadGain:F2} micPassthrough={engine.MicPassthrough} browserSend={engine.BrowserSend}");
            }

            if (callCount % 96 == 0)
            {
                var blockPeak = 0f;
                for (var i = 0; i < count; i++) blockPeak = MathF.Max(blockPeak, MathF.Abs(data[offset + i]));
                Console.WriteLine($"tap t~{callCount * 480 / 48000.0:F1}s peak={blockPeak:F4} voices={engine.ActivePadVoices} micPeak={engine.MicMeter.Peak:F3} webPeak={engine.BrowserMeter.Peak:F3} master={engine.MasterGain:F2} pad={engine.PadGain:F2} mon={engine.MonitorOutputActive}");
            }

            var bytes = new byte[count * 4];
            Buffer.BlockCopy(data, offset * 4, bytes, 0, bytes.Length);
            tapWriter.Write(bytes, 0, bytes.Length);
        };
    }

    if (!engine.Start(device.Id))
    {
        Console.Error.WriteLine("failed to start output");
        return 5;
    }

    engine.PlayClip(clip, "probe");
    Console.WriteLine($"playing {file} -> {device.Name} ({clip.Duration.TotalSeconds:F2}s)");
    Console.WriteLine($"peak meter: {engine.OutputMeter.Peak:F3}");

    while (engine.IsPlaying("probe"))
    {
        await Task.Delay(100);
    }

    await Task.Delay(300);
    engine.Stop();
    tapWriter?.Dispose();
    return 0;
}

static void WriteWav(string path, List<float> samples, int sampleRate, int channels)
{
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

    var frames = samples.Count / channels;
    var dataBytes = frames * channels * 2;

    using var writer = new BinaryWriter(File.Create(path));

    writer.Write("RIFF"u8.ToArray());
    writer.Write(36 + dataBytes);
    writer.Write("WAVE"u8.ToArray());
    writer.Write("fmt "u8.ToArray());
    writer.Write(16);
    writer.Write((short)1);
    writer.Write((short)channels);
    writer.Write(sampleRate);
    writer.Write(sampleRate * channels * 2);
    writer.Write((short)(channels * 2));
    writer.Write((short)16);
    writer.Write("data"u8.ToArray());
    writer.Write(dataBytes);

    foreach (var sample in samples)
    {
        var clamped = Math.Clamp(sample, -1f, 1f);
        writer.Write((short)(clamped * short.MaxValue));
    }
}

sealed class ClipSampleProvider : ISampleProvider
{
    private readonly AudioClip _clip;
    private readonly float _gain;
    private int _position;

    public ClipSampleProvider(AudioClip clip, float gain)
    {
        _clip = clip;
        _gain = gain;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var sample = _position < _clip.Samples.Length ? _clip.Samples[_position] : 0f;
            buffer[offset + i] = sample * _gain;
            _position++;
        }

        return count;
    }
}
