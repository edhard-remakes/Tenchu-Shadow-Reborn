using System.Diagnostics;
using System.Reflection;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace TenchuSARecomp;

// USA-only native half-step prototype. Test builds only: actor integration and
// animation sampling are adapted here; camera/items/combat are not yet audited.
internal static class BenchmarkHalfStep
{
    delegate void Guest(CpuContext cpu, IMemory memory);
    delegate void Detour(Guest original, CpuContext cpu, IMemory memory);
    static readonly List<Hook> hooks = new();
    static readonly Stopwatch clock = new();
    static readonly Dictionary<uint, ActorMemory> actors = new();
    static readonly Dictionary<uint, MotionMemory> motions = new();
    static int activateAt, phase;
    static bool enabled, inGameplayActors;
    static uint currentManager;
    static long poses, fractionalSamples, integratedAxes;
    static long sampleAt, priorPoses, priorFractional, priorIntegrated;
    static double requestedDistance, appliedDistance;
    public static bool Enabled => enabled;
    public static int Phase => phase;
    public static long FrameNumber { get; private set; }
    public static bool InGameplayActors => inGameplayActors;
    static readonly Dictionary<(uint Actor, uint Sound, uint Motion, ushort Id, ushort Count, ushort Loop), (long Frame, uint Result)> sounds = new();

    public static void Install(Assembly game)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("TENCHU_TEST_HALFSTEP_AT"), out activateAt)) return;
        var type = game.GetType("Recompiled.Tenchu", true)!;
        void Add(string name, Detour handler) => hooks.Add(new Hook(type.GetMethod(name)!, handler));
        Add("func_80016660", EndDrawing);
        Add("func_800275C8", ControlActor);
        Add("func_80028BD8", AllActors);
        Add("func_8001BB5C", PlayMotion);
        Add("func_8001B748", GetSpline);
        Add("func_8001B3D4", ActiveMotion);
        Add("func_80027A44", DefaultAction);
        Add("func_8001B598", SplineDerivatives);
        Add("func_8001BDE4", ActorAction);
        BenchmarkCameraHalfStep.Install(game);
        BenchmarkItemHalfStep.Install(game);
        BenchmarkRoninGameplay.Install(game);
        BenchmarkFramePacing.Install(game);
        BenchmarkHdRikimaru.Install(game);
        if (Environment.GetEnvironmentVariable("TENCHU_TEST_LEGACY_TIMERS") == "1")
        {
            Add("func_80017804_game", StartDrawing);
            Add("func_8004EFE4", ActorSound);
            Add("func_8004D2E4", StageSequence);
        }
        clock.Start();
        Console.WriteLine($"[halfstep] actor/animation adaptation scheduled at {activateAt}s");
    }

    static void EndDrawing(Guest original, CpuContext cpu, IMemory memory)
    {
        if (cpu.RA == 0x80015B08 && cpu.A0 == 0xFFFFFFFE)
        {
            BenchmarkFramePacing.End();
            int hitStop = BenchmarkStamina.TakeHitStop();
            if (hitStop > 0) { Console.WriteLine($"[stamina] impact freeze {hitStop}ms frame={FrameNumber}"); Thread.Sleep(hitStop); }
            long now = clock.ElapsedMilliseconds;
            if (!enabled && now >= activateAt * 1000L)
            {
                enabled = true; phase = 0; sampleAt = now;
                Console.WriteLine("[halfstep] enabled; native loop 60 Hz, actor dt=1/60, animation time step=0.5");
            }
            else if (enabled) phase ^= 1;
            if (enabled) FrameNumber++;
            if (enabled && now - sampleAt >= 3000)
            {
                double dt = (now - sampleAt) / 1000.0;
                Console.WriteLine($"[halfstep] t={now / 1000.0:F2}s actor-poses/s={(poses - priorPoses) / dt:F2} fractional-spline-samples/s={(fractionalSamples - priorFractional) / dt:F2} scaled-integration-writes/s={(integratedAxes - priorIntegrated) / dt:F2} player-requested-distance={requestedDistance:F1} player-applied-distance={appliedDistance:F1}");
                requestedDistance = appliedDistance = 0;
                sampleAt = now; priorPoses = poses; priorFractional = fractionalSamples; priorIntegrated = integratedAxes;
            }
        }
        original(cpu, memory);
    }

    static void ControlActor(Guest original, CpuContext cpu, IMemory memory)
    {
        if (!enabled || !inGameplayActors) { original(cpu, memory); return; }
        uint actor = cpu.A0;
        if (!actors.TryGetValue(actor, out var proxy)) actors[actor] = proxy = new ActorMemory();
        proxy.Inner = memory; proxy.Cpu = cpu; proxy.Actor = actor;
        proxy.Position = memory.ReadU32(actor + 0x38);
        proxy.Rotation = memory.ReadU32(actor + 0x3C);
        proxy.Trace = memory.ReadU32(actor + 0x70);
        proxy.Player = memory.ReadU32(0x8009590C) == actor;
        BenchmarkExecution.Trace(memory,actor,FrameNumber);
        if(!BenchmarkRoninGameplay.Enabled || !BenchmarkExecution.IsPairedActor(memory,actor))
        { original(cpu,proxy); return; }
        int savedPhase=phase, ticks=BenchmarkExecution.Ticks(FrameNumber);
        var entry=cpu.Snapshot();
        try
        {
            for(int i=0;i<ticks;i++)
            {
                if(i>0)
                {
                    if(!BenchmarkExecution.IsPairedActor(memory,actor)) break;
                    cpu.Restore(entry);
                }
                phase=(int)((BenchmarkExecution.TickStart(FrameNumber)+i)&1);
                original(cpu,proxy);
            }
        }
        finally { phase=savedPhase; }
    }

    static void StartDrawing(Guest original, CpuContext c, IMemory m)
    {
        BenchmarkFramePacing.Begin(c,m);
        bool halfClock = enabled && phase == 0 && c.RA == 0x80015A38;
        if (!halfClock) { original(c, m); return; }
        uint address = c.GP + 0x18, before = m.ReadU32(address);
        original(c, m);
        // GameClock retains its original 30-ticks/second unit for stage time,
        // scripts and saved scores. The main simulation still executes at 60 Hz.
        if (halfClock) m.WriteU32(address, before);
    }

    static void StageSequence(Guest original, CpuContext c, IMemory m)
    {
        if (!enabled || c.RA != 0x800159E4) { original(c, m); return; }
        original(c, new StageMemory { Inner = m, TimeAddress = c.GP + 0x5D0 });
    }

    static void ActorSound(Guest original, CpuContext c, IMemory m)
    {
        if (!enabled || !inGameplayActors) { original(c, m); return; }
        uint actor = c.A0, sound = c.A1, motion = m.ReadU32(actor + 0x5C);
        var key = (actor, sound, motion, m.ReadU16(motion), m.ReadU16(motion + 2), m.ReadU16(motion + 4));
        if (sounds.TryGetValue(key, out var prior) && (prior.Frame == FrameNumber || prior.Frame + 1 == FrameNumber) && prior.Result != uint.MaxValue)
        {
            c.V0 = prior.Result;
            return;
        }
        original(c, m);
        sounds[key] = (FrameNumber, c.V0);
        if (sounds.Count > 4096)
            foreach (var stale in sounds.Where(pair => pair.Value.Frame + 2 < FrameNumber).Select(pair => pair.Key).ToArray()) sounds.Remove(stale);
    }

    static void AllActors(Guest original, CpuContext cpu, IMemory memory)
    {
        bool previous = inGameplayActors;
        inGameplayActors = enabled && cpu.RA == 0x80015A48;
        try
        {
            if(inGameplayActors)
            {
                int count=Math.Clamp((int)memory.ReadU16(cpu.GP+0x8C),0,64);
                BenchmarkRoninGameplay.HeavyFixtureTick(cpu,memory,count);
                BenchmarkRoninGameplay.TickHeavyContacts(cpu,memory);
            }
            original(cpu,memory);
        }
        finally { inGameplayActors = previous; }
    }

    static void PlayMotion(Guest original, CpuContext cpu, IMemory memory)
    {
        if (!enabled || !inGameplayActors) { original(cpu, memory); return; }
        uint manager = cpu.A0;
        if (phase == 0 && (short)cpu.A1 == 0)
        {
            cpu.V0 = unchecked((uint)(short)memory.ReadU16(manager + 2));
            return;
        }
        if (!motions.TryGetValue(manager, out var proxy)) motions[manager] = proxy = new MotionMemory();
        proxy.Inner = memory; proxy.Manager = manager;
        proxy.Sweeping = (short)memory.ReadU16(manager + 2) < 0;
        if (proxy.Sweeping) proxy.FindSweepFields();
        uint previous = currentManager;
        currentManager = manager;
        try { original(cpu, proxy); poses++; }
        finally { currentManager = previous; }
    }

    static void ActiveMotion(Guest original, CpuContext cpu, IMemory memory)
    {
        uint manager = cpu.A0;
        bool firstHalfOfFirstFrame = enabled && (phase == 0 || BenchmarkRoninGameplay.HeavyClock(memory,manager)) && memory.ReadU16(manager + 2) == 0 &&
            memory.ReadU16(memory.ReadU32(manager + 0x10) + 4) > 0;
        original(cpu, memory);
        // The original caller treats zero as end-of-animation. A held counter
        // at the first half-frame is still an active animation, not completion.
        if (firstHalfOfFirstFrame && cpu.V0 == 0 && memory.ReadU16(manager + 2) == 0) cpu.V0 = 1;
    }

    static void DefaultAction(Guest original, CpuContext cpu, IMemory memory)
    {
        if (memory is not ActorMemory actor) { original(cpu, memory); return; }
        actor.GravityActive = true;
        try { original(cpu, memory); }
        finally { actor.GravityActive = false; }
    }

    static void ActorAction(Guest original, CpuContext cpu, IMemory memory)
    {
        if (memory is not ActorMemory actor) { original(cpu, memory); return; }
        actor.TurnRateScope = true;
        try { BenchmarkRoninGameplay.ActorAction(original.Invoke, cpu, memory); }
        finally { actor.TurnRateScope = false; }
    }

    static void GetSpline(Guest original, CpuContext cpu, IMemory memory)
    {
        if (!enabled || currentManager == 0) { original(cpu, memory); return; }
        // Double only the spline's time coordinates, not positions/angles.
        // Sampling at 2*frame + phase evaluates genuine half-frame bone poses.
        uint control = cpu.A1;
        uint a = memory.ReadU32(control), b = memory.ReadU32(control + 4);
        bool heavyClock=BenchmarkRoninGameplay.HeavyClock(memory,currentManager);
        int scale=heavyClock?32:2;
        var proxy = new SplineTimeMemory { Inner = memory, Control = control,Scale=scale,
            Low = Math.Min(a, b) - 8, High = Math.Max(a, b) + 16 };
        int duration = (short)memory.ReadU16(memory.ReadU32(currentManager + 0x10) + 4);
        cpu.A2 = unchecked((uint)Math.Min(heavyClock && (short)cpu.A2>=0 ? (int)(BenchmarkRoninGameplay.HeavySample*scale) : (short)cpu.A2*scale+phase,duration*scale));
        original(cpu, proxy);
        if (phase == 1) fractionalSamples++;
    }

    static void SplineDerivatives(Guest original, CpuContext cpu, IMemory memory)
    {
        // Hermite tangents are invariant under a uniform change of time scale.
        // Compute them in original units, avoiding the SDK's signed-byte delta
        // overflow for long keyframe intervals after doubling their time values.
        original(cpu, memory is SplineTimeMemory spline ? spline.Inner : memory);
    }

    internal class Proxy : IMemory
    {
        public IMemory Inner = null!;
        public virtual byte ReadU8(uint a) => Inner.ReadU8(a);
        public virtual ushort ReadU16(uint a) => Inner.ReadU16(a);
        public virtual uint ReadU32(uint a) => Inner.ReadU32(a);
        public virtual void WriteU8(uint a, byte v) => Inner.WriteU8(a, v);
        public virtual void WriteU16(uint a, ushort v) => Inner.WriteU16(a, v);
        public virtual void WriteU32(uint a, uint v) => Inner.WriteU32(a, v);
        public uint ReadWordLeft(uint v, uint a) => Inner.ReadWordLeft(v, a);
        public uint ReadWordRight(uint v, uint a) => Inner.ReadWordRight(v, a);
        public void WriteWordLeft(uint a, uint v) => Inner.WriteWordLeft(a, v);
        public void WriteWordRight(uint a, uint v) => Inner.WriteWordRight(a, v);
        public void LoadBytes(uint a, byte[] data) => Inner.LoadBytes(a, data);
        public void ZeroRange(uint a, uint n) => Inner.ZeroRange(a, n);
        public void SetCd(CdController cd) => Inner.SetCd(cd);
    }

    sealed class ActorMemory : Proxy
    {
        public CpuContext Cpu = null!;
        public uint Actor, Position, Rotation, Trace;
        public bool Player, GravityActive, TurnRateScope;
        readonly Dictionary<uint, int> remainders = new();
        public override ushort ReadU16(uint a)
        {
            ushort value = base.ReadU16(a);
            // Humanoid.turn is the per-update angular step, not a target angle.
            if (TurnRateScope && a == Actor + 6) return unchecked((ushort)((short)value / 2));
            return value;
        }
        public override void WriteU32(uint a, uint v)
        {
            if (phase == 0 && a == Actor + 0xB0 && (Inner.ReadU32(a) & 0xFF) > 0 && v == unchecked(Inner.ReadU32(a) - 1)) return;
            // Verified USA integration block between PlayMotion's return and
            // UpdateCoordinate: x += vx, z += vz, y += vy. Do not scale snaps.
            if (Cpu.RA == 0x800276C8 && (a == Position || a == Position + 4 || a == Position + 8))
            {
                uint old = Inner.ReadU32(a);
                int delta = unchecked((int)(v - old));
                if(Player && a!=Position+4)delta=BenchmarkRoninGameplay.HeavyForward(Inner,Actor,delta,a==Position);
                if(Player)delta=(int)Math.Round(delta*BenchmarkRoninGameplay.HeavyTempo(Inner,Actor));
                remainders.TryGetValue(a, out int carry);
                int sum = delta + carry;
                int step = sum / 2;
                remainders[a] = sum - step * 2;
                if (Player && a != Position + 4) { requestedDistance += Math.Abs(delta); appliedDistance += Math.Abs(step); }
                v = unchecked(old + (uint)step); integratedAxes++;
            }
            base.WriteU32(a, v);
        }
        public override void WriteU16(uint a, ushort v)
        {
            if(Player && a==Actor+8 && (short)v<(short)Inner.ReadU16(a) &&
                BenchmarkRoninGameplay.Enabled && (BenchmarkCrowd.Protects(Inner,Actor) || BenchmarkExecution.IsExecuting(Inner,Actor))) return;
            // Original guard recoil (0500) uses a negative loop counter, not
            // the normal animation clock. Preserve its 30 Hz time units too.
            uint guardMotion = Inner.ReadU32(Actor + 0x5C);
            if ((a == guardMotion + 2 || a == Actor + 0x40 || a == Actor + 0x44) &&
                Inner.ReadU16(guardMotion) == 0x500 && (short)Inner.ReadU16(guardMotion + 4) < 0)
            {
                short old = (short)Inner.ReadU16(a), change = unchecked((short)(v - old));
                if (a == guardMotion + 2 && change == -1 && phase == 0) return;
                if (a != guardMotion + 2 && Math.Abs((int)change) == 4) v = unchecked((ushort)(old + change / 2));
            }
            // USA ActATTACK's inlined TrackAttackTarget uses a fixed +/-100
            // angular step instead of Humanoid.turn. Its verified return-site
            // tag distinguishes this rate from instantaneous facing changes.
            if (a == Rotation + 2 && Cpu.RA == 0x800213A8)
            {
                if(Player && BenchmarkRoninGameplay.HeavyStrike(Inner,Actor))return;
                ushort oldYaw = Inner.ReadU16(a);
                v = unchecked((ushort)(oldYaw + (short)(v - oldYaw) / 2));
            }
            if (phase == 0 && a == Actor + 0x16 && v == unchecked((ushort)(Inner.ReadU16(a) + 1))) return;
            if (phase == 0 && Trace != 0 && a == Trace + 2)
            {
                ushort old = Inner.ReadU16(a);
                if (v == unchecked((ushort)(old + 1)) || v == unchecked((ushort)(old - 1))) return;
            }
            // HUMAN.C: airborne vy += 20 at 30 Hz; use +10 at 60 Hz.
            if (GravityActive && a == Actor + 0x42 && (short)v - (short)Inner.ReadU16(a) == 20)
                v = unchecked((ushort)((short)Inner.ReadU16(a)+(int)Math.Round(10*BenchmarkRoninGameplay.HeavyTempo(Inner,Actor))));
            base.WriteU16(a, v);
        }
        public override void WriteU8(uint a, byte v)
        {
            if (phase == 0 && (a == Actor + 0x8A || a == Actor + 0x8B))
            {
                byte old = Inner.ReadU8(a);
                if (v == unchecked((byte)(old + 1)) || v == unchecked((byte)(old - 1))) return;
            }
            base.WriteU8(a, v);
        }
    }

    sealed class MotionMemory : Proxy
    {
        public uint Manager;
        public bool Sweeping;
        readonly HashSet<uint> positions = new(), rotations = new();
        readonly Dictionary<uint, int> residual = new();
        public void FindSweepFields()
        {
            positions.Clear(); rotations.Clear();
            uint model = Inner.ReadU32(Manager + 0xC);
            uint parts = Inner.ReadU32(model + 0x68);
            int n = Math.Clamp((int)(short)Inner.ReadU16(Manager + 6), 0, 32);
            for (int i = 0; i < n; i++)
            {
                uint part = Inner.ReadU32(parts + (uint)i * 4);
                if (i == 0) { positions.Add(part + 0x18); positions.Add(part + 0x1C); positions.Add(part + 0x20); }
                rotations.Add(part + 0x50); rotations.Add(part + 0x52); rotations.Add(part + 0x54);
            }
        }
        int Half(uint a, int delta)
        {
            residual.TryGetValue(a, out int carry);
            int sum = delta + carry, step = sum / 2;
            residual[a] = sum - step * 2;
            return step;
        }
        public override void WriteU16(uint a, ushort v)
        {
            ushort old = Inner.ReadU16(a);
            if (a == Manager + 2 && v == unchecked((ushort)(old + 1)))
            {
                if(BenchmarkRoninGameplay.HeavyClock(Inner,Manager))
                {if(v>(int)BenchmarkRoninGameplay.HeavySample)return;}
                else if(phase==0)return;
            }
            if (Sweeping && rotations.Contains(a)) v = unchecked((ushort)((short)old + Half(a, (short)v - (short)old)));
            base.WriteU16(a, v);
        }
        public override void WriteU32(uint a, uint v)
        {
            if (Sweeping && positions.Contains(a))
            {
                uint old = Inner.ReadU32(a);
                v = unchecked(old + (uint)Half(a, unchecked((int)(v - old))));
            }
            base.WriteU32(a, v);
        }
    }

    sealed class SplineTimeMemory : Proxy
    {
        public int Scale=2;
        public uint Control, Low, High;
        public override ushort ReadU16(uint a)
        {
            ushort value = base.ReadU16(a);
            if (a == Control + 0xE || (a >= Low && a < High && ((a - Low) & 7) == 6))
                return unchecked((ushort)(value * Scale));
            return value;
        }
    }

    sealed class StageMemory : Proxy
    {
        public uint TimeAddress;
        public override void WriteU32(uint a, uint v)
        {
            if (phase == 0 && a == TimeAddress && v == unchecked(Inner.ReadU32(a) + 1)) return;
            base.WriteU32(a, v);
        }
    }
}
