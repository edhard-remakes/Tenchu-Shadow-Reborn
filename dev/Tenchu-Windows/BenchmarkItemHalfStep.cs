using System.Reflection;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace TenchuSARecomp;

// Common USA projectile trajectory/rolling physics. Test-only until item
// lifetimes, inventory use and collision cases have completed validation.
internal static class BenchmarkItemHalfStep
{
    delegate void Guest(CpuContext cpu, IMemory memory);
    delegate void Detour(Guest original, CpuContext cpu, IMemory memory);
    static readonly List<Hook> hooks = new();
    static readonly Dictionary<uint, RollingMemory> rolling = new();
    static bool inItems;
    static long arcs, rolls;

    public static void Install(Assembly game)
    {
        if (Environment.GetEnvironmentVariable("TENCHU_TEST_ITEM_HALFSTEP") != "1") return;
        var type = game.GetType("Recompiled.Tenchu", true)!;
        void Add(string name, Detour handler) => hooks.Add(new Hook(type.GetMethod(name)!, handler));
        Add("func_8004952C", Items);
        Add("func_8003D55C", MoveFly);
        Add("func_8003CF90", MoveRolling);
        Console.WriteLine("[halfstep-items] native projectile/rolling integration installed");
    }

    static bool Active => BenchmarkHalfStep.Enabled && (inItems || BenchmarkHalfStep.InGameplayActors);

    static void Items(Guest original, CpuContext c, IMemory m)
    {
        bool prior = inItems;
        inItems = BenchmarkHalfStep.Enabled && c.RA == 0x80015A68;
        try { original(c, m); }
        finally { inItems = prior; }
    }

    static void MoveFly(Guest original, CpuContext c, IMemory m)
    {
        uint item = c.A0, data = c.A1;
        if (!Active || m.ReadU8(data + 0x28) != 0 || m.ReadU8(data + 0x25) == 0)
        {
            original(c, m); return;
        }
        int count = m.ReadU8(data + 0x24), total = m.ReadU8(data + 0x25);
        double time = Math.Max(0, count - BenchmarkHalfStep.Phase * 0.5);
        uint model = m.ReadU32(item + 0x10);
        Span<int> points = stackalloc int[3], velocity = stackalloc int[3];
        for (int axis = 0; axis < 3; axis++)
        {
            points[axis] = Curve(m, data, axis, time, total);
            if (count == 0)
            {
                int lastOriginalPoint = Curve(m, data, axis, 1, total);
                // Rolling velocity retains original units; its integrator
                // applies dt=0.5, so do not seed it from a half-step delta.
                velocity[axis] = points[axis] - lastOriginalPoint;
            }
        }
        for (int axis = 0; axis < 3; axis++)
            m.WriteU32(model + 0x18 + (uint)axis * 4, unchecked((uint)points[axis]));
        if (count == 0)
        {
            for (int axis = 0; axis < 3; axis++) m.WriteU16(data + 4 + (uint)axis * 2, unchecked((ushort)velocity[axis]));
            m.WriteU32(data, 0); m.WriteU8(data + 0xA, 0); m.WriteU8(data + 0x28, 1);
        }
        else if (BenchmarkHalfStep.Phase == 1) m.WriteU8(data + 0x24, (byte)(count - 1));
        if (++arcs == 1) Console.WriteLine("[halfstep-items] first native half-step flight evaluated");
    }

    static int Curve(IMemory m, uint data, int axis, double time, int total)
    {
        uint offset = (uint)axis * 4;
        int q = 4096 - (int)(time * 4096) / total;
        int squared = q * q >> 12, endWeight = 4096 - 2 * q + squared, middleWeight = 2 * q - 2 * squared;
        int end = (int)m.ReadU32(data + offset), start = (int)m.ReadU32(data + 0xC + offset), control = (int)m.ReadU32(data + 0x18 + offset);
        return unchecked(endWeight * end + middleWeight * control + squared * start) / 4096;
    }

    static void MoveRolling(Guest original, CpuContext c, IMemory m)
    {
        if (!Active) { original(c, m); return; }
        uint data = c.A1, model = m.ReadU32(c.A0 + 0x10);
        if (!rolling.TryGetValue(data, out var proxy)) rolling[data] = proxy = new RollingMemory();
        proxy.Inner = m; proxy.Data = data; proxy.Position = model + 0x18;
        Array.Clear(proxy.Writes);
        original(c, proxy);
        if (++rolls == 1) Console.WriteLine("[halfstep-items] first native half-step rolling update evaluated");
    }

    sealed class RollingMemory : BenchmarkHalfStep.Proxy
    {
        public uint Data, Position;
        public readonly int[] Writes = new int[3], Carry = new int[3];
        int gravityCarry;
        public override void WriteU32(uint a, uint v)
        {
            if (a >= Position && a <= Position + 8 && ((a - Position) & 3) == 0)
            {
                int axis = (int)(a - Position) / 4;
                int write = Writes[axis]++;
                uint before = Inner.ReadU32(a);
                int delta = unchecked((int)(v - before));
                int velocity = (short)Inner.ReadU16(Data + 4 + (uint)axis * 2);
                // Scale initial integration and its collision rollback; leave
                // absolute ground-level correction untouched.
                if (write == 0 || (write == 1 && delta == -velocity))
                {
                    int sum = delta + Carry[axis], step = sum / 2;
                    Carry[axis] = sum - step * 2;
                    v = unchecked(before + (uint)step);
                }
            }
            base.WriteU32(a, v);
        }
        public override void WriteU16(uint a, ushort v)
        {
            if (a == Data + 6 && Inner.ReadU8(Data + 0xA) == 0 && (short)v - (short)Inner.ReadU16(a) == 15)
            {
                int sum = 15 + gravityCarry, step = sum / 2;
                gravityCarry = sum - step * 2;
                v = unchecked((ushort)((short)Inner.ReadU16(a) + step));
            }
            base.WriteU16(a, v);
        }
    }
}
