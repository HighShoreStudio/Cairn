namespace HighshoreCairn.Services;

/// <summary>
/// Generates the notification sounds of the tomato timer as WAV data (16-bit mono PCM), so the app
/// needs no sound files. The View layer only has to play the bytes.
/// </summary>
public static class ToneSynth
{
    public const int SampleRate = 44100;

    public static readonly string[] Sounds = { "Chime", "Bell", "Digital" };

    /// <summary>The WAV bytes of a built-in sound, or null for "None" / unknown names.</summary>
    /// <param name="volume">0-100.</param>
    public static byte[]? Render(string sound, int volume)
    {
        var gain = Math.Clamp(volume, 0, 100) / 100.0;
        if (gain <= 0) return null;
        double[]? samples = sound switch
        {
            "Chime" => Chime(),
            "Bell" => Bell(),
            "Digital" => Digital(),
            _ => null
        };
        return samples is null ? null : ToWav(samples, gain);
    }

    /// <summary>Three soft notes going up (E5, G#5, B5).</summary>
    private static double[] Chime()
    {
        var notes = new[] { 659.25, 830.61, 987.77 };
        var total = new double[(int)(SampleRate * 1.6)];
        for (var n = 0; n < notes.Length; n++)
        {
            var start = (int)(SampleRate * 0.22 * n);
            for (var i = start; i < total.Length; i++)
            {
                var t = (i - start) / (double)SampleRate;
                var envelope = Math.Exp(-3.2 * t) * Math.Min(1, t * 200);
                total[i] += 0.30 * envelope * (Math.Sin(2 * Math.PI * notes[n] * t) + 0.25 * Math.Sin(4 * Math.PI * notes[n] * t));
            }
        }
        return total;
    }

    /// <summary>One bell strike with a long tail (inharmonic partials).</summary>
    private static double[] Bell()
    {
        var partials = new[] { (520.0, 1.0, 1.6), (1040.0, 0.6, 2.2), (1560.5, 0.4, 3.0), (2204.0, 0.25, 4.2), (2756.0, 0.15, 5.5) };
        var total = new double[(int)(SampleRate * 2.4)];
        for (var i = 0; i < total.Length; i++)
        {
            var t = i / (double)SampleRate;
            double value = 0;
            foreach (var (frequency, level, decay) in partials)
                value += level * Math.Exp(-decay * t) * Math.Sin(2 * Math.PI * frequency * t);
            total[i] = 0.28 * value * Math.Min(1, t * 400);
        }
        return total;
    }

    /// <summary>Three short beeps, like a kitchen timer.</summary>
    private static double[] Digital()
    {
        var total = new double[(int)(SampleRate * 0.9)];
        for (var beep = 0; beep < 3; beep++)
        {
            var start = (int)(SampleRate * 0.28 * beep);
            var length = (int)(SampleRate * 0.14);
            for (var i = 0; i < length && start + i < total.Length; i++)
            {
                var t = i / (double)SampleRate;
                var edge = Math.Min(1, Math.Min(i, length - i) / (SampleRate * 0.005));
                var square = Math.Sin(2 * Math.PI * 1760 * t) >= 0 ? 1.0 : -1.0;
                total[start + i] = 0.22 * edge * (0.6 * square + 0.4 * Math.Sin(2 * Math.PI * 1760 * t));
            }
        }
        return total;
    }

    private static byte[] ToWav(double[] samples, double gain)
    {
        var dataLength = samples.Length * 2;
        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                 // size of the format chunk
        writer.Write((short)1);           // PCM
        writer.Write((short)1);           // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);     // bytes per second
        writer.Write((short)2);           // bytes per sample frame
        writer.Write((short)16);          // bits per sample
        writer.Write("data"u8);
        writer.Write(dataLength);
        foreach (var sample in samples)
            writer.Write((short)Math.Clamp(sample * gain * short.MaxValue, short.MinValue, short.MaxValue));
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Applies the volume to the bytes of a 16-bit PCM .wav file. Other formats are returned unchanged.
    /// </summary>
    public static byte[] ScaleWav(byte[] wav, int volume)
    {
        var gain = Math.Clamp(volume, 0, 100) / 100.0;
        if (gain >= 0.999 || wav.Length < 44) return wav;
        try
        {
            if (wav[0] != 'R' || wav[8] != 'W') return wav;
            var pos = 12;
            short format = 0, bits = 0;
            while (pos + 8 <= wav.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
                var size = BitConverter.ToInt32(wav, pos + 4);
                if (size < 0) return wav; // damaged file: play it as it is
                var body = pos + 8;
                if (id == "fmt ")
                {
                    format = BitConverter.ToInt16(wav, body);
                    bits = BitConverter.ToInt16(wav, body + 14);
                }
                else if (id == "data")
                {
                    if (format != 1 || bits != 16) return wav;
                    var copy = (byte[])wav.Clone();
                    var end = Math.Min(wav.Length, body + Math.Max(0, size));
                    for (var i = body; i + 1 < end; i += 2)
                    {
                        var value = (short)(BitConverter.ToInt16(copy, i) * gain);
                        copy[i] = (byte)(value & 0xFF);
                        copy[i + 1] = (byte)((value >> 8) & 0xFF);
                    }
                    return copy;
                }
                pos = body + size + (size & 1);
            }
        }
        catch
        {
            // malformed file: play it as it is
        }
        return wav;
    }
}
