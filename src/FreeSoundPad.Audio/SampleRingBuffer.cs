namespace FreeSoundPad.Audio;

/// <summary>
/// Thread-safe single-producer/single-consumer float ring buffer.
/// When full, the oldest samples are dropped so capture never blocks the audio thread.
/// </summary>
public sealed class SampleRingBuffer
{
    private readonly float[] _data;
    private readonly object _gate = new();
    private int _read;
    private int _write;
    private int _count;

    public SampleRingBuffer(int capacitySamples)
    {
        if (capacitySamples <= 0) throw new ArgumentOutOfRangeException(nameof(capacitySamples));
        _data = new float[capacitySamples];
    }

    public int Capacity => _data.Length;

    public int Available
    {
        get
        {
            lock (_gate) return _count;
        }
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;

        lock (_gate)
        {
            if (samples.Length >= _data.Length)
            {
                samples = samples[^_data.Length..];
                _read = 0;
                _write = 0;
                _count = 0;
            }

            var free = _data.Length - _count;
            if (samples.Length > free)
            {
                var drop = samples.Length - free;
                _read = (_read + drop) % _data.Length;
                _count -= drop;
            }

            var first = Math.Min(samples.Length, _data.Length - _write);
            samples[..first].CopyTo(_data.AsSpan(_write));
            if (samples.Length > first)
            {
                samples[first..].CopyTo(_data);
            }

            _write = (_write + samples.Length) % _data.Length;
            _count += samples.Length;
        }
    }

    /// <summary>
    /// Copies available samples into destination, zero-filling the remainder.
    /// Returns the number of samples actually read.
    /// </summary>
    public int Read(Span<float> destination)
    {
        if (destination.IsEmpty) return 0;

        lock (_gate)
        {
            var count = Math.Min(destination.Length, _count);
            var first = Math.Min(count, _data.Length - _read);
            _data.AsSpan(_read, first).CopyTo(destination);
            if (count > first)
            {
                _data.AsSpan(0, count - first).CopyTo(destination[first..]);
            }

            _read = (_read + count) % _data.Length;
            _count -= count;

            if (count < destination.Length)
            {
                destination[count..].Clear();
            }

            return count;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _read = 0;
            _write = 0;
            _count = 0;
        }
    }
}
