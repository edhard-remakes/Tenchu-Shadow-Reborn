using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Hardware;

namespace TenchuSARecomp;

internal static partial class BenchmarkRoninGameplay
{
    static long heavyRequest=-1,heavyStart,heavyRecovery=-1;
    static uint heavyOwner;
    static int heavyPhase;
    static long heavySerial;
    static int heavyPart,heavyHangCount=8;
    static long heavyClipStart;
    static ushort heavyHeading;
    const ushort HeavyMotion=0x704; // native third combo slash; original hitboxes
    internal static bool HeavyStrike(IMemory m,uint actor)
        => installed && heavyPhase==2 && actor==heavyOwner && actor==m.ReadU32(0x8009590C) &&
           Pointer(actor) && Frame-heavyStart<480 && m.ReadU16(actor+2)==7 &&
           m.ReadU16(m.ReadU32(actor+0x5C))==HeavyMotion;

    internal static double HeavyTime(long elapsed,int apex)
    {
        int duration=apex*2+15;
        return elapsed<duration ? Math.Max(0,elapsed)*apex/(double)duration : apex+(elapsed-duration)/2.0;
    }
    internal static bool HeavyClock(IMemory m,uint manager)=> installed && heavyPhase==2 && heavyPart==0 &&
        Pointer(heavyOwner) && m.ReadU32(heavyOwner+0x5C)==manager && m.ReadU16(manager)==HeavyMotion;
    internal static double HeavySample=>HeavyTime(Frame-heavyClipStart,heavyHangCount);
    internal static double HeavyTempo(IMemory m,uint actor)=>actor==heavyOwner && HeavyClock(m,m.ReadU32(actor+0x5C)) &&
        Frame-heavyClipStart<heavyHangCount*2+15 ? heavyHangCount*2.0/(heavyHangCount*2+15):1;
    internal static int HeavyForward(IMemory m,uint actor,int delta,bool x)
    {
        if(actor!=heavyOwner || !HeavyStrike(m,actor))return delta;
        double angle=heavyHeading*Math.PI*2/4096,fx=-Math.Sin(angle),fz=-Math.Cos(angle);
        double speed=(short)m.ReadU16(actor+0x40)*fx+(short)m.ReadU16(actor+0x44)*fz;
        return (int)Math.Round(speed*(x?fx:fz));
    }

    static bool HeavyAction(CpuContext c,IMemory m,uint actor)
    {
        if(actor!=player)return false;
        uint manager=m.ReadU32(actor+0x5C);
        ushort status=m.ReadU16(actor+2),motion=m.ReadU16(manager);
        bool ground=status is 0 or 1 or 2 or 5 or 6 && motion!=0x500;
        if(heavyPhase!=0 && (actor!=heavyOwner || !Gameplay(c,m) || BenchmarkStamina.IsBroken(actor) ||
            (short)m.ReadU16(actor+8)<=0 || Frame-heavyStart>=480 ||
            (heavyPhase==2 ? motion!=HeavyMotion && !ground : !ground)))
        {heavyPhase=0;heavyOwner=0;}
        if(heavyPhase==0 && Frame<=heavyRequest && ground && Gameplay(c,m) && !guardHeld &&
            !itemAimSession && (held&Controller.R2)==0 && !BenchmarkStamina.IsBroken(actor))
        {
            heavyRequest=-1;
            if(!BenchmarkStamina.TryGrabCost(m,actor))return false;
            if(!RequestMotion(c,m,actor,0x501))return false;
            heavyOwner=actor;heavyStart=Frame;heavyPhase=1;heavyPart=0;heavyHangCount=8;
            heavySerial++;
            heavyHeading=m.ReadU16(m.ReadU32(actor+0x3C)+2);attackUntil=-1;heldGuardPose=false;
            Console.WriteLine($"[heavy] windup frame={Frame}");
        }
        if(heavyPhase==0)return false;
        if(heavyPhase==1 && Frame-heavyStart>=3)
        {
            if(!RequestMotion(c,m,actor,HeavyMotion)){heavyPhase=0;heavyOwner=0;return false;}
            heavyClipStart=Frame;
            var helper=HeavyHelper(c);helper.A0=actor;helper.A1=HeavyMotion;heavyAttackId(helper,m);
            if((short)helper.V0 is >=0 and <512)heavyHangCount=Math.Clamp((short)m.ReadU16(0x80087324u+helper.V0*16)-3,2,12);
            heavyPhase=2;ShowNotice("GOLPE PESADO");
            Console.WriteLine($"[heavy] native slash={HeavyMotion:X4} frame={Frame}");
        }
        else if(heavyPhase==2 && motion!=HeavyMotion)
        {
            heavyPhase=3;heavyRecovery=Frame+24;Console.WriteLine($"[heavy] recovery frame={Frame}");
        }
        else if(heavyPhase==3 && Frame>=heavyRecovery)
        {heavyPhase=0;heavyOwner=0;return false;}
        // The native actor/animation/weapon handler still runs. Only suppress
        // new commands, so interruption, collisions and native damage survive.
        m.WriteU16(actor+0x10,0);m.WriteU16(actor+0x14,0);
        if(heavyPhase==2)m.WriteU16(m.ReadU32(actor+0x3C)+2,heavyHeading);
        if(heavyPhase!=2){m.WriteU16(actor+0x40,0);m.WriteU16(actor+0x44,0);}
        return true;
    }

    internal sealed class HeavyDamageMemory : BenchmarkHalfStep.Proxy
    {
        public uint Health;
        public uint Power;
        public bool Applied;
        public override ushort ReadU16(uint address)
        {
            ushort value=base.ReadU16(address);
            return address==Power && Power!=0 && (short)value>0
                ? (ushort)Math.Min(short.MaxValue,(int)Math.Ceiling(value*1.0)):value;
        }
        public override void WriteU16(uint address,ushort value)
        {
            if(address==Health && (short)Inner.ReadU16(address)>0 && (short)value<(short)Inner.ReadU16(address))Applied=true;
            base.WriteU16(address,value);
        }
    }
}


