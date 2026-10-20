using System.Reflection;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace TenchuSARecomp;

internal static partial class BenchmarkRoninGameplay
{
    static bool grabEnabled, grabHit;
    static uint grabOwner, grabVictim;
    static long grabStarted, grabCooldown;
    const int GrabLength = 54, GrabImpactFrame = 27;

    static void InstallGrab(Type type)
    {
        grabEnabled = Environment.GetEnvironmentVariable("TENCHU_GAMEPLAY_GRAB") == "1";
        if (!grabEnabled) return;
        hooks.Add(new Hook(type.GetMethod("func_80016CBC")!, (Detour)GrabDraw));
        Console.WriteLine("[grab] Triangle: close-range pull/knee prototype; cost=20, posture=40, life=5, duration=54 frames");
    }

    static bool GrabAlive(IMemory m, uint a)
        => Pointer(a) && (short)m.ReadU16(a + 8) > 0 && Pointer(m.ReadU32(a + 0x5C));

    static bool GrabInRange(IMemory m, uint a, uint b)
    {
        uint p = m.ReadU32(a + 0x38), q = m.ReadU32(b + 0x38);
        if (!Pointer(p) || !Pointer(q)) return false;
        double dx = (int)m.ReadU32(q) - (double)(int)m.ReadU32(p);
        double dz = (int)m.ReadU32(q + 8) - (double)(int)m.ReadU32(p + 8);
        return dx * dx + dz * dz <= 1400.0 * 1400 &&
            Math.Abs((int)m.ReadU32(q + 4) - (double)(int)m.ReadU32(p + 4)) < 500;
    }

    static bool GrabPose(CpuContext c, IMemory m, uint a, uint clip)
    {
        if (!RequestMotion(c, m, a, clip)) return false;
        uint manager = m.ReadU32(a + 0x5C);
        m.WriteU16(manager + 2, 0); m.WriteU16(manager + 4, 0);
        m.WriteU32(manager + 8, uint.MaxValue);
        var helper = new CpuContext(); helper.Restore(c.Snapshot()); helper.SP -= 0x400;
        helper.A0 = manager; helper.A1 = 1; playMotion(helper, m);
        return true;
    }

    static void TryGrab(CpuContext c, IMemory m)
    {
        if (!grabEnabled || grabVictim != 0 || Frame < grabCooldown || itemAimSession || guardHeld ||
            !Gameplay(c, m) || BenchmarkStamina.IsBroken(player) ||
            m.ReadU16(player + 2) is not (0 or 1 or 2 or 5 or 6)) return;
        uint victim = target != 0 ? target : FindTarget(c, m);
        if (!TargetValid(c, m, victim) || !GrabInRange(m, player, victim) ||
            BenchmarkStamina.IsBroken(victim) || m.ReadU16(victim + 2) is not (0 or 1 or 2 or 5 or 6) ||
            (m.ReadU16(victim + 4) & 0x100) != 0) { ShowNotice("GRAB: aproxime-se de frente"); return; }
        uint p = m.ReadU32(player + 0x38), q = m.ReadU32(victim + 0x38);
        double facing = Angle((int)m.ReadU32(q) - (double)(int)m.ReadU32(p), (int)m.ReadU32(q + 8) - (double)(int)m.ReadU32(p + 8));
        uint rotation = m.ReadU32(player + 0x3C);
        if (!Pointer(rotation) || Math.Abs(Wrap(facing - (short)m.ReadU16(rotation + 2) * Math.PI * 2 / 4096)) > .95) return;
        if (!BenchmarkStamina.TryGrabCost(m, player)) { ShowNotice("STAMINA INSUFICIENTE"); return; }
        if (!GrabPose(c, m, player, 0x501) || !GrabPose(c, m, victim, 0x1001)) return;
        m.WriteU16(rotation + 2, (ushort)YawUnits(facing));
        uint vr = m.ReadU32(victim + 0x3C);
        if (Pointer(vr)) m.WriteU16(vr + 2, (ushort)YawUnits(facing + Math.PI));
        grabOwner = player; grabVictim = victim; grabStarted = Frame; grabHit = false;
        grabCooldown = Frame + 96; attackUntil = -1; heldGuardPose = false;
        ShowNotice("PUXAO + JOELHADA");
        Console.WriteLine($"[grab] begin frame={Frame} victim={victim:X8}");
    }

    static bool GrabAction(CpuContext c, IMemory m, uint actor)
    {
        if (!grabEnabled || grabVictim == 0) return false;
        if (actor != grabOwner && actor != grabVictim) return false;
        long age = Frame - grabStarted;
        bool valid = grabOwner == player && Gameplay(c, m) && GrabAlive(m, grabOwner) && GrabAlive(m, grabVictim) &&
            GrabInRange(m, grabOwner, grabVictim) && !BenchmarkStamina.IsBroken(grabOwner);
        if (!valid || age >= GrabLength)
        {
            uint a = grabOwner, b = grabVictim; grabOwner = grabVictim = 0;
            foreach (uint x in new[] { a, b })
                if (GrabAlive(m, x) && !BenchmarkStamina.IsBroken(x)) RequestMotion(c, m, x, 0x501);
            return false;
        }
        if (age >= GrabImpactFrame && !grabHit)
        {
            grabHit = true; BenchmarkStamina.GrabImpact(m, grabVictim);
            Console.WriteLine($"[grab] knee frame={Frame} victim={grabVictim:X8}");
        }
        // A broken victim immediately returns to native execution handling.
        if (grabHit && BenchmarkStamina.IsBroken(grabVictim))
        {
            uint owner = grabOwner; grabOwner = grabVictim = 0;
            if (GrabAlive(m, owner)) RequestMotion(c, m, owner, 0x501);
            return false;
        }
        m.WriteU16(actor + 0x40, 0); m.WriteU16(actor + 0x44, 0);
        return true;
    }

    static void GrabDraw(Guest original, CpuContext c, IMemory m)
    {
        if (grabVictim == 0 || !GrabAlive(m, grabOwner) || !GrabAlive(m, grabVictim)) { original(c, m); return; }
        bool owner = c.A0 == m.ReadU32(grabOwner + 0x58);
        if (!owner && c.A0 != m.ReadU32(grabVictim + 0x58)) { original(c, m); return; }
        uint objects = m.ReadU32(c.A0 + 0x68); int count = m.ReadU16(c.A0 + 0x64);
        if (!Pointer(objects) || count is < 12 or > 32) { original(c, m); return; }
        double age = Frame - grabStarted;
        double reach = Math.Clamp(age / 12, 0, 1) * Math.Clamp((54 - age) / 12, 0, 1);
        double knee = Math.Max(0, 1 - Math.Abs(age - 27) / 12);
        var saved = new List<(uint Address, byte[] Bytes)>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                uint obj = m.ReadU32(objects + (uint)i * 4);
                if (!Pointer(obj)) continue;
                var bytes = new byte[0x50];
                for (uint j = 0; j < bytes.Length; j++) bytes[j] = m.ReadU8(obj + j);
                saved.Add((obj, bytes));
                double angle = owner ? i switch { 1 => -.12 * knee, 3 or 5 => -.6 * reach, 4 or 6 => -.4 * reach,
                    10 => -1.3 * knee, 11 => 1.6 * knee, _ => 0 } : i == 1 ? .6 * knee : 0;
                if (angle != 0)
                {
                    // Local rotation only: keep native body positions and collision unchanged.
                    double co = Math.Cos(angle), si = Math.Sin(angle);
                    for (uint row = 0; row < 3; row++)
                    {
                        uint offset = obj + 4 + row * 6;
                        short y = (short)m.ReadU16(offset + 2), z = (short)m.ReadU16(offset + 4);
                        m.WriteU16(offset + 2, unchecked((ushort)(short)Math.Round(y * co + z * si)));
                        m.WriteU16(offset + 4, unchecked((ushort)(short)Math.Round(-y * si + z * co)));
                    }
                }
                m.WriteU32(obj, 0); // invalidate cached coordinate matrices
            }
            original(c, m);
        }
        finally
        {
            foreach (var (address, bytes) in saved)
                for (uint j = 0; j < bytes.Length; j++) m.WriteU8(address + j, bytes[j]);
        }
    }
}
