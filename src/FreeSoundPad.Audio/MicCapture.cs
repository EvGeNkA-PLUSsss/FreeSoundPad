using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FreeSoundPad.Audio;

/// <summary>Captures the selected microphone into a 48 kHz stereo float ring buffer.</summary>
public sealed class MicCapture : IDisposable
{
    private const int TargetSampleRate = 48000;

    private readonly WasapiCapture _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _provider;
    private readonly SampleRingBuffer _target;
    private readonly object _bufferGate = new();
    private Thread? _pumpThread;
    private volatile bool _running;

    public MicCapture(MMDevice device, SampleRingBuffer target)
    {
        _target = target;
        _capture = new WasapiCapture(device, true, 20);

        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };

        _capture.DataAvailable += OnDataAvailable;

        ISampleProvider provider = _buffer.ToSampleProvider();
        if (provider.WaveFormat.Channels == 1)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }
        else if (provider.WaveFormat.Channels > 2)
        {
            provider = new FirstTwoChannelsSampleProvider(provider);
        }

        if (provider.WaveFormat.SampleRate != TargetSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, TargetSampleRate);
        }

        _provider = provider;
        CaptureFormat = _capture.WaveFormat;
    }

    public WaveFormat CaptureFormat { get; }

    public string? LastError { get; private set; }

    public void Start()
    {
        if (_running) return;

        _running = true;
        _pumpThread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "FreeSoundPad microphone pump",
        };
        _pumpThread.Start();

        try
        {
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _running = false;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        try
        {
            lock (_bufferGate)
            {
                _buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
            }
        }
        catch
        {
            // Ignore malformed buffers.
        }
    }

    private void PumpLoop()
    {
        var buffer = new float[480 * 2];

        while (_running)
        {
            var read = 0;
            try
            {
                lock (_bufferGate)
                {
                    read = _provider.Read(buffer, 0, buffer.Length);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                break;
            }

            if (read > 0)
            {
                _target.Write(buffer.AsSpan(0, read));
            }
            else
            {
                Thread.Sleep(2);
            }
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        try
        {
            _capture.StopRecording();
        }
        catch
        {
            // ignore
        }

        try
        {
            _pumpThread?.Join(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // ignore
        }

        _pumpThread = null;
    }

    public void Dispose()
    {
        Stop();
        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
        _buffer.ClearBuffer();
    }

    private sealed class FirstTwoChannelsSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _sourceChannels;
        private float[] _temp = Array.Empty<float>();

        public FirstTwoChannelsSampleProvider(ISampleProvider source)
        {
            _source = source;
            _sourceChannels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / 2;
            var sourceSamples = frames * _sourceChannels;
            if (_temp.Length < sourceSamples) _temp = new float[sourceSamples];

            var read = _source.Read(_temp, 0, sourceSamples);
            var readFrames = read / _sourceChannels;
            for (var frame = 0; frame < readFrames; frame++)
            {
                buffer[offset + frame * 2] = _temp[frame * _sourceChannels];
                buffer[offset + frame * 2 + 1] = _temp[frame * _sourceChannels + 1];
            }

            return readFrames * 2;
        }
    }
}
