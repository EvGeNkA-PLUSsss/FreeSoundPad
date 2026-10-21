namespace FreeSoundPad.Audio;

/// <summary>Peak/RMS meter with simple per-block decay. Safe to read from any thread.</summary>
public sealed class LevelMeter
{
    private float _peak;
    private float _rms;
    private float _hold;

    public float Peak => Volatile.Read(ref _peak);
    public float Rms => Volatile.Read(ref _rms);
    public float Hold => Volatile.Read(ref _hold);

    public float PeakDb => 20f * MathF.Log10(MathF.Max(Peak, 1e-5f));
    public float RmsDb => 20f * MathF.Log10(MathF.Max(Rms, 1e-5f));

    public void Update(float[] buffer, int offset, int count)
    {
        if (count <= 0) return;

        var peak = 0f;
        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            var value = MathF.Abs(buffer[offset + i]);
            if (value > peak) peak = value;
            sum += value * value;
        }

        var rms = MathF.Sqrt((float)(sum / count));

        var prevPeak = Volatile.Read(ref _peak);
        var nextPeak = MathF.Max(peak, prevPeak * 0.90f);
        Volatile.Write(ref _peak, nextPeak);

        var prevHold = Volatile.Read(ref _hold);
        Volatile.Write(ref _hold, MathF.Max(peak, prevHold * 0.995f));

        var prevRms = Volatile.Read(ref _rms);
        Volatile.Write(ref _rms, MathF.Max(rms, prevRms * 0.92f));
    }

    public void Reset()
    {
        Volatile.Write(ref _peak, 0);
        Volatile.Write(ref _rms, 0);
        Volatile.Write(ref _hold, 0);
    }
}
