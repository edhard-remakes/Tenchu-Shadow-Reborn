using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace TenchuSARecomp;

internal static partial class BenchmarkRoninGameplay
{
    sealed record HeavyContact(Action<CpuContext,IMemory> Original,uint Actor,uint Victim,uint Model,uint Weapon,uint Slot,uint GP,
        ushort WeaponId,byte[] SlotBytes,byte[] Globals,uint Manager,byte[] MotionBytes,long Serial,long Due,bool Guarded);
    static readonly List<HeavyContact> heavyContacts=new();
    static readonly HashSet<(long Serial,uint Victim)> heavyContactSeen=new();
    internal static bool SecondHeavyContact {get;private set;}
    static HeavyContact? CaptureHeavyContact(Action<CpuContext,IMemory> original,CpuContext c,IMemory m,uint victim)
    {
        if(SecondHeavyContact || !HeavyStrike(m,heavyOwner) || heavyContactSeen.Contains((heavySerial,victim)))return null;
        int id=m.ReadU16(victim+0x46);if(id>=64)return null;
        uint slot=0x800B9D60u+(uint)id*120,weapon=m.ReadU32(slot);
        if(!Pointer(weapon))return null;
        byte[] bytes=new byte[120],globals=new byte[28],motion=new byte[8];uint manager=m.ReadU32(heavyOwner+0x5C);
        for(uint i=0;i<120;i++)bytes[i]=m.ReadU8(slot+i);
        for(uint i=0;i<28;i++)globals[i]=m.ReadU8(c.GP+0x80C+i);
        for(uint i=0;i<8;i++)motion[i]=m.ReadU8(manager+i);
        return new(original,heavyOwner,victim,m.ReadU32(victim+0x58),weapon,slot,c.GP,m.ReadU16(weapon+0x58),bytes,globals,manager,motion,heavySerial,Frame+6,false);
    }
    static void QueueHeavyContact(HeavyContact contact)
    {
        if(heavyContactSeen.Add((contact.Serial,contact.Victim)) && heavyContacts.Count<64)heavyContacts.Add(contact);
        if(heavyContactSeen.Count>128)heavyContactSeen.RemoveWhere(k=>k.Serial<heavySerial-2);
    }
    internal static void TickHeavyContacts(CpuContext c,IMemory m)
    {
        for(int i=heavyContacts.Count-1;i>=0;i--)
        {
            var hit=heavyContacts[i];if(Frame<hit.Due)continue;heavyContacts.RemoveAt(i);
            if(hit.Serial!=heavySerial || hit.Actor!=heavyOwner || (short)m.ReadU16(hit.Actor+8)<=0 ||
                m.ReadU16(hit.Actor+2) is not (0 or 1 or 2 or 5 or 6 or 7) ||
                m.ReadU32(hit.Victim+0x58)!=hit.Model || (short)m.ReadU16(hit.Victim+8)<=0 ||
                BenchmarkStamina.IsBroken(hit.Actor))continue;
            // A block that already broke posture ends this slash; preserve its execution window.
            if(hit.Guarded && BenchmarkStamina.IsBroken(hit.Victim))continue;
            var proxy=new HeavyContactMemory{Inner=m,Slot=hit.Slot,Weapon=hit.Weapon,GP=hit.GP,Manager=hit.Manager,Actor=hit.Actor,
                Victim=hit.Victim,ContactId=(ushort)((hit.Slot-0x800B9D60u)/120),
                Followthrough=!hit.Guarded,
                MotionBytes=(byte[])hit.MotionBytes.Clone(),
                SlotBytes=(byte[])hit.SlotBytes.Clone(),Globals=(byte[])hit.Globals.Clone(),WeaponId=hit.WeaponId};
            var helper=HeavyHelper(c);helper.GP=hit.GP;helper.A0=hit.Victim;
            // Only this already-confirmed follow-through can hit its victim
            // again during the native knockdown invulnerability window.
            if(!hit.Guarded){proxy.WriteU16(hit.GP+0x824,0x501);proxy.WriteU16(hit.GP+0x826,ushort.MaxValue);}
            try
            {
                SecondHeavyContact=true;
                BenchmarkStamina.ReplayHeavyContact(hit.Original,helper,proxy,hit.Victim,hit.Actor);
                ushort motion=proxy.ReadU16(hit.GP+0x824);
                if(motion!=ushort.MaxValue)RequestMotion(helper,m,hit.Victim,motion);
                Console.WriteLine($"[heavy] second contact same slash frame={Frame} victim={hit.Victim:X8}");
            }
            finally{SecondHeavyContact=false;}
        }
    }
    // Reuse only the confirmed contact. Its consumed collision slot can already
    // belong to someone else; all slot/global/id writes stay in this local copy.
    internal sealed class HeavyContactMemory : BenchmarkHalfStep.Proxy
    {
        public uint Slot,Weapon,GP,Manager,Actor,Victim;public ushort WeaponId,ContactId;
        public bool Followthrough;
        public byte[] SlotBytes=null!,Globals=null!,MotionBytes=null!;
        bool ReadShadow(uint a,out byte value)
        {
            if(a>=Slot && a<Slot+120){value=SlotBytes[a-Slot];return true;}
            if(a>=GP+0x80C && a<GP+0x828){value=Globals[a-GP-0x80C];return true;}
            if(a>=Manager && a<Manager+8){value=MotionBytes[a-Manager];return true;}
            if(a==Actor+2 || a==Actor+3){value=a==Actor+2?(byte)7:(byte)0;return true;}
            if(Followthrough && (a==Victim+2 || a==Victim+3)){value=a==Victim+2?(byte)7:(byte)0;return true;}
            if(a==Victim+0x46 || a==Victim+0x47){value=(byte)(ContactId>>(int)((a-Victim-0x46)*8));return true;}
            if(a==Weapon+0x58 || a==Weapon+0x59){value=(byte)(WeaponId>>(int)((a-Weapon-0x58)*8));return true;}
            value=0;return false;
        }
        bool WriteShadow(uint a,byte v)
        {
            if(a>=Slot && a<Slot+120){SlotBytes[a-Slot]=v;return true;}
            if(a>=GP+0x80C && a<GP+0x828){Globals[a-GP-0x80C]=v;return true;}
            if(a>=Manager && a<Manager+8){MotionBytes[a-Manager]=v;return true;}
            if(a==Actor+2 || a==Actor+3)return true;
            if(a==Victim+0x46 || a==Victim+0x47)return true;
            if(a==Weapon+0x58 || a==Weapon+0x59)
            {int shift=(int)((a-Weapon-0x58)*8);WeaponId=(ushort)((WeaponId&~(255<<shift))|(v<<shift));return true;}
            return false;
        }
        public override byte ReadU8(uint a)=>ReadShadow(a,out byte v)?v:base.ReadU8(a);
        public override ushort ReadU16(uint a)=> (ushort)(ReadU8(a)|(ReadU8(a+1)<<8));
        public override uint ReadU32(uint a)=> (uint)(ReadU16(a)|(ReadU16(a+2)<<16));
        public override void WriteU8(uint a,byte v){if(!WriteShadow(a,v))base.WriteU8(a,v);}
        public override void WriteU16(uint a,ushort v){WriteU8(a,(byte)v);WriteU8(a+1,(byte)(v>>8));}
        public override void WriteU32(uint a,uint v){WriteU16(a,(ushort)v);WriteU16(a+2,(ushort)(v>>16));}
    }
}

