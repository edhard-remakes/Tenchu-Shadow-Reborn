using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace TenchuSARecomp;

public enum SplashChoice { None, Play, Exit }

// The launcher's front screen: title, three options, and an About panel. Shown after the disc and
// save location are resolved and BEFORE any recompile work begins, so a first run does not make
// somebody stare at a build log before they have chosen to start.
//
// Drawn as an IPanel through the engine's own PanelManager, which is public API, so this needs no
// changes to the runtime's render loop. The launcher pumps frames until Choice stops being None.
//
// Ported from AlienResurrection-Launcher/SplashScreen.cs. The layout machinery below is carried
// over verbatim because it encodes several non-obvious fixes (see the comments on Begin, on the
// glow helper, and on the line tightening). The CONTENT is not carried over: the palette, the
// title, the background animation and the known-issues list were all specific to that game.
public sealed class SplashScreen : IPanel
{
    public string Name => "Launcher";
    public bool IsOpen { get; set; } = true;

    public SplashChoice Choice { get; private set; } = SplashChoice.None;

    readonly KujiBackground _kuji = new();

    bool _credits;
    bool _pendingOpen = true;
    bool _focusPending = true;

    const string PopupId = "##splash";

    // No glyph tricks here. The sibling launcher spells its title with a lowercase 'i' to dodge a
    // spiked capital-I in one specific display face; that is a property of that font and must not
    // be copied blind. If a Tenchu display font is chosen later and has its own quirk, handle it
    // the same way it did: branch on HostFonts.Loaded so the fallback face still gets real capitals.
    const string TitleTop = "TENCHU";
    const string TitleBottom = "STEALTH ASSASSINS";
    const string Subtitle = "Recompilation";

    // Kept in sync with TenchuSARecomp.csproj's <Version> -- there is no build step that threads
    // MSBuild's version into this string, so bump both together.
    const string AppVersion = "0.1.0";

    // How far the second title line is pulled up toward the first, in pixels. Proportional to the
    // title size so the pairing survives a font-size change; raise it to tighten further.
    static float TitleLineTighten => (HostFonts.Title != null ? HostFonts.TitleSize : 20f) * 0.34f;

    // PLACEHOLDER PALETTE, chosen to be legible rather than to be right.
    //
    // The sibling launcher's bio-green was picked to match its game and was iterated on at length
    // against a real screen; none of that work transfers. What DOES transfer is the constraint that
    // produced it: this is the first thing anyone sees, full-screen on a dark field, so text and
    // buttons need real contrast rather than the muted greys that read fine in a small debug panel
    // and disappear here. Retune freely, but check it on an actual display before calling it done.
    static readonly Vector4 Bg      = new(0.11f, 0.11f, 0.12f, 1f);
    static readonly Vector4 Text    = new(0.94f, 0.94f, 0.95f, 1f);
    static readonly Vector4 Dim     = new(0.72f, 0.73f, 0.75f, 1f);
    static readonly Vector4 Accent  = new(0.92f, 0.72f, 0.35f, 1f);
    static readonly Vector4 Btn     = new(0.22f, 0.23f, 0.26f, 1f);
    static readonly Vector4 BtnHov  = new(0.34f, 0.36f, 0.40f, 1f);
    static readonly Vector4 BtnAct  = new(0.48f, 0.51f, 0.56f, 1f);
    static readonly Vector4 Border  = new(0.45f, 0.47f, 0.51f, 1f);
    static readonly Vector4 Warn    = new(1.00f, 0.76f, 0.30f, 1f);
    // The core stays bright and near-neutral so the letterforms read crisply; the halo carries the
    // colour. Drawing the colour alone at full strength reads as blur, not glow.
    static readonly Vector4 TitleCore = new(0.98f, 0.97f, 0.96f, 1f);
    static readonly Vector4 TitleGlow = new(0.78f, 0.16f, 0.18f, 1f);

    public void Draw()
    {
        if (!IsOpen) return;

        // NOT a modal, despite a modal being the obvious choice for a front screen. A modal blocks
        // input to everything behind it -- including the main menu bar, which is where Settings ->
        // Disc/BIOS/Saves live. Locking the user out of the save-location picker on the very screen
        // where they would want it is worse than the problem a modal solves.
        //
        // The problem it solved was real, though: HostWindow.DrawDockspace() paints a full-viewport
        // DockSpace before PanelManager.DrawPanels() runs, and the sibling launcher's first attempt
        // lost every click to it. The actual culprit was NoBringToFrontOnFocus on the window, which
        // pinned it behind that dockspace. A plain window WITHOUT that flag, focused once on the way
        // in, sits above the dockspace and below the menu bar -- exactly the arrangement wanted.
        var vp = ImGui.GetMainViewport();

        // WorkPos/WorkSize, not Pos/Size: the work area excludes the menu bar, so the splash stops
        // short of it instead of covering the thing it must not cover.
        ImGui.SetNextWindowPos(vp.WorkPos);
        ImGui.SetNextWindowSize(vp.WorkSize);
        if (_pendingOpen)
        {
            ImGui.SetNextWindowFocus();
            _pendingOpen = false;
        }

        ImGui.PushStyleColor(ImGuiCol.WindowBg, Bg);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.Button, Btn);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, BtnHov);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, BtnAct);
        ImGui.PushStyleColor(ImGuiCol.Border, Border);
        // The focus rectangle nav draws around the selected button. ImGui's default is blue, which
        // on this screen belongs to nothing else on it.
        ImGui.PushStyleColor(ImGuiCol.NavHighlight, TitleGlow);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);

        if (ImGui.Begin(PopupId,
                ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoDocking))
        {
            // Background first, so every bit of content below draws over it. The sibling launcher
            // has a motion-tracker radar at this point; this is the Tenchu equivalent -- the nine
            // kuji medallions from the game's own title screen, turning. Same construction: pure
            // draw-list primitives, no textures and no shaders.
            //
            // Held well back in alpha. It sits directly behind the title and the buttons, and at
            // full strength the halo washes out exactly the text that has to stay readable.
            var bgSize = ImGui.GetContentRegionAvail();
            _kuji.Draw(ImGui.GetWindowDrawList(),
                       ImGui.GetWindowPos() + new Vector2(bgSize.X * 0.5f, bgSize.Y * 0.52f),
                       MathF.Min(bgSize.X, bgSize.Y) * 0.30f,
                       alpha: 0.55f,
                       font: HostFonts.Ui);

            if (_credits) DrawCredits();
            else DrawMenu();
        }
        ImGui.End();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(7);
    }

    void Close(SplashChoice choice)
    {
        Choice = choice;
        IsOpen = false;
    }

    void DrawMenu()
    {
        float w = ImGui.GetContentRegionAvail().X;

        ImGui.Dummy(new Vector2(0, ImGui.GetContentRegionAvail().Y * 0.18f));

        // The HostFonts faces are null unless the user supplied a display font -- see the launcher's
        // font resolution. Everything still lays out correctly on the default face, just smaller.
        PushFont(HostFonts.Title);
        GlowCentered(TitleTop, w, TitleCore, TitleGlow);
        PopFont(HostFonts.Title);

        // Pull the second line up. The gap is not padding we added -- it is the font's own leading:
        // the capitals occupy only about 58% of the em box, so a 96px line reserves roughly 40px of
        // empty space beneath the letterforms, and the default item spacing sits under that again.
        // Closing it by a fraction of the title size keeps the two lines locked together at any size.
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - TitleLineTighten);

        PushFont(HostFonts.Subtitle);
        GlowCentered(TitleBottom, w, TitleCore, TitleGlow);
        PopFont(HostFonts.Subtitle);

        ImGui.Dummy(new Vector2(0, 10));

        // Plain accent, no halo -- the glow stays on the title alone so it reads as the focal point
        // rather than competing with the line under it.
        PushFont(HostFonts.Ui);
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        Centered(Subtitle, w);
        ImGui.PopStyleColor();
        PopFont(HostFonts.Ui);

        ImGui.Dummy(new Vector2(0, 40));

        // Button labels ride the same UI face so they are readable at a glance rather than at the
        // default 13px, which is sized for debug panels.
        PushFont(HostFonts.Ui);
        var btn = new Vector2(320, HostFonts.Ui != null ? 58 : 46);
        if (CenteredButton("Play Game", btn, w)) Close(SplashChoice.Play);
        // Where the focus rectangle starts, so a pad user has something selected on arrival rather
        // than having to press a direction first to find out anything is selectable.
        if (_focusPending) { ImGui.SetItemDefaultFocus(); _focusPending = false; }
        ImGui.Dummy(new Vector2(0, 8));
        if (CenteredButton("About", btn, w)) _credits = true;
        ImGui.Dummy(new Vector2(0, 8));
        if (CenteredButton("Exit", btn, w)) Close(SplashChoice.Exit);
        PopFont(HostFonts.Ui);

        ImGui.Dummy(new Vector2(0, 28));
        Centered("Requires your own Tenchu: Stealth Assassins (USA) disc. No game data is distributed.",
                 w, dim: true);
    }

    void DrawCredits()
    {
        ImGui.Dummy(new Vector2(0, 12));
        float w = ImGui.GetContentRegionAvail().X;
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        Centered("About Recompilation", w);
        ImGui.PopStyleColor();
        Centered($"v{AppVersion}", w, dim: true);
        ImGui.Separator();

        if (ImGui.BeginChild("##creditsbody", new Vector2(0, ImGui.GetContentRegionAvail().Y - 60)))
        {
            // First, not last. A warning nobody scrolls to is a warning nobody reads.
            //
            // THERE IS DELIBERATELY NO KNOWN-ISSUES LIST HERE YET. The sibling launcher has a
            // six-item one, and copying it across would have this project claim, in its own About
            // box, to have observed bugs it has never run. Fill this in from actual playtesting,
            // one line per issue, and only for issues seen in THIS game.
            Section("Work in progress");
            ImGui.PushStyleColor(ImGuiCol.Text, Warn);
            ImGui.TextWrapped(
                "This is an early, incomplete recompilation and it has not been played through. "
                + "Expect to run into problems, including ones serious enough to block progress. "
                + "Save often, and keep more than one save.");
            ImGui.PopStyleColor();

            Section("Recompilation engine");
            Item("RecompOne", "by BlackLabelHQ -- the recompiler and runtime this is built on (MIT). "
                            + "This project uses a fork of it, shared with the Alien Resurrection "
                            + "recompilation. Not affiliated with or endorsed by them.");

            Section("Original game");
            Item("Tenchu: Stealth Assassins", "developed by Acquire, published by Activision in North "
                            + "America. All game code, assets and trademarks belong to their "
                            + "respective owners. This launcher distributes none of them -- it "
                            + "recompiles the disc you already own, on your own machine.");

            Section("Libraries");
            Item("Dear ImGui", "by Omar Cornut and contributors (MIT)");
            Item("Silk.NET", "by the .NET Foundation and contributors (MIT)");
            Item("NativeFileDialogSharp", "bindings for nativefiledialog (zlib)");
            Item("MonoMod", "runtime detour library (MIT)");
            Item(".NET / Roslyn", "by Microsoft (MIT)");

            Section("Assets");
            Item("SDL_GameControllerDB", "community controller mappings");
            Item("Shinonome 16dot font", "by Yasuyuki Furukawa (public domain), maintained by /efont/ "
                            + "-- the source of the BIOS character fonts");
            if (HostFonts.Loaded)
                Item("Display font", "supplied by you; not distributed with this launcher.");

            Section("This port");
            Item("Engine fixes and launcher", "built on the RecompOne fork maintained for this project.");

            ImGui.Dummy(new Vector2(0, 8));
            ImGui.TextWrapped(
                "Licenses differ between components. See each project's own license for the terms "
                + "that apply to it.");
        }
        ImGui.EndChild();

        ImGui.Separator();
        if (CenteredButton("Back", new Vector2(160, 34), ImGui.GetContentRegionAvail().X))
        {
            _credits = false;
            _focusPending = true;   // re-seat the focus rectangle on Play Game
        }
    }

    static void PushFont(ImFontPtr? f) { if (f is { } v) ImGui.PushFont(v); }
    static void PopFont(ImFontPtr? f)  { if (f != null) ImGui.PopFont(); }

    // Poor-man's glow: ImGui has no shader effects, so the halo is the same string stamped around
    // the centre at two radii with low alpha, then the core drawn on top. Cheap, and it composites
    // correctly because each stamp is additive-ish over the dark background.
    //
    // Uses the window draw list rather than ImGui.Text so every stamp lands at an exact pixel offset
    // from one computed origin -- walking the cursor instead would round differently per pass and
    // make the halo lopsided.
    static void GlowCentered(string text, float width, Vector4 core, Vector4 glow)
    {
        var sz = ImGui.CalcTextSize(text);
        var origin = ImGui.GetCursorScreenPos();
        origin.X += Math.Max(0, (width - sz.X) * 0.5f);

        var dl = ImGui.GetWindowDrawList();
        ReadOnlySpan<(float r, float a)> rings = [(4f, 0.16f), (2f, 0.30f)];
        foreach (var (r, a) in rings)
        {
            uint col = ImGui.GetColorU32(glow with { W = a });
            for (int i = 0; i < 8; i++)
            {
                float ang = MathF.PI * 2f * i / 8f;
                dl.AddText(origin + new Vector2(MathF.Cos(ang) * r, MathF.Sin(ang) * r), col, text);
            }
        }
        dl.AddText(origin, ImGui.GetColorU32(core), text);

        // The draw list does not move the cursor; reserve the space so layout continues normally.
        ImGui.Dummy(sz);
    }

    static void Section(string s)
    {
        ImGui.Dummy(new Vector2(0, 6));
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        ImGui.TextUnformatted(s);
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    static void Item(string name, string detail)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextUnformatted(name);
        ImGui.Indent();
        ImGui.PushStyleColor(ImGuiCol.Text, Dim);
        ImGui.TextWrapped(detail);
        ImGui.PopStyleColor();
        ImGui.Unindent();
    }

    static void Centered(string text, float width, bool dim = false)
    {
        var sz = ImGui.CalcTextSize(text);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (width - sz.X) * 0.5f));
        if (dim) ImGui.PushStyleColor(ImGuiCol.Text, Dim);
        ImGui.TextUnformatted(text);
        if (dim) ImGui.PopStyleColor();
    }

    static bool CenteredButton(string label, Vector2 size, float width)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (width - size.X) * 0.5f));
        return ImGui.Button(label, size);
    }
}
