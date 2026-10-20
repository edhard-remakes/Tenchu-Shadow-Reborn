using System.Diagnostics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace TenchuSARecomp;

// A top-level "Game" menu with Reset. Relaunches the launcher so the game starts again from a
// cold boot, without anyone having to kill and re-run it from a terminal.
//
// WHY A RELAUNCH AND NOT AN IN-PROCESS RESET. A real reset would keep the window and re-run
// Recompiled.Entry.Run against a fresh PSMemory. Nothing in the runtime supports that today: the
// emulated machine's state is spread across statics in Dispatcher (loaded overlays), the BIOS, the
// CD controller, the SPU, the timers, SIO0, the GPU and Interrupts, and there is no ResetAll to
// call -- only piecemeal Irq.Reset/XaAudio.Reset. Adding one means auditing every one of those, and
// the failure mode of getting it subtly wrong is the worst kind: the first reset looks fine and the
// third behaves oddly for reasons nobody connects back to here.
//
// A relaunch is a genuine cold boot with no such doubt, and it is cheap because the recompile is
// cached -- the rebuild is skipped and the process comes straight back up. The cost is that the
// window closes and reopens. If that becomes annoying enough to be worth the audit, the in-process
// version is the right answer and this can be replaced.
//
// UNSAVED PROGRESS IS LOST, exactly as it would be on real hardware -- anything not written to a
// memory card is gone. Hence the confirm step below rather than a single click.
public static class GameReset
{
    static bool _requested;
    const string PopupId = "Reset game##gamereset";

    // TOP-LEVEL, not parented under "Debug".
    //
    // MainMenuBar.Draw() calls its own DebugMenu() and THEN MenuRegistry.DrawMenus(), and each does
    // its own ImGui.BeginMenu("Debug"). Registering under that parent therefore produced a SECOND
    // menu with the same label sitting beside the built-in one, and opening the built-in one -- the
    // obvious thing to do -- showed no Game item at all. A distinct top-level menu has no such
    // ambiguity.
    public static void Register()
    {
        MenuRegistry.Register("Game", DrawItems);
        // Drawn outside the menu, which is the whole point -- see DrawConfirm.
        MenuRegistry.RegisterWindow(DrawConfirm);
    }

    static void DrawItems()
    {
        if (ImGui.MenuItem("Reset game...")) _requested = true;

        // Lives here rather than under "Settings" for the same reason Reset is not under "Debug":
        // MainMenuBar draws its own Settings menu and MenuRegistry would add a SECOND one beside it.
        if (ImGui.MenuItem("Recompiled game location...")) BuildPickerPopup.Instance?.Show();

        ImGui.Separator();
        ImGui.TextDisabled("Relaunches the launcher for a cold boot.");
        ImGui.TextDisabled("The recompile is cached, so it returns quickly.");
    }

    // The confirmation has to live OUTSIDE the menu. An ImGui MenuItem closes the menu when it is
    // clicked, so the first attempt -- arm on one click, confirm on a second menu item -- dismissed
    // the menu the moment it was armed and looked like the item simply did nothing. A modal popup
    // drawn from the window pass stays up until it is answered.
    static void DrawConfirm()
    {
        if (_requested)
        {
            ImGui.OpenPopup(PopupId);
            _requested = false;
        }

        var vp = ImGui.GetMainViewport();
        var centre = new System.Numerics.Vector2(vp.WorkPos.X + vp.WorkSize.X * 0.5f,
                                                 vp.WorkPos.Y + vp.WorkSize.Y * 0.5f);
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));

        if (!ImGui.BeginPopupModal(PopupId)) return;

        ImGui.TextUnformatted("Restart the game from a cold boot?");
        ImGui.Spacing();
        ImGui.TextDisabled("Anything not written to a memory card is lost,");
        ImGui.TextDisabled("exactly as on real hardware.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Reset", new System.Numerics.Vector2(130, 0)))
        {
            ImGui.CloseCurrentPopup();
            Restart();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new System.Numerics.Vector2(130, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    static void Restart()
    {
        try
        {
            // ProcessPath is the HOST, which is the dotnet binary for a framework-dependent launch
            // and the apphost for a self-contained one. GetCommandLineArgs()[0] is the managed
            // assembly either way, so it has to be re-supplied as argument 0 only in the first case
            // -- passing it to an apphost would make the app try to open itself as a file.
            string host = Environment.ProcessPath ?? "dotnet";
            var cli = Environment.GetCommandLineArgs();

            var psi = new ProcessStartInfo
            {
                FileName = host,
                UseShellExecute = false,          // also inherits the environment, so CHAIN_IRQ and
                WorkingDirectory = Environment.CurrentDirectory,   // friends survive the restart
            };

            if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                psi.ArgumentList.Add(cli[0]);
            for (int i = 1; i < cli.Length; i++) psi.ArgumentList.Add(cli[i]);

            Console.WriteLine($"[launcher] reset requested -- relaunching {host}");
            Process.Start(psi);
        }
        catch (Exception e)
        {
            // Do NOT exit if the relaunch failed, or the reset becomes a quit.
            Console.Error.WriteLine($"[launcher] reset failed, staying up: {e.Message}");
            RecompOne.Runtime.Runtime.ShowNotice($"Could not restart:\n{e.Message}");
            return;
        }

        Environment.Exit(0);
    }
}
