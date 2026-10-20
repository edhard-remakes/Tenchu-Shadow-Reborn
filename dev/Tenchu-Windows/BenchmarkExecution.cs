using RecompOne.Runtime.Memory;

namespace TenchuSARecomp;

internal static class BenchmarkExecution
{
    static readonly bool trace=Environment.GetEnvironmentVariable("TENCHU_EXECUTION_TRACE")=="1";
    static readonly Dictionary<uint,long> starts=new();
    public static void Trace(IMemory m,uint actor,long frame)
    {
        if(!trace)return;
        bool active=IsPairedActor(m,actor);
        if(active && !starts.ContainsKey(actor))
        { starts[actor]=frame;Console.WriteLine($"[execution-speed] begin actor={actor:X8} frame={frame}"); }
        else if(!active && starts.Remove(actor,out long start))
            Console.WriteLine($"[execution-speed] end actor={actor:X8} frame={frame} duration-60hz-frames={frame-start}");
    }
    static bool Pointer(uint p)=>p>=0x80010000 && p<0x80200000 && (p&3)==0;
    public static bool IsExecuting(IMemory m,uint actor)
    {
        if(!Pointer(actor))return false;
        uint manager=m.ReadU32(actor+0x5C);
        return Pointer(manager) && m.ReadU16(manager) is >=0x714 and <=0x719;
    }
    public static bool IsPairedActor(IMemory m,uint actor)
    {
        if(!Pointer(actor))return false;
        uint manager=m.ReadU32(actor+0x5C);
        if(!Pointer(manager))return false;
        int motion=m.ReadU16(manager);
        return motion is >=0x714 and <=0x719 or >=0x1109 and <=0x110E;
    }
    // 6 full half-step updates per 5 output frames: exactly 1.2x speed.
    // Native event counts advance individually; no animation key is skipped.
    public static long TickStart(long frame)=>frame*6/5;
    public static int Ticks(long frame)=>(int)(TickStart(frame+1)-TickStart(frame));
}
