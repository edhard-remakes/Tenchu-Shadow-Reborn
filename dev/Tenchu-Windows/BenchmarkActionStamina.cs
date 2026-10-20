using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace TenchuSARecomp;

internal static partial class BenchmarkRoninGameplay
{
    internal static bool HeavyInProgress(uint actor)=>heavyPhase!=0 && actor==heavyOwner;
    internal static bool DodgeWindow(IMemory m,uint actor)=>Frame<=dodgeUntil &&
        m.ReadU16(m.ReadU32(actor+0x5C)) is >=0xB05 and <=0xB08;
    internal static bool IsProjectileHit(IMemory m,int id)
    {
        if(id is <0 or >=80)return false;
        uint slot=0x800B9D60u+(uint)id*120;
        if(m.ReadU32(slot+0x24)!=1)return false;
        // Native GetItemNo scans these 30 item records by collision ID.
        for(uint i=0;i<30;i++)
        {
            uint entry=0x800BD808u+i*0x58,model=m.ReadU32(entry+0x10);
            if(Pointer(model) && (short)m.ReadU16(model+0x58)==id)
                return m.ReadU32(entry+8) is 1 or 20 or 21 or 22 or 23;
        }
        return false;
    }
}
