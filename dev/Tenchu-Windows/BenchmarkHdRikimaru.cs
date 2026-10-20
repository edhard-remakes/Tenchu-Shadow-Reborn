using System.Numerics;
using System.Reflection;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Hle;

namespace TenchuSARecomp;

// Experimental host-side skinning. Only short marker packets enter PS1 RAM;
// the full mesh and its animation calculations stay in ordinary PC memory.
internal static class BenchmarkHdRikimaru
{
    delegate void Guest(CpuContext c, IMemory m);
    delegate void Detour(Guest original, CpuContext c, IMemory m);
    static Hook? hook, menuHook;
    static Guest getLs = null!, menuGetLs = null!;
    static bool menuReported;
    static readonly (uint Count,uint A,uint B)[] nativeSignature = [
        (16,0x009800A2,0x0000004A),(37,0xFFFB0081,0x00000010),(42,0x000E0000,0x0000FF66),
        (18,0x0023FFA7,0x00000034),(14,0x005D0041,0x0000FFFB),(18,0x0022005A,0x00000034),
        (14,0x005CFFBF,0x0000FFFA),(15,0x0219FF6C,0x0000FFFE),(12,0x011B0002,0x00000047),
        (8,0x003BFFC2,0x00000049),(15,0x02190094,0x0000FFFE),(12,0x011BFFFE,0x00000047),
        (8,0x003C003E,0x00000049),(9,0x004A003A,0x0000FFC0),(15,0x00320006,0x0000FFC9)];
    record struct Influence(int Bone, float Weight, Vector3 Local);
    record struct Vertex(Influence A, Influence B, byte R, byte G, byte Bc, short U=0, short V=0);
    record struct Triangle(HleVertex A, HleVertex B, HleVertex C, float Depth, PrimFlags Flags);
    static bool refined;
    static PrimFlags[] faceFlags=[];
    static Vertex[] vertices = [];
    static int[] indices = [];
    static readonly Dictionary<int, List<Triangle>> queued = new();
    static int serial;
    static bool reported;
    static int captureFrame;
    static readonly bool capture = Environment.GetEnvironmentVariable("TENCHU_HD_CAPTURE") == "1";
    static readonly Matrix4x4[] matrices = new Matrix4x4[15];
    static readonly Vector3[] bindOrigins = BuildOrigins();
    static readonly Quaternion[] real = new Quaternion[15], dual = new Quaternion[15];
    static Vector3[] rest = [];
    static Vector3[] BuildOrigins()
    {
        int[] parents = [-1,0,1,1,3,1,5,0,7,8,0,10,11,4,6];
        Vector3[] a = [new(0,-1004,-45),new(-7,-114,-12),new(0,-500,22),new(-175,-315,67),new(-49,254,-30),new(174,-314,67),new(49,254,-29),new(-115,55,4),new(-26,439,-19),new(3,444,49),new(115,55,4),new(26,439,-19),new(-3,444,49),new(-5,275,-9),new(5,274,-10)];
        for(int i=0;i<a.Length;i++) if(parents[i]>=0) a[i]+=a[parents[i]];
        return a;
    }
    static HleVertex[] projected = [];
    static bool Pointer(uint p) => p >= 0x80010000 && p < 0x80200000 && (p & 3) == 0;

    public static void Install(Assembly game)
    {
        refined=Environment.GetEnvironmentVariable("TENCHU_REFINED_RIKIMARU")=="1";
        if (!refined && Environment.GetEnvironmentVariable("TENCHU_HD_RIKIMARU") != "1") return;
        string path = Path.Combine(AppContext.BaseDirectory, "assets", refined?"rikimaru-refined.bin":"rikimaru-hd.bin");
        Load(path);
        if(refined)
        {
            nearMesh=new(vertices,indices,faceFlags,rest,projected);
            Load(Path.Combine(AppContext.BaseDirectory,"assets","rikimaru-refined-far.bin"));
            farMesh=new(vertices,indices,faceFlags,rest,projected);
        }
        var type = game.GetType("Recompiled.Tenchu", true)!;
        getLs = type.GetMethod("func_80064978")!.CreateDelegate<Guest>();
        hook = new Hook(type.GetMethod("func_80016CBC")!, (Detour)DrawArchive);
        if(refined)
        {
            menuGetLs=type.GetMethod("func_8002D728")!.CreateDelegate<Guest>();
            menuHook=new Hook(type.GetMethod("func_80014724")!, (Detour)((original,c,m)=>DrawArchive(original,c,m,true)));
        }
        Console.WriteLine($"[hd-rikimaru] loaded {vertices.Length} vertices / {indices.Length/3} triangles; native rigid parts, HD texture routing");
    }

    sealed record Mesh(Vertex[] Vertices,int[] Indices,PrimFlags[] Flags,Vector3[] Rest,HleVertex[] Projected);
    static Mesh? nearMesh,farMesh;
    static void Load(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        string signature = new(reader.ReadChars(4));
        if (refined ? signature is not ("RHD2" or "RHD3") : signature != "RHD1") throw new InvalidDataException("HD mesh signature");
        int count = reader.ReadInt32(), triangles = reader.ReadInt32();
        if (count < 1 || count > 200000 || triangles < 1 || triangles > 200000) throw new InvalidDataException("HD mesh counts");
        vertices = new Vertex[count]; projected = new HleVertex[count]; indices = new int[triangles * 3];
        rest = new Vector3[count];
        Influence ReadInfluence() => new(reader.ReadInt32(), reader.ReadSingle(), new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
        for (int i = 0; i < count; i++)
        {
            var a = ReadInfluence(); var b = ReadInfluence();
            if (a.Bone is < 0 or >= 15 || b.Bone is < 0 or >= 15 || Math.Abs(a.Weight + b.Weight - 1) > .001f) throw new InvalidDataException("HD skin weights");
            vertices[i] = new(a, b, reader.ReadByte(), reader.ReadByte(), reader.ReadByte()); reader.ReadByte();
            if(refined)vertices[i]=vertices[i] with { U=reader.ReadInt16(),V=reader.ReadInt16() };
            rest[i] = a.Local + bindOrigins[a.Bone];
            if(signature != "RHD3" && b.Weight>0 && Vector3.Distance(rest[i],b.Local+bindOrigins[b.Bone])>.01f)
                throw new InvalidDataException("HD influences disagree on bind surface");
        }
        for (int i = 0; i < indices.Length; i++) { indices[i] = reader.ReadInt32(); if (indices[i] < 0 || indices[i] >= count) throw new InvalidDataException("HD mesh index"); }
        faceFlags=new PrimFlags[triangles];
        for(int i=0;i<triangles;i++)
        {
            faceFlags[i]=new PrimFlags {Gouraud=true};
            if(refined)faceFlags[i]=new PrimFlags {Gouraud=true,Textured=reader.ReadInt32()!=0,TPage=reader.ReadUInt16(),Clut=reader.ReadUInt16()};
        }
    }
    static void DrawArchive(Guest original, CpuContext c, IMemory m)
        => DrawArchive(original,c,m,false);
    static bool IsNativeRikimaru(uint archive,IMemory m)
    {
        if(!Pointer(archive) || m.ReadU16(archive+0x64)!=15)return false;
        uint objects=m.ReadU32(archive+0x68);
        if(!Pointer(objects))return false;
        for(int i=0;i<15;i++)
        {
            uint model=m.ReadU32(objects+(uint)i*4);
            if(!Pointer(model))return false;
            uint tmd=m.ReadU32(model+0x6C);
            if(!Pointer(tmd) || m.ReadU32(tmd+4)!=nativeSignature[i].Count)return false;
            uint v=m.ReadU32(tmd);
            if(!Pointer(v) || m.ReadU32(v)!=nativeSignature[i].A || m.ReadU32(v+4)!=nativeSignature[i].B)return false;
        }
        return true;
    }
    static void DrawArchive(Guest original, CpuContext c, IMemory m,bool menu)
    {
        uint player = m.ReadU32(0x8009590C);
        bool target=menu ? IsNativeRikimaru(c.A0,m) : BenchmarkHalfStep.Enabled && Pointer(player) &&
            m.ReadU16(player)==0 && c.A0==m.ReadU32(player+0x58);
        if (!target || !Pointer(c.A0) || !GpuHle.Active || GpuHle.Backend is null ||
            m.ReadU16(c.A0 + 0x64) != 15 || m.ReadU16(c.GP + (menu?0x14u:0x1Cu)) == 1)
        { original(c, m); return; }
        if (GpuHle.Backend is not HdBackend) GpuHle.Backend = new HdBackend(GpuHle.Backend);
        uint objects = m.ReadU32(c.A0 + 0x68), table = m.ReadU32(c.GP + (menu?0x12Cu:0x7C8u));
        if (!Pointer(objects) || !Pointer(table)) { original(c, m); return; }
        uint packetAddress=menu?0x800960F8u:0x800C4180u;
        uint ot = m.ReadU32(table + 4), packet = m.ReadU32(packetAddress);
        int limit = menu ? 1 << (int)Math.Min(m.ReadU32(table),13u) : (int)m.ReadU32(0x8005B384), gap = (int)c.A1;
        if (!Pointer(ot) || !Pointer(packet) || limit is < 2 or > 8192) { original(c, m); return; }
        var helper = new CpuContext(); helper.Restore(c.Snapshot()); helper.SP -= 0x600;
        uint output = helper.SP + 0x500;
        for (int i = 0; i < 15; i++)
        {
            uint model = m.ReadU32(objects + (uint)i * 4);
            if (!Pointer(model)) { original(c, m); return; }
            helper.A0 = model; helper.A1 = output; (menu?menuGetLs:getLs)(helper, m);
            float R(int n) => (short)m.ReadU16(output + (uint)n * 2) / 4096f;
            matrices[i] = new Matrix4x4(R(0),R(3),R(6),0, R(1),R(4),R(7),0, R(2),R(5),R(8),0,
                (int)m.ReadU32(output+20),(int)m.ReadU32(output+24),(int)m.ReadU32(output+28),1);
        }
        for(int i=0;i<15;i++)
        {
            real[i]=Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(matrices[i]));
            var t=matrices[i].Translation-Vector3.Transform(bindOrigins[i],real[i]);
            dual[i]=Quaternion.Multiply(new Quaternion(t,0)*real[i],.5f);
        }
        float ox = (int)Gte.ReadControl(24) / 65536f, oy = (int)Gte.ReadControl(25) / 65536f, h = Gte.ReadControl(26) & 65535;
        if (h < 1) { original(c, m); return; }
        if(refined && nearMesh!=null && farMesh!=null)
        {
            var mesh=1800*h/Math.Max(matrices[0].Translation.Z,1)>160 ? nearMesh : farMesh;
            vertices=mesh.Vertices;indices=mesh.Indices;faceFlags=mesh.Flags;rest=mesh.Rest;projected=mesh.Projected;
        }
        if (capture && captureFrame++ % 30 == 0 && captureFrame < 3600)
        {
            string directory = Path.Combine(AppContext.BaseDirectory,"logs","hd-poses");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,$"pose-{captureFrame:0000}.json"), System.Text.Json.JsonSerializer.Serialize(new {
                h, ox, oy, matrices = matrices.Select(a => new[] { a.M11,a.M12,a.M13,a.M21,a.M22,a.M23,a.M31,a.M32,a.M33,a.M41,a.M42,a.M43 }).ToArray()
            }));
        }
        for (int i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            if(refined)
            {
                Vector3 rigid=Vector3.Transform(v.A.Local,matrices[v.A.Bone]);
                if(v.B.Weight>0)
                    rigid=rigid*v.A.Weight+Vector3.Transform(v.B.Local,matrices[v.B.Bone])*v.B.Weight;
                projected[i]=new HleVertex {X=ox+rigid.X*h/Math.Max(rigid.Z,1),Y=oy+rigid.Y*h/Math.Max(rigid.Z,1),Z=rigid.Z,
                    R=v.R,G=v.G,B=v.Bc,U=v.U,V=v.V,HasGteZ=true};
                continue;
            }
            // Blend rigid motions rather than averaging rotated positions, which
            // collapses elbow/shoulder volume and twists the wrist into a spike.
            var qa=real[v.A.Bone];var qb=real[v.B.Bone];
            float wb=Quaternion.Dot(qa,qb)<0 ? -v.B.Weight : v.B.Weight;
            var qr=Quaternion.Multiply(qa,v.A.Weight)+Quaternion.Multiply(qb,wb);
            var qd=Quaternion.Multiply(dual[v.A.Bone],v.A.Weight)+Quaternion.Multiply(dual[v.B.Bone],wb);
            float inv=1/qr.Length();qr=Quaternion.Multiply(qr,inv);qd=Quaternion.Multiply(qd,inv);
            var translation=Quaternion.Multiply(qd*Quaternion.Conjugate(qr),2);
            Vector3 pos=Vector3.Transform(rest[i],qr)+new Vector3(translation.X,translation.Y,translation.Z);
            projected[i] = new HleVertex { X = ox + pos.X * h / Math.Max(pos.Z,1), Y = oy + pos.Y * h / Math.Max(pos.Z,1), Z = pos.Z,
                R = v.R, G = v.G, B = v.Bc, U=v.U,V=v.V,HasGteZ = true };
        }
        var buckets = new SortedDictionary<int, List<Triangle>>();
        for (int i = 0; i < indices.Length; i += 3)
        {
            var a = projected[indices[i]]; var b = projected[indices[i+1]]; var d = projected[indices[i+2]];
            if (a.Z < 32 || b.Z < 32 || d.Z < 32) continue;
            // glTF CCW faces become negative screen winding with PS1's Y-down
            // projection. Keep the source mesh intact; cull only back faces.
            if (!refined && (b.X-a.X)*(d.Y-a.Y)-(b.Y-a.Y)*(d.X-a.X) >= 0) continue;
            if (Math.Max(a.X,Math.Max(b.X,d.X)) < -160 || Math.Min(a.X,Math.Min(b.X,d.X)) > 800 || Math.Max(a.Y,Math.Max(b.Y,d.Y)) < -100 || Math.Min(a.Y,Math.Min(b.Y,d.Y)) > 600) continue;
            float z = (a.Z+b.Z+d.Z)/3;
            int bucket = (int)((a.Z+b.Z+d.Z)*5/256) + gap;
            if (bucket <= 0 || bucket >= limit || !Pointer(ot+(uint)bucket*4)) continue;
            if (!buckets.TryGetValue(bucket,out var list)) buckets[bucket] = list = new();
            list.Add(new(a,b,d,z,faceFlags[i/3]));
        }
        // The native character uses more packet space than these sparse markers.
        if (buckets.Count > 700 || packet + buckets.Count * 20 >= 0x80200000) { original(c,m); return; }
        if(refined)
        {
            // Preserve the native draw's GTE/timing work. Its packets become
            // NOPs; only the refined triangles are presented at the same OT.
            uint begin=packet;
            original(c,m);
            uint end=m.ReadU32(packetAddress),cursor=begin;
            if(end<begin || end>=0x80200000)return;
            while(cursor<end)
            {
                uint words=m.ReadU32(cursor)>>24;
                if(words>32 || cursor+(words+1)*4>end)return;
                cursor+=(words+1)*4;
            }
            if(cursor!=end || end+buckets.Count*20>=0x80200000)return;
            cursor=begin;
            while(cursor<end)
            {
                uint words=m.ReadU32(cursor)>>24;
                for(uint j=1;j<=words;j++)m.WriteU32(cursor+j*4,0);
                cursor+=(words+1)*4;
            }
            packet=end;
        }
        foreach (var (bucket,list) in buckets)
        {
            list.Sort((a,b)=>b.Depth.CompareTo(a.Depth));
            int id = serial++ & 65535;
            queued[id] = list;
            uint slot = ot + (uint)bucket*4;
            m.WriteU32(packet, (m.ReadU32(slot)&0xFFFFFF) | 0x04000000);
            m.WriteU32(packet+4, 0x200000ED | ((uint)id << 8));
            m.WriteU32(packet+8,0); m.WriteU32(packet+12,1); m.WriteU32(packet+16,0x00010000);
            m.WriteU32(slot,packet&0xFFFFFF); packet += 20;
        }
        m.WriteU32(packetAddress,packet); c.V0 = 1;
        if(menu && !menuReported){menuReported=true;Console.WriteLine("[hd-rikimaru] selection model replaced; all 15 native geometry signatures verified");}
        if (!reported) { reported = true; Console.WriteLine($"[hd-rikimaru] live draw {buckets.Sum(x=>x.Value.Count)} triangles; {buckets.Count} OT markers; h={h} offset={ox},{oy}"); }
    }

    sealed class HdBackend(IGpuBackend inner) : IGpuBackend
    {
        public bool Ready => inner.Ready;
        public void SetDrawEnv(in HleDrawEnv env) => inner.SetDrawEnv(env);
        public void DrawTri(in HleVertex a,in HleVertex b,in HleVertex c,in PrimFlags f)
        {
            if (!f.Textured && !f.Gouraud && a.R==237 && b.X-a.X==1 && b.Y==a.Y && c.X==a.X && c.Y-a.Y==1 && queued.Remove(a.G | (a.B<<8),out var list))
            {
                foreach(var t in list)
                {
                    var x=t.A;var y=t.B;var z=t.C;
                    x.X+=a.X; y.X+=a.X; z.X+=a.X; x.Y+=a.Y; y.Y+=a.Y; z.Y+=a.Y;
                    inner.DrawTri(x,y,z,t.Flags);
                }
                return;
            }
            inner.DrawTri(a,b,c,f);
        }
        public void DrawRect(in HleRect r,in PrimFlags f)=>inner.DrawRect(r,f);
        public void DrawLine(in HleVertex a,in HleVertex b,in PrimFlags f)=>inner.DrawLine(a,b,f);
        public void FillRect(int x,int y,int w,int h,ushort c)=>inner.FillRect(x,y,w,h,c);
        public void CopyVram(int sx,int sy,int dx,int dy,int w,int h)=>inner.CopyVram(sx,sy,dx,dy,w,h);
        public void WriteVram(int x,int y,int w,int h,ReadOnlySpan<ushort> p)=>inner.WriteVram(x,y,w,h,p);
        public void ReadVram(int x,int y,int w,int h,Span<ushort> p)=>inner.ReadVram(x,y,w,h,p);
        public void Flush()=>inner.Flush();
        public void Present(in HleDispEnv disp) { inner.Present(disp); if(queued.Count>4000)queued.Clear(); }
    }
}

