using RecompOne.Recompiler.Config;
using RecompOne.Runtime.Cdrom;

namespace TenchuSARecomp;

// Does this disc actually contain the files the build needs?
//
// The serial check that runs before this one -- SYSTEM.CNF naming SLUS_007.06 -- establishes the
// disc is the right GAME. It does not establish the disc is usable. A damaged rip, a partial dump
// or a modified disc carries the right serial and is then handed straight to the recompiler.
//
// AND THE RECOMPILER WILL NOT STOP IT. A missing overlay source is a warning there, not an error
// (OverlayWriter.cs: "WARNING: disc file not found", after which the overlay returns null and is
// skipped). A disc missing \TENCHU\MAIN.EXE;1 therefore still produces a Tenchu.dll -- one holding
// the bootstrap and none of the game -- which compiles cleanly and dies at runtime with an
// "unmapped call". The build log does say why, but it says so in the middle of a multi-minute
// build that nobody is reading.
//
// So the check belongs here, at the moment the disc is chosen, where refusing costs the user
// seconds instead of minutes and the message can name what is actually missing.
public static class DiscCheck
{
    /// <summary>
    /// Confirms the boot executable and every file-sourced overlay can be found on the disc.
    /// Returns the paths that could not be, empty when the disc is complete.
    /// </summary>
    public static IReadOnlyList<string> FindMissing(CueFs fs, string bootExe, RecompOneConfig cfg)
    {
        var missing = new List<string>();

        // The boot executable as a FILE, not merely as a name in SYSTEM.CNF. Those are different
        // claims, and only the second one has been checked up to this point.
        if (!Exists(fs, bootExe)) missing.Add(bootExe);

        foreach (var ov in cfg.Overlays)
        {
            // Overlays sourced by raw LBA have no name to look up. Absent is not the same as
            // unverifiable, so they are skipped rather than reported. Tenchu's four all use "file".
            if (string.IsNullOrWhiteSpace(ov.File)) continue;
            if (!Exists(fs, ov.File!)) missing.Add(ov.File!);
        }

        return missing;
    }

    // Locate is the recompiler's own route to a disc file (OverlayWriter.cs:474), so this verifies
    // by exactly the path the build will later take rather than by a parallel implementation that
    // could disagree with it.
    static bool Exists(CueFs fs, string path)
    {
        try { return fs.Locate(path, out _, out _); }
        catch { return false; }
    }
}
