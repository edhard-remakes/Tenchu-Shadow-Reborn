using System.Diagnostics;
using System.Reflection;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace TenchuSARecomp;

// Preserve SDK VSync and the 60-Hz hardware/IRQ/CD clocks. The runtime's
// read-count heuristic is for idle hardware polling, not active guest work.
internal static class BenchmarkFramePacing
{
    static Hook? hook,vsyncHook;
    delegate void Guest(CpuContext c,IMemory m);
    delegate void GuestHook(Guest original,CpuContext c,IMemory m);
    static bool compute;
    static bool liveFrame;
    static long nextFrame;
    static readonly bool originalPacing=Environment.GetEnvironmentVariable("TENCHU_TEST_PACING_ORIGINAL")=="1";
    static long start,waitTicks,waitCalls,bypasses,windowStart;
    static readonly List<double> costs=new(180);
    public static void Install(Assembly game)
    {
        var method=typeof(RecompOne.Runtime.Runtime).GetMethod("ThrottleBusyWait",BindingFlags.Static|BindingFlags.NonPublic)!;
        hook=new Hook(method,(Action<Action>)Throttle);
        var vsync=game.GetType("Recompiled.Tenchu",true)!.GetMethod("func_8007A248")!;
        vsyncHook=new Hook(vsync,(GuestHook)VSync);
        Console.WriteLine($"[frame-pacing] fixed gameplay deadline={!originalPacing}; hardware IRQ/CD/SPU clocks preserved");
    }
    static void Throttle(Action original)
    {
        if(!compute) {original();return;}
        if(!originalPacing){bypasses++;return;}
        long before=Stopwatch.GetTimestamp();original();waitTicks+=Stopwatch.GetTimestamp()-before;waitCalls++;
    }
    public static void Begin(CpuContext c,IMemory m)
    {
        if(c.RA!=0x80015A38 && c.RA!=0x80015A40)return;
        compute=BenchmarkHalfStep.Enabled && m.ReadU16(c.GP+0x78)==0 && (m.ReadU32(0x80095AC8)&4)==0;
        liveFrame=compute;
        start=Stopwatch.GetTimestamp();if(windowStart==0)windowStart=start;
    }
    static void VSync(Guest original,CpuContext c,IMemory m)
    {
        if(originalPacing || !liveFrame || c.RA!=0x800167EC || c.A0!=0)
        {original(c,m);return;}
        liveFrame=false;
        long now=Stopwatch.GetTimestamp(),period=Stopwatch.Frequency/60;
        // A fixed deadline includes the time already spent on AI and drawing.
        // The native wait starts counting from its entry and can miss a vblank
        // which was already delivered during a crowded frame.
        nextFrame=Deadline(nextFrame,now,period);
        while((now=Stopwatch.GetTimestamp())<nextFrame)
        {
            RecompOne.Runtime.Runtime.PumpBackgroundIfDue();
            double remaining=(nextFrame-Stopwatch.GetTimestamp())*1000.0/Stopwatch.Frequency;
            if(remaining>3)Thread.Sleep(Math.Max(1,(int)remaining-2));
            else Thread.SpinWait(100);
        }
        // Preserve the runtime's own 60-Hz hardware IRQ, CD and SPU schedule;
        // do not synthesize an extra interrupt or advance the game clock here.
        RecompOne.Runtime.Runtime.PumpBackgroundIfDue();
        c.V0=0;
    }
    internal static long Deadline(long previous,long now,long period)
        => (previous==0 || now-previous>period*6 ? now : previous)+period;
    public static void End()
    {
        if(!compute)return;compute=false;
        long now=Stopwatch.GetTimestamp();costs.Add((now-start)*1000.0/Stopwatch.Frequency);
        if(costs.Count<60)return;
        costs.Sort();double elapsed=(now-windowStart)/(double)Stopwatch.Frequency;
        Console.WriteLine($"[frame-cost] logic={costs.Count/elapsed:F2}/s compute p50={costs[costs.Count/2]:F2} p95={costs[(int)(costs.Count*.95)]:F2} max={costs[^1]:F2}ms artificial-wait={waitTicks*1000.0/Stopwatch.Frequency:F1}ms calls={waitCalls} bypassed={bypasses}");
        costs.Clear();windowStart=now;waitTicks=waitCalls=bypasses=0;
    }
}
