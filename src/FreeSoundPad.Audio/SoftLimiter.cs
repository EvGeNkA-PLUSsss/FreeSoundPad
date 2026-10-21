namespace FreeSoundPad.Audio;

/// <summary>Cheap soft-knee limiter: transparent below the threshold, tanh-style saturation above it.</summary>
public static class SoftLimiter
{
    private const float Threshold = 0.78f;

    public static void Process(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var value = samples[i];
            var absolute = MathF.Abs(value);

            if (absolute > Threshold)
            {
                var over = absolute - Threshold;
                var compressed = Threshold + (1f - Threshold) * MathF.Tanh(over / (1f - Threshold));
                value = MathF.CopySign(compressed, value);
            }

            samples[i] = Math.Clamp(value, -1f, 1f);
        }
    }
}
