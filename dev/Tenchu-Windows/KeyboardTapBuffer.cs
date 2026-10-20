using System.Diagnostics;
using System.Reflection;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using Silk.NET.Input;

namespace TenchuSARecomp;

// A key-down and key-up can arrive together between two GLFW polls. Preserve
// short game-control taps for 50 ms so the 30 Hz guest can sample them. Normal
// held keys still use the runtime's original state; OS repeat never extends a tap.
internal static class KeyboardTapBuffer
{
    static Hook? hook;
    static readonly Dictionary<Key, long> until = new();
    static readonly HashSet<Key> held = new();
    static readonly List<Key> expired = new();
    static readonly long minimumTicks = Stopwatch.Frequency / 20;

    public static void Install()
    {
        if (hook != null) return;
        var input = typeof(RecompOne.Runtime.Runtime).Assembly.GetType("RecompOne.Runtime.Host.InputManager", true)!;
        var keyboardPoll = input.GetMethod("PollKeyboard", BindingFlags.Static | BindingFlags.NonPublic)!;
        hook = new Hook(keyboardPoll, (Action<Action>)Poll);
        Event.AddListener<KeyboardEvent>(OnKey);
        Console.WriteLine("[input] short keyboard taps preserved for guest sampling");
    }

    static void OnKey(KeyboardEvent e)
    {
        var key = (Key)e.Key;
        if (!e.Pressed)
        {
            held.Remove(key);
            return;
        }
        if (Mask(ConfigManager.Game.Keys, key) == 0 && Mask(ConfigManager.Game.Keys2, key) == 0) return;
        if (held.Add(key)) until[key] = Stopwatch.GetTimestamp() + minimumTicks;
    }

    static void Poll(Action original)
    {
        original();
        if (until.Count == 0) return;
        long now = Stopwatch.GetTimestamp();
        expired.Clear();
        foreach (var (key, deadline) in until)
        {
            if (now >= deadline) { expired.Add(key); continue; }
            Controller.State &= (ushort)~Mask(ConfigManager.Game.Keys, key);
            Controller.State2 &= (ushort)~Mask(ConfigManager.Game.Keys2, key);
        }
        foreach (var key in expired) until.Remove(key);
    }

    static ushort Mask(KeyBindings cfg, Key key)
    {
        ushort mask = 0;
        Add(cfg.Cross, Controller.Cross); Add(cfg.Circle, Controller.Circle);
        Add(cfg.Square, Controller.Square); Add(cfg.Triangle, Controller.Triangle);
        Add(cfg.Start, Controller.Start); Add(cfg.Select, Controller.Select);
        Add(cfg.Up, Controller.Up); Add(cfg.Down, Controller.Down);
        Add(cfg.Left, Controller.Left); Add(cfg.Right, Controller.Right);
        Add(cfg.L1, Controller.L1); Add(cfg.R1, Controller.R1);
        Add(cfg.L2, Controller.L2); Add(cfg.R2, Controller.R2);
        Add(cfg.L3, Controller.L3); Add(cfg.R3, Controller.R3);
        return mask;
        void Add(string name, ushort bit)
        {
            if (Enum.TryParse<Key>(name, out var bound) && bound == key) mask |= bit;
        }
    }
}
