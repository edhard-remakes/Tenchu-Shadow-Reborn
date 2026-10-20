using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using ImGuiNET;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;

namespace TenchuSARecomp;

// Opt-in gameplay extension for the verified USA revision. No disc or AI
// awareness flags are patched. Native paired executions remain authoritative.
internal static class BenchmarkStamina
{
    delegate void Guest(CpuContext c, IMemory m);
    delegate void Detour(Guest original, CpuContext c, IMemory m);
    static readonly List<Hook> hooks = new();
    static readonly Dictionary<uint, Human> humans = new();
    static readonly Dictionary<(uint Defender, uint Attacker), Contact> contacts = new();
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static Guest setMotion = null!, playMotion = null!, cancelAttack = null!, passage = null!, attackControl = null!, requestLifeBar = null!;
    static Type gameType = null!;
    static bool enabled, handlersInstalled;
    static long frame;
    static uint player, executionVictim;
    static bool executionScope;
    static int hitStop;
    static bool fixture;
    static bool hudFixture, hudFixtureUsed;
    static uint fixtureVictim;
    static long fixtureFrame;
    static HudFrame hud = new(0, 100, false, Array.Empty<Bar>());
    const float Maximum = 100, BlockCost = 25, ParryCost = 55, RegenPerSecond = 20;
    const int RecoveryDelay = 48, BreakDuration = 180;
    sealed class Human
    {
        public uint Actor, Model;
        public ushort Kind;
        public float Value = Maximum;
        public long LastFrame, LastSpend = -RecoveryDelay, BrokenUntil;
        public bool Frozen;
        public bool PendingBreak, ReactionSeen;
        public long ReactionStarted;
        public long Swing;
        public ushort LastMotion, LastCount, LastLoop;
    }
    readonly record struct Contact(long Swing, ushort Motion, long Frame, bool Parried);
    readonly record struct Bar(float X, float Y, float Width, float Height, float Value, bool Broken);
    static Bar[] enemyBars = Array.Empty<Bar>();
    static long enemyBarsFrame = -100;
    sealed record HudFrame(long Time, float PlayerValue, bool PlayerBroken, Bar[] Bars);
    static bool Pointer(uint p) => p is >= 0x80010000 and < 0x80200000 && (p & 3) == 0;
    static bool HumanAlive(IMemory m, uint actor) => Pointer(actor) && m.ReadU16(actor) < 0xA0 && (short)m.ReadU16(actor + 8) > 0;
    public static bool IsBroken(uint actor) => enabled && humans.TryGetValue(actor, out var h) && h.BrokenUntil > frame;
    internal static bool HeavyGuardSurvives(uint actor)=>enabled && humans.TryGetValue(actor,out var h) &&
        h.Value>50 && !h.PendingBreak && !IsBroken(actor);
    public static int TakeHitStop() => enabled ? Interlocked.Exchange(ref hitStop, 0) : 0;

    public static bool TryGrabCost(IMemory m, uint actor)
    {
        if (!enabled || !HumanAlive(m, actor) || IsBroken(actor)) return false;
        var h = Ensure(m, actor);
        if (h.Value < 20) return false;
        Spend(h, 20); return true;
    }

    public static void GrabImpact(IMemory m, uint actor)
    {
        if (!enabled || !HumanAlive(m, actor)) return;
        // Nonlethal hit: leave native death/execution routines authoritative.
        m.WriteU16(actor + 8, (ushort)Math.Max(1, (short)m.ReadU16(actor + 8) - 5));
        Spend(Ensure(m, actor), 40);
    }

    public static void Install(Assembly game)
    {
        if (Environment.GetEnvironmentVariable("TENCHU_GAMEPLAY_STAMINA") != "1") return;
        gameType = game.GetType("Recompiled.Tenchu", true)!;
        fixture = Environment.GetEnvironmentVariable("TENCHU_TEST_EXECUTION") == "1" &&
            int.TryParse(Environment.GetEnvironmentVariable("TENCHU_BENCHMARK_SECONDS"), out int seconds) && seconds > 0;
        Guest Function(string name) => gameType.GetMethod(name)!.CreateDelegate<Guest>();
        setMotion = Function("func_80026A3C"); playMotion = Function("func_8001BB5C");
        cancelAttack = Function("func_80026E54"); passage = Function("func_800196F4");
        attackControl = Function("func_8001E2D4");
        requestLifeBar = Function("func_800499CC");
        hudFixture = Environment.GetEnvironmentVariable("TENCHU_TEST_HUD") == "1" &&
            int.TryParse(Environment.GetEnvironmentVariable("TENCHU_BENCHMARK_SECONDS"), out int hudSeconds) && hudSeconds > 0;
        void Add(string name, Detour action) => hooks.Add(new Hook(gameType.GetMethod(name)!, action));
        Add("func_8001AC34", ActionMotion);
        Add("func_80028BD8", AllActors);
        Add("func_8001BDE4", ActorAction);
        Add("func_8001CC80", Damage);
        Add("func_8001E2D4", Attack);
        Add("func_80029198", Nearest);
        Add("func_8004A14C", EnemyLifeHud);
        PanelManager.Register(new StaminaHud());
        enabled = true;
        Console.WriteLine("[stamina] 100 points for every human; block costs defender 25; parry costs attacker 55; break=3s; native executions during enemy break");
    }

    static Human Ensure(IMemory m, uint actor)
    {
        uint model = m.ReadU32(actor + 0x58); ushort kind = m.ReadU16(actor);
        if (!humans.TryGetValue(actor, out var h) || h.Model != model || h.Kind != kind)
            humans[actor] = h = new Human { Actor = actor, Model = model, Kind = kind, LastFrame = frame };
        return h;
    }

    static void Tick(Human h)
    {
        long elapsed = Math.Clamp(frame - h.LastFrame, 0, 3);
        h.LastFrame = frame;
        if (h.PendingBreak) return;
        if (h.BrokenUntil != 0)
        {
            if (frame < h.BrokenUntil) return;
            h.BrokenUntil = 0; h.Value = 60; h.LastSpend = frame;
        }
        else if (frame - h.LastSpend >= RecoveryDelay) h.Value = Math.Min(Maximum, h.Value + RegenPerSecond * elapsed / 60f);
    }

    static void BeginBreak(Human h)
    {
        h.PendingBreak = false;
        h.BrokenUntil = frame + BreakDuration;
        BenchmarkCombatAudio.GuardBreak();
        if (fixture && h.Actor == fixtureVictim) fixtureFrame = frame;
        // Guard break keeps running without a global impact pause.
        Console.WriteLine($"[stamina] posture broken actor=0x{h.Actor:X8} until={h.BrokenUntil}");
    }

    static void Spend(Human h, float amount) => SpendCore(h, amount, false);
    static void AdvanceReaction(Human h, ushort motion)
    {
        if (!h.PendingBreak) return;
        if (motion == 0x1001) h.ReactionSeen = true;
        if ((h.ReactionSeen && motion != 0x1001 && (motion >> 8) != 16) || frame - h.ReactionStarted >= 180) h.PendingBreak = false;
    }
    static void SpendCore(Human h, float amount, bool waitForReaction)
    {
        if (h.BrokenUntil > frame || h.PendingBreak) return;
        h.Value = Math.Max(0, h.Value - amount); h.LastSpend = frame;
        if (h.Value == 0)
        {
            BeginBreak(h);
            if (waitForReaction) { h.PendingBreak = true; h.ReactionSeen = false; h.ReactionStarted = frame; }
        }
    }

    static bool ContactOnce(uint defender, uint attacker, IMemory m, bool parry)
    {
        var h = Ensure(m, attacker); ushort motion = m.ReadU16(m.ReadU32(attacker + 0x5C));
        var key = (defender, attacker);
        if (contacts.TryGetValue(key, out var before))
        {
            if (before.Frame == frame && before.Parried) return false;
            if (before.Swing == h.Swing && before.Motion == motion && frame - before.Frame < 30) return false;
        }
        contacts[key] = new Contact(h.Swing, motion, frame, parry);
        return true;
    }

    public static void Parry(uint defender, uint attacker, CpuContext c, IMemory m)
    {
        if (!enabled || !HumanAlive(m, defender) || !HumanAlive(m, attacker)) return;
        if (ContactOnce(defender, attacker, m, true)) SpendCore(Ensure(m, attacker), ParryCost, HasMotion(m, attacker, 0x1001));
    }

    static void Damage(Guest original, CpuContext c, IMemory m)
    {
        uint victim = m.ReadU32(c.GP + 0x80C);
        if (!BenchmarkHalfStep.InGameplayActors || !HumanAlive(m, victim)) { original(c, m); return; }
        int id = m.ReadU16(victim + 0x46);
        uint attacker = id < 64 ? m.ReadU32(0x800B9D84 + (uint)id * 120) : 0;
        ushort life = m.ReadU16(victim + 8);
        uint weapon = id < 64 ? m.ReadU32(0x800B9D60 + (uint)id * 120) : 0;
        bool activeWeapon = Pointer(weapon) && m.ReadU16(weapon + 0x58) != ushort.MaxValue;
        bool melee = HumanAlive(m, attacker) && attacker != victim && m.ReadU16(attacker + 2) == 7;
        bool heavy = melee && activeWeapon && (BenchmarkRoninGameplay.SecondHeavyContact || BenchmarkRoninGameplay.HeavyStrike(m,attacker));
        bool hitBroken=melee && attacker==m.ReadU32(0x8009590C) && victim!=attacker && IsBroken(victim) && !BenchmarkExecution.IsExecuting(m,attacker);
        IMemory contactMemory=hitBroken ? new BrokenContactMemory{Inner=m,Actor=victim,GP=c.GP}:m;
        if(heavy) BenchmarkRoninGameplay.HeavyDamage(original.Invoke,c,contactMemory,victim);
        else original(c, contactMemory);
        if(hitBroken && m.ReadU16(victim+8)<life)
        {
            EndBreakOnHit(victim);
            if(!heavy)BenchmarkRoninGameplay.LaunchBrokenHit(c,m,attacker,victim);
        }
        if (!melee || !activeWeapon || m.ReadU16(weapon + 0x58) != ushort.MaxValue || m.ReadU16(victim + 8) != life || m.ReadU16(m.ReadU32(victim + 0x5C)) != 0x500) return;
        if (contacts.TryGetValue((victim, attacker), out var contact) && contact.Frame == frame && contact.Parried) return;
        if (ContactOnce(victim, attacker, m, false)) SpendHit(c, m, victim, heavy ? 50 : BlockCost);
    }
    internal static void ReplayHeavyContact(Action<CpuContext,IMemory> original,CpuContext c,IMemory m,uint victim,uint attacker)
    {contacts.Remove((victim,attacker));Damage(original.Invoke,c,m);}
    internal static void EndBreakOnHit(uint actor)
    {
        if(!humans.TryGetValue(actor,out var h))return;
        h.BrokenUntil=0;h.PendingBreak=false;h.Frozen=false;h.ReactionSeen=false;
        h.Value=60;h.LastSpend=frame;
        Console.WriteLine($"[stamina] connected follow-up ends break actor={actor:X8} frame={frame}");
    }
    sealed class BrokenContactMemory : BenchmarkHalfStep.Proxy
    {
        public uint Actor,GP;bool preparing=true;
        public override ushort ReadU16(uint address)
        {
            if(address==Actor+2)return 7; // no automatic guarding while posture is broken
            if(preparing && address==GP+0x824)return 0x501;
            return base.ReadU16(address);
        }
        public override void WriteU16(uint address,ushort value)
        {if(address==GP+0x824)preparing=false;base.WriteU16(address,value);}
    }

    static void SpendHit(CpuContext c, IMemory m, uint actor, float amount)
    {
        var h = Ensure(m, actor);
        bool wasBroken = IsBroken(actor) || h.PendingBreak;
        SpendCore(h, amount, HasMotion(m, actor, 0x1001));
        if (!wasBroken && h.PendingBreak)
        {
            // A blocked hit ends in 0500. Start the same full recoil used by
            // parry before ActorAction holds its vulnerable pose. Do this only
            // on the breaking contact, never on subsequent hits.
            if (Motion(c, m, actor, 0x1001)) h.ReactionSeen = true;
            else h.PendingBreak = false; // missing/failed clip must not lock AI
        }
    }

    static CpuContext Helper(CpuContext c) { var helper = new CpuContext(); helper.Restore(c.Snapshot()); helper.SP -= 0x800; return helper; }
    static bool Motion(CpuContext c, IMemory m, uint actor, uint id)
    {
        var helper = Helper(c); helper.A0 = actor; helper.A1 = id; helper.A2 = 1;
        setMotion(helper, m); return helper.V0 != 0;
    }

    static void InstallStateHandlers(IMemory m)
    {
        if (handlersInstalled) return;
        var methods = new HashSet<uint>();
        for (uint i = 0; i < 18; i++)
        {
            uint address = m.ReadU32(0x80084C68 + i * 4);
            if (!methods.Add(address)) continue;
            string name = $"func_{address:X8}";
            var method = gameType.GetMethod(name) ?? gameType.GetMethod(name + "_game") ?? throw new MissingMethodException(name);
            hooks.Add(new Hook(method, (Detour)StateAction));
        }
        handlersInstalled = true;
    }

    static void AllActors(Guest original, CpuContext c, IMemory m)
    {
        if (!enabled || c.RA != 0x80015A48 || m.ReadU16(c.GP + 0x78) != 0) { original(c, m); return; }
        frame = BenchmarkHalfStep.FrameNumber; player = m.ReadU32(0x8009590C);
        InstallStateHandlers(m);
        int count = Math.Clamp((int)m.ReadU16(c.GP + 0x8C), 0, 64);
        for (uint i = 0; i < count; i++)
        {
            uint actor = m.ReadU32(0x800BC410 + i * 4);
            if (!HumanAlive(m, actor)) { humans.Remove(actor); continue; }
            BenchmarkRoninGameplay.ObserveHeavyAlive(m,actor);
            Tick(Ensure(m, actor));
        }
        if (fixture) PrepareFixture(c, m, count);
        BenchmarkCrowd.Tick(c,m);
        original(c, m);
        Snapshot(c, m);
        if (contacts.Count > 1024)
            foreach (var key in contacts.Where(p => frame - p.Value.Frame > 300).Select(p => p.Key).ToArray()) contacts.Remove(key);
    }

    // Isolated, timed integration fixture: exercises the actual guest combat
    // and paired animations, rather than substituting a fake execution.
    static void PrepareFixture(CpuContext c, IMemory m, int count)
    {
        if (fixtureVictim != 0 || frame < 30 || !HumanAlive(m, player)) return;
        for (uint i = 0; i < count; i++)
        {
            uint actor = m.ReadU32(0x800BC410 + i * 4);
            int kind = m.ReadU16(actor);
            if (!HumanAlive(m, actor) || !(kind is >= 7 and <= 9 or >= 0x10 and < 0x80)) continue;
            uint am = m.ReadU32(actor + 0x5C);
            string clips = string.Join(",", Enumerable.Range(0, 3).Select(j => $"{0x1109+j:X4}:{HasMotion(m,actor,(ushort)(0x1109+j))}"));
            Console.WriteLine($"[execution-test] candidate={actor:X8} kind={m.ReadU16(actor):X2} model={m.ReadU32(actor+0x58):X8} registry={m.ReadU32(am+0x14):X8} clips={clips}");
            if (!ExecutionClipsAvailable(m, actor)) continue;
            bool reactionTest = Environment.GetEnvironmentVariable("TENCHU_TEST_REACTION") == "1";
            fixtureVictim = actor; fixtureFrame = reactionTest ? -1000 : frame;
            var h = Ensure(m, actor); SpendCore(h, Maximum, reactionTest);
            uint p = m.ReadU32(player + 0x38), e = m.ReadU32(actor + 0x38);
            for (uint axis = 0; axis < 3; axis++) m.WriteU32(e + axis * 4, m.ReadU32(p + axis * 4));
            m.WriteU32(e, unchecked(m.ReadU32(e) + 650));
            m.WriteU16(m.ReadU32(player + 0x3C) + 2, 3072);
            m.WriteU16(actor + 4, (ushort)((m.ReadU16(actor + 4) & ~0x80) | 0x42));
            Motion(c, m, player, 0x501);
            if (reactionTest) { Motion(c, m, actor, 0x1001); Console.WriteLine($"[reaction-test] native recoil started frame={frame}"); }
            Console.WriteLine($"[execution-test] prepared broken enemy={actor:X8} frame={frame}");
            break;
        }
    }

    static void StateAction(Guest original, CpuContext c, IMemory m)
    {
        uint actor = m.ReadU32(c.GP + 0x80C);
        if(BenchmarkRoninGameplay.HeavyTestIdle(m,actor)) {m.WriteU16(c.GP+0x826,ushort.MaxValue);return;}
        if (BenchmarkHalfStep.InGameplayActors && IsBroken(actor) && humans.TryGetValue(actor, out var broken) && !broken.PendingBreak && HumanAlive(m, actor) && (m.ReadU16(actor + 4) & 0x100) == 0)
        {
            // HumanActionControl has already processed incoming damage. Skip
            // only its action handler, leaving death/damage and physics alive.
            m.WriteU16(c.GP + 0x826, ushort.MaxValue);
            return;
        }
        // Combat handlers do not all call AttackControl on every sword press
        // (notably recoil and combo continuations). Give the finisher first
        // refusal here, after HumanActionControl prepared its native globals.
        if (BenchmarkHalfStep.InGameplayActors && actor == player && HumanAlive(m, actor) &&
            (m.ReadU16(actor + 4) & 0x100) == 0 && (m.ReadU16(actor + 0x14) & 0x80) != 0 &&
            m.ReadU16(actor + 2) is 0 or 1 or 2 or 5 or 6 or 7 or 11)
        {
            uint victim = FindExecution(c, m);
            if (victim != 0)
            {
                var helper = Helper(c); attackControl(helper, m);
                if (m.ReadU16(victim + 8) == 0) return;
            }
        }
        original(c, m);
    }

    static void ActionMotion(Guest original,CpuContext c,IMemory m)
    {
        uint actor=m.ReadU32(0x8009590C),manager=c.A0;
        if(!BenchmarkHalfStep.InGameplayActors || !HumanAlive(m,actor) || manager!=m.ReadU32(actor+0x5C)) {original(c,m);return;}
        ushort before=m.ReadU16(manager),requested=(ushort)c.A1;
        float cost=ActionCost(before,requested,BenchmarkRoninGameplay.HeavyInProgress(actor));
        if(cost==0){original(c,m);return;}
        var h=Ensure(m,actor);
        if(IsBroken(actor) || h.Value<cost){c.V0=0;return;}
        original(c,m);
        if(c.V0!=0 && m.ReadU16(manager)==requested)
        {
            Spend(h,cost);
            Console.WriteLine($"[stamina-action] motion={requested:X4} cost={cost} remaining={h.Value:F2} frame={frame}");
        }
    }
    internal static float ActionCost(ushort before,ushort requested,bool heavy)
    {
        if(before==requested)return 0;
        if(requested is >=0xB05 and <=0xB08)return before is >=0xB05 and <=0xB08 ? 0 : 15;
        if(!heavy && requested is >=0x700 and <=0x712 && requested!=0x710)return 8;
        return 0;
    }
    static void ActorAction(Guest original, CpuContext c, IMemory m)
    {
        uint actor = c.A0;
        if (!BenchmarkHalfStep.InGameplayActors || !HumanAlive(m, actor)) { original(c, m); return; }
        var h = Ensure(m, actor); uint manager = m.ReadU32(actor + 0x5C);
        bool fixtureAttack = fixture && fixtureVictim != 0 && actor == player && frame == fixtureFrame + 12;
        if (fixtureAttack)
        {
            m.WriteU16(actor + 0x10, 0x80); m.WriteU16(actor + 0x14, 0x80);
            Console.WriteLine($"[execution-test] sword press candidate={FindExecution(c,m):X8} broken={IsBroken(fixtureVictim)} canExecute={CanExecute(m,fixtureVictim)} attr={m.ReadU16(fixtureVictim+4):X4} playerY={(int)m.ReadU32(m.ReadU32(player+0x38)+4)} victimY={(int)m.ReadU32(m.ReadU32(fixtureVictim+0x38)+4)} map={m.ReadU32(player+0x2C):X8}");
        }
        ushort mid = m.ReadU16(manager), count = m.ReadU16(manager + 2), loop = m.ReadU16(manager + 4);
        if (h.PendingBreak && mid == 0x1001) h.ReactionSeen = true;
        if (mid != h.LastMotion || (mid >> 8) == 7 && (count < h.LastCount || loop != h.LastLoop)) h.Swing++;
        h.LastMotion = mid; h.LastCount = count; h.LastLoop = loop;
        if (!IsBroken(actor) && h.Frozen)
        {
            h.Frozen = false;
            if (m.ReadU16(manager + 4) == 0x8001) Motion(c, m, actor, (m.ReadU16(actor + 4) & 0x40) != 0 ? 0x501u : 0u);
        }
        original(c, m);
        if (h.PendingBreak && HumanAlive(m, actor))
        {
            AdvanceReaction(h, m.ReadU16(manager));
            if (fixture && actor == fixtureVictim && !h.PendingBreak) Console.WriteLine($"[reaction-test] native recoil completed frame={frame}; existing break window continues");
            if (h.PendingBreak) return; // let native recoil animation play to completion
        }
        if (fixtureAttack)
            Console.WriteLine($"[execution-test] result victimLife={m.ReadU16(fixtureVictim+8)} playerMotion={m.ReadU16(manager):X4} victimMotion={m.ReadU16(m.ReadU32(fixtureVictim+0x5C)):X4}");
        if (!IsBroken(actor) || !HumanAlive(m, actor) || (m.ReadU16(actor + 4) & 0x100) != 0) return;
        mid = m.ReadU16(manager);
        if ((mid is >= 0x714 and <= 0x719) || (mid >> 8) == 17) return;
        if (!h.Frozen || mid != 0x1001)
        {
            // Cancel the broken human's live hitboxes using the game's own
            // effect cleanup; preserve the current actor's action globals.
            var helper = Helper(c); uint prior = m.ReadU32(c.GP + 0x80C);
            m.WriteU32(c.GP + 0x80C, actor); helper.A0 = 3; cancelAttack(helper, m);
            m.WriteU32(c.GP + 0x80C, prior);
            if (Motion(c, m, actor, 0x1001))
            {
                m.WriteU16(manager + 2, 0); m.WriteU16(manager + 4, 0); m.WriteU32(manager + 8, uint.MaxValue);
                helper = Helper(c); helper.A0 = manager; helper.A1 = 1; playMotion(helper, m);
                m.WriteU16(manager + 2, 0); m.WriteU16(manager + 4, 0x8001); h.Frozen = true;
            }
        }
        m.WriteU16(actor + 0x40, 0); m.WriteU16(actor + 0x44, 0);
    }

    static bool HasMotion(IMemory m, uint actor, ushort id)
    {
        uint manager = m.ReadU32(actor + 0x5C), table = m.ReadU32(manager + 0x14), model = m.ReadU32(actor + 0x58);
        if (!Pointer(table) || !Pointer(model)) return false;
        foreach (uint registry in new[] { table, 0x80084CD8u })
            for (uint i = 0; i < 256; i++)
            {
                ushort candidate = m.ReadU16(registry + i * 8);
                if (candidate == ushort.MaxValue) break;
                if (candidate != id) continue;
                uint data = m.ReadU32(registry + i * 8 + 4);
                // Native UpdateMotion accepts the blob and clamps its bone
                // count to the model's count. Packed blobs need not align to
                // four bytes. The old check rejected valid native clips.
                if (data is >= 0x80010000 and < 0x80200000) return true;
                break;
            }
        return false;
    }

    static bool CanExecute(IMemory m, uint actor)
        => IsBroken(actor) && ExecutionClipsAvailable(m, actor);

    static bool ExecutionClipsAvailable(IMemory m, uint actor)
    {
        if (actor == player || !HumanAlive(m, actor) || (m.ReadU16(actor + 4) & 0x100) != 0) return false;
        int kind = m.ReadU16(actor);
        if (!(kind is >= 7 and <= 9 or >= 0x10 and < 0x90)) return false;
        int offset = m.ReadU16(player) == 1 ? 3 : 0;
        for (ushort i = 0; i < 3; i++)
            if (!HasMotion(m, player, (ushort)(0x714 + offset + i)) || !HasMotion(m, actor, (ushort)(0x1109 + offset + i))) return false;
        return true;
    }

    static uint FindExecution(CpuContext c, IMemory m)
    {
        uint p = m.ReadU32(player + 0x38), best = 0;
        double range = m.ReadU16(player + 0xC) + 1000.0, distance = range * range;
        // Match native GetNearestHumanoid: use the human group. The render
        // visibility list can omit a nearby victim after camera rotation.
        int count = Math.Clamp((int)m.ReadU16(c.GP + 0x8C), 0, 64);
        for (uint i = 0; i < count; i++)
        {
            uint actor = m.ReadU32(0x800BC410 + i * 4);
            if (!CanExecute(m, actor)) continue;
            uint position = m.ReadU32(actor + 0x38);
            double dx = (int)m.ReadU32(position) - (double)(int)m.ReadU32(p), dz = (int)m.ReadU32(position + 8) - (double)(int)m.ReadU32(p + 8);
            double d = dx * dx + dz * dz;
            if (d < distance && m.ReadU32(position + 4) == m.ReadU32(p + 4) && ClearPassage(c, m, p, position, -800)) { best = actor; distance = d; }
        }
        return best;
    }

    static void Attack(Guest original, CpuContext c, IMemory m)
    {
        if (!BenchmarkHalfStep.InGameplayActors || m.ReadU32(c.GP + 0x80C) != player || !HumanAlive(m, player)) { original(c, m); return; }
        uint victim = FindExecution(c, m);
        if (victim == 0) { original(c, m); return; }
        uint previous = executionVictim; bool prior = executionScope;
        executionVictim = victim; executionScope = true;
        ushort criticals = m.ReadU16(0x80095918), murders = m.ReadU16(0x8009591C), attr = m.ReadU16(victim + 4);
        try { original(c, new ExecutionMemory { Inner = m, Victim = victim, Cpu = c }); }
        finally { executionVictim = previous; executionScope = prior; }
        if (m.ReadU16(victim + 8) == 0 && (attr & 0x43) != 0 && m.ReadU16(0x80095918) == unchecked((ushort)(criticals + 1)))
        {
            // A combat posture finisher is not a stealth-score exploit.
            m.WriteU16(0x80095918, criticals); m.WriteU16(0x8009591C, unchecked((ushort)(murders + 1)));
            Console.WriteLine($"[stamina] native posture execution victim=0x{victim:X8}");
        }
    }

    static void Nearest(Guest original, CpuContext c, IMemory m)
    {
        if (executionScope && c.RA == 0x8001E314 && c.A0 == player) { c.V0 = executionVictim; return; }
        original(c, m);
    }

    sealed class ExecutionMemory : BenchmarkHalfStep.Proxy
    {
        public uint Victim;
        public CpuContext Cpu = null!;
        public override ushort ReadU16(uint address)
        {
            ushort value = base.ReadU16(address);
            if (address == Victim + 4) return (ushort)(value & ~0x43);
            // Only bypass the original boss-page filter when paired clips
            // were verified. Actual identity remains intact for death scripts.
            if (address == Victim && Cpu.RA == 0x8001E314 && (value & 0xF0) == 0x80) return 0x10;
            return value;
        }
    }

    static bool ClearPassage(CpuContext c, IMemory m, uint from, uint to, int heightOffset = 0)
    {
        if (!Pointer(m.ReadU32(0x8009538C))) return false;
        var helper = Helper(c); uint start = helper.SP + 0x100, direction = start + 16;
        int steps = 1;
        Span<int> delta = stackalloc int[3];
        for (uint i = 0; i < 3; i++) delta[(int)i] = unchecked((int)(m.ReadU32(to + i * 4) - m.ReadU32(from + i * 4)));
        while (Math.Abs((long)delta[0]) > 100 || Math.Abs((long)delta[1]) > 100 || Math.Abs((long)delta[2]) > 100) { for (int i = 0; i < 3; i++) delta[i] >>= 1; steps <<= 1; if (steps > 1024) return false; }
        for (uint i = 0; i < 3; i++) { m.WriteU32(start + i * 4, m.ReadU32(from + i * 4)); m.WriteU16(direction + i * 2, unchecked((ushort)(short)delta[(int)i])); }
        m.WriteU32(start + 4, unchecked(m.ReadU32(start + 4) + (uint)heightOffset));
        helper.A0 = m.ReadU32(0x8009538C); helper.A1 = start; helper.A2 = direction; helper.A3 = (uint)steps;
        passage(helper, m); return helper.V0 == 0;
    }

    static void Snapshot(CpuContext c, IMemory m)
    {
        if (!HumanAlive(m, player)) { hud = new(clock.ElapsedMilliseconds, 0, false, Array.Empty<Bar>()); return; }
        if(hudFixture && frame>=60 && frame<1800 && (!hudFixtureUsed || frame%60==0))
        {
            int shown=0, count=Math.Clamp((int)m.ReadU16(c.GP+0x8C),0,64);
            for(uint i=0;i<count && shown<3;i++)
            {
                uint actor=m.ReadU32(0x800BC410+i*4);
                if(actor==player || !HumanAlive(m,actor)) continue;
                int kind=m.ReadU16(actor);
                if(!(kind is >=7 and <=9 or >=0x10 and <0x90)) continue;
                Ensure(m,actor).Value=shown==0?70:shown==1?40:95;
                var helper=Helper(c);helper.A0=actor;requestLifeBar(helper,m);shown++;
            }
            hudFixtureUsed=shown>0;
            if(hudFixtureUsed) Console.WriteLine($"[hud-test] requested {shown} native enemy HP displays");
        }
        // Enemy HUD ownership comes from the actual native LifeBar slots.
        // No per-enemy projection, terrain raycasts or execution search here.
        hud = new(clock.ElapsedMilliseconds, Ensure(m, player).Value, IsBroken(player), Array.Empty<Bar>());
    }

    static Bar[] ReadEnemyLifeBars(IMemory m)
    {
        const uint slots = 0x800BE520, sprite = 0x8008C5B0;
        int width = m.ReadU16(sprite + 8), height = m.ReadU16(sprite + 10);
        if (width <= 0 || height <= 0 || width > 256 || height > 256) return Array.Empty<Bar>();
        float sx = (short)m.ReadU16(sprite + 0x1C) / 4096f, sy = (short)m.ReadU16(sprite + 0x1E) / 4096f;
        if (sx == 0) sx = 1; if (sy == 0) sy = 1;
        double angle = (int)m.ReadU32(sprite + 0x20) / 4096.0 * Math.PI / 180;
        float co = (float)Math.Cos(angle), si = (float)Math.Sin(angle);
        float w = width * sx, h = height * sy;
        // Bounding rectangle of the same native rotated/scaled HP frame.
        float x1=w*co, y1=w*si, x2=-h*si, y2=h*co;
        float left=Math.Min(0,Math.Min(x1,Math.Min(x2,x1+x2)));
        float top=Math.Min(0,Math.Min(y1,Math.Min(y2,y1+y2)));
        float right=Math.Max(0,Math.Max(x1,Math.Max(x2,x1+x2)));
        float bottom=Math.Max(0,Math.Max(y1,Math.Max(y2,y1+y2)));
        var bars = new List<Bar>(5);
        for(uint i=0;i<5;i++)
        {
            uint slot=slots+i*20, actor=m.ReadU32(slot);
            if ((int)m.ReadU32(slot+12)<=0 || m.ReadU32(slot+16)!=1 || !Pointer(actor) || actor==player || m.ReadU16(actor)>=0xA0) continue;
            float value=(short)m.ReadU16(actor+8)>0 ? Ensure(m,actor).Value : 0;
            bars.Add(new Bar(-140+i*60+left,-90+top,right-left,bottom-top,value,IsBroken(actor)));
        }
        return bars.ToArray();
    }

    static void EnemyLifeHud(Guest original, CpuContext c, IMemory m)
    {
        // Called only when the game actually submits its temporary enemy HP.
        enemyBars=ReadEnemyLifeBars(m); enemyBarsFrame=frame;
        if(hudFixture && enemyBars.Length>0 && frame%60==0)
            Console.WriteLine("[hud-test] bars="+string.Join(";",enemyBars.Select(b=>$"{b.X},{b.Y} {b.Width}x{b.Height} value={b.Value}")));
        const uint enemyNumberY = 0x8008C5AE;
        ushort prior=m.ReadU16(enemyNumberY);
        if(enemyBars.Length>0)
        {
            int digitHeight=Math.Clamp((int)m.ReadU16(0x800BE43A),1,16);
            short offset=(short)MathF.Round(enemyBars[0].Y+90-digitHeight-2);
            m.WriteU16(enemyNumberY,unchecked((ushort)offset));
        }
        try { original(c,m); }
        finally { m.WriteU16(enemyNumberY,prior); }
    }

    sealed class StaminaHud : IPanel
    {
        public string Name => "Stamina";
        public bool IsOpen { get; set; } = true;
        public void Draw()
        {
            var snapshot = hud;
            if (clock.ElapsedMilliseconds - snapshot.Time > 300 || !PanelManager.Panels.Any(p => p.Name == "Output" && p.IsOpen)) return;
            if (!ImGui.Begin("Output")) { ImGui.End(); return; }
            Vector2 start = ImGui.GetWindowPos() + ImGui.GetWindowContentRegionMin(), end = ImGui.GetWindowPos() + ImGui.GetWindowContentRegionMax();
            ImGui.End();
            Vector2 available = end - start;
            if (available.X < 1 || available.Y < 1) return;
            var output = typeof(RecompOne.Runtime.Runtime).Assembly.GetType("RecompOne.Runtime.Host.Window.OutputPanel")!;
            float aspect = (float)(output.GetField("_aspect", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) ?? (4f / 3));
            float fit = Math.Min(available.X / aspect, available.Y);
            Vector2 size = NativeFullscreen.StretchEnabled ? available : new(aspect * fit, fit); start += (available - size) / 2;
            float scale = Math.Clamp(size.X / 640, 0.75f, 1.5f);
            var draw = ImGui.GetForegroundDrawList(); draw.PushClipRect(start, start + size, true);
            void Meter(Vector2 pos, float length, float value, bool broken, float height)
            {
                // Square, subdued frame and lacquer-red fill echo the native
                // life HUD; avoid rounded neon meters over the original art.
                pos = new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y));
                Vector2 finish = pos + new Vector2(length, height);
                draw.AddRectFilled(pos - new Vector2(3, 2) * scale, finish + new Vector2(3, 2) * scale, 0xD0101010);
                uint frameColor = broken ? 0xFF4545C4u : 0xFF728797u;
                draw.AddRectFilled(pos - Vector2.One * scale, finish + Vector2.One * scale, frameColor);
                draw.AddRectFilled(pos, finish, 0xFF21191Du);
                uint color = 0xFF38D8EFu;
                float fill = length * Math.Clamp(value / Maximum, 0, 1);
                if (fill > 0)
                {
                    draw.AddRectFilled(pos, pos + new Vector2(fill, height), color);
                    draw.AddLine(pos, pos + new Vector2(fill, 0), 0xFF91F4FF, Math.Max(1, scale));
                }
                draw.AddLine(pos - new Vector2(3 * scale, scale), pos + new Vector2(0, height + scale), frameColor, scale);
                draw.AddLine(pos + new Vector2(length + 3 * scale, -scale), finish, frameColor, scale);
            }
            // Match the native 320x240 life HUD: the thin stamina strip sits
            // directly below the red HP fill, above the white HP digits.
            float playerNativeScale=size.Y/240f;
            Vector2 playerPos=start+new Vector2(46,215)*playerNativeScale;
            Meter(playerPos,44*playerNativeScale,snapshot.PlayerValue,snapshot.PlayerBroken,2*playerNativeScale);
            if (frame-enemyBarsFrame <= 1)
            {
                float nativeScale=size.Y/240;
                Vector2 center=start+size/2;
                foreach(var bar in enemyBars)
                {
                    bool horizontal=bar.Width>=bar.Height;
                    Vector2 pos=center+new Vector2(bar.X,bar.Y)*nativeScale;
                    float thin=2*nativeScale;
                    Vector2 dimensions;
                    if(horizontal) { pos.Y+=bar.Height*nativeScale; dimensions=new(bar.Width*nativeScale,thin); }
                    else { pos.X+=bar.Width*nativeScale; dimensions=new(thin,bar.Height*nativeScale); }
                    pos=new(MathF.Round(pos.X),MathF.Round(pos.Y));
                    Vector2 finish=pos+dimensions;
                    draw.AddRectFilled(pos,finish,0xFF26251B);
                    float fraction=Math.Clamp(bar.Value/Maximum,0,1);
                    Vector2 fill=horizontal ? new(dimensions.X*fraction,dimensions.Y) : new(dimensions.X,dimensions.Y*fraction);
                    if(fraction>0) draw.AddRectFilled(pos,pos+fill,0xFF38D8EF);
                }
            }
            draw.PopClipRect();
        }
    }
}





