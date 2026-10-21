namespace FreeSoundPad.Audio;

/// <summary>Mixes decoded pad clips. All members are thread-safe.</summary>
public sealed class PadMixer
{
    private readonly List<PadVoice> _voices = new();
    private readonly object _gate = new();

    public void Add(PadVoice voice)
    {
        lock (_gate) _voices.Add(voice);
    }

    public void MixAdd(float[] buffer, int offset, int count, float busGain)
    {
        lock (_gate)
        {
            for (var i = _voices.Count - 1; i >= 0; i--)
            {
                var voice = _voices[i];
                voice.MixAdd(buffer, offset, count, busGain);
                if (voice.Finished) _voices.RemoveAt(i);
            }
        }
    }

    public bool AnyPlaying(string tag)
    {
        lock (_gate) return _voices.Any(v => v.Tag == tag);
    }

    public double Progress(string tag)
    {
        lock (_gate)
        {
            var voice = _voices.FirstOrDefault(v => v.Tag == tag);
            return voice?.Progress ?? 0;
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_gate) return _voices.Count;
        }
    }

    public void StopAll()
    {
        lock (_gate) _voices.Clear();
    }

    public void StopTag(string tag)
    {
        lock (_gate) _voices.RemoveAll(v => v.Tag == tag);
    }
}

public sealed class PadVoice
{
    private readonly AudioClip _clip;
    private int _position;

    public PadVoice(AudioClip clip, string tag, float gain = 1f, bool loop = false)
    {
        _clip = clip;
        Tag = tag;
        Gain = gain;
        Loop = loop;
    }

    public string Tag { get; }
    public float Gain { get; set; }
    public bool Loop { get; set; }
    public bool Finished { get; private set; }

    public double Progress => _clip.Frames > 0 ? (double)_position / _clip.Frames : 0;

    public void MixAdd(float[] buffer, int offset, int count, float busGain)
    {
        if (Finished) return;

        var framesToWrite = count / 2;
        var samples = _clip.Samples;
        var clipFrames = _clip.Frames;
        var position = _position;
        var gain = Gain * busGain;

        for (var frame = 0; frame < framesToWrite; frame++)
        {
            if (position >= clipFrames)
            {
                if (!Loop)
                {
                    Finished = true;
                    break;
                }

                position = 0;
            }

            buffer[offset + frame * 2] += samples[position * 2] * gain;
            buffer[offset + frame * 2 + 1] += samples[position * 2 + 1] * gain;
            position++;
        }

        _position = position;
        if (!Loop && _position >= clipFrames) Finished = true;
    }
}
