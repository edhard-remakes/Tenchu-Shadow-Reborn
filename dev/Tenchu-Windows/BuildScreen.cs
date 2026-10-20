using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace TenchuSARecomp;

// What the launcher shows while it recompiles the game off the disc and builds the result.
//
// The work happens on a background thread and the main thread pumps frames (see Program.cs), so
// something has to occupy the window for the length of a genuinely slow operation. Previously that
// was a notice popup over an empty screen, which is indistinguishable from a hang.
//
// NO PERCENTAGE, DELIBERATELY. Neither half of the work can report progress: OverlayWriter.Write
// runs to completion in one call, and so does the Roslyn emit. A bar creeping to 90% and sitting
// there is a lie, and worse than no bar, because it tells the user the thing is nearly done when
// nothing is known. What IS known is which of the two phases is running, so that is what is shown,
// alongside the same turning ring the front screen uses -- the motion carries "still alive", which
// is the actual question being asked, and the ring spells the phase out.
public sealed class BuildScreen : IPanel
{
    public string Name => "Building";
    public bool IsOpen { get; set; }

    public enum Stage { Recompiling, Building, Done }

    /// <summary>Set from the build thread; read on the UI thread. Assignment is atomic.</summary>
    public volatile Stage Current = Stage.Recompiling;

    readonly KujiBackground _ring = new();
    float _elapsed;
    bool _focusPending = true;

    // Nine slots each, to fill the ring exactly. The ring pads to nine anyway, but choosing words
    // that already fit means no slot sits conspicuously empty.
    static string RingText(Stage s) => s switch
    {
        Stage.Recompiling => "RECOMPILE",   // 9
        Stage.Building    => "ASSEMBLE ",   // 8 + a space, so the ring keeps a steady rhythm
        _                 => "  READY  ",
    };

    static string Caption(Stage s) => s switch
    {
        Stage.Recompiling => "Translating the disc to C#",
        Stage.Building    => "Compiling the translated code",
        _                 => "Ready",
    };

    static readonly Vector4 Bg = new(0.11f, 0.11f, 0.12f, 1f);

    // NOT a muted grey. The palette note in SplashScreen.cs records this exact trap: greys that read
    // fine in a small debug panel disappear on a full-screen dark field, and it cost the sibling
    // launcher a round of rework. Worse here, because this text sits over a moving amber halo, so
    // there is no single background colour to pick a contrast against.
    //
    // The fix used on the title is the one used here: draw the string several times offset in a
    // warm colour to build an outline, then the near-white core on top. That reads over the dark
    // field AND over the bright middle of the halo, which no flat colour manages.
    static readonly Vector4 TextCore = new(1.00f, 0.98f, 0.94f, 1f);
    static readonly Vector4 TextGlow = new(0.55f, 0.16f, 0.02f, 1f);

    public void Draw()
    {
        if (!IsOpen) return;

        _elapsed += Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);

        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.WorkPos);
        ImGui.SetNextWindowSize(vp.WorkSize);

        // Focused once, on the way in. NOT NoBringToFrontOnFocus -- see the long note in
        // SplashScreen.Draw: HostWindow.DrawDockspace() paints a full-viewport DockSpace before
        // PanelManager.DrawPanels() runs, and that flag pins a window BEHIND it. The debug panels
        // (Output in particular) are docked in that dockspace and are open during the build, so
        // everything drawn here came out muddied by whatever the dockspace put on top -- which reads
        // as "the screen is too dark" and is not a colour problem at all.
        //
        // The splash avoids this by not setting the flag and focusing once; this does the same.
        if (_focusPending)
        {
            ImGui.SetNextWindowFocus();
            _focusPending = false;
        }
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Bg);

        if (ImGui.Begin("##building",
                ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoDocking))
        {
            var dl = ImGui.GetWindowDrawList();
            var size = ImGui.GetContentRegionAvail();
            var centre = ImGui.GetWindowPos() + new Vector2(size.X * 0.5f, size.Y * 0.44f);
            float radius = MathF.Min(size.X, size.Y) * 0.24f;

            var stage = Current;
            _ring.Draw(dl, centre, radius, 1f, RingText(stage), HostFonts.Ui);

            // Caption below the ring, clear of the halo. Both lines use the larger UI face -- the
            // default 13px face is unreadable at this distance from the screen regardless of colour.
            float y = centre.Y + radius * 1.9f;
            Centred(Caption(stage), y, size.X, HostFonts.Ui);

            // Elapsed time is the one honest number available, and it answers "is this normal?"
            // better than a fabricated percentage would.
            Centred($"{_elapsed:0} s elapsed  --  first run only, the result is cached",
                    y + ImGui.GetTextLineHeightWithSpacing() * 2.0f, size.X, HostFonts.Ui, 0.82f);
        }
        ImGui.End();
        ImGui.PopStyleColor();
    }

    // Same construction as SplashScreen.GlowCentered: an outline built from eight offset copies at
    // two radii, then the core. Drawn through the draw list rather than the cursor because this
    // screen positions everything absolutely around the ring.
    static void Centred(string text, float screenY, float width, ImFontPtr? font, float scale = 1f)
    {
        if (font is { } f) ImGui.PushFont(f);

        var sz = ImGui.CalcTextSize(text);
        var origin = new Vector2(ImGui.GetWindowPos().X + (width - sz.X) * 0.5f, screenY);
        var dl = ImGui.GetWindowDrawList();

        ReadOnlySpan<(float r, float a)> rings = [(4f, 0.20f), (2f, 0.38f)];
        foreach (var (r, a) in rings)
        {
            uint col = ImGui.GetColorU32(TextGlow with { W = a * scale });
            for (int i = 0; i < 8; i++)
            {
                float ang = MathF.PI * 2f * i / 8f;
                dl.AddText(origin + new Vector2(MathF.Cos(ang) * r, MathF.Sin(ang) * r), col, text);
            }
        }
        dl.AddText(origin, ImGui.GetColorU32(TextCore with { W = scale }), text);

        if (font is not null) ImGui.PopFont();
    }
}
