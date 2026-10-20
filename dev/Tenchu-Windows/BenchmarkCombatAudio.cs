using System.Collections.Concurrent;
using System.Text;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime;

namespace TenchuSARecomp;

// Mix local PCM effects into the existing audio stream. This uses the game's
// audio device/master volume and remains responsive during game-thread hitstop.
internal static class BenchmarkCombatAudio
{
    delegate void OriginalMix(Spu spu, short[] samples, int frames);
    delegate void MixDetour(OriginalMix original, Spu spu, short[] samples, int frames);
    static Hook? hook;
    static bool installed;
    static short[][] parries = Array.Empty<short[]>();
    static short[] guardBreak = Array.Empty<short>();
    static readonly ConcurrentQueue<short[]> pending = new();
    static readonly List<Voice> voices = new();
    static int nextParry;
    static int mixedEffects;
    sealed class Voice(short[] samples) { public readonly short[] Samples = samples; public int Position; }

    public static void Install()
    {
        if (installed) return;
        try
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "assets", "combat-audio");
            parries = Directory.GetFiles(folder, "parry-*.wav").Order(StringComparer.Ordinal).Select(ReadWave).ToArray();
            guardBreak = ReadWave(Path.Combine(folder, "guard-break.wav"));
            if (parries.Length == 0) throw new InvalidDataException("No parry effects found");
            hook = new Hook(typeof(Spu).GetMethod(nameof(Spu.Mix))!, (MixDetour)Mix);
            installed = true;
            Console.WriteLine($"[combat-audio] loaded {parries.Length} parry variants and guard-break effect; 44100Hz stereo; existing master volume");
            if (Environment.GetEnvironmentVariable("TENCHU_TEST_COMBAT_AUDIO") == "1" &&
                int.TryParse(Environment.GetEnvironmentVariable("TENCHU_BENCHMARK_SECONDS"), out int seconds) && seconds > 0)
            {
                Parry();
                _ = Task.Run(async () => { await Task.Delay(2000); GuardBreak(); });
            }
        }
        catch (Exception e) { Console.WriteLine($"[combat-audio] custom effects unavailable: {e.Message}"); }
    }

    public static void Parry()
    {
        if (!installed) return;
        int variant = (int)((uint)(Interlocked.Increment(ref nextParry) - 1) % (uint)parries.Length);
        Enqueue(parries[variant]);
        Console.WriteLine($"[combat-audio] parry variant={variant + 1}");
    }
    public static void GuardBreak()
    {
        if (!installed) return;
        Enqueue(guardBreak);
        Console.WriteLine("[combat-audio] guard break");
    }
    static void Enqueue(short[] clip)
    {
        if (pending.Count < 16) pending.Enqueue(clip);
    }
    static void Mix(OriginalMix original, Spu spu, short[] samples, int frames)
    {
        original(spu, samples, frames);
        MixInto(samples, frames);
    }
    static void MixInto(short[] samples, int frames)
    {
        while (pending.TryDequeue(out var clip))
        {
            if (voices.Count == 8) voices.RemoveAt(0);
            voices.Add(new Voice(clip));
            if (++mixedEffects <= 2) Console.WriteLine($"[combat-audio] live PCM mixer consumed effect #{mixedEffects}");
        }
        int count = Math.Min(samples.Length, checked(frames * 2));
        for (int i = 0; i < count; i++)
        {
            int value = samples[i];
            foreach (var voice in voices)
                if (voice.Position < voice.Samples.Length) value += (int)(voice.Samples[voice.Position++] * 0.75f);
            samples[i] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        }
        voices.RemoveAll(v => v.Position >= v.Samples.Length);
    }
    static short[] ReadWave(string file)
    {
        using var reader = new BinaryReader(File.OpenRead(file), Encoding.ASCII);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Invalid WAV");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Invalid WAV");
        bool valid = false; byte[]? data = null;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            string id = new(reader.ReadChars(4)); int size = reader.ReadInt32(); long start = reader.BaseStream.Position;
            if (size < 0 || start + size > reader.BaseStream.Length) throw new InvalidDataException("Truncated WAV");
            if (id == "fmt " && size >= 16)
            {
                ushort format = reader.ReadUInt16(), channels = reader.ReadUInt16(); uint rate = reader.ReadUInt32();
                reader.ReadUInt32(); reader.ReadUInt16(); ushort bits = reader.ReadUInt16();
                valid = format == 1 && channels == 2 && rate == 44100 && bits == 16;
            }
            if (id == "data") data = reader.ReadBytes(size);
            reader.BaseStream.Position = start + size + (size & 1);
        }
        if (!valid || data == null || data.Length == 0 || data.Length % 4 != 0) throw new InvalidDataException("Effects must be 44100Hz stereo PCM16");
        short[] output = new short[data.Length / 2]; Buffer.BlockCopy(data, 0, output, 0, data.Length); return output;
    }
}
