// Tenchu SA Recomp -- ships no game-derived code. First run: get the user's own disc and BIOS,
// recompile locally through RecompOne.Recompiler, build in-process via Roslyn (already a transitive
// dependency of RecompOne.Runtime -- see RecompOne.Runtime/Modding/ModCompiler.cs for the
// established in-process-compile pattern this reuses), then run the built game in the SAME process
// and the SAME window -- no separate launcher-then-game handoff. Reuses RecompOne.Runtime's own
// disc/BIOS pickers for its setup UI, so there is no duplicate picker code between the launcher and
// the eventual game.
//
// This is the second recomp built on the shared RecompOne-fork engine; the first is
// AlienResurrection-Launcher, which is the reference implementation for everything here.

using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using RecompOne.Recompiler.CodeGen;
using RecompOne.Recompiler.Config;
using RecompOne.Recompiler.Psx;
using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Memory;

// Portable Windows setup for the supplied disc. Keep configuration, compiled
// game and saves together so double-clicking the executable needs no wizard.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);
// Double-clicking the Windows executable uses the approved gameplay settings.
foreach(var setting in new (string Key,string Value)[]{
 ("TENCHU_AUTOSTART","1"),("TENCHU_GAMEPLAY_RONIN","1"),("TENCHU_HD_RIKIMARU","0"),("TENCHU_REFINED_RIKIMARU","1"),
 ("TENCHU_TEXTURE_PACK","1"),("TENCHU_GAMEPLAY_GRAB","0"),("TENCHU_GAMEPLAY_STAMINA","1"),
 ("TENCHU_TEST_UNLOCK_AT","0"),("TENCHU_TEST_STRICT60","1"),("TENCHU_TEST_HALFSTEP_AT","0"),
 ("TENCHU_TEST_CAMERA_HALFSTEP","1"),("TENCHU_TEST_ITEM_HALFSTEP","1"),("TENCHU_TEST_LEGACY_TIMERS","1")})
 if(Environment.GetEnvironmentVariable(setting.Key)==null)Environment.SetEnvironmentVariable(setting.Key,setting.Value);
var bundledDisc=Path.Combine(AppContext.BaseDirectory,"disc","Tenchu - Stealth Assassins (USA).cue");
var existingDisc=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Tenchu-PC","disc","Tenchu - Stealth Assassins (USA).cue"));
if(Environment.GetEnvironmentVariable("TENCHU_DISC_PATH")==null && !File.Exists(bundledDisc) && File.Exists(existingDisc))
 Environment.SetEnvironmentVariable("TENCHU_DISC_PATH",existingDisc);
Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "logs"));
var localLog = new StreamWriter(new FileStream(
    Path.Combine(AppContext.BaseDirectory, "logs", "last-run.log"),
    FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
Console.SetOut(TextWriter.Synchronized(localLog));
Console.SetError(TextWriter.Synchronized(localLog));
Environment.SetEnvironmentVariable("RECOMPONE_DATA_DIR", Path.Combine(AppContext.BaseDirectory, "saves"));
ConfigManager.Load();
ConfigManager.Game.CdPath = Environment.GetEnvironmentVariable("TENCHU_DISC_PATH") ?? Path.Combine(AppContext.BaseDirectory, "disc", "Tenchu - Stealth Assassins (USA).cue");
ConfigManager.Game.SavePath = Path.Combine(AppContext.BaseDirectory, "saves");
ConfigManager.Game.BuildPath = Path.Combine(AppContext.BaseDirectory, "game");
ConfigManager.SaveGame();

// CHAIN_IRQ, NOW MEASURED AND ENABLED.
//
// This note used to say "deliberately absent", and told whoever came next to A/B it and record the
// numbers before making it a default. That has now been done, so here is the answer.
//
// Tenchu drives its pad through its OWN SIO0 driver hung off the SysEnqIntRP chain -- the same
// shape as the sibling Alien recomp, though that was evidence about one title and not proof about
// this one. MENU.EXE registers a verifier/handler pair on every boot (chainHeads slot 2, at
// 0x8009D330 under the menu overlay and 0x8008D1A8 during the bootstrap, so it re-registers per
// overlay). With the chain unwalked that handler never runs, the port exchanges ZERO bytes across
// an entire 800 s session, and no button ever reaches the game.
//
// MEASURED, CHAIN_IRQ=1 vs unset, identical builds:
//     chain(verify=0,     claim=0,     handler=0)      SIO0 tx=0
//     chain(verify=6490,  claim=6486,  handler=6486)   SIO0 tx=12972
// Operator-confirmed on real hardware: START then reaches the main menu and character select.
//
// WHY THE DELAY IS NOT OPTIONAL. The warning in the old note was right -- walking the chain during
// early init hangs the boot. Bisected on this game:
//     CHAIN_DELAY=600 (the engine default) -- decodes freeze at 396, GPU DMA frozen, title never
//                                             reached, though the pad itself is wide awake
//     CHAIN_DELAY=900                      -- same freeze
//     CHAIN_DELAY=1200                     -- healthy
//     CHAIN_DELAY=6000                     -- healthy, verified over a full 800 s run to the title
// So the boundary sits between 900 and 1200 vblanks (~15-20 s of emulated time); WHAT completes in
// that window is still not determined.
//
// THIS WAS 6000, AND THAT WAS A REAL COST, not the free margin the old note here claimed. The
// claim was "the title screen is not reached until around 500 s, so the pad is live long before a
// human could press anything". True of the title screen and wrong about everything before it: the
// gate holds the WHOLE interrupt chain, the pad ISR rides on it, and the intro movies play inside
// the window. At 60 Hz, 6000 vblanks is 100 SECONDS during which no button does anything -- so the
// intro was unskippable and looked like a hang. Reported from play, not from a log.
//
// 1200 now, and the old note's own objection is what settled it: it refused this value because the
// bisect "was run headless while the real launcher renders and paces differently". That is exactly
// the evidence since gathered -- a full windowed session at 1200 reached the title, skipped the
// intro on demand, played a mission and saved. Movies are skippable ~20 s in instead of ~100 s.
//
// RESIDUAL RISK, stated plainly: 900 fails and 1200 works, so this is one bisect step above a
// known cliff rather than a comfortable margin, and the failure mode is a boot hang rather than
// anything subtle. Raise it -- 2400 keeps a 2x margin -- if a cold boot ever stalls before the
// title. The right fix is to find the condition the delay stands in for and gate on THAT; the
// number is a proxy and every value here is a guess about how long something else takes.
//
// Both are set here, as the FIRST statements in the process, because Interrupts.ChainMode is a
// `static readonly` read once at type-initialization time; setting it after anything has touched
// Interrupts is silently ignored. An explicit value from the environment always wins, so
// CHAIN_IRQ=0 still works for A/B without editing this file.
if (Environment.GetEnvironmentVariable("CHAIN_IRQ") is null)
    Environment.SetEnvironmentVariable("CHAIN_IRQ", "1");
if (Environment.GetEnvironmentVariable("CHAIN_DELAY") is null)
    Environment.SetEnvironmentVariable("CHAIN_DELAY", "1200");

// Namespaces this game's writable state (memory cards) under its own per-user data directory rather
// than letting it land in whatever directory the process happened to start in. Must be set before
// anything reads Runtime.CardA/CardB, which resolve their paths on first access and cache them.
//
// It is also what keeps this game's memory cards separate from the Alien recomp's: UserData.AppId
// defaults to "RecompOne" for every host that does not set it, so two recomps that both forgot this
// line would quietly share carda.sav and cardb.sav.
RecompOne.Runtime.Storage.UserData.AppId = "Tenchu";

// The window/GL context and audio context are thread-affine, and gameplay's recompiled call chains
// need a deep stack (RecompOne translates every MIPS call into a real C# call). Running the ENTIRE
// launcher flow on a dedicated 64MB-stack thread from the very start means the window created here
// for the disc/BIOS pickers is the SAME window (owned by the SAME thread) gameplay ends up
// rendering into -- one process, one window, for the whole session.
int exitCode = 1;
var thread = new Thread(() =>
{
    try { exitCode = RunLauncher(); }
    catch (Exception e) { Console.Error.WriteLine(e); exitCode = 1; }
}, 64 * 1024 * 1024);
thread.Start();
thread.Join();
return exitCode;

int RunLauncher()
{
    // Tenchu: Stealth Assassins (US). Verified against the Greatest Hits disc, whose SYSTEM.CNF
    // reads BOOT=cdrom:\SLUS_007.06;1 -- note no spaces around '=' and no TCB line at all, both of
    // which SystemCfg.Parse handles (it trims each side and defaults Tcb to 4).
    const string ExpectedBootExe = "SLUS_007.06";
    const string GameTitle = "Tenchu";
    // The PRODUCT name, shown in the window title bar. Deliberately separate from GameTitle:
    // GameTitle names the GAME and is used in disc-validation messages ("this doesn't look like
    // Tenchu (USA)"), where a product name would read as nonsense.
    const string ProductName = "Tenchu Shadow Reborn";
    const string DisplayFontFile = "Tenchu.ttf";

    string launcherDir = AppContext.BaseDirectory;
    // NOT launcherDir. AppContext.BaseDirectory is read-only when running from an AppImage (a
    // mounted SquashFS image), so recompiling into "<launcherDir>/game" throws
    // System.IO.IOException: Read-only file system the first time anyone clicks Play. UserData.Dir
    // is the same per-user writable directory already used for memory cards and the display font.
    // (Build paths are resolved after Step 2a, once the location has been chosen.)

    // ---- Step 0: optional display font, user-supplied ----------------------------------------
    // Nothing is shipped. Display faces are routinely licensed "free for personal use", which
    // permits USE but grants no redistribution right, so bundling one would have this repo's own
    // licence promise rights over someone else's work. Same answer as the disc and the BIOS: the
    // user supplies it. Drop the file beside the executable or in the save/user-data folder and the
    // title screen uses it; otherwise HostFonts falls back to an installed system face at the same
    // sizes, and the screen still looks intentional.
    // Must be set before Initialize -- ImGui builds its font atlas inside that call.
    foreach (var candidate in new[]
             {
                 Path.Combine(RecompOne.Runtime.Storage.UserData.Dir, DisplayFontFile),
                 Path.Combine(AppContext.BaseDirectory, DisplayFontFile),
             })
    {
        if (!File.Exists(candidate)) continue;
        RecompOne.Runtime.Host.Window.HostFonts.DisplayFontPath = candidate;
        break;
    }

    // The nine kuji the launcher's background ring draws. Declared HERE, before Initialize, because
    // ImGui bakes its glyph set into the atlas during that call -- asking afterwards is silently too
    // late, exactly as it is for DisplayFontPath above. If no installed face covers them,
    // HostFonts.ExtraGlyphsAvailable stays false and KujiBackground draws Latin instead, so this is
    // safe to request unconditionally.
    RecompOne.Runtime.Host.Window.HostFonts.ExtraGlyphs = TenchuSARecomp.KujiBackground.Kuji;

    // Debug -> Game -> Reset. Registered before Initialize so the item exists for the whole session,
    // including while the game itself is running -- which is the only time anyone wants it.
    TenchuSARecomp.GameReset.Register();

    // ---- Step 1: get a valid disc + resolve BIOS, reusing the engine's own pickers -------------
    string appVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
    TenchuSARecomp.TexturePack.InstallShader();
    TenchuSARecomp.NativeFullscreen.Install();
    RecompOne.Runtime.Runtime.Initialize($"{ProductName} v{appVersion}");
    RecompOne.Runtime.Runtime.WaitForValidDisc(); // blocks with the disc picker until CdPath is valid
    RecompOne.Runtime.Runtime.WaitForBiosResolution(); // the disc and BIOS prompts are sequenced
                                                        // (never shown in the same frame), so
                                                        // WaitForValidDisc alone can return before
                                                        // the BIOS prompt has even appeared -- this
                                                        // waits it out too.
    string discPath = ConfigManager.Game.CdPath;
    string biosPath = ConfigManager.Game.BiosPath;

    // ---- Step 2: validate the disc is actually this game, before doing anything with it --------
    // Loaded here rather than inside the build task: the completeness check below needs it, and
    // loading it twice would risk the check and the build disagreeing about what the disc must hold.
    RecompOneConfig recompConfig;
    using (var validateFs = CueFs.Open(discPath))
    {
        var sysCfg = SystemCfg.Parse(validateFs);
        if (!string.Equals(sysCfg.BootExe, ExpectedBootExe, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"[launcher] this disc's boot executable is '{sysCfg.BootExe}', expected " +
                $"'{ExpectedBootExe}' -- this doesn't look like {GameTitle} (USA). Pick the " +
                "correct disc and try again.");
            RecompOne.Runtime.Runtime.ShowNotice(
                $"Wrong disc: found '{sysCfg.BootExe}', expected '{ExpectedBootExe}'.\n" +
                $"Please select your {GameTitle} (USA) disc.");
            return 1;
        }

        // The serial says it is the right GAME. It does not say the disc is COMPLETE -- and a
        // missing overlay is only a warning inside the recompiler, so an incomplete disc would
        // build a Tenchu.dll holding the bootstrap and none of the game, then die at runtime with
        // an "unmapped call". Refuse here instead, where it costs seconds and can name the file.
        //
        // The expected list comes from the recompiler config's own overlays rather than a second
        // hardcoded copy, so adding an overlay to assets/config.json extends this automatically.
        recompConfig = ConfigLoader.Load(Path.Combine(launcherDir, "assets", "config.json"));
        var missing = TenchuSARecomp.DiscCheck.FindMissing(validateFs, sysCfg.BootExe, recompConfig);
        if (missing.Count > 0)
        {
            Console.Error.WriteLine("[launcher] this disc is missing files the build needs:");
            foreach (var m in missing) Console.Error.WriteLine($"    {m}");
            RecompOne.Runtime.Runtime.ShowNotice(
                $"This disc reports the right serial ({sysCfg.BootExe}) but is missing "
              + $"{missing.Count} file(s) the build needs:\n\n  "
              + string.Join("\n  ", missing)
              + "\n\nIt may be an incomplete or damaged dump. Please use a complete disc.");
            return 1;
        }

        Console.WriteLine($"[launcher] disc OK: {sysCfg.BootExe} "
                        + $"({1 + recompConfig.Overlays.Count(o => !string.IsNullOrWhiteSpace(o.File))} files verified)");
    }

    // ---- Step 2a: where should the recompiled game go? ---------------------------------------
    // Asked HERE, immediately after the disc is accepted, because this is the moment the answer is
    // about to be used and the disc is the thing it is derived from. Asked ONCE: the choice is
    // remembered in settings.json, and Settings -> Recompiled game... reopens it later.
    //
    // The popup is registered either way, so the menu entry works on every run; it is only SHOWN
    // when nothing has been chosen yet.
    // ---- Step 2b: memory cards, named the way Batocera/libretro names them ------------------
    //
    // Both write the SAME thing -- a raw 128 KiB PS1 card image starting with "MC" -- so the only
    // thing standing between this recomp and a card shared with Batocera is the filename. Batocera
    // keeps one card per game as "<rom basename>.srm"; deriving the same name from the user's own
    // disc means pointing the save location at its psx saves folder is all the interop needs.
    //
    // Card B has NO Batocera convention: libretro's .srm is card 1 only. ".srm2" keeps the second
    // slot working here while staying invisible to a front end that only looks for .srm.
    //
    // Set BEFORE anything touches Runtime.CardA/CardB, which resolve and cache on first access.
    {
        var cardBase = Path.GetFileNameWithoutExtension(discPath);
        RecompOne.Runtime.Runtime.CardAName = cardBase + ".srm";
        RecompOne.Runtime.Runtime.CardBName = cardBase + ".srm2";
        Console.WriteLine($"[launcher] memory cards: '{cardBase}.srm' and '{cardBase}.srm2'");
    }

    // Asked once, alongside the build location. Point it at a Batocera psx saves folder and the
    // card is shared; leave it empty and it stays in the per-user directory as before.
    if (string.IsNullOrWhiteSpace(ConfigManager.Game.SavePath))
        RecompOne.Runtime.Runtime.WaitForSaveLocation();

    var buildPicker = new TenchuSARecomp.BuildPickerPopup();
    RecompOne.Runtime.Host.Window.PanelManager.Register(buildPicker);

    if (string.IsNullOrWhiteSpace(ConfigManager.Game.BuildPath)
        && !File.Exists(Path.Combine(TenchuSARecomp.BuildPickerPopup.DefaultDir, ".stamp")))
    {
        buildPicker.Show();
        while (!buildPicker.Answered) RecompOne.Runtime.Runtime.Pump();
        Console.WriteLine($"[launcher] build location: {TenchuSARecomp.BuildPickerPopup.Resolve()}");
    }

    // Resolved HERE, not earlier: Resolve() reads the setting the picker above may have just
    // written, so hoisting these to the top of the method would bake in the pre-picker value and
    // silently build into the default no matter what was chosen.
    string gameDir = TenchuSARecomp.BuildPickerPopup.Resolve();
    string recompiledDir = Path.Combine(gameDir, "Recompiled");
    string stampFile = Path.Combine(gameDir, ".stamp");
    string builtDllPath = Path.Combine(gameDir, "Tenchu.dll");

    // ---- Step 2b: front screen ---------------------------------------------------------------
    // Deliberately AFTER the disc and save location are settled and BEFORE any recompile: a first
    // run should not open on a build log. Play falls through to the cache check below; Exit leaves
    // without touching the game directory at all.
    var splash = new TenchuSARecomp.SplashScreen();
    RecompOne.Runtime.Host.Window.PanelManager.Register(splash);

    // Pad/keyboard navigation for OUR menu only. Turned off again the moment the choice is made,
    // before the guest ever runs -- from that point the pad belongs to the emulated PlayStation
    // controller and ImGui must not also be consuming it. See HostNav.
    RecompOne.Runtime.Host.Window.HostNav.UiNavigation = true;
    try
    {
        // Paced deliberately. The window is created with VSync off and no frame cap (see
        // HostWindow.Initialize), so an unpaced pump loop spins a core flat out -- measured at 64%
        // of one CPU sitting on a static menu. This screen can sit here for as long as someone
        // reads the About page. ~8ms still gives well over 60fps.
        while (splash.Choice == TenchuSARecomp.SplashChoice.None && Environment.GetEnvironmentVariable("TENCHU_AUTOSTART") != "1")
        {
            RecompOne.Runtime.Runtime.Pump();
            Thread.Sleep(8);
        }
    }
    finally
    {
        RecompOne.Runtime.Host.Window.HostNav.UiNavigation = false;
    }
    splash.IsOpen = false;

    if (splash.Choice == TenchuSARecomp.SplashChoice.Exit)
    {
        Console.WriteLine("[launcher] exit chosen at the front screen");
        return 0;
    }

    // ---- Step 3: cache check ---------------------------------------------------------------
    // Keyed on the disc, the BIOS and the recompiler config, so editing any of the three forces a
    // rebuild automatically.
    string configHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(Path.Combine(launcherDir, "assets", "config.json"))))[..16];
    string stamp = $"{discPath}|{biosPath}|{configHash}";

    bool cacheHit = File.Exists(stampFile) && File.Exists(builtDllPath)
        && File.ReadAllText(stampFile) == stamp;

    byte[]? builtBytes = null;

    if (!cacheHit)
    {
        Console.WriteLine("[launcher] no valid cache -- recompiling and building");

        // A full-screen animated stage display instead of a notice popup over an empty window. The
        // work below takes minutes on a first run and reports no progress of its own, so the only
        // honest thing to show is WHICH phase is running and that the process is still alive -- see
        // BuildScreen for why there is deliberately no percentage.
        var buildScreen = new TenchuSARecomp.BuildScreen { IsOpen = true };
        RecompOne.Runtime.Host.Window.PanelManager.Register(buildScreen);

        // The recompile+build below is genuinely slow and has no natural "pump the window" points of
        // its own. Run it on a background thread and keep pumping the window from this (the
        // dedicated) thread meanwhile -- exactly the pattern Modding.ModLoader.LoadAll() already
        // uses for mod compilation -- so the window keeps rendering instead of looking hung.
        string? buildError = null;
        byte[]? emitted = null;
        var buildTask = Task.Run(() =>
        {
            try
            {
                // ---- Step 4: materialize the Recompiled/ template ------------------------------
                Directory.CreateDirectory(recompiledDir);
                File.Copy(Path.Combine(launcherDir, "GameTemplate", "Stubs.cs"),
                          Path.Combine(recompiledDir, "Stubs.cs"), overwrite: true);

                // ---- Step 5: recompile in-process (no CLI subprocess needed -- OverlayWriter.Write
                // is a normal public static method) ----------------------------------------------
                var config = recompConfig;   // loaded and validated against the disc in Step 2
                config.Cue = discPath; // override the shipped placeholder with the user's actual disc
                using (var fs = CueFs.Open(discPath))
                    OverlayWriter.Write(config, fs, recompiledDir);

                // ---- Step 6: build in-process via Roslyn ---------------------------------------
                buildScreen.Current = TenchuSARecomp.BuildScreen.Stage.Building;
                var sourceFiles = Directory.GetFiles(recompiledDir, "*.cs");

                var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
                var trees = sourceFiles.Select(path => CSharpSyntaxTree.ParseText(
                    SourceText.From(File.ReadAllText(path), Encoding.UTF8), parseOptions, path)).ToList();

                // ImplicitUsings is an SDK/MSBuild feature (it generates a GlobalUsings.g.cs under
                // the hood) with no equivalent in raw CSharpCompilation, and the generated
                // Recompiled/*.cs relies on it -- so synthesize the same implicit-usings set the
                // SDK adds for a net10.0 project.
                trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(
                    "global using System;\n" +
                    "global using System.Collections.Generic;\n" +
                    "global using System.IO;\n" +
                    "global using System.Linq;\n" +
                    "global using System.Threading;\n" +
                    "global using System.Threading.Tasks;\n",
                    Encoding.UTF8), parseOptions, "GlobalUsings.g.cs"));

                var references = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                    .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
                    .ToList();

                var compileOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithAllowUnsafe(true)
                    .WithOptimizationLevel(OptimizationLevel.Release)
                    .WithNullableContextOptions(NullableContextOptions.Disable)
                    // Generated code is machine-emitted; these are expected there.
                    .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic>
                    {
                        ["CS0164"] = ReportDiagnostic.Suppress,
                        ["CS0219"] = ReportDiagnostic.Suppress,
                        ["CS0162"] = ReportDiagnostic.Suppress,
                        ["CS0168"] = ReportDiagnostic.Suppress,
                        ["CS8321"] = ReportDiagnostic.Suppress,
                    });

                var compilation = CSharpCompilation.Create("Tenchu", trees, references, compileOptions);
                using var ms = new MemoryStream();
                var emitResult = compilation.Emit(ms);
                if (!emitResult.Success)
                {
                    var sb = new StringBuilder();
                    foreach (var diag in emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                    {
                        Console.Error.WriteLine($"[launcher] build error: {diag}");
                        sb.AppendLine(diag.ToString());
                    }
                    buildError = sb.ToString();
                    return;
                }
                emitted = ms.ToArray();
                Console.WriteLine($"[launcher] built ({emitted.Length} bytes)");

                Directory.CreateDirectory(gameDir);
                File.WriteAllBytes(builtDllPath, emitted); // kept on disk only for the cache check
                File.WriteAllText(stampFile, stamp);
            }
            catch (Exception ex)
            {
                buildError = ex.ToString();
            }
        });

        while (!buildTask.IsCompleted)
        {
            RecompOne.Runtime.Runtime.Pump();
            Thread.Sleep(16);
        }

        buildScreen.IsOpen = false;

        if (buildError != null)
        {
            Console.Error.WriteLine($"[launcher] build failed:\n{buildError}");
            RecompOne.Runtime.Runtime.ShowNotice($"Build failed:\n{buildError}");
            return 1;
        }
        builtBytes = emitted;
    }
    else
    {
        Console.WriteLine("[launcher] cache hit -- skipping recompile/build");
        builtBytes = File.ReadAllBytes(builtDllPath);
    }

    // ---- Step 7: run the built game in-process, in this same window -----------------------------
    // No child process, no separate bin/ directory, no DLL/native-lib/runtimeconfig copying -- the
    // compiled assembly loads directly into this already-running process, which already has every
    // reference and native dependency resolved, and Assembly.Load binds the emitted references to
    // those already-loaded assemblies by identity rather than duplicating them.
    Console.WriteLine("[launcher] starting the game");
    TenchuSARecomp.TexturePack.Activate();
    TenchuSARecomp.KeyboardTapBuffer.Install();
    var asm = Assembly.Load(builtBytes!);
    var entryType = asm.GetType("Recompiled.Entry")
        ?? throw new InvalidOperationException("Recompiled.Entry type not found in the built assembly");
    var runMethod = entryType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("Entry.Run method not found on Recompiled.Entry");

    var mem = new PSMemory();
#if TENCHU_BENCHMARKS
    TenchuSARecomp.BenchmarkPad.Install();
    TenchuSARecomp.BenchmarkGameTiming.Install(asm);
    TenchuSARecomp.BenchmarkHalfStep.Install(asm);
    TenchuSARecomp.QuickTestStart.Install(asm);
#endif
    if (int.TryParse(Environment.GetEnvironmentVariable("TENCHU_BENCHMARK_SECONDS"), out int benchmarkSeconds) && benchmarkSeconds > 0)
    {
        Console.WriteLine($"[benchmark] starting {benchmarkSeconds}s windowed run");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(benchmarkSeconds));
            Console.WriteLine("[benchmark] timed run complete");
            Console.Out.Flush();
            Environment.Exit(0);
        });
    }
    runMethod.Invoke(null, [mem, discPath]);
    return 0;
}



