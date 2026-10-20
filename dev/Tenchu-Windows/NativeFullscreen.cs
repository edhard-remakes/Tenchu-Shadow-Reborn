using MonoMod.RuntimeDetour;
using Silk.NET.Windowing;
using System.Numerics;
using System.Reflection;
using MonoMod.Cil;
using Mono.Cecil;
using ImGuiNET;

namespace TenchuSARecomp;

internal static class NativeFullscreen
{
    delegate IWindow Create(WindowOptions options);
    delegate IWindow Detour(Create original,WindowOptions options);
    static Hook? hook;
    static Hook? stretchHook;
    static ILHook? outputTabHook;
    delegate Vector2 Fit(Vector2 source,Vector2 available);
    delegate Vector2 FitDetour(Fit original,Vector2 source,Vector2 available);
    internal static bool StretchEnabled => Environment.GetEnvironmentVariable("TENCHU_STRETCH") != "0";
    public static void Install()
    {
        hook=new Hook(typeof(Window).GetMethod(nameof(Window.Create),new[]{typeof(WindowOptions)})!, (Detour)Open);
        var host=typeof(RecompOne.Runtime.Runtime).Assembly.GetType("RecompOne.Runtime.Host.HostWindow",true)!;
        outputTabHook=new ILHook(host.GetMethod("DrawDockspace",BindingFlags.NonPublic|BindingFlags.Static)!,il=>
        {
            var cursor=new ILCursor(il);
            if(!cursor.TryGotoNext(MoveType.Before,i=>i.Operand is MethodReference method && method.Name=="DockSpace" && method.Parameters.Count==3))
                throw new InvalidOperationException("Output dockspace call unavailable");
            cursor.EmitDelegate<Func<ImGuiDockNodeFlags,ImGuiDockNodeFlags>>(flags=>flags|(ImGuiDockNodeFlags)4096);
        });
        if(StretchEnabled)
        {
            var output=typeof(RecompOne.Runtime.Runtime).Assembly.GetType("RecompOne.Runtime.Host.Window.OutputPanel",true)!;
            stretchHook=new Hook(output.GetMethod("FitAspect",BindingFlags.NonPublic|BindingFlags.Static)!,
                (FitDetour)((original,source,available)=>available));
            Console.WriteLine("[display] stretch fills the entire window content");
        }
    }
    static IWindow Open(Create original,WindowOptions options)
    {
        options.Title="Tenchu Shadow Reborn";
        if(Environment.GetEnvironmentVariable("TENCHU_FULLSCREEN")=="1" && Environment.GetEnvironmentVariable("TENCHU_TEST_WINDOWED")!="1")
        {
            try
            {
                var monitor=Silk.NET.Windowing.Monitor.GetMainMonitor(null);
                options.VideoMode=monitor.VideoMode;
                options.Size=monitor.VideoMode.Resolution??monitor.Bounds.Size;
                options.Position=monitor.Bounds.Origin;
                options.WindowState=WindowState.Fullscreen;
                RecompOne.Runtime.Config.ConfigManager.View.Fullscreen=true;
                Console.WriteLine($"[display] fullscreen desktop mode {options.Size.X}x{options.Size.Y} monitor={monitor.Name}");
            }
            catch(Exception e){Console.WriteLine($"[display] desktop mode unavailable: {e.Message}");}
        }
        if(Environment.GetEnvironmentVariable("TENCHU_FULLSCREEN")!="1")
        {
            options.WindowState=WindowState.Normal;
            options.Size=new Silk.NET.Maths.Vector2D<int>(1024,768);
            RecompOne.Runtime.Config.ConfigManager.View.Fullscreen=false;
        }
        return original(options);
    }
}



