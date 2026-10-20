using System.Diagnostics;
using System.Reflection;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace TenchuSARecomp;

// Test-only experiment for the supplied USA revision. Do not ship as a speed
// option: it deliberately measures whether changing the wait also speeds logic.
internal static class BenchmarkGameTiming
{
    delegate void Guest(CpuContext cpu, IMemory memory);
    delegate void GuestHook(Guest original, CpuContext cpu, IMemory memory);
    static Hook? hook;
    static Hook? motionHook;
    static bool strict60;
    static long motionSteps;
    static long lastMotionSteps;
    static readonly Stopwatch clock = new();
    static int unlockAt;
    static long lastSample;
    static uint lastGameClock;
    static int frames;

    public static void Install(Assembly game)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("TENCHU_TEST_UNLOCK_AT"), out unlockAt)) return;
        var method = game.GetType("Recompiled.Tenchu", true)!.GetMethod("func_80016660")!;
        hook = new Hook(method, (GuestHook)EndDrawing);
        strict60 = Environment.GetEnvironmentVariable("TENCHU_TEST_STRICT60") == "1";
        motionHook = new Hook(game.GetType("Recompiled.Tenchu", true)!.GetMethod("func_8001BB5C")!, (GuestHook)PlayMotion);
        clock.Start();
        Console.WriteLine($"[timing-test] experiment scheduled at t={unlockAt}s");
    }

    static void PlayMotion(Guest original, CpuContext cpu, IMemory memory)
    {
        uint manager = cpu.A0;
        uint player = memory.ReadU32(0x8009590C);
        bool isPlayer = player is >= 0x80010000 and < 0x80200000 &&
            memory.ReadU32(player + 0x5C) == manager;
        short before = isPlayer ? (short)memory.ReadU16(manager + 2) : (short)0;
        original(cpu, memory);
        if (!isPlayer) return;
        short after = (short)memory.ReadU16(manager + 2);
        if (after == before + 1 || (before >= 0 && after == 0 && before != 0)) motionSteps++;
    }

    static void EndDrawing(Guest original, CpuContext cpu, IMemory memory)
    {
        // Only the identified main gameplay loop, not menus or loading screens.
        if (cpu.RA == 0x80015B08 && cpu.A0 == 0xFFFFFFFE)
        {
            long now = clock.ElapsedMilliseconds;
            uint gameClock = memory.ReadU32(cpu.GP + 0x18);
            bool unlocked = now >= unlockAt * 1000L;
            // VSync(1) is a nonblocking query in the PS1 SDK, not a one-frame
            // wait. EndDrawing(0) reaches VSync(0), which waits one vblank.
            if (unlocked)
            {
                cpu.A0 = 0;
                // Bypass the original adaptive frame-skip branch, which would
                // otherwise execute extra simulation iterations between draws.
                if (strict60) memory.WriteU16(cpu.GP + 0x1C, 2);
            }
            frames++;
            if (lastSample == 0)
            {
                lastSample = now;
                lastGameClock = gameClock;
                lastMotionSteps = motionSteps;
                frames = 0;
            }
            else if (now - lastSample >= 3000)
            {
                double seconds = (now - lastSample) / 1000.0;
                Console.WriteLine($"[timing-test] t={now / 1000.0:F1}s mode={(unlocked ? (strict60 ? "strict60" : "unlocked") : "original")} updates/s={frames / seconds:F2} GameClock/s={unchecked(gameClock - lastGameClock) / seconds:F2} player-motion-steps/s={(motionSteps - lastMotionSteps) / seconds:F2}");
                lastSample = now;
                lastGameClock = gameClock;
                lastMotionSteps = motionSteps;
                frames = 0;
            }
        }
        original(cpu, memory);
    }
}
