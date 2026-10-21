using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FreeSoundPad.Audio;

public sealed class AudioClip
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    public AudioClip(string path, float[] samples, int frames, float[] peaks)
    {
        Path = path;
        Samples = samples;
        Frames = frames;
        Peaks = peaks;
    }

    public string Path { get; }

    /// <summary>Interleaved stereo float samples at 48 kHz.</summary>
    public float[] Samples { get; }

    public int Frames { get; }

    /// <summary>Normalized peaks for waveform drawing (0..1).</summary>
    public float[] Peaks { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames / SampleRate);

    public string DisplayDuration => FormatDuration(Duration);

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1) return duration.ToString(@"h\:mm\:ss");
        return duration.ToString(@"m\:ss");
    }
}

public sealed class AudioClipLoader
{
    public const int SampleRate = AudioClip.SampleRate;
    public const int Channels = AudioClip.Channels;

    /// <summary>Pads are short by nature; cap decoding at 3 minutes to keep memory bounded.</summary>
    private const int MaxFrames = SampleRate * 60 * 3;

    private const int PeakBins = 420;

    static AudioClipLoader()
    {
        NAudio.MediaFoundation.MediaFoundationApi.Startup();
    }

    public static AudioClip Load(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден", path);

        using var reader = new MediaFoundationReader(path);
        ISampleProvider provider = reader.ToSampleProvider();

        if (provider.WaveFormat.Channels == 1)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }
        else if (provider.WaveFormat.Channels > 2)
        {
            provider = new FirstTwoChannelsSampleProvider(provider);
        }

        if (provider.WaveFormat.SampleRate != SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, SampleRate);
        }

        var estimated = (int)Math.Min(MaxFrames * 2L, Math.Max(4096, reader.Length / 4));
        var samples = new List<float>(estimated);
        var buffer = new float[16384];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = provider.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;

            if (samples.Count + read > MaxFrames * 2)
            {
                read = MaxFrames * 2 - samples.Count;
                if (read > 0) samples.AddRange(buffer.AsSpan(0, read));
                break;
            }

            samples.AddRange(buffer.AsSpan(0, read));
        }

        var frames = samples.Count / Channels;
        if (frames <= 0) throw new InvalidDataException("Не удалось декодировать аудиофайл (пустой поток)");

        var total = frames * Channels;
        var array = new float[total];
        samples.CopyTo(0, array, 0, total);

        return new AudioClip(path, array, frames, ComputePeaks(array, frames, PeakBins));
    }

    private static float[] ComputePeaks(float[] samples, int frames, int bins)
    {
        bins = Math.Min(bins, Math.Max(1, frames));
        var peaks = new float[bins];
        if (frames == 0) return peaks;

        var framesPerBin = (double)frames / bins;
        for (var bin = 0; bin < bins; bin++)
        {
            var start = (int)(bin * framesPerBin);
            var end = Math.Max(start + 1, (int)((bin + 1) * framesPerBin));
            if (end > frames) end = frames;

            var max = 0f;
            for (var frame = start; frame < end; frame++)
            {
                var value = MathF.Abs((samples[frame * 2] + samples[frame * 2 + 1]) * 0.5f);
                if (value > max) max = value;
            }

            peaks[bin] = max;
        }

        // Normalize so quiet sounds still render a readable waveform.
        var globalMax = 0f;
        foreach (var peak in peaks)
        {
            if (peak > globalMax) globalMax = peak;
        }

        if (globalMax > 0.001f)
        {
            for (var i = 0; i < peaks.Length; i++) peaks[i] = Math.Clamp(peaks[i] / globalMax, 0.05f, 1f);
        }

        return peaks;
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
