using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace TenchuSARecomp;

// Timed, opt-in integration fixture. Never active from the playing shortcuts.
internal static class BenchmarkCrowd
{
    static readonly bool enabled=Environment.GetEnvironmentVariable("TENCHU_TEST_CROWD")=="1" &&
        int.TryParse(Environment.GetEnvironmentVariable("TENCHU_BENCHMARK_SECONDS"),out int seconds) && seconds>0;
    static readonly List<uint> enemies=new();
    static long lastReport;
    static bool prepared;
    public static bool Protects(IMemory m,uint actor)=>enabled && prepared && actor==m.ReadU32(0x8009590C);
    public static void Tick(CpuContext c,IMemory m)
    {
        if(!enabled)return;
        uint player=m.ReadU32(0x8009590C);
        bool Pointer(uint a)=>a>=0x80010000 && a<0x80200000 && (a&3)==0;
        if(!Pointer(player))return;
        if(!prepared && BenchmarkHalfStep.FrameNumber>=90)
        {
            int count=Math.Clamp((int)m.ReadU16(c.GP+0x8C),0,64);
            for(uint i=0;i<count && enemies.Count<3;i++)
            {
                uint a=m.ReadU32(0x800BC410+i*4);
                if(!Pointer(a) || a==player || (short)m.ReadU16(a+8)<=0)continue;
                int kind=m.ReadU16(a);if(kind>=0x10 && kind<0x80)enemies.Add(a);
            }
            if(enemies.Count<3){enemies.Clear();return;}
            void Place(uint a,int x,int y,int z)
            {
                uint p=m.ReadU32(a+0x38);m.WriteU32(p,unchecked((uint)x));m.WriteU32(p+4,unchecked((uint)y));m.WriteU32(p+8,unchecked((uint)z));
                m.WriteU16(a+0x40,0);m.WriteU16(a+0x42,0);m.WriteU16(a+0x44,0);
            }
            // Retain the normal spawn area to avoid scripted area transitions.
            uint pp=m.ReadU32(player+0x38);
            int px=(int)m.ReadU32(pp),py=(int)m.ReadU32(pp+4),pz=(int)m.ReadU32(pp+8);
            Place(enemies[0],px+1300,py,pz);Place(enemies[1],px+1300,py,pz+1200);Place(enemies[2],px,py,pz+1500);
            foreach(uint a in enemies)m.WriteU16(a+4,(ushort)((m.ReadU16(a+4)&~0x80)|0x42));
            prepared=true;Console.WriteLine("[crowd-test] three live native enemies moved to fixed courtyard combat positions");
        }
        if(prepared)
        {
            // Test endurance only: retain native AI/collision/action routines.
            // Keep participants alive so every measured run has the same load.
            m.WriteU16(player+8,100);
            foreach(uint a in enemies)if(Pointer(a))m.WriteU16(a+8,100);
            if(BenchmarkHalfStep.FrameNumber-lastReport>=180)
            {
                lastReport=BenchmarkHalfStep.FrameNumber;
                Console.WriteLine("[crowd-test] motions="+string.Join(",",enemies.Select(a=>m.ReadU16(m.ReadU32(a+0x5C)).ToString("X4"))));
            }
        }
    }
}
