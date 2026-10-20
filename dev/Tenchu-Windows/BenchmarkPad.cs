using System.Diagnostics;
using System.Reflection;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime.Hardware;

namespace TenchuSARecomp;

// Integration-test fixture. Enabled only for timed benchmark runs with an
// explicitly supplied command file. Operates on the virtual PS1 controller;
// never sends Windows input or takes focus from another application.
internal static class BenchmarkPad
{
    static Hook? hook;
    static Hook? gamepadIsolation;
    static string? commandPath;
    static string lastCommand = "";
    static long nextRead;
    static long releaseAt;
    static ushort held;
    static readonly Dictionary<string, byte> axes = new();
    static ushort previousHostState = ushort.MaxValue;
    static bool traceHost;
    static bool virtualOnly;
    static readonly Stopwatch clock = new();

    public static void Install()
    {
        commandPath = Environment.GetEnvironmentVariable("TENCHU_TEST_PAD_FILE");
        if (string.IsNullOrWhiteSpace(commandPath) ||
            !int.TryParse(Environment.GetEnvironmentVariable("TENCHU_BENCHMARK_SECONDS"), out int seconds) || seconds <= 0)
            return;
        var input = typeof(RecompOne.Runtime.Runtime).Assembly.GetType("RecompOne.Runtime.Host.InputManager", true)!;
        var poll = input.GetMethod("Poll", BindingFlags.Static | BindingFlags.Public)!;
        hook = new Hook(poll, (Action<Action>)Poll);
        if (Environment.GetEnvironmentVariable("TENCHU_TEST_KEYBOARD_ONLY") == "1")
        {
            var gamepads = input.GetMethod("PollGamepads", BindingFlags.Static | BindingFlags.NonPublic)!;
            gamepadIsolation = new Hook(gamepads, (Action<Action>)(_ =>
            {
                Controller.LeftX = Controller.LeftY = Controller.RightX = Controller.RightY = 128;
                Controller.LeftX2 = Controller.LeftY2 = Controller.RightX2 = Controller.RightY2 = 128;
            }));
            Console.WriteLine("[test-pad] physical gamepads isolated for keyboard validation");
        }
        clock.Start();
        traceHost = Environment.GetEnvironmentVariable("TENCHU_TEST_INPUT_TRACE") == "1";
        virtualOnly = Environment.GetEnvironmentVariable("TENCHU_TEST_VIRTUAL_ONLY") == "1";
        if (traceHost)
            RecompOne.Runtime.Events.Event.AddListener<RecompOne.Runtime.Events.KeyboardEvent>(e =>
            {
                // Only the game's mapped controls and UI toggles, never other apps' input.
                var key = (Silk.NET.Input.Key)e.Key;
                if (key is Silk.NET.Input.Key.Enter or Silk.NET.Input.Key.Z or Silk.NET.Input.Key.X
                    or Silk.NET.Input.Key.A or Silk.NET.Input.Key.S or Silk.NET.Input.Key.Up
                    or Silk.NET.Input.Key.Down or Silk.NET.Input.Key.Left or Silk.NET.Input.Key.Right
                    or Silk.NET.Input.Key.F1 or Silk.NET.Input.Key.F11)
                    Console.WriteLine($"[test-key] t={clock.Elapsed.TotalSeconds:F3}s key={key} down={e.Pressed}");
            });
        Console.WriteLine("[test-pad] virtual controller fixture enabled");
    }

    static void Poll(Action original)
    {
        original();
        if (virtualOnly)
        {
            Controller.State = Controller.State2 = ushort.MaxValue;
            Controller.LeftX = Controller.LeftY = Controller.RightX = Controller.RightY = 128;
        }
        long now = clock.ElapsedMilliseconds;
        if (traceHost && Controller.State != previousHostState)
        {
            previousHostState = Controller.State;
            Console.WriteLine($"[test-host-pad] t={now / 1000.0:F3}s buttons=0x{(ushort)~Controller.State:X4}");
        }
        if (now >= nextRead)
        {
            nextRead = now + 100;
            try
            {
                string command = File.ReadAllText(commandPath!).Trim();
                if (command != lastCommand)
                {
                    lastCommand = command;
                    // Unique command id, comma-separated PS1 button names, duration in ms.
                    var fields = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length == 3 && int.TryParse(fields[2], out int duration) && duration is > 0 and <= 5000)
                    {
                        ushort mask = 0;
                        axes.Clear();
                        foreach (var name in fields[1].Split(','))
                        {
                            var axis = name.Split('=');
                            if (axis.Length == 2 && axis[0] is "LeftX" or "LeftY" or "RightX" or "RightY" && byte.TryParse(axis[1], out byte value))
                            { axes[axis[0]] = value; continue; }
                            var field = typeof(Controller).GetField(name, BindingFlags.Public | BindingFlags.Static);
                            if (field?.IsLiteral != true || field.FieldType != typeof(ushort))
                                throw new ArgumentException("Unknown PS1 button: " + name);
                            mask |= (ushort)field.GetRawConstantValue()!;
                        }
                        held = mask;
                        releaseAt = now + duration;
                        Console.WriteLine($"[test-pad] t={now / 1000.0:F2}s buttons={fields[1]} hold={duration}ms");
                    }
                }
            }
            catch (IOException) { }
        }
        if ((held != 0 || axes.Count != 0) && now >= releaseAt)
        {
            held = 0;
            axes.Clear();
            Console.WriteLine($"[test-pad] t={now / 1000.0:F2}s released");
        }
        Controller.State &= (ushort)~held;
        foreach (var axis in axes) typeof(Controller).GetField(axis.Key)!.SetValue(null, axis.Value);
    }
}
