using System.Runtime.InteropServices;

namespace TavernGames.Plugin.Windows;

/// <summary>
/// The table's own sounds: a dice rattle, a mug knocked on the wood, coins clinking, a chime
/// when the table turns to you, a little fanfare, two falling notes for a defeat.
/// <para>
/// They are made here, out of sine waves and noise, rather than borrowed from the game: the
/// game's chimes already mean things (a tell sounds like a tell), and nothing has to be shipped.
/// They play through Windows directly, one at a time, so the game's mixer never sees them and
/// the volume is ours. A 16-bit WAV of yours in the Sounds folder replaces any of them.
/// </para>
/// </summary>
internal static class Sound
{
    public enum Cue
    {
        Turn,
        Alert,
        Chips,
        Dice,
        Deal,
        Victory,
        Defeat,
        Spin,
    }

    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    private static readonly Dictionary<Cue, GCHandle> Loaded = new();
    private static readonly List<(GCHandle Handle, double RetiredAt)> Retired = new();
    private static float _volume = 0.6f;
    private static bool _dirty = true;
    private static bool _warned;

    /// <summary>The switch, from the config.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>0 to 1, baked into the samples; a change rebuilds them on the next play.</summary>
    public static float Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(clamped - _volume) < 0.001f)
                return;
            _volume = clamped;
            _dirty = true;
        }
    }

    /// <summary>Where a WAV of the user's own would be, one per cue, named after it.</summary>
    public static string? Directory { get; set; }

    public static void YourTurn() => Play(Cue.Turn);

    public static void Alert() => Play(Cue.Alert);

    public static void Chips() => Play(Cue.Chips);

    public static void Dice() => Play(Cue.Dice);

    public static void Deal() => Play(Cue.Deal);

    public static void Victory() => Play(Cue.Victory);

    public static void Defeat() => Play(Cue.Defeat);

    public static void Spin() => Play(Cue.Spin);

    /// <summary>The file a cue would be read from, if it were there.</summary>
    public static string FileName(Cue cue) => cue.ToString().ToLowerInvariant() + ".wav";

    /// <summary>Forgets the loaded sounds, so a file dropped in the folder is picked up.</summary>
    public static void Reload() => _dirty = true;

    /// <summary>Plays a cue whether or not sounds are on: the Setup page's preview.</summary>
    public static void Preview(Cue cue)
    {
        var was = Enabled;
        Enabled = true;
        Play(cue);
        Enabled = was;
    }

    public static void Play(Cue cue)
    {
        if (!Enabled)
            return;

        try
        {
            if (_dirty)
                Load();
            if (Loaded.TryGetValue(cue, out var handle))
                PlaySound(handle.AddrOfPinnedObject(), IntPtr.Zero, SndMemory | SndAsync | SndNoDefault);
        }
        catch (Exception ex)
        {
            // A sound is never worth an error in the log more than once.
            if (!_warned)
            {
                _warned = true;
                Plugin.Log.Warning(ex, "Tavern Games: could not play a sound; sounds are off for this session.");
                Enabled = false;
            }
        }
    }

    // -- loading -----------------------------------------------------------------------------

    /// <summary>
    /// Builds every cue at the current volume, from the user's file where there is one. The
    /// buffers are pinned because Windows reads them while the sound plays; old ones are kept
    /// a while before they are freed, in case one is still playing.
    /// </summary>
    private static void Load()
    {
        _dirty = false;
        var now = Fx.Now;

        for (var i = Retired.Count - 1; i >= 0; i--)
        {
            if (now - Retired[i].RetiredAt > 10.0)
            {
                Retired[i].Handle.Free();
                Retired.RemoveAt(i);
            }
        }

        foreach (var cue in Enum.GetValues<Cue>())
        {
            var bytes = Custom(cue) ?? Wav(Synth(cue));
            if (Loaded.TryGetValue(cue, out var old))
                Retired.Add((old, now));
            Loaded[cue] = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        }
    }

    /// <summary>The user's own WAV for a cue, at our volume when it is plain 16-bit PCM, or null.</summary>
    private static byte[]? Custom(Cue cue)
    {
        if (Directory is null)
            return null;

        var path = Path.Combine(Directory, FileName(cue));
        if (!File.Exists(path))
            return null;

        try
        {
            var bytes = File.ReadAllBytes(path);
            ScalePcm16(bytes, _volume);
            return bytes;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Tavern Games: could not read {Path}; using the built-in sound.", path);
            return null;
        }
    }

    /// <summary>Walks a RIFF WAVE file and scales its samples, if they are 16-bit PCM. Anything else is left alone.</summary>
    private static void ScalePcm16(byte[] bytes, float volume)
    {
        if (bytes.Length < 12 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F' || bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E')
            return;

        var pcm16 = false;
        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            var size = BitConverter.ToInt32(bytes, pos + 4);
            var body = pos + 8;
            if (size < 0 || body + size > bytes.Length)
                return;

            if (id == "fmt " && size >= 16)
                pcm16 = BitConverter.ToInt16(bytes, body) == 1 && BitConverter.ToInt16(bytes, body + 14) == 16;

            if (id == "data")
            {
                if (!pcm16)
                    return;
                for (var i = body; i + 1 < body + size; i += 2)
                {
                    var sample = (short)(bytes[i] | (bytes[i + 1] << 8));
                    var scaled = (short)Math.Clamp(sample * volume, short.MinValue, short.MaxValue);
                    bytes[i] = (byte)scaled;
                    bytes[i + 1] = (byte)(scaled >> 8);
                }
                return;
            }

            pos = body + size + (size & 1);
        }
    }

    // -- making the sounds -------------------------------------------------------------------

    private static float[] Synth(Cue cue) => cue switch
    {
        Cue.Turn => TurnSamples(),
        Cue.Alert => AlertSamples(),
        Cue.Chips => ChipsSamples(),
        Cue.Dice => DiceSamples(),
        Cue.Deal => DealSamples(),
        Cue.Victory => VictorySamples(),
        Cue.Spin => SpinSamples(),
        _ => DefeatSamples(),
    };

    /// <summary>Two bell notes, a fourth apart: the table's way of tapping you on the shoulder.</summary>
    private static float[] TurnSamples()
    {
        var mix = new Mix(0.6);
        mix.Tone(0.00, 659.25, 0.32, 0.004, 0.11, (1, 1.0), (2, 0.28), (3, 0.08), (4.2, 0.04));
        mix.Tone(0.17, 880.00, 0.43, 0.004, 0.16, (1, 1.0), (2, 0.28), (3, 0.08), (4.2, 0.04));
        return mix.Buffer;
    }

    /// <summary>A mug knocked on the wood: a clack over a thump.</summary>
    private static float[] AlertSamples()
    {
        var mix = new Mix(0.35);
        var rng = new Random(3);
        mix.Noise(0.00, 0.07, 0.012, 1.0, 1800, rng);
        mix.Tone(0.00, 110, 0.28, 0.002, 0.07, (1, 1.0), (2, 0.35));
        mix.Tone(0.00, 172, 0.16, 0.002, 0.04, (1, 0.45));
        return mix.Buffer;
    }

    /// <summary>Three coins landing on the pile.</summary>
    private static float[] ChipsSamples()
    {
        var mix = new Mix(0.42);
        var rng = new Random(5);
        double[] pitches = { 2350, 2900, 3450 };
        for (var i = 0; i < pitches.Length; i++)
        {
            var at = i * 0.075;
            mix.Tone(at, pitches[i], 0.14, 0.001, 0.028, (1, 1.0), (2.76, 0.35), (5.4, 0.12));
            mix.Noise(at, 0.012, 0.003, 0.35, 7000, rng);
        }
        return mix.Buffer;
    }

    /// <summary>A cup of dice shaken and tipped out: a run of clicks that slow and settle.</summary>
    private static float[] DiceSamples()
    {
        var mix = new Mix(0.55);
        var rng = new Random(7);
        var at = 0.0;
        for (var k = 0; k < 9; k++)
        {
            mix.Noise(at, 0.022, 0.006, 1.0 - k * 0.07, 3500, rng);
            mix.Tone(at, 850 + rng.Next(500), 0.03, 0.001, 0.008, (1, 0.35));
            at += 0.03 + rng.NextDouble() * 0.045;
        }
        return mix.Buffer;
    }

    /// <summary>A card flicked off the deck.</summary>
    private static float[] DealSamples()
    {
        var mix = new Mix(0.12);
        var rng = new Random(11);
        mix.Noise(0.0, 0.06, 0.014, 0.7, 2600, rng);
        mix.Tone(0.0, 1900, 0.012, 0.001, 0.004, (1, 0.25));
        return mix.Buffer;
    }

    /// <summary>A short fanfare: up the chord, and the top held with a fifth under it.</summary>
    private static float[] VictorySamples()
    {
        var mix = new Mix(1.15);
        double[] notes = { 523.25, 659.25, 783.99 };
        for (var i = 0; i < notes.Length; i++)
            mix.Tone(i * 0.11, notes[i], 0.15, 0.004, 0.06, (1, 1.0), (2, 0.35), (3, 0.15));
        mix.Tone(0.33, 1046.50, 0.75, 0.004, 0.24, (1, 1.0), (2, 0.35), (3, 0.15));
        mix.Tone(0.33, 783.99, 0.75, 0.004, 0.24, (1, 0.55), (2, 0.15));
        return mix.Buffer;
    }

    /// <summary>A ball round the rim of a wheel: ticks that space out as it slows, a rattle, and the drop into a pocket.</summary>
    private static float[] SpinSamples()
    {
        var mix = new Mix(2.9);
        var rng = new Random(13);
        var at = 0.0;
        var spacing = 0.032;
        while (at < 2.5)
        {
            mix.Noise(at, 0.012, 0.004, 0.5 + 0.15 * rng.NextDouble(), 5000, rng);
            mix.Tone(at, 2300 + rng.Next(700), 0.02, 0.001, 0.005, (1, 0.22));
            at += spacing;
            spacing *= 1.055;
        }

        for (var i = 0; i < 4; i++)
            mix.Noise(2.55 + i * 0.045, 0.02, 0.006, 0.8, 3000, rng);
        mix.Tone(2.72, 140, 0.16, 0.002, 0.045, (1, 0.9), (2, 0.3));
        return mix.Buffer;
    }

    /// <summary>Two notes, the second lower, with a little more grain in them.</summary>
    private static float[] DefeatSamples()
    {
        var mix = new Mix(0.95);
        mix.Tone(0.00, 440.00, 0.34, 0.01, 0.20, (1, 1.0), (2, 0.5), (3, 0.3), (4, 0.15));
        mix.Tone(0.32, 329.63, 0.62, 0.01, 0.28, (1, 1.0), (2, 0.5), (3, 0.3), (4, 0.15));
        return mix.Buffer;
    }

    /// <summary>Samples to a 16-bit mono WAV at the current volume, peak-normalised so every cue sits at the same level.</summary>
    private static byte[] Wav(float[] samples)
    {
        var peak = 0f;
        foreach (var s in samples)
            peak = MathF.Max(peak, MathF.Abs(s));
        var gain = peak > 0f ? 0.9f * _volume / peak : 0f;

        var dataSize = samples.Length * 2;
        var bytes = new byte[44 + dataSize];
        void Ascii(int at, string text)
        {
            for (var i = 0; i < text.Length; i++)
                bytes[at + i] = (byte)text[i];
        }

        Ascii(0, "RIFF");
        BitConverter.GetBytes(36 + dataSize).CopyTo(bytes, 4);
        Ascii(8, "WAVE");
        Ascii(12, "fmt ");
        BitConverter.GetBytes(16).CopyTo(bytes, 16);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 20);              // PCM
        BitConverter.GetBytes((short)1).CopyTo(bytes, 22);              // mono
        BitConverter.GetBytes(Mix.Rate).CopyTo(bytes, 24);
        BitConverter.GetBytes(Mix.Rate * 2).CopyTo(bytes, 28);          // bytes per second
        BitConverter.GetBytes((short)2).CopyTo(bytes, 32);              // block align
        BitConverter.GetBytes((short)16).CopyTo(bytes, 34);             // bits per sample
        Ascii(36, "data");
        BitConverter.GetBytes(dataSize).CopyTo(bytes, 40);

        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Clamp(samples[i] * gain * 32767f, short.MinValue, short.MaxValue);
            bytes[44 + i * 2] = (byte)value;
            bytes[45 + i * 2] = (byte)(value >> 8);
        }

        return bytes;
    }

    /// <summary>A buffer of samples that tones and noise are added into.</summary>
    private sealed class Mix
    {
        public const int Rate = 44100;

        public readonly float[] Buffer;

        public Mix(double seconds) => Buffer = new float[(int)(seconds * Rate)];

        /// <summary>A note: partials over a fundamental, a quick attack, an exponential decay, and a soft cut at the end.</summary>
        public void Tone(double at, double frequency, double length, double attack, double decay, params (double Harmonic, double Amplitude)[] partials)
        {
            var start = (int)(at * Rate);
            var count = (int)(length * Rate);
            for (var i = 0; i < count && start + i < Buffer.Length; i++)
            {
                var t = i / (double)Rate;
                var envelope = t < attack ? t / attack : Math.Exp(-(t - attack) / decay);
                var tail = count - i;
                if (tail < 220)
                    envelope *= tail / 220.0; // no click at the cut

                var sample = 0.0;
                foreach (var (harmonic, amplitude) in partials)
                    sample += amplitude * Math.Sin(2 * Math.PI * frequency * harmonic * t);

                Buffer[start + i] += (float)(sample * envelope);
            }
        }

        /// <summary>A burst of noise through a one-pole low-pass, decaying: the material of clicks, clacks and flicks.</summary>
        public void Noise(double at, double length, double decay, double amplitude, double cutoff, Random rng)
        {
            var start = (int)(at * Rate);
            var count = (int)(length * Rate);
            var k = 1 - Math.Exp(-2 * Math.PI * cutoff / Rate);
            var y = 0.0;
            for (var i = 0; i < count && start + i < Buffer.Length; i++)
            {
                var t = i / (double)Rate;
                var x = rng.NextDouble() * 2 - 1;
                y += (x - y) * k;
                Buffer[start + i] += (float)(y * Math.Exp(-t / decay) * amplitude);
            }
        }
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(IntPtr data, IntPtr module, uint flags);
}
