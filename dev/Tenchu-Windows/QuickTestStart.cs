using System.Reflection;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace TenchuSARecomp;

// Explicit test shortcut for this USA executable. Cold-load the native gameplay
// overlay through the normal runtime; keep its stage/AI initialization intact.
// This is a repeatable mission start, not a partial RAM save state.
internal static class QuickTestStart
{
    delegate void Guest(CpuContext c, IMemory m);
    delegate void Detour(Guest original, CpuContext c, IMemory m);
    static Hook? bootHook, briefingHook;
    static bool skipBriefing;
    public static void Install(Assembly game)
    {
        if (Environment.GetEnvironmentVariable("TENCHU_QUICK_TEST") != "1" && Environment.GetEnvironmentVariable("TENCHU_QUICK_MENU") != "1") return;
        var type = game.GetType("Recompiled.Tenchu", true)!;
        bootHook = new Hook(type.GetMethod("func_8001A578")!, (Detour)Boot);
        briefingHook = new Hook(type.GetMethod("func_80015B10")!, (Detour)((original,c,m) =>
        {
            if (!skipBriefing) { original(c,m); return; }
            skipBriefing = false;
            Console.WriteLine("[quick-test] initial briefing/loadout skipped; native stage initialization continues");
        }));
    }

    static void Boot(Guest original, CpuContext c, IMemory m)
    {
        using var fs = CueFs.Open(RecompOne.Runtime.Config.ConfigManager.Game.CdPath);
        byte[] exe = fs.ReadFile("TENCHU/MAIN.EXE");
        uint U(int offset) => BitConverter.ToUInt32(exe,offset);
        uint entry = U(0x10), address = U(0x18), size = U(0x1c);
        if (entry != 0x8005E5CC || address != 0x80011000 || size > exe.Length-0x800)
            throw new InvalidDataException("Quick-test requires the verified USA MAIN.EXE");
        m.LoadBytes(address,exe.AsSpan(0x800,(int)size).ToArray());
        Dispatcher.Load("game");
        // Defaults verified against USA InitPersistentState (8001576C).
        const uint state=0x80010000;
        m.ZeroRange(state,0x470);
        m.WriteU32(state,0x19980722);
        m.WriteU8(state+4,0); // Rikimaru
        m.WriteU8(state+5,0); // Punish the Evil Merchant
        m.WriteU8(state+6,0); // repeatable enemy placement
        m.WriteU8(state+7,255); // grapple
        m.WriteU8(state+8,10); // shuriken
        m.WriteU8(state+10,2); // medicine
        m.WriteU8(state+0x59,1);
        m.WriteU8(state+0x5a,127); m.WriteU8(state+0x5b,127);
        m.WriteU8(state+0x5d,1);
        m.WriteU8(state+0x60,1); m.WriteU8(state+0x61,1);
        for(uint i=0;i<32;i++)
        {
            m.WriteU8(state+0x27+i,m.ReadU8(state+7+i));
            m.WriteU8(state+0x40c+i,254); m.WriteU8(state+0x42c+i,254);
        }
        m.WriteU8(state+0x40c,255); m.WriteU8(state+0x40d,6); m.WriteU8(state+0x40f,2);
        m.WriteU8(state+0x42c,255); m.WriteU8(state+0x42d,6); m.WriteU8(state+0x42f,2);
        if(Environment.GetEnvironmentVariable("TENCHU_QUICK_MENU")=="1")
        {
            m.WriteU8(state+0x48,1); // Native returning-to-menu flag skips the opening movie in this fixture.
            exe=fs.ReadFile("TENCHU/MENU.EXE");
            entry=U(0x10);address=U(0x18);size=U(0x1c);
            if(address!=0x80011000 || size>exe.Length-0x800)throw new InvalidDataException("Invalid menu test overlay");
            m.LoadBytes(address,exe.AsSpan(0x800,(int)size).ToArray());Dispatcher.Load("menu");
            c.GP=U(0x14);c.RA=0;
            Console.WriteLine($"[quick-test] native menu boot entry={entry:X8}");
            Dispatcher.Call(c,m,entry);return;
        }
        c.GP=U(0x14); c.RA=0;
        skipBriefing=true;
        Console.WriteLine("[quick-test] direct native mission boot: Rikimaru / mission 1 / layout 0");
        Dispatcher.Call(c,m,entry);
    }
}
