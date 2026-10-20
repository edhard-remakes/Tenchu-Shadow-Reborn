using System.Reflection;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace TenchuSARecomp;

internal static partial class BenchmarkRoninGameplay
{
    static bool heavyBloodScope;
    static int bloodRemainder,executionBloodRemainder;
    static Guest heavyWorld=null!,heavyDrawPart=null!,heavyAttackId=null!,heavyNativeBlood=null!,heavyHumanAction=null!;
    sealed record Detached(uint Object,int Index,byte[] Matrix)
    {public readonly byte[] Backup=new byte[0x50];public ushort Attribute;}
    sealed record Severed(uint Actor,uint Model,uint Head,uint Parts,long Frame,int Floor,byte[] Matrix,int Index,Detached[] Group)
    {public long LastTrail=-1;public bool Splashed;}
    static readonly Dictionary<uint,Severed> severed=new();
    static readonly bool heavyFixture=Environment.GetEnvironmentVariable("TENCHU_TEST_HEAVY_TARGET")=="1" &&
        int.TryParse(Environment.GetEnvironmentVariable("TENCHU_BENCHMARK_SECONDS"),out int testSeconds) && testSeconds>0;
    static uint heavyTestTarget;
    static long heavyInjectedSerial=-1;
    static bool damageAudited;
    static int nextSeverPart;
    internal static bool IsBossKind(ushort kind)=>(kind&0xF0)==0x80;
    internal static bool CanSeverKind(ushort kind)=>kind is >=7 and <=9 or >=0x10 and <0x80;
    internal static bool HeavyTestIdle(IMemory m,uint actor)=>heavyFixture && actor==heavyTestTarget &&
        (short)m.ReadU16(actor+8)>0 && m.ReadU16(actor+2)<16;
    internal static void HeavyFixtureTick(CpuContext c,IMemory m,int count)
    {
        if(!heavyFixture || Frame<120)return;
        uint player=m.ReadU32(0x8009590C);
        if(!damageAudited)
        {
            damageAudited=true;
            foreach(uint clip in new uint[]{0x700,0x701,0x704,0x706,0x709})
            {
                var helper=HeavyHelper(c);helper.A0=player;helper.A1=clip;heavyAttackId(helper,m);
                int id=(short)helper.V0;
                Console.WriteLine($"[damage-audit] clip={clip:X4} row={id} base={(id>=0 && id<512?(short)m.ReadU16(0x80087322u+(uint)id*16):-1)} difficulty={m.ReadU8(0x80095652)}");
            }
        }
        if(heavyTestTarget!=0)
        {
            if((short)m.ReadU16(heavyTestTarget+8)>0)
            {
                uint p=m.ReadU32(player+0x38),e=m.ReadU32(heavyTestTarget+0x38);
                for(uint j=0;j<3;j++)m.WriteU32(e+j*4,m.ReadU32(p+j*4));
                m.WriteU32(e,m.ReadU32(e)+700);
                m.WriteU16(heavyTestTarget+0x40,0);m.WriteU16(heavyTestTarget+0x42,0);m.WriteU16(heavyTestTarget+0x44,0);
                if(Environment.GetEnvironmentVariable("TENCHU_TEST_HEAVY_CONTACT")=="1" && heavyPhase==2 &&
                    heavyInjectedSerial!=heavySerial && !heavyContactSeen.Contains((heavySerial,heavyTestTarget)))
                {
                    uint model=m.ReadU32(player+0x58),parts=m.ReadU32(model+0x68);
                    for(uint hand=13;hand<=14 && hand<m.ReadU16(model+0x64);hand++)
                    {
                        uint weapon=m.ReadU32(parts+hand*4);int id=(short)m.ReadU16(weapon+0x58);
                        if(id<0 ||id>=64 || m.ReadU32(0x800B9D84u+(uint)id*120)!=player)continue;
                        m.WriteU16(heavyTestTarget+0x46,(ushort)id);
                        m.WriteU16(heavyTestTarget+4,(ushort)(m.ReadU16(heavyTestTarget+4)|0x4000));
                        heavyInjectedSerial=heavySerial;Console.WriteLine($"[heavy-test] injected confirmed native weapon slot={id} frame={Frame}");
                        byte[] globals=new byte[28];for(uint j=0;j<28;j++)globals[j]=m.ReadU8(c.GP+0x80C+j);
                        try{var helper=HeavyHelper(c);helper.A0=heavyTestTarget;heavyHumanAction(helper,m);}
                        finally{for(uint j=0;j<28;j++)m.WriteU8(c.GP+0x80C+j,globals[j]);}
                        break;
                    }
                }
            }
            return;
        }
        for(uint i=0;i<count;i++)
        {
            uint a=m.ReadU32(0x800BC410+i*4);if(!Pointer(a)||a==player || m.ReadU16(a)<0x10 ||m.ReadU16(a)>=0x80 ||(short)m.ReadU16(a+8)<=0)continue;
            uint p=m.ReadU32(player+0x38),e=m.ReadU32(a+0x38);
            for(uint j=0;j<3;j++)m.WriteU32(e+j*4,m.ReadU32(p+j*4));
            m.WriteU32(e,m.ReadU32(e)+750);m.WriteU16(m.ReadU32(player+0x3C)+2,3072);
            m.WriteU16(a+8,50);m.WriteU16(a+4,(ushort)((m.ReadU16(a+4)&~0x80)|0x42));
            m.WriteU16(a+0x40,0);m.WriteU16(a+0x42,0);m.WriteU16(a+0x44,0);
            m.WriteU32(player+0x74,m.ReadU32(a+0x58));
            heavyTestTarget=a;Console.WriteLine($"[heavy-test] native target={a:X8} prepared life=50 frame={Frame}");return;
        }
    }
    static void InstallHeavyEffects(Type type)
    {
        heavyWorld=type.GetMethod("func_800646B8")!.CreateDelegate<Guest>();
        heavyNativeBlood=type.GetMethod("func_80032814")!.CreateDelegate<Guest>();
        heavyAttackId=type.GetMethod("func_8002A228")!.CreateDelegate<Guest>();
        heavyHumanAction=type.GetMethod("func_8001BDE4")!.CreateDelegate<Guest>();
        heavyDrawPart=type.GetMethod("func_80016878")!.CreateDelegate<Guest>();
        hooks.Add(new Hook(type.GetMethod("func_80032814")!, (Detour)HeavyBlood));
        hooks.Add(new Hook(type.GetMethod("func_8004EFE4")!, (Detour)HeavySound));
        hooks.Add(new Hook(type.GetMethod("func_800389AC")!, (Detour)HeavyGuardSparks));
        hooks.Add(new Hook(type.GetMethod("func_80016CBC")!, (Detour)HeavyCorpseDraw));
    }
    internal static int ExtraBlood(int count)
    {
        int scaled=count*24+bloodRemainder;
        bloodRemainder=scaled%10;return scaled/10;
    }
    internal static int ExecutionBlood(int count)
    {
        int scaled=count*15+executionBloodRemainder;
        executionBloodRemainder=scaled%10;return scaled/10;
    }
    static void HeavyBlood(Guest original,CpuContext c,IMemory m)
    {
        uint count=c.A1;
        if(heavyBloodScope && count is >0 and <=32)
        {c.A1=(uint)ExtraBlood((int)count);Console.WriteLine($"[heavy] blood particles={count}->{c.A1}");}
        else if(count is >0 and <=256 && BenchmarkHalfStep.InGameplayActors &&
            BenchmarkExecution.IsPairedActor(m,m.ReadU32(c.GP+0x80C)))
        {c.A1=(uint)ExecutionBlood((int)count);Console.WriteLine($"[execution] blood particles={count}->{c.A1}");}
        original(c,m);
    }
    static void HeavySound(Guest original,CpuContext c,IMemory m)
    {
        // The native damage callback emits this once per connected sword hit.
        if(heavyBloodScope && c.A1 is 4 or 5)
        {c.A1=4;Console.WriteLine($"[heavy] connected impact sound frame={Frame}");}
        original(c,m);
    }
    static void HeavyGuardSparks(Guest original,CpuContext c,IMemory m)
    {
        bool doubleSpark=heavyBloodScope && c.RA==0x8001DA44 &&
            BenchmarkStamina.HeavyGuardSurvives(m.ReadU32(c.GP+0x80C));
        if(!doubleSpark){original(c,m);return;}
        var entry=c.Snapshot();original(c,m);var done=c.Snapshot();
        try{c.Restore(entry);original(c,m);}finally{c.Restore(done);}
    }
    internal static void HeavyDamage(Action<CpuContext,IMemory> original,CpuContext c,IMemory m,uint victim)
    {
        // One native contact only; no delayed replay for this slash.
        bool prior=heavyBloodScope;heavyBloodScope=true;
        int attack=(short)m.ReadU16(heavyOwner+0x8C);
        var proxy=new HeavyDamageMemory{Inner=m,Health=victim+8,Power=attack is >=0 and <512 ? 0x80087322u+(uint)attack*16:0};
        try{original(c,proxy);}
        finally{heavyBloodScope=prior;}


        if(proxy.Applied)
        {
            Console.WriteLine($"[heavy] connected hit part={(SecondHeavyContact?2:1)} victim={victim:X8} life={(short)m.ReadU16(victim+8)}");
            if((short)m.ReadU16(victim+8)<=0)SeverHead(c,m,victim);
            HeavyLaunch(c,m,victim);
        }
    }
    static void HeavyLaunch(CpuContext c,IMemory m,uint victim)
        => LaunchHit(c,m,heavyOwner,victim,SecondHeavyContact);
    internal static void LaunchBrokenHit(CpuContext c,IMemory m,uint attacker,uint victim)
        => LaunchHit(c,m,attacker,victim,false);
    static void LaunchHit(CpuContext c,IMemory m,uint attacker,uint victim,bool second)
    {
        if((short)m.ReadU16(victim+8)<=0)return; // Preserve native death motion; never launch a corpse.
        uint p=m.ReadU32(attacker+0x38),q=m.ReadU32(victim+0x38);
        if(!Pointer(p)||!Pointer(q))return;
        double dx=(int)m.ReadU32(q)-(double)(int)m.ReadU32(p),dz=(int)m.ReadU32(q+8)-(double)(int)m.ReadU32(p+8);
        double distance=Math.Sqrt(dx*dx+dz*dz);
        if(distance<1){double a=heavyHeading*Math.PI*2/4096;dx=-Math.Sin(a);dz=-Math.Cos(a);distance=1;}
        int speed=second?300:240;
        if((short)m.ReadU16(victim+8)>0)
        {
            RequestMotion(c,m,victim,0x1005);
            m.WriteU16(c.GP+0x824,0x1005);m.WriteU16(c.GP+0x826,1);
        }
        m.WriteU16(victim+0x40,unchecked((ushort)(short)Math.Round(dx/distance*speed)));
        m.WriteU16(victim+0x44,unchecked((ushort)(short)Math.Round(dz/distance*speed)));
        m.WriteU16(victim+0x42,unchecked((ushort)(short)(second?-180:-140)));
        Console.WriteLine($"[heavy] launch hit={(second?2:1)} frame={Frame} victim={victim:X8}");
    }
    internal static void ObserveHeavyAlive(IMemory m,uint actor)
    {
        if(severed.Count>0)severed.Remove(m.ReadU32(actor+0x58));
    }
    static CpuContext HeavyHelper(CpuContext c)
    {var helper=new CpuContext();helper.Restore(c.Snapshot());helper.SP-=0x1000;return helper;}
    static void SeverHead(CpuContext c,IMemory m,uint actor)
    {
        if(!CanSeverKind(m.ReadU16(actor)))return;
        uint model=m.ReadU32(actor+0x58),pos=m.ReadU32(actor+0x38);
        if(!Pointer(model)||!Pointer(pos)||severed.ContainsKey(model)||m.ReadU16(model+0x64)<3)return;
        uint parts=m.ReadU32(model+0x68);
        if(!Pointer(parts))return;
        int sequence=nextSeverPart++;
        if(heavyFixture && Environment.GetEnvironmentVariable("TENCHU_TEST_SEVER_ARM")=="1")sequence=1;
        int index=ChooseSeverRoot(m,model,sequence);
        uint head=m.ReadU32(parts+(uint)index*4);if(!Pointer(head))return;
        var group=new List<Detached>();
        int n=Math.Clamp((int)m.ReadU16(model+0x64),0,32);
        for(int i=0;i<n;i++)
        {
            uint obj=m.ReadU32(parts+(uint)i*4);if(!Pointer(obj))continue;
            uint parent=obj;bool belongs=false;
            for(int depth=0;depth<32 && Pointer(parent);depth++)
            {if(parent==head){belongs=true;break;}parent=m.ReadU32(parent+0x48);}
            if(!belongs)continue;
            var partHelper=HeavyHelper(c);uint partMatrix=partHelper.SP+0x100;
            partHelper.A0=obj;partHelper.A1=partMatrix;heavyWorld(partHelper,m);
            byte[] partBytes=new byte[32];for(uint j=0;j<32;j++)partBytes[j]=m.ReadU8(partMatrix+j);
            group.Add(new(obj,i,partBytes));
        }
        if(group.Count==0)return;
        var helper=HeavyHelper(c);uint matrix=helper.SP+0x100;
        helper.A0=head;helper.A1=matrix;heavyWorld(helper,m);
        byte[] saved=new byte[32];for(uint i=0;i<32;i++)saved[i]=m.ReadU8(matrix+i);
        if(severed.Count>=128)severed.Remove(severed.Keys.First());
        severed[model]=new(actor,model,head,parts,Frame,(int)m.ReadU32(pos+4)-120,saved,index,group.ToArray());
        Console.WriteLine($"[heavy] severed {(index==2?"head":"arm")} actor={actor:X8} model={model:X8} index={index} pieces={group.Count}");
    }
    internal static int ChooseSeverRoot(IMemory m,uint model,int sequence)
    {
        if((sequence&1)==0)return 2;
        int n=m.ReadU16(model+0x64);uint parts=m.ReadU32(model+0x68);
        if(!Pointer(parts)||n<7)return 2;
        uint torso=m.ReadU32(parts+4);int first=(sequence&2)==0?3:5,other=first==3?5:3;
        foreach(int index in new[]{first,other})
        {
            uint arm=m.ReadU32(parts+(uint)index*4);
            if(Pointer(arm) && m.ReadU32(arm+0x48)==torso)return index;
        }
        return 2;
    }
    static void HeavyCorpseDraw(Guest original,CpuContext c,IMemory m)
    {
        if(!severed.TryGetValue(c.A0,out var piece)){original(c,m);return;}
        if(m.ReadU32(piece.Actor+0x58)!=piece.Model || (short)m.ReadU16(piece.Actor+8)>0 ||
            !CanSeverKind(m.ReadU16(piece.Actor)) || m.ReadU32(piece.Model+0x68)!=piece.Parts || m.ReadU32(piece.Parts+(uint)piece.Index*4)!=piece.Head)
        {severed.Remove(c.A0);original(c,m);return;}
        foreach(var part in piece.Group)
            if(m.ReadU32(piece.Parts+(uint)part.Index*4)!=part.Object)
            {severed.Remove(c.A0);original(c,m);return;}
        foreach(var part in piece.Group)
        {part.Attribute=m.ReadU16(part.Object+0x5A);m.WriteU16(part.Object+0x5A,(ushort)(part.Attribute|1));}
        try{original(c,m);}finally{foreach(var part in piece.Group)m.WriteU16(part.Object+0x5A,part.Attribute);}
        // Draw the same textured geometry detached, without changing the body
        // hierarchy, collision objects or shared mesh data permanently.
        double t=Math.Min(150,Math.Max(0,Frame-piece.Frame));
        int x=BitConverter.ToInt32(piece.Matrix,20)+(int)(Math.Min(t,65)*24);
        int z=BitConverter.ToInt32(piece.Matrix,28)+(int)(Math.Min(t,65)*16);
        int y=Math.Min(piece.Floor,BitConverter.ToInt32(piece.Matrix,24)+(int)(-52*t+.85*t*t));
        foreach(var part in piece.Group)
        {
            uint head=part.Object;var saved=part.Backup;ushort attr=part.Attribute;
            for(uint i=0;i<saved.Length;i++)saved[i]=m.ReadU8(head+i);
            try
            {
            for(uint i=0;i<32;i++)m.WriteU8(head+4+i,part.Matrix[i]);
            m.WriteU32(head+0x18,unchecked((uint)(x+BitConverter.ToInt32(part.Matrix,20)-BitConverter.ToInt32(piece.Matrix,20))));
            m.WriteU32(head+0x1C,unchecked((uint)(y+BitConverter.ToInt32(part.Matrix,24)-BitConverter.ToInt32(piece.Matrix,24))));
            m.WriteU32(head+0x20,unchecked((uint)(z+BitConverter.ToInt32(part.Matrix,28)-BitConverter.ToInt32(piece.Matrix,28))));
            m.WriteU32(head,0);m.WriteU32(head+0x44,0);m.WriteU32(head+0x48,0);m.WriteU32(head+0x4C,0);
            m.WriteU16(head+0x5A,(ushort)((attr&~1)|2));
            var helper=HeavyHelper(c);helper.A0=head;heavyDrawPart(helper,m);
            }
            finally
            {for(uint i=0;i<saved.Length;i++)m.WriteU8(head+i,saved[i]);m.WriteU16(head+0x5A,attr);}
        }
            bool landed=y>=piece.Floor;
            if((landed && !piece.Splashed) || (!landed && t<90 && Frame-piece.LastTrail>=6))
            {
                SpawnHeavyBlood(c,m,x,y,z,landed?6u:3u);
                piece.LastTrail=Frame;if(landed)piece.Splashed=true;
            }
    }
    static void SpawnHeavyBlood(CpuContext c,IMemory m,int x,int y,int z,uint count)
    {
        var helper=HeavyHelper(c);uint p=helper.SP+0x100;
        m.WriteU32(p,unchecked((uint)x));m.WriteU32(p+4,unchecked((uint)y));m.WriteU32(p+8,unchecked((uint)z));m.WriteU32(p+12,0);
        helper.A0=p;helper.A1=count;helper.A2=90;heavyNativeBlood(helper,m);
    }
}




