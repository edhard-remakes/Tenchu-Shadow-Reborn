using System.Reflection;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;
using ImGuiNET;
using System.Numerics;
using System.Diagnostics;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Config;

namespace TenchuSARecomp;

// Opt-in USA gameplay prototype. All hooks are excluded from normal releases.
// Guest actions keep their original collision and animation routines.
internal static partial class BenchmarkRoninGameplay
{
    internal static bool Enabled => installed;
    delegate void Guest(CpuContext c, IMemory m);
    delegate void Detour(Guest original, CpuContext c, IMemory m);
    static readonly List<Hook> hooks = new();
    static Guest setMotion = null!, deleteConflict = null!, playMotion = null!, setCamera = null!;
    static bool heldGuardPose;
    static bool installed, cameraScope, orbitInitialized, guardHeld;
    static uint player, target;
    static ushort held, previousHeld;
    static long parryUntil = -1, nextParry, dodgeUntil = -1, nextDodge, attackUntil = -1;
    static long dodgeRequestedUntil = -1;
    static double yaw, pitch;
    static float moveX, moveY;
    static int parries, dodges;
    static bool reportedCamera;
    static long nextStateReport;
    static string notice = "";
    static long noticeUntil;
    static readonly Stopwatch hudClock = Stopwatch.StartNew();
    static long hudUpdated;
    static IMemory? inputMemory;
    static CpuContext? inputCpu;
    static ushort rawButtons;
    static bool inputRemapped;
    static bool itemAimHeld, itemAimSession, itemButtonDown, itemFirePending, itemAimReady, itemStartPulse;
    static ushort previousItemDirections;
    static bool shurikenSession, quickThrowScope, quickShuriken;

    static uint grappleManager;
    static int grappleCount = -1;
    static bool grapplePrepared;
    static long itemPressedAt, itemReleasedAt = -1, itemButtonReleasedAt = -1;
    static long nextLockSearch;
    const uint View = 0x800B5CD8, CameraState = 0x80088034;
    static long Frame => BenchmarkHalfStep.FrameNumber;
    static bool Pointer(uint p) => p >= 0x80010000 && p < 0x80200000 && (p & 3) == 0;
    static double Dead(byte b) { double v = (b - 128) / 127.0; return Math.Abs(v) < 0.18 ? 0 : Math.CopySign(Math.Min(1, (Math.Abs(v) - 0.18) / 0.82), v); }
    static double Angle(double x, double z) => Math.Atan2(-x, -z);
    static int YawUnits(double a) => ((int)Math.Round(a * 4096 / (2 * Math.PI))) & 4095;
    static double Wrap(double a) => Math.IEEERemainder(a, 2 * Math.PI);
    static bool Edge(ushort bit) => (held & bit) != 0 && (previousHeld & bit) == 0;
    // The guest PADtype uses Psy-Q's byte-swapped button layout.
    static ushort GuestMask(ushort bit) => (ushort)((bit << 8) | (bit >> 8));

    public static void Install(Assembly game)
    {
        if (Environment.GetEnvironmentVariable("TENCHU_GAMEPLAY_RONIN") != "1") return;
        var type = game.GetType("Recompiled.Tenchu", true)!;
        Guest Function(string name) => type.GetMethod(name)!.CreateDelegate<Guest>();
        setMotion = Function("func_80026A3C"); deleteConflict = Function("func_80019BAC");
        playMotion = Function("func_8001BB5C");
        setCamera = Function("func_80030D0C");


        hooks.Add(new Hook(type.GetMethod("func_8003104C")!, (Detour)Camera));
        hooks.Add(new Hook(type.GetMethod("func_80030034")!, (Detour)CameraPosition));
        hooks.Add(new Hook(type.GetMethod("func_8001CC80")!, (Detour)Damage));
        hooks.Add(new Hook(type.GetMethod("func_800207D4")!, (Detour)Engage));
        hooks.Add(new Hook(type.GetMethod("func_8001AAB0")!, (Detour)AimAxes));
        hooks.Add(new Hook(type.GetMethod("func_8003E00C")!, (Detour)SightShot));
        hooks.Add(new Hook(type.GetMethod("func_8003C36C")!, (Detour)QuickThrowTarget));
        hooks.Add(new Hook(type.GetMethod("func_8003E42C")!, (Detour)GrappleSight));
        hooks.Add(new Hook(type.GetMethod("func_8001FFA4")!, (Detour)GrappleAction));
        hooks.Add(new Hook(type.GetMethod("func_80025224")!, (Detour)ShurikenAction));
        var input = typeof(RecompOne.Runtime.Runtime).Assembly.GetType("RecompOne.Runtime.Host.InputManager", true)!;
        hooks.Add(new Hook(input.GetMethod("Poll", BindingFlags.Public | BindingFlags.Static)!, (Action<Action>)PollInput));
        installed = true;
        PanelManager.Register(new LockHud());
        BenchmarkStamina.Install(game);
        BenchmarkCombatAudio.Install();
        InstallGrab(type);
        InstallHeavyEffects(type);
        Console.WriteLine("[ronin-v9] hold R1 guard posture plus timed parry; L2 item action with right-stick aim; full R2 stealth; hold L1 lock; D-pad items");
    }

    static void PollInput(Action original)
    {
        bool gameplay = inputMemory != null && inputCpu != null && Gameplay(inputCpu, inputMemory);
        if (gameplay && (inputMemory!.ReadU32(0x80095AC8) & 4) != 0) { original(); return; }
        if (!gameplay) { original(); inputRemapped = false; itemAimHeld = itemAimSession = itemButtonDown = itemFirePending = itemAimReady = false; itemReleasedAt = -1; return; }
        // Keep stick-generated digital directions out of D-pad item selection.
        // This is transient: do not save or replace the user's binding file.
        var bindings = ConfigManager.Game.Pad;
        var up = bindings.Up; var down = bindings.Down; var left = bindings.Left; var right = bindings.Right;
        bindings.Up = up.Where(b => b < 102).ToArray(); bindings.Down = down.Where(b => b < 102).ToArray();
        bindings.Left = left.Where(b => b < 102).ToArray(); bindings.Right = right.Where(b => b < 102).ToArray();
        try { original(); }
        finally { bindings.Up = up; bindings.Down = down; bindings.Left = left; bindings.Right = right; }
        rawButtons = (ushort)~Controller.State;
        ushort itemDirections = (ushort)(rawButtons & (Controller.Left | Controller.Right));
        if ((itemDirections & ~previousItemDirections) != 0 && itemAimSession)
            CancelItemAim(inputMemory!);
        previousItemDirections = itemDirections;
        ReconcileItemAim(inputMemory!);
        UpdateItemAim((rawButtons & Controller.L2) != 0, hudClock.ElapsedMilliseconds);
        ushort mapped = (ushort)(rawButtons & ~(Controller.Up | Controller.Down | Controller.Left | Controller.Right |
            Controller.L1 | Controller.R1 | Controller.L2 | Controller.R2 | Controller.R3 | Controller.Triangle));
        if ((rawButtons & Controller.Left) != 0) mapped |= Controller.R2; // previous item
        if ((rawButtons & Controller.Right) != 0) mapped |= Controller.L2; // next item
        if ((rawButtons & Controller.R2) != 0) mapped |= Controller.R1; // full original stealth input, before Think/command parsing
        bool aimedItem = inputMemory!.ReadU16(0x800957CA) is 0 or 1;
        if (!aimedItem && (rawButtons & Controller.L2) != 0) mapped |= Controller.Triangle;
        Controller.State = (ushort)~mapped;
        inputRemapped = true;
    }

    static void UpdateItemAim(bool down, long now)
    {
        ushort selected = inputMemory == null ? (ushort)0 : inputMemory.ReadU16(0x800957CA);
        if (selected == 1 || (shurikenSession && itemAimSession)) UpdateShurikenButton(down, now);
        else { shurikenSession = false; UpdateAimButton(down, now, selected == 0); }
    }

    static void UpdateShurikenButton(bool down, long now)
    {
        bool pressed = down && !itemButtonDown, released = !down && itemButtonDown;
        itemButtonDown = down;
        if (pressed && !itemAimSession)
        {
            shurikenSession = true; quickShuriken = false;
            itemAimSession = itemAimHeld = true;
            itemAimReady = itemFirePending = false;
            itemPressedAt = now; itemReleasedAt = -1;
        }
        if (released && itemAimSession && !itemFirePending)
        {
            quickShuriken = now - itemPressedAt < 200;
            itemFirePending = true; itemReleasedAt = now;
            if (itemAimReady) itemAimHeld = false;
        }
    }

    static void UpdateAimButton(bool down, long now, bool aimedItem)
    {
        itemStartPulse = false;
        bool pressed = down && !itemButtonDown;
        if (!down && itemButtonDown) itemButtonReleasedAt = now;
        bool deliberatePress = pressed && itemButtonReleasedAt >= 0 && now - itemButtonReleasedAt >= 100;
        itemButtonDown = down;
        if (!aimedItem)
        {
            itemAimHeld = itemAimSession = itemFirePending = itemAimReady = false;
            itemReleasedAt = -1;
            return;
        }
        if (pressed) Console.WriteLine($"[item-flow] press session={itemAimSession} ready={itemAimReady} pending={itemFirePending} held={itemAimHeld}");
        if (!pressed || itemFirePending) return;
        if (!itemAimSession)
        {
            itemPressedAt = now; itemReleasedAt = -1;
            itemAimSession = itemAimHeld = true; itemAimReady = false; itemStartPulse = true;
        }
        else if (itemAimReady && deliberatePress)
        {
            if (itemAimReady) itemAimHeld = false;
            itemFirePending = true; itemReleasedAt = now;
        }
    }
    static void ReconcileItemAim(IMemory m)
    {
        if (!itemAimSession && !itemFirePending) return;
        uint manager = m.ReadU32(player + 0x5C);
        if (!Pointer(manager)) return;
        ushort motion = m.ReadU16(manager);
        // The native action can cancel or finish outside the item callback.
        // A ready preview is no longer alive once its slot is gone or its
        // preparation motion has ended. Release all session flags together.
        if (itemAimReady &&
            (m.ReadU8(player + 0xCD) == 0 || motion is not (0x400 or 0xE00)))
        {
            Console.WriteLine($"[item-flow] native ended motion={motion:X4} slot={m.ReadU8(player + 0xCD)}; rearmed");
            itemAimSession = itemAimHeld = itemAimReady = itemFirePending = itemStartPulse = false;
        }
    }
    static void CancelItemAim(IMemory m)
    {
        uint position = m.ReadU32(player + 0x38);
        if (Pointer(position))
            for (uint offset = 0; offset < 12; offset += 4)
                m.WriteU32(CameraState + offset, m.ReadU32(position + offset));
        m.WriteU8(player + 0xCD, 0);
        itemAimHeld = itemAimSession = itemFirePending = itemAimReady = itemStartPulse = false;
    }
    static bool Gameplay(CpuContext c, IMemory m)
    {
        uint p = m.ReadU32(0x8009590C);
        if (!BenchmarkHalfStep.Enabled || !Pointer(p) || (short)m.ReadU16(p + 8) <= 0 ||
            m.ReadU16(c.GP + 0x78) != 0 || m.ReadU32(CameraState + 0x10) != p) return false;
        uint motion = m.ReadU32(p + 0x5C);
        return Pointer(motion) && m.ReadU16(motion) is not (>= 0x714 and <= 0x719);
    }

    static void Camera(Guest original, CpuContext c, IMemory m)
    {
        BenchmarkFramePacing.Begin(c,m);
        bool prior = cameraScope;
        cameraScope = c.RA == 0x80015A40 && Gameplay(c, m);
        if (cameraScope) { inputMemory = m; inputCpu = c; }
        if (cameraScope)
        {
            if (!reportedCamera) { Console.WriteLine("[ronin] gameplay camera/input active"); reportedCamera = true; }
            uint current = m.ReadU32(0x8009590C);
            if (current != player) { player = current; target = 0; orbitInitialized = false; previousHeld = 0; heldGuardPose = false; }
            held = inputRemapped ? rawButtons : (ushort)~Controller.State;
            if (m.ReadU16(0x800957CA) is 0 or 1) held = (ushort)(held & ~Controller.L2);
            if (itemAimHeld) held |= Controller.L2;
            hudUpdated = hudClock.ElapsedMilliseconds;
            moveX = (float)Dead(Controller.LeftX); moveY = (float)-Dead(Controller.LeftY);
            bool lockHeld = (held & Controller.L1) != 0;
            if (!lockHeld)
            {
                if (target != 0) ShowNotice("Alvo liberado");
                target = 0;
            }
            if (!TargetValid(c, m, target)) target = 0;
            if (lockHeld && target == 0 && (Edge(Controller.L1) || Frame >= nextLockSearch))
            {
                nextLockSearch = Frame + 14;
                target = FindTarget(c, m);
                if (target != 0 || Edge(Controller.L1)) ShowNotice(target != 0 ? "ALVO TRAVADO - segure L1" : "Nenhum inimigo visivel ao alcance");
            }
            if (Edge(Controller.R1) && Frame >= nextParry) { parryUntil = Frame + 11; nextParry = Frame + 24; }
            guardHeld = (held & Controller.R1) != 0;
            if (!guardHeld) parryUntil = -1;
            if (Edge(Controller.Square)) attackUntil = Frame + 9;
            // Physical Circle has its own edge. The guest Circle is already
            // held by R2 crouch, so its native trigger cannot detect this press.
            if (Edge(Controller.Circle)) dodgeRequestedUntil = Frame + 8;
            if (Edge(Controller.Triangle)) heavyRequest = Frame + 8;
            if (Edge(Controller.L2) && m.ReadU16(0x800957CA) is 0 or 1 && !BenchmarkStamina.IsBroken(player) &&
                m.ReadU16(player + 2) is 0 or 1 or 2 or 5 or 6 or 11 or 14)
            {
                var helper = new CpuContext(); helper.Restore(c.Snapshot()); helper.SP -= 0x400;
                ushort selected = m.ReadU16(0x800957CA);
                Console.WriteLine($"[item-flow] start selected={selected} stock={m.ReadU8(player + 0xB4u + selected)} slot={m.ReadU8(player + 0xCD)} motion={m.ReadU16(m.ReadU32(player + 0x5C)):X4}");
                if (m.ReadU8(player + 0xB4u + selected) != 0)
                {
                    grappleManager = 0; grappleCount = -1; grapplePrepared = false;
                    if (RequestMotion(c, m, player, selected == 0 ? (ushort)0x400 : (ushort)0xE00))
                    { helper.A0 = 3; setCamera(helper, m); }
                    else CancelItemAim(m);
                }
                else CancelItemAim(m);
            }
            else if ((previousHeld & Controller.L2) != 0 && (held & Controller.L2) == 0)
            {
                yaw = ViewYaw(m);
                // With an unavailable item there is no projectile callback to
                // close its sight. Do not leave an empty sight camera latched.
                if (m.ReadU32(CameraState + 0x14) == 1 && m.ReadU8(CameraState + 0x1C) == 3 &&
                    m.ReadU16(player + 2) is not (4 or 9 or 14 or 15))
                {
                    var helper = new CpuContext(); helper.Restore(c.Snapshot()); helper.SP -= 0x400;
                    helper.A0 = 0; setCamera(helper, m);
                }
            }
        }
        ushort savedPad = cameraScope ? m.ReadU16(player + 0x10) : (ushort)0;
        if (cameraScope) m.WriteU16(player + 0x10, (ushort)(savedPad & ~GuestMask(Controller.L1)));
        try { original(c, m); }
        finally
        {
            if (cameraScope) { m.WriteU16(player + 0x10, savedPad); previousHeld = held; }
            cameraScope = prior;
        }
    }

    static void AimAxes(Guest original, CpuContext c, IMemory m)
    {
        if (!cameraScope || c.RA != 0x800312F8 || (held & Controller.L2) == 0) { original(c, m); return; }
        double step = BenchmarkHalfStep.Enabled ? 18 : 36;
        m.WriteU16(c.A1, unchecked((ushort)(short)Math.Round(Dead(Controller.RightX) * step)));
        m.WriteU16(c.A2, unchecked((ushort)(short)Math.Round(-Dead(Controller.RightY) * step)));
    }

    // ActKAGI assumes its first call already reached animation count 1.
    // At 60 Hz count 0 is observable, and count 1 occurs twice. An empty
    // preview slot at count 0 otherwise starts flight toward an old target.
    static void GrappleAction(Guest original, CpuContext c, IMemory m) => PreparedItemAction(original, c, m, 0x400);
    static void ShurikenAction(Guest original, CpuContext c, IMemory m) => PreparedItemAction(original, c, m, 0xE00);
    static void PreparedItemAction(Guest original, CpuContext c, IMemory m, ushort prepareMotion)
    {
        uint actor = m.ReadU32(c.GP + 0x80C), manager = m.ReadU32(c.GP + 0x820);
        if (!installed || actor != player || !Pointer(manager)) { original(c, m); return; }
        if (m.ReadU16(manager) != prepareMotion)
        { grappleManager = 0; grappleCount = -1; grapplePrepared = false; original(c, m); return; }
        int count = (short)m.ReadU16(manager + 2);
        if (manager != grappleManager || (count == 0 && grappleCount != 0)) grapplePrepared = false;
        grappleManager = manager; grappleCount = count;
        // A live preview owns this session even if the animation loops.
        if (m.ReadU8(actor + 0xCD) != 0) grapplePrepared = true;
        if (count == 0 && !grapplePrepared) return;
        original(c, new GrappleActionMemory { Inner = m, Manager = manager, SkipCreation = count == 1 && grapplePrepared });
        if (count == 1 && m.ReadU8(actor + 0xCD) != 0) grapplePrepared = true;
    }

    sealed class GrappleActionMemory : BenchmarkHalfStep.Proxy
    {
        public uint Manager;
        public bool SkipCreation;
        public override ushort ReadU16(uint address)
            => SkipCreation && address == Manager + 2 ? (ushort)2 : base.ReadU16(address);
    }
    static void SightShot(Guest original, CpuContext c, IMemory m)
        => ItemSight(original, c, m, true);
    static void GrappleSight(Guest original, CpuContext c, IMemory m)
        => ItemSight(original, c, m, false);
    static void ItemSight(Guest original, CpuContext c, IMemory m, bool shuriken)
    {
        uint item = c.A0;
        if (!installed || !Pointer(item) || m.ReadU32(item) != player || m.ReadU8(item + 0x54) == 0xFF)
        { original(c, m); return; }
        // A callback without a live aiming session must cancel, never infer
        // permission to fire from a missing/late input poll.
        if (!itemAimSession && !itemFirePending && m.ReadU8(player + 0xCD) != 0)
            CancelItemAim(m);        if (shuriken && itemAimHeld && m.ReadU16(m.ReadU32(player + 0x5C)) == 0xE00) m.WriteU8(item + 0x50, 0);
        if (!shuriken && itemAimHeld)
        {
            // ProcSightShot reasserts SIGHT on every held update. Do the same
            // for the grapple, whose native callback only draws its sprite.
            var helper = new CpuContext(); helper.Restore(c.Snapshot()); helper.SP -= 0x400;
            helper.A0 = 3; setCamera(helper, m);
        }
        if (itemAimHeld && !itemAimReady) Console.WriteLine($"[item-flow] sight ready kind={(shuriken ? 1 : 0)} motion={m.ReadU16(m.ReadU32(player + 0x5C)):X4}");
        if (itemAimHeld) itemAimReady = true;
        if (itemFirePending && itemAimReady) itemAimHeld = false;
        bool previousQuick = quickThrowScope;
        quickThrowScope = shuriken && shurikenSession && quickShuriken && itemFirePending;
        try { original(c, new ItemAimMemory { Inner = m, Owner = player, AimHeld = itemAimHeld, Grapple = !shuriken, Cpu = c }); }
        finally { quickThrowScope = previousQuick; }
        if (itemFirePending && m.ReadU8(player + 0xCD) == 0)
            itemAimSession = itemFirePending = itemAimReady = false;
    }
    // Initial aim assistance only; native targeting retains terrain collision.
    static void QuickThrowTarget(Guest original, CpuContext c, IMemory m)
    {
        if (!quickThrowScope || c.A0 != player) { original(c, m); return; }
        uint candidate = FindTarget(c, m);
        if (candidate == 0) { original(c, m); return; }
        uint position = m.ReadU32(candidate + 0x38), origin = c.A2;
        double dx = (int)m.ReadU32(position) - (double)(int)m.ReadU32(origin);
        double dz = (int)m.ReadU32(position + 8) - (double)(int)m.ReadU32(origin + 8);
        if (dx * dx + dz * dz > 6000.0 * 6000) { original(c, m); return; }
        double height = Math.Clamp((short)m.ReadU16(candidate + 0xE) * 2.0 / 3, 400, 1000);
        double dy = (int)m.ReadU32(position + 4) - height - (int)m.ReadU32(origin + 4);
        uint rotation = c.A1;
        ushort pitch = m.ReadU16(rotation), yawBefore = m.ReadU16(rotation + 2);
        m.WriteU16(rotation, unchecked((ushort)(short)Math.Round(Math.Atan2(dy, Math.Sqrt(dx * dx + dz * dz)) * 4096 / (Math.PI * 2))));
        m.WriteU16(rotation + 2, (ushort)YawUnits(Angle(dx, dz)));
        try { original(c, m); }
        finally { m.WriteU16(rotation, pitch); m.WriteU16(rotation + 2, yawBefore); }
    }

    sealed class ItemAimMemory : BenchmarkHalfStep.Proxy
    {
        public uint Owner;
        public bool AimHeld;
        public bool Grapple;
        public CpuContext Cpu = null!;
        public override uint ReadU32(uint address)
        {
            uint value = base.ReadU32(address);
            // USA ProcKaginawa hides the sight below the horizon. Override
            // only that local visibility test while held, never the actual
            // view, aim rotation, ray trace or release validation.
            if (Grapple && AimHeld && Cpu.RA == 0x8003E4E8 && address == Cpu.SP + 0x30 && (int)value >= 0)
                return unchecked((uint)-1);
            return value;
        }
        public override ushort ReadU16(uint address)
        {
            ushort value = base.ReadU16(address);
            return address == Owner + 0x10 ? (ushort)(AimHeld ? value | 0x10 : value & ~0x10) : value;
        }
    }

    static bool TargetValid(CpuContext c, IMemory m, uint actor)
    {
        if (!Pointer(actor) || actor == player || (short)m.ReadU16(actor + 8) <= 0) return false;
        int count = Math.Clamp((int)m.ReadU16(c.GP + 0x8E), 0, 40);
        bool visible = false;
        for (int i = 0; i < count; i++) if (m.ReadU32(0x800BC4B0 + (uint)i * 4) == actor) { visible = true; break; }
        if (!visible || (m.ReadU16(actor + 4) & 0x80) != 0) return false;
        ushort attr = m.ReadU16(actor + 4);
        int kind = m.ReadU16(actor);
        // Enemy identity is independent of awareness. Civilians, allies and
        // animals are not lock candidates; selecting a calm guard does not
        // alter his AI, weapon state or detection flags.
        if (!(kind is >= 7 and <= 9 or >= 0x10 and <= 0x8F)) return false;
        uint a = m.ReadU32(actor + 0x38), p = m.ReadU32(player + 0x38);
        if (!Pointer(a) || !Pointer(p)) return false;
        double dx = (int)m.ReadU32(a) - (double)(int)m.ReadU32(p), dz = (int)m.ReadU32(a + 8) - (double)(int)m.ReadU32(p + 8);
        return dx * dx + dz * dz < 14000.0 * 14000 && Math.Abs((int)m.ReadU32(a + 4) - (double)(int)m.ReadU32(p + 4)) < 2000;
    }

    static uint FindTarget(CpuContext c, IMemory m)
    {
        uint best = 0, p = m.ReadU32(player + 0x38);
        double score = double.MaxValue;
        int count = Math.Clamp((int)m.ReadU16(c.GP + 0x8E), 0, 40);
        for (int i = 0; i < count; i++)
        {
            uint actor = m.ReadU32(0x800BC4B0 + (uint)i * 4);
            if (!TargetValid(c, m, actor)) continue;
            uint a = m.ReadU32(actor + 0x38);
            double dx = (int)m.ReadU32(a) - (double)(int)m.ReadU32(p), dz = (int)m.ReadU32(a + 8) - (double)(int)m.ReadU32(p + 8);
            double viewYaw = ViewYaw(m);
            double angle = Math.Abs(Wrap(Angle(dx, dz) - viewYaw));
            if (angle > Math.PI * 0.55 || dx * dx + dz * dz > 10000.0 * 10000) continue;
            double candidate = Math.Sqrt(dx * dx + dz * dz) + angle * 1500;
            if (candidate < score) { best = actor; score = candidate; }
        }
        return best;
    }

    static void CameraPosition(Guest original, CpuContext c, IMemory m)
    {
        uint mode = m.ReadU32(CameraState + 0x14);
        // Retain authored cinematic, grapple, ledge and first-person cameras.
        if (cameraScope && mode is 0 or 5 or 11 or 12 &&
            m.ReadU16(player + 2) is 0 or 1 or 2 or 5 or 6 or 7 or 9 or 11 or 16)
        {
            if (!orbitInitialized)
            {
                yaw = (short)m.ReadU16(c.A1 + 2) * Math.PI * 2 / 4096;
                pitch = 0;
                orbitInitialized = true;
            }
            yaw += Dead(Controller.RightX) * 0.0385;
            pitch = Math.Clamp(pitch + Dead(Controller.RightY) * 0.022, -0.55, 0.55);
            if (target != 0)
            {
                uint p = m.ReadU32(player + 0x38);
                uint t = m.ReadU32(target + 0x38);
                yaw += Wrap(Angle((int)m.ReadU32(t) - (double)(int)m.ReadU32(p), (int)m.ReadU32(t + 8) - (double)(int)m.ReadU32(p + 8)) - yaw) * 0.132;
            }
            // Supply only a virtual rotation. The original camera retains its
            // reference/position endpoints, distance, terrain trace, AntiWall
            // and smoothing. Reserve space ABOVE the callee's stack frame.
            uint rotation = c.A1;
            c.SP -= 16;
            m.WriteU16(c.SP, unchecked((ushort)((short)m.ReadU16(rotation) + (short)(pitch * 4096 / (2 * Math.PI)))));
            m.WriteU16(c.SP + 2, (ushort)YawUnits(yaw));
            m.WriteU32(c.SP + 4, m.ReadU32(rotation + 4));
            c.A1 = c.SP;
            try { original(c, m); }
            finally { c.SP += 16; }
            return;
        }
        original(c, m);
    }

    static double ViewYaw(IMemory m)
    {
        double dx = (int)m.ReadU32(View + 12) - (double)(int)m.ReadU32(View);
        double dz = (int)m.ReadU32(View + 20) - (double)(int)m.ReadU32(View + 8);
        return dx * dx + dz * dz > 1 ? Angle(dx, dz) : yaw;
    }

    static double MovementDirection(IMemory m)
    {
        // Use the actual SDK world-to-screen matrix's right vector, not an
        // assumed handedness or the desired orbit angle (which may still lag).
        double fx = (int)m.ReadU32(View + 12) - (double)(int)m.ReadU32(View);
        double fz = (int)m.ReadU32(View + 20) - (double)(int)m.ReadU32(View + 8);
        double fl = Math.Sqrt(fx * fx + fz * fz);
        if (fl < 1) { fx = -Math.Sin(yaw); fz = -Math.Cos(yaw); fl = 1; }
        fx /= fl; fz /= fl;
        double rx = (short)m.ReadU16(0x800C4128), rz = (short)m.ReadU16(0x800C412C);
        double rl = Math.Sqrt(rx * rx + rz * rz);
        if (rl < 100) { rx = -fz; rz = fx; rl = 1; }
        return Angle(fx * moveY + rx / rl * moveX, fz * moveY + rz / rl * moveX);
    }

    static bool RequestMotion(CpuContext c, IMemory m, uint actor, uint motion)
    {
        var helper = new CpuContext(); helper.Restore(c.Snapshot()); helper.SP -= 0x400;
        helper.A0 = actor; helper.A1 = motion; helper.A2 = 1;
        setMotion(helper, m);
        return helper.V0 != 0;
    }

    static void GuardPosture(CpuContext c, IMemory m, uint actor, uint manager)
    {
        int state = m.ReadU16(actor + 2), motion = m.ReadU16(manager);
        if (heldGuardPose && motion != 0x500) heldGuardPose = false;
        bool canHold = guardHeld && !BenchmarkStamina.IsBroken(actor) && (m.ReadU16(actor + 4) & 0x100) == 0 &&
            (held & (Controller.Circle | Controller.Cross | Controller.Square | Controller.R2 | Controller.L2)) == 0;
        if (heldGuardPose && !canHold)
        {
            heldGuardPose = false;
            if (!RequestMotion(c, m, actor, 0x501)) { m.WriteU16(manager + 2, 0); m.WriteU16(manager + 4, 0); }
            return;
        }
        // Actual hit recoil completes normally before returning to held guard.
        if (!heldGuardPose && canHold && state is 0 or 1 or 2 or 5 or 6 && motion != 0x500)
        {
            if (!RequestMotion(c, m, actor, 0x500)) return;
            m.WriteU32(manager + 8, uint.MaxValue);
            m.WriteU16(manager + 2, 0); m.WriteU16(manager + 4, 0);
            var helper = new CpuContext(); helper.Restore(c.Snapshot()); helper.SP -= 0x400;
            helper.A0 = manager; helper.A1 = 1;
            playMotion(helper, m); // evaluate the original guard's first pose once
            m.WriteU16(manager + 2, 0); m.WriteU16(manager + 4, 0x8000);
            m.WriteU16(actor + 0x40, 0); m.WriteU16(actor + 0x44, 0);
            heldGuardPose = true;
        }
        if (heldGuardPose && target != 0)
        {
            uint p = m.ReadU32(actor + 0x38), t = m.ReadU32(target + 0x38), r = m.ReadU32(actor + 0x3C);
            double angle = Angle((int)m.ReadU32(t) - (double)(int)m.ReadU32(p), (int)m.ReadU32(t + 8) - (double)(int)m.ReadU32(p + 8));
            m.WriteU16(r + 2, (ushort)YawUnits(angle));
        }
    }

    static void Engage(Guest original, CpuContext c, IMemory m)
    {
        uint actor = m.ReadU32(c.GP + 0x80C), manager = m.ReadU32(c.GP + 0x820);
        if (installed && actor == player && heldGuardPose && guardHeld && Pointer(manager) &&
            m.ReadU16(manager) == 0x500 && m.ReadU16(manager + 4) == 0x8000)
        {
            // Hold the evaluated guard pose without re-running the original
            // recoil timer, sliding backwards, or repeatedly spawning dust.
            m.WriteU16(c.GP + 0x826, ushort.MaxValue);
            return;
        }
        original(c, m);
    }

    public static void ActorAction(Action<CpuContext, IMemory> original, CpuContext c, IMemory m)
    {
        uint actor = c.A0;
        if (installed && GrabAction(c, m, actor)) return;
        if (!installed || actor != player || !Gameplay(c, m)) { original(c, m); return; }
        if (HeavyAction(c,m,actor)) { original(c,m); return; }
        uint manager = m.ReadU32(actor + 0x5C), rotation = m.ReadU32(actor + 0x3C);
        GuardPosture(c, m, actor, manager);
        int status = m.ReadU16(actor + 2), motion = m.ReadU16(manager);
        bool locomotion = (status is 0 or 1 or 2 or 5 or 6 or 11) && motion != 0x500;
        bool ordinaryAttack = status == 7 && motion < 0x714;
        ushort pad = m.ReadU16(actor + 0x10), trigger = m.ReadU16(actor + 0x14);
        ushort originalTrigger = trigger;
        // Item cycling is remapped before guest PadProc so the inventory HUD
        // receives it too. Actor movement is supplied only by the left stick.
        pad = (ushort)(pad & ~GuestMask(Controller.L1 | Controller.R1 | Controller.R3 | Controller.Circle | Controller.Up | Controller.Down | Controller.Left | Controller.Right));
        trigger = (ushort)(trigger & ~GuestMask(Controller.L1 | Controller.R1 | Controller.R3 | Controller.Circle));
        if ((held & Controller.R2) != 0)
        {
            pad |= GuestMask(Controller.Circle);
            trigger |= (ushort)(originalTrigger & GuestMask(Controller.Circle));
        }
        // Wall pressing, wall sliding, ledges, water and item aiming still
        // need authored directional commands. D-pad stays reserved for items.
        if (status is 3 or 4 or 8 or 9 or 10 or 12 or 13 or 14 or 15 &&
            !((held & Controller.L2) != 0 && status is 4 or 9 or 14 or 15))
        {
            if (moveX > 0.25f) pad |= GuestMask(Controller.Right);
            else if (moveX < -0.25f) pad |= GuestMask(Controller.Left);
            if (moveY > 0.25f) pad |= GuestMask(Controller.Up);
            else if (moveY < -0.25f) pad |= GuestMask(Controller.Down);
        }
        if (Frame <= attackUntil && locomotion) { pad |= GuestMask(Controller.Square); trigger |= GuestMask(Controller.Square); attackUntil = -1; }
        bool rollPress = Frame <= dodgeRequestedUntil ||
            ((held & Controller.R2) == 0 && (m.ReadU16(actor + 0x14) & GuestMask(Controller.Circle)) != 0);
        if (locomotion && motion is not (>= 0xB05 and <= 0xB09) &&
            !BenchmarkStamina.IsBroken(actor) && (m.ReadU16(actor + 4) & 0x100) == 0 && Frame >= nextDodge &&
            (held & Controller.Circle) != 0 && rollPress)
        {
            double direction = yaw + Math.Atan2(-moveX, Math.Abs(moveY) + 0.0001);
            if (moveY < -0.1) direction = yaw + Math.Atan2(-moveX, moveY);
            if (Math.Abs(moveX) + Math.Abs(moveY) < 0.1) direction = yaw + Math.PI;
            m.WriteU16(rotation + 2, (ushort)YawUnits(direction));
            if (RequestMotion(c, m, actor, 0xB05))
            {
                dodgeUntil = Frame + 17; nextDodge = Frame + 36; dodgeRequestedUntil = -1; locomotion = false;
                Console.WriteLine($"[ronin] dodge #{++dodges} frame={Frame}");
            }
        }
        if (locomotion && Math.Abs(moveX) + Math.Abs(moveY) > 0.1)
        {
            double direction = MovementDirection(m);
            m.WriteU16(rotation + 2, (ushort)YawUnits(direction));
            if (target != 0)
            {
                // Face the lock target BEFORE DamageControl as well, so
                // guarding while strafing uses the enemy-facing cone.
                uint p = m.ReadU32(actor + 0x38), t = m.ReadU32(target + 0x38);
                m.WriteU16(rotation + 2, (ushort)YawUnits(Angle((int)m.ReadU32(t) - (double)(int)m.ReadU32(p), (int)m.ReadU32(t + 8) - (double)(int)m.ReadU32(p + 8))));
            }
            // Reuse forward locomotion with camera-relative facing. Crouch
            // retains its original speed, sound and detection footprint.
            pad = (ushort)((pad & ~0xF000) | GuestMask(Controller.Up));
        }
        else if (target != 0 && (locomotion || ordinaryAttack))
        {
            uint p = m.ReadU32(actor + 0x38), t = m.ReadU32(target + 0x38);
            double a = Angle((int)m.ReadU32(t) - (double)(int)m.ReadU32(p), (int)m.ReadU32(t + 8) - (double)(int)m.ReadU32(p + 8));
            int before = m.ReadU16(rotation + 2) & 4095, delta = (YawUnits(a) - before + 6144) % 4096 - 2048;
            m.WriteU16(rotation + 2, unchecked((ushort)(before + Math.Clamp(delta, -110, 110))));
        }
        m.WriteU16(actor + 0x10, pad); m.WriteU16(actor + 0x14, trigger);
        original(c, m);
        if (locomotion && m.ReadU16(actor + 2) is 0 or 1 or 2 or 5 or 6 or 11 && Math.Abs(moveX) + Math.Abs(moveY) > 0.1)
        {
            // Existing walk/run states retain velocity until a turn command.
            // Rotate their current speed without inventing speed after a wall
            // stop; the engine remains responsible for collision response.
            double speed = Math.Sqrt(Math.Pow((short)m.ReadU16(actor + 0x40), 2) + Math.Pow((short)m.ReadU16(actor + 0x44), 2));
            double direction = MovementDirection(m);
            m.WriteU16(actor + 0x40, unchecked((ushort)(short)Math.Round(-Math.Sin(direction) * speed)));
            m.WriteU16(actor + 0x44, unchecked((ushort)(short)Math.Round(-Math.Cos(direction) * speed)));
            if (target != 0)
            {
                uint p = m.ReadU32(actor + 0x38), t = m.ReadU32(target + 0x38);
                double a = Angle((int)m.ReadU32(t) - (double)(int)m.ReadU32(p), (int)m.ReadU32(t + 8) - (double)(int)m.ReadU32(p + 8));
                m.WriteU16(rotation + 2, (ushort)YawUnits(a));
            }
        }
        if (Frame >= nextStateReport)
        {
            nextStateReport = Frame + 180;
            uint p = m.ReadU32(actor + 0x38);
            Console.WriteLine($"[ronin-state] frame={Frame} position={(int)m.ReadU32(p)},{(int)m.ReadU32(p + 4)},{(int)m.ReadU32(p + 8)} motion={m.ReadU16(manager):X4} yaw={yaw:F3} pitch={pitch:F3} target={target:X8}");
        }
    }

    static void Damage(Guest original, CpuContext c, IMemory m)
    {
        uint actor = m.ReadU32(c.GP + 0x80C);
        // Check before Gameplay(): that routine deliberately excludes paired
        // executions from camera/input remapping. Incoming damage must instead
        // be rejected until the execution motion has actually finished.
        if(installed && actor==m.ReadU32(0x8009590C) &&
            (BenchmarkCrowd.Protects(m,actor) || BenchmarkExecution.IsExecuting(m,actor))) return;
        if (!installed || actor != player || !BenchmarkHalfStep.InGameplayActors || !Gameplay(c, m)) { original(c, m); return; }
        int id = m.ReadU16(actor + 0x46), motion = m.ReadU16(m.ReadU32(actor + 0x5C));
        if (id >= 80 || motion is >= 0x714 and <= 0x719) { original(c, m); return; }
        uint slot = 0x800B9D60 + (uint)id * 120, enemy = m.ReadU32(slot + 0x24);
        if(DodgeWindow(m,actor) && IsProjectileHit(m,id))
        {
            var projectileHelper=new CpuContext();projectileHelper.Restore(c.Snapshot());projectileHelper.SP-=0x400;projectileHelper.A0=m.ReadU32(slot);
            if(Pointer(projectileHelper.A0))deleteConflict(projectileHelper,m);
            m.WriteU16(actor+4,(ushort)(m.ReadU16(actor+4)&~0x4000));
            Console.WriteLine($"[ronin] projectile evaded frame={Frame}");
            return;
        }
        // Guard/parry remain melee-only. Environmental hazards keep native damage.
        if (!Pointer(enemy) || enemy == actor || (short)m.ReadU16(enemy + 8) <= 0 || m.ReadU16(enemy + 2) != 7) { original(c, m); return; }
        uint p = m.ReadU32(actor + 0x38), e = m.ReadU32(enemy + 0x38), r = m.ReadU32(actor + 0x3C);
        if (!Pointer(p) || !Pointer(e) || !Pointer(r)) { original(c, m); return; }
        double angle = Angle((int)m.ReadU32(e) - (double)(int)m.ReadU32(p), (int)m.ReadU32(e + 8) - (double)(int)m.ReadU32(p + 8));
        double facing = (m.ReadU16(r + 2) & 4095) * Math.PI * 2 / 4096;
        bool front = Math.Abs(Wrap(angle - facing)) <= Math.PI * 0.42;
        int state = m.ReadU16(actor + 2);
        bool guarding = guardHeld && !BenchmarkStamina.IsBroken(actor) && front && (m.ReadU16(actor + 4) & 0x100) == 0 && state is 0 or 1 or 2 or 5 or 6 or 11;
        bool parry = guarding && Frame <= parryUntil;
        bool evade = Frame <= dodgeUntil && motion is >= 0xB05 and <= 0xB08;
        if (!guarding && !evade) { original(c, m); return; }
        if (guarding)
        {
            uint manager = m.ReadU32(actor + 0x5C);
            ushort life = m.ReadU16(actor + 8);
            // Select the original retreat/block branch at USA 8001D74C.
            // Do not suppress damage here: the original routine checks angle,
            // attack type, terrain passage and whether the guard clip exists,
            // then plays 0500, applies recoil, sounds, sparks and rumble.
            var native = new NativeGuardMemory { Inner = m, MotionAddress = c.GP + 0x824,
                ExistingMotion = m.ReadU16(c.GP + 0x824), ForceRetreat = heldGuardPose };
            original(c, native);
            short recoil = (short)m.ReadU16(manager + 4);
            bool blocked = m.ReadU16(actor + 8) == life && m.ReadU16(manager) == 0x500 && recoil < 0 && recoil != short.MinValue;
            if (!blocked) return;
            heldGuardPose = false; // let native hit recoil and recovery run
            if (m.ReadU16(c.GP + 0x824) == native.ExistingMotion) m.WriteU16(c.GP + 0x824, 0x500);
            if (parry)
            {
                parryUntil = -1;
                BenchmarkStamina.Parry(actor, enemy, c, m);
                BenchmarkCombatAudio.Parry();
                RequestMotion(c, m, enemy, 0x1001);
                Console.WriteLine($"[ronin] native-guard parry #{++parries} frame={Frame}");
                ShowNotice("PARRY!");
            }
            else { ShowNotice("DEFESA"); Console.WriteLine($"[ronin] native guard reaction frame={Frame}"); }
            return;
        }
        var helper = new CpuContext(); helper.Restore(c.Snapshot()); helper.SP -= 0x400; helper.A0 = m.ReadU32(slot);
        if (Pointer(helper.A0)) deleteConflict(helper, m);
        m.WriteU16(actor + 4, (ushort)(m.ReadU16(actor + 4) & ~0x4000));
    }

    sealed class NativeGuardMemory : BenchmarkHalfStep.Proxy
    {
        public uint MotionAddress;
        public ushort ExistingMotion;
        public bool ForceRetreat;
        public override ushort ReadU16(uint address)
        {
            ushort value = base.ReadU16(address);
            return address == MotionAddress && value == ExistingMotion && (value != 0x500 || ForceRetreat) ? (ushort)0x602 : value;
        }
    }

    static void ShowNotice(string value) { notice = value; noticeUntil = hudClock.ElapsedMilliseconds + 2200; }

    sealed class LockHud : IPanel
    {
        public string Name => "Gameplay status";
        public bool IsOpen { get; set; } = true;
        public void Draw()
        {
            if (hudClock.ElapsedMilliseconds - hudUpdated > 1000) return;
            string text = hudClock.ElapsedMilliseconds < noticeUntil ? notice : target != 0 ? "ALVO TRAVADO - segure L1" : guardHeld ? "DEFESA - R1" : "";
            if (text.Length == 0) return;
            var viewport = ImGui.GetMainViewport();
            Vector2 size = ImGui.CalcTextSize(text);
            Vector2 pos = viewport.Pos + new Vector2((viewport.Size.X - size.X) / 2, 40);
            var draw = ImGui.GetForegroundDrawList();
            draw.AddRectFilled(pos - new Vector2(10, 6), pos + size + new Vector2(10, 6), 0xC0000000, 5);
            draw.AddText(pos, target != 0 ? 0xFF60E8FFu : 0xFFFFFFFFu, text);
        }
    }
}
















