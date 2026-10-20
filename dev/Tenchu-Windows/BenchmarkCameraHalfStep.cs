using System.Reflection;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace TenchuSARecomp;

// Native 1/60 camera integration for the verified USA camera routines. This
// remains test-only alongside the actor half-step adaptation.
internal static class BenchmarkCameraHalfStep
{
    delegate void Guest(CpuContext cpu, IMemory memory);
    delegate void Detour(Guest original, CpuContext cpu, IMemory memory);
    static readonly List<Hook> hooks = new();
    static readonly Dictionary<uint, Spring> springs = new();
    static bool inCamera;
    static readonly int[] axisCarry = new int[2];
    sealed class Spring
    {
        public double Speed;
        public short LastStored;
        public readonly double[] Carry = new double[3];
    }

    public static void Install(Assembly game)
    {
        if (Environment.GetEnvironmentVariable("TENCHU_TEST_CAMERA_HALFSTEP") != "1") return;
        var type = game.GetType("Recompiled.Tenchu", true)!;
        void Add(string name, Detour handler) => hooks.Add(new Hook(type.GetMethod(name)!, handler));
        Add("func_8003104C", Camera);
        Add("func_8002F708", MakeDifSub);
        Add("func_8001AAB0", CameraAxes);
        Add("func_800312C4", CameraDirection);
        Console.WriteLine("[halfstep-camera] native camera integration installed");
    }

    static void Camera(Guest original, CpuContext cpu, IMemory memory)
    {
        bool previous = inCamera;
        inCamera = BenchmarkHalfStep.Enabled && cpu.RA == 0x80015A40;
        try { original(cpu, memory); }
        finally { inCamera = previous; }
    }

    static void MakeDifSub(Guest original, CpuContext c, IMemory m)
    {
        if (!inCamera) { original(c, m); return; }
        uint source = c.A0, target = c.A1, output = c.A2, info = c.A3;
        Span<double> d = stackalloc double[3];
        double lengthSquared = 0, previousSquared = 0, dot = 0;
        for (int i = 0; i < 3; i++)
        {
            d[i] = (int)m.ReadU32(target + (uint)i * 4) - (double)(int)m.ReadU32(source + (uint)i * 4);
            double before = (short)m.ReadU16(info + 6 + (uint)i * 2);
            lengthSquared += d[i] * d[i]; previousSquared += before * before; dot += d[i] * before;
        }
        if (lengthSquared == 0)
        {
            for (int i = 0; i < 3; i++) m.WriteU32(output + (uint)i * 4, 0);
            return;
        }
        double length = Math.Sqrt(lengthSquared);
        double cosine = previousSquared > 0 ? Math.Clamp(dot / Math.Sqrt(lengthSquared * previousSquared), -1, 1) : 0;
        short physicalSpeed = (short)m.ReadU16(info + 4);
        if (!springs.TryGetValue(info, out var state)) springs[info] = state = new Spring { Speed = physicalSpeed, LastStored = physicalSpeed };
        if (state.LastStored != physicalSpeed) state.Speed = physicalSpeed;
        // Two half-steps compose to v' = alignment*v + acceleration when
        // alignment is constant; acceleration and drag keep their 30 Hz units.
        double drag = Math.Sqrt((cosine + 1) * 0.5);
        double acceleration = (short)m.ReadU16(info + 2);
        double speed = state.Speed * drag + acceleration / (1 + drag);
        int divide = Math.Clamp((int)(short)m.ReadU16(info), 0, 20);
        double gain = Math.ScaleB(1, -divide);
        double cap = length * 2 * (1 - Math.Sqrt(1 - gain));
        state.Speed = Math.Clamp(speed, 0, cap);
        state.LastStored = (short)Math.Clamp((int)Math.Round(state.Speed), short.MinValue, short.MaxValue);
        m.WriteU16(info + 4, unchecked((ushort)state.LastStored));
        for (int i = 0; i < 3; i++)
        {
            double precise = d[i] / length * state.Speed * 0.5 + state.Carry[i];
            int step = (int)precise;
            state.Carry[i] = precise - step;
            m.WriteU32(output + (uint)i * 4, unchecked((uint)step));
            m.WriteU16(info + 6 + (uint)i * 2, unchecked((ushort)(short)d[i]));
        }
    }

    static void CameraAxes(Guest original, CpuContext c, IMemory m)
    {
        uint x = c.A1, y = c.A2;
        original(c, m);
        if (!inCamera) return;
        for (int i = 0; i < 2; i++)
        {
            uint address = i == 0 ? x : y;
            int sum = (short)m.ReadU16(address) + axisCarry[i], step = sum / 2;
            axisCarry[i] = sum - step * 2;
            m.WriteU16(address, unchecked((ushort)step));
        }
    }

    static void CameraDirection(Guest original, CpuContext c, IMemory m)
    {
        uint differences = c.A1;
        original(c, m);
        if (!inCamera) return;
        // The first-person path uses /8 for viewpoint and /4 for reference.
        // Convert each gain so two 60 Hz updates equal one original update.
        for (int i = 0; i < 6; i++)
        {
            double divisor = i < 3 ? 8 : 4;
            double multiplier = divisor * (1 - Math.Sqrt(1 - 1 / divisor));
            uint address = differences + (uint)i * 4;
            m.WriteU32(address, unchecked((uint)(int)Math.Round((int)m.ReadU32(address) * multiplier)));
        }
    }
}
