using System.Globalization;
using System.IO.Compression;
using System.IO.Hashing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime.Hle;
using Silk.NET.OpenGL;

namespace TenchuSARecomp;

// Independent adapter for DuckStation's documented texpage/texupload identities.
// Native VRAM remains authoritative for collision-free texture identity, PS1
// transparency/mask semantics, framebuffer feedback and every unmatched draw.
internal static class TexturePack
{
    delegate uint BuildShader(GL gl, string vertex, string fragment, string name);
    delegate uint ShaderHook(BuildShader original, GL gl, string vertex, string fragment, string name);
    static Hook? shaderHook;
    static GL? gl;
    static uint program;
    static bool enabled => Environment.GetEnvironmentVariable("TENCHU_TEXTURE_PACK") == "1";
    static readonly string packPath = Path.GetFullPath(Environment.GetEnvironmentVariable("TENCHU_TEXTURE_PACK_PATH") ??
        Path.Combine(AppContext.BaseDirectory, "..", "texture-packs", "tenchu-native"));

    public static void InstallShader()
    {
        if (!enabled) return;
        var type = typeof(GpuHle).Assembly.GetType("RecompOne.Runtime.Hle.GlShaders", true)!;
        shaderHook = new Hook(type.GetMethod("Build", BindingFlags.Public | BindingFlags.Static)!, (ShaderHook)Compile);
    }

    static uint Compile(BuildShader original, GL api, string vertex, string fragment, string name)
    {
        if (name != "prim") return original(api, vertex, fragment, name);
        const string point = "    ivec3 t8 = ivec3(texel.rgb * 31.0 + 0.5) << 3;";
        if (!fragment.Contains(point)) throw new InvalidOperationException("Unsupported primitive shader; texture pack disabled");
        fragment = fragment.Replace("uniform sampler2D uVram;", """
uniform sampler2D uVram;
uniform sampler2D uReplacement;
uniform int uReplacementEnabled;
uniform vec4 uReplacementRect;
""");
        fragment = fragment.Replace(point, """
    if (uReplacementEnabled != 0 && all(greaterThanEqual(vec2(uv), uReplacementRect.xy)) &&
        all(lessThan(vec2(uv), uReplacementRect.xy + uReplacementRect.zw))) {
        vec2 subpixel = fract(vUV);
        vec2 lookup = (vec2(uv) + subpixel - uReplacementRect.xy) / uReplacementRect.zw;
        vec4 replacement = texture(uReplacement, clamp(lookup, vec2(0.0), vec2(1.0)));
        if (replacement.a < 0.1) discard;
        // The original CLUT still decides PS1 semitransparency and mask bits.
        vec3 shaded = replacement.rgb * vColor.rgb * (255.0 / 128.0);
        FragColor = vec4(clamp(shaded, 0.0, 1.0), max(texel.a, uSetMask));
        BlendColor = texel.a >= 0.5 ? uBlend : uBlendOpaque;
        return;
    }
""" + "\n" + point);
        fragment=fragment.Replace("    if (texMode == 4) {", """
    if (uReplacementEnabled == 2) {
        vec4 replacement=texture(uReplacement,(vUV+vec2(0.5))/vec2(textureSize(uReplacement,0)));
        if(replacement.a<0.1) discard;
        FragColor=vec4(clamp(replacement.rgb*vColor.rgb*(255.0/128.0),0.0,1.0),uSetMask);
        BlendColor=uBlendOpaque;
        return;
    }
    if (texMode == 4) {
""");
        program = original(api, vertex, fragment, name);
        if (program == 0) throw new InvalidOperationException("Texture pack shader failed to compile");
        gl = api;
        Console.WriteLine("[texpack] native primitive shader extended");
        return program;
    }

    public static void Activate()
    {
        if (!enabled) return;
        if (GpuHle.Backend is not GlBackend native || gl == null || program == 0)
            throw new InvalidOperationException("Texture pack requires the OpenGL backend");
        var records = JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(Path.Combine(packPath, "index.json")))!;
        // These two supplied copyright crops contain truncated text (and a
        // pack-build notice) rather than the native copyright atlas. Keep the
        // original credits while allowing every other HD menu replacement.
        records=records.Where(e=>e.File is not "0d51914a993fb1f6fea4edd5.rgba.z" and not "bd8db5dd6266dfe97ff26167.rgba.z").ToArray();
        Console.WriteLine("[texpack] native title copyright retained; two broken replacements excluded");
        GpuHle.Backend = new Backend(native, gl, records);
        Console.WriteLine($"[texpack] indexed {records.Length} textures from {packPath}");
    }

    internal sealed class Entry
    {
        public string Type { get; set; } = "";
        public int Mode { get; set; }
        public bool Semitransparent { get; set; }
        public string Hash { get; set; } = "";
        public string Palette { get; set; } = "";
        public int SourceWidth { get; set; }
        public int SourceHeight { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int PaletteMin { get; set; }
        public int PaletteMax { get; set; }
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
        public string File { get; set; } = "";
        public string Original { get; set; } = "";
        ulong? sourceHash, paletteHash;
        public ulong SourceHash => sourceHash ??= ulong.Parse(Hash, NumberStyles.HexNumber);
        public ulong PaletteHash => paletteHash ??= ulong.Parse(Palette, NumberStyles.HexNumber);
    }

    internal sealed class Tracker
    {
        public readonly ushort[] Vram = new ushort[1024 * 512];
        readonly Dictionary<(int Mode, ulong Hash), Entry[]> index;
        readonly Entry[] pageEntries;
        readonly Dictionary<(int Mode,int Length,ulong Hash),Entry[]> palettePages;
        readonly Dictionary<int,int[]> paletteLengths;
        readonly Dictionary<(int Page, int Clut), Region[]> cache = new();
        readonly List<Upload> uploads = new();
        internal readonly record struct Upload(int X, int Y, int W, int H, ulong Hash);
        public Tracker(Entry[] entries)
        {
            foreach (var e in entries)
                if (e.Width <= 0 || e.Height <= 0 || e.SourceWidth <= 0 || e.SourceHeight <= 0 ||
                    e.ImageWidth <= 0 || e.ImageHeight <= 0 || e.ImageWidth > 8192 || e.ImageHeight > 8192 ||
                    Path.GetFileName(e.File) != e.File) throw new InvalidDataException("Texture pack index");
            index = entries.GroupBy(e => (e.Mode, e.SourceHash)).ToDictionary(g => g.Key, g => g.ToArray());
            pageEntries = entries.Where(e => e.Type == "page").ToArray();
            palettePages=pageEntries.GroupBy(e=>(e.Mode,e.Mode==2?0:e.PaletteMax-e.PaletteMin+1,e.PaletteHash))
                .ToDictionary(g=>g.Key,g=>g.ToArray());
            paletteLengths=pageEntries.GroupBy(e=>e.Mode).ToDictionary(g=>g.Key,
                g=>g.Select(e=>e.Mode==2?0:e.PaletteMax-e.PaletteMin+1).Distinct().ToArray());
        }
        public void Invalidate(int x, int y, int w, int h)
        {
            uploads.RemoveAll(u => x < u.X + u.W && x + w > u.X && y < u.Y + u.H && y + h > u.Y);
            foreach (var key in cache.Keys.ToArray())
            {
                int px = (key.Page & 15) * 64, py = ((key.Page >> 4) & 1) * 256;
                int pw = 64 << ((key.Page >> 7) & 3);
                int cx = (key.Clut & 63) * 16, cy = (key.Clut >> 6) & 511;
                int cw = ((key.Page >> 7) & 3) == 0 ? 16 : 256;
                if ((x < px+pw && x+w > px && y < py+256 && y+h > py) ||
                    (x < cx+cw && x+w > cx && y <= cy && y+h > cy)) cache.Remove(key);
            }
        }
        public void Write(int x, int y, int w, int h, ReadOnlySpan<ushort> pixels)
        {
            Invalidate(x, y, w, h);
            for (int row = 0; row < h; row++)
                for (int col = 0; col < w; col++) Vram[((y + row) & 511) * 1024 + ((x + col) & 1023)] = pixels[row * w + col];
            ulong hash = XxHash3.HashToUInt64(MemoryMarshal.AsBytes(pixels[..(w * h)]));
            uploads.Add(new(x, y, w, h, hash));
            if (uploads.Count > 4096) uploads.RemoveRange(0, uploads.Count - 4096);
        }
        ulong HashRect(int x, int y, int w, int h)
        {
            var hash = new XxHash3();
            for (int row = 0; row < h; row++)
            {
                int yy = (y + row) & 511;
                // Texture pages may wrap at the edge of VRAM.
                int first = Math.Min(w, 1024 - (x & 1023));
                hash.Append(MemoryMarshal.AsBytes(Vram.AsSpan(yy * 1024 + (x & 1023), first)));
                if (first < w) hash.Append(MemoryMarshal.AsBytes(Vram.AsSpan(yy * 1024, w - first)));
            }
            return hash.GetCurrentHashAsUInt64();
        }
        bool PaletteMatches(Entry entry, int clut)
        {
            if (entry.Mode == 2) return true;
            int n = entry.PaletteMax - entry.PaletteMin + 1;
            if (n is < 1 or > 256) return false;
            // Filename Pmin-Pmax controls the hashed length. DuckStation hashes
            // the prefix of that length, even when Pmin is nonzero.
            int start = ((clut >> 6) & 511) * 1024 + ((clut & 63) * 16);
            return start + n <= Vram.Length && XxHash3.HashToUInt64(MemoryMarshal.AsBytes(Vram.AsSpan(start, n))) == entry.PaletteHash;
        }
        public Region[] Lookup(int tpage, int clut)
        {
            int page = tpage & 0x19f, mode = (tpage >> 7) & 3;
            if (mode > 2) return Array.Empty<Region>();
            if (cache.TryGetValue((page, clut), out var found)) return found;
            int x = (tpage & 15) * 64, y = ((tpage >> 4) & 1) * 256, shift = 2 - mode;
            var regions = new List<Region>();
            var pageHash = HashRect(x, y, 64 << mode, 256);
            if (index.TryGetValue((mode, pageHash), out var candidates))
                foreach (var e in candidates)
                    if (e.Type == "page" && PaletteMatches(e, clut)) regions.Add(new(e, e.X, e.Y));
            var subHashes = new Dictionary<(int X,int Y,int W,int H),ulong>();
            var compatiblePages = new List<Entry>();
            if(paletteLengths.TryGetValue(mode,out var lengths))
                foreach(int length in lengths)
                {
                    int start=((clut>>6)&511)*1024+(clut&63)*16;
                    if(length<0 || length>256 || start+length>Vram.Length) continue;
                    ulong palette=mode==2 ? 0 : XxHash3.HashToUInt64(MemoryMarshal.AsBytes(Vram.AsSpan(start,length)));
                    if(palettePages.TryGetValue((mode,length,palette),out var group)) compatiblePages.AddRange(group);
                }
            foreach (var e in compatiblePages)
            {
                int wordWidth = (e.Width + (1 << shift) - 1) >> shift;
                var rect = (X:x+(e.X>>shift), Y:y+e.Y, W:wordWidth, H:e.Height);
                if (rect.W <= 0 || rect.W > 256 || rect.H <= 0 || rect.H > 256) continue;
                if (!subHashes.TryGetValue(rect,out ulong hash))
                    subHashes[rect] = hash = HashRect(rect.X,rect.Y,rect.W,rect.H);
                if (hash == e.SourceHash && !regions.Any(r => ReferenceEquals(r.Entry,e))) regions.Add(new(e,e.X,e.Y));
            }
            foreach (var u in uploads)
            {
                if (u.X >= x + (64 << mode) || u.X + u.W <= x || u.Y >= y + 256 || u.Y + u.H <= y) continue;
                if (!index.TryGetValue((mode, u.Hash), out candidates)) continue;
                foreach (var e in candidates)
                    if (e.Type == "upload" && e.SourceWidth == u.W && e.SourceHeight == u.H && PaletteMatches(e, clut))
                        regions.Add(new(e, ((u.X - x) << shift) + e.X, u.Y - y + e.Y));
            }
            found = regions.OrderByDescending(r=>r.Entry.Semitransparent).ThenBy(r => r.Entry.Width * r.Entry.Height).ToArray();
            cache[(page, clut)] = found;
            return found;
        }
    }
    internal sealed record Region(Entry Entry, int X, int Y);

    internal static (int Min, int Max) WindowBounds(int min, int max, int mask, int offset)
    {
        if(mask==0 && min>=0 && max<256) return (min,max);
        int and = ~(mask * 8) & 255, or = (offset & mask) * 8;
        int lo = 255, hi = 0;
        // Bound the complete interval, rather than only its endpoints: masks
        // can fold a long UV interval into a small repeating texture tile.
        if (max - min >= 256) { min = 0; max = 255; }
        for (int i = min; i <= max; i++) { int v = (i & and) | or; lo = Math.Min(lo,v); hi = Math.Max(hi,v); }
        return (lo,hi);
    }

    sealed class Backend : IGpuBackend
    {
        readonly IGpuBackend native;
        readonly GL api;
        readonly Tracker tracker;
        HleDrawEnv env;
        Region? selected;
        bool refinedSelected;
        uint refinedAtlas;
        readonly int uEnabled, uRect;
        readonly Dictionary<Entry, (uint Texture, long Bytes, long Stamp)> textures = new();
        readonly Dictionary<Entry,Task<byte[]>> decoding = new();
        readonly HashSet<Entry> failed = new();
        readonly SemaphoreSlim decodeWorkers = new(2);
        long decodingBytes;
        long textureBytes, stamp, hits, draws, nextReport;
        readonly HashSet<string> matched = new();
        public bool Ready => native.Ready;
        public Backend(IGpuBackend native, GL api, Entry[] entries)
        {
            this.native = native; this.api = api; tracker = new(entries);
            uEnabled = api.GetUniformLocation(program, "uReplacementEnabled");
            uRect = api.GetUniformLocation(program, "uReplacementRect");
            api.UseProgram(program); api.Uniform1(api.GetUniformLocation(program, "uReplacement"), 2);
        }
        uint Texture(Entry e)
        {
            if (textures.TryGetValue(e, out var t)) { textures[e] = (t.Texture, t.Bytes, ++stamp); return t.Texture; }
            int length = checked(e.ImageWidth * e.ImageHeight * 4);
            if(failed.Contains(e)) return 0;
            if(!decoding.TryGetValue(e,out var task))
            {
                if(decodingBytes!=0 && decodingBytes+length>64L*1024*1024) return 0;
                decodingBytes+=length;
                decoding[e]=Task.Run(async ()=>
                {
                    await decodeWorkers.WaitAsync();
                    try
                    {
                        var pixels=new byte[length];
                        using var input=File.OpenRead(Path.Combine(packPath,e.File));
                        using var z=new ZLibStream(input,CompressionMode.Decompress);
                        z.ReadExactly(pixels); if(z.ReadByte()!=-1) throw new InvalidDataException("Replacement image size");
                        return pixels;
                    }
                    finally { decodeWorkers.Release(); }
                });
                return 0; // Native texture remains visible while I/O/decode runs off-thread.
            }
            if(!task.IsCompleted) return 0;
            decoding.Remove(e); decodingBytes-=length;
            if(!task.IsCompletedSuccessfully)
            {
                failed.Add(e); Console.WriteLine($"[texpack] decode failed: {e.Original}: {task.Exception?.GetBaseException().Message}"); return 0;
            }
            var pixels=task.Result;
            const long budget = 256L * 1024 * 1024;
            native.Flush();
            while (textureBytes + length > budget && textures.Count > 0)
            {
                var oldest = textures.MinBy(p => p.Value.Stamp);
                api.DeleteTexture(oldest.Value.Texture); textureBytes -= oldest.Value.Bytes; textures.Remove(oldest.Key);
            }
            uint id = api.GenTexture(); api.ActiveTexture(TextureUnit.Texture2); api.BindTexture(TextureTarget.Texture2D, id);
            api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            api.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)e.ImageWidth, (uint)e.ImageHeight, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            api.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, 9728);
            api.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, 9728);
            api.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, 33071);
            api.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, 33071);
            api.ActiveTexture(TextureUnit.Texture0); api.PixelStore(PixelStoreParameter.UnpackAlignment, 2);
            textures.Add(e, (id, length, ++stamp)); textureBytes += length;
            if (matched.Add(e.Original)) Console.WriteLine($"[texpack] matched {e.Original} ({e.ImageWidth}x{e.ImageHeight})");
            return id;
        }
        void Select(Region? region)
        {
            uint texture=region==null ? 0 : Texture(region.Entry);
            if(texture==0) region=null;
            if (!refinedSelected && Equals(selected, region)) return;
            native.Flush();
            refinedSelected=false;
            selected = region; api.UseProgram(program);
            if (region == null) { api.Uniform1(uEnabled, 0); return; }
            api.UseProgram(program); api.Uniform1(uEnabled, 1);
            api.Uniform4(uRect, (float)region.X, (float)region.Y, (float)region.Entry.Width, (float)region.Entry.Height);
            api.ActiveTexture(TextureUnit.Texture2); api.BindTexture(TextureTarget.Texture2D, texture); api.ActiveTexture(TextureUnit.Texture0);
        }
        void Choose(in PrimFlags f, int minU, int minV, int maxU, int maxV)
        {
            draws++;
            if (draws % 10000 == 0) Report();
            if (!f.Textured) { Select(null); return; }
            (minU,maxU) = WindowBounds(minU,maxU,env.TwMaskX,env.TwOffX);
            (minV,maxV) = WindowBounds(minV,maxV,env.TwMaskY,env.TwOffY);
            Region? region=null;
            foreach(var candidate in tracker.Lookup(f.TPage,f.Clut))
                if((!candidate.Entry.Semitransparent || f.SemiTrans) && minU>=candidate.X && minV>=candidate.Y &&
                    maxU<=candidate.X+candidate.Entry.Width && maxV<=candidate.Y+candidate.Entry.Height)
                { region=candidate; break; }
            Select(region); if (selected != null) hits++;
        }
        public void SetDrawEnv(in HleDrawEnv e) { env = e; native.SetDrawEnv(in e); }
        public void DrawTri(in HleVertex a, in HleVertex b, in HleVertex c, in PrimFlags f)
        {
            if(f.Textured && f.TPage==0x10F && f.Clut==0x7FFF)
            {
                if(!refinedSelected)
                {
                    native.Flush();
                    if(refinedAtlas==0)
                    {
                        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"assets","rikimaru-refined-atlas.rgba.z"));
                        using var reader=new BinaryReader(input,System.Text.Encoding.UTF8,true);
                        int width=reader.ReadInt32(),height=reader.ReadInt32();
                        if(width<1 || height<1 || width>8192 || height>8192)throw new InvalidDataException("Refined atlas dimensions");
                        var pixels=new byte[checked(width*height*4)];
                        using var compressed=new ZLibStream(input,CompressionMode.Decompress);compressed.ReadExactly(pixels);
                        refinedAtlas=api.GenTexture();api.ActiveTexture(TextureUnit.Texture2);api.BindTexture(TextureTarget.Texture2D,refinedAtlas);
                        api.PixelStore(PixelStoreParameter.UnpackAlignment,1);
                        api.TexImage2D<byte>(TextureTarget.Texture2D,0,InternalFormat.Rgba8,(uint)width,(uint)height,0,PixelFormat.Rgba,PixelType.UnsignedByte,pixels.AsSpan());
                        api.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,9728);
                        api.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,9728);
                        api.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,33071);
                        api.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,33071);
                        api.PixelStore(PixelStoreParameter.UnpackAlignment,2);
                        Console.WriteLine($"[refined-atlas] HD character atlas ready {width}x{height}");
                    }
                    api.UseProgram(program);api.Uniform1(uEnabled,2);
                    api.ActiveTexture(TextureUnit.Texture2);api.BindTexture(TextureTarget.Texture2D,refinedAtlas);api.ActiveTexture(TextureUnit.Texture0);
                    selected=null;refinedSelected=true;
                }
                native.DrawTri(in a,in b,in c,in f);return;
            }
            Choose(in f, Math.Min(a.U, Math.Min(b.U,c.U)), Math.Min(a.V, Math.Min(b.V,c.V)), Math.Max(a.U,Math.Max(b.U,c.U)), Math.Max(a.V,Math.Max(b.V,c.V)));
            native.DrawTri(in a,in b,in c,in f);
        }
        public void DrawRect(in HleRect r, in PrimFlags f) { Choose(in f,r.U,r.V,r.U+r.W,r.V+r.H); native.DrawRect(in r,in f); }
        public void DrawLine(in HleVertex a,in HleVertex b,in PrimFlags f) { Select(null); native.DrawLine(in a,in b,in f); }
        public void FillRect(int x,int y,int w,int h,ushort color)
        {
            Select(null); native.FillRect(x,y,w,h,color); tracker.Invalidate(x,y,w,h);
            for(int row=0;row<h;row++) for(int col=0;col<w;col++) tracker.Vram[((y+row)&511)*1024+((x+col)&1023)] = color;
        }
        public void WriteVram(int x,int y,int w,int h,ReadOnlySpan<ushort> p) { Select(null); native.WriteVram(x,y,w,h,p); tracker.Write(x,y,w,h,p); }
        public void CopyVram(int sx,int sy,int dx,int dy,int w,int h)
        {
            Select(null);
            var p = new ushort[w*h];
            // Framebuffer copies may include rendered pixels that do not exist
            // in the CPU upload mirror. Read the real source before the copy.
            native.ReadVram(sx,sy,w,h,p);
            native.CopyVram(sx,sy,dx,dy,w,h);
            tracker.Write(dx,dy,w,h,p);
        }
        public void ReadVram(int x,int y,int w,int h,Span<ushort> p) { Select(null); native.ReadVram(x,y,w,h,p); }
        void Report()
        {
            long now = Environment.TickCount64;
            if (now < nextReport) return;
            nextReport = now + 3000;
            Console.WriteLine($"[texpack] replaced-draws={hits}/{draws} unique={matched.Count} cache={textureBytes/1048576}MiB");
        }
        public void Flush() { native.Flush(); Report(); }
        public void Present(in HleDispEnv d)
        {
            Select(null); native.Present(in d);
            Report();
        }
    }
}

