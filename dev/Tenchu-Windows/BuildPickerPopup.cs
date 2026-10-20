using System.Numerics;
using System.Text;
using ImGuiNET;
using NativeFileDialogSharp;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Host.Window;

namespace TenchuSARecomp;

// Where the recompiled game is written: the generated C# and the assembly built from it.
//
// Mirrors the engine's SavePickerPopup (Host/Window/Panels/Configuration/SavePickerPopup.cs) --
// folder picker, Confirm / Use Default / Cancel, create-and-probe validation rather than a refusal.
// Read that one first if this needs changing; the layout and the reasoning behind it are documented
// there and deliberately not repeated.
//
// TWO REAL DIFFERENCES from the save picker:
//
//   1. It is asked ONCE, during first-run setup, straight after the disc is accepted -- because
//      that is the moment the answer is about to be used. Afterwards the choice is remembered in
//      settings.json and never asked again; Settings -> Recompiled game... reopens it.
//
//   2. The default is a PER-USER data directory, not the launcher's own folder. AppContext
//      .BaseDirectory is read-only when running from an AppImage, so building into it throws
//      "Read-only file system" the first time anyone clicks Play.
//
// SIZE MATTERS HERE in a way it does not for saves: the output is roughly 20 MB for this game --
// about 14 MB of generated C# plus a 5 MB assembly -- so the folder is worth choosing deliberately
// if the default lives on a small partition.
public sealed class BuildPickerPopup : IPanel
{
    public string Name => "Recompiled Game Location";
    public bool IsOpen { get; set; }

    /// <summary>The live instance, so the menu can reopen this without threading a reference
    /// through the launcher. Set when the popup is constructed; there is only ever one.</summary>
    public static BuildPickerPopup? Instance { get; private set; }

    public BuildPickerPopup() => Instance = this;

    const string PopupId = "##BuildPicker";
    const int BufSize = 1024;

    byte[] _pathBuf = new byte[BufSize];
    string _error = "";
    bool _pendingOpen;

    /// <summary>True once the user has answered, so a first-run flow can wait on it.</summary>
    public bool Answered { get; private set; }

    /// <summary>The default when nothing is chosen: a per-user, writable, AppImage-safe location.</summary>
    public static string DefaultDir => Path.Combine(RecompOne.Runtime.Storage.UserData.Dir, "game");

    /// <summary>The location to build into, honouring the saved choice.</summary>
    public static string Resolve()
    {
        var p = ConfigManager.Game.BuildPath;
        return string.IsNullOrWhiteSpace(p) ? DefaultDir : Path.GetFullPath(p);
    }

    public void Show()
    {
        SetPathBuf(ConfigManager.Game.BuildPath ?? "");
        _error = "";
        Answered = false;
        _pendingOpen = true;
        IsOpen = true;
    }

    public void Draw()
    {
        if (!IsOpen) return;

        if (_pendingOpen) { ImGui.OpenPopup(PopupId); _pendingOpen = false; }

        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.GetCenter(), ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(580, 0), ImGuiCond.Always);

        bool open = true;
        if (!ImGui.BeginPopupModal(PopupId, ref open,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove))
        {
            if (!open) { IsOpen = false; Answered = true; }
            return;
        }

        ImGui.TextUnformatted("Recompiled Game Location");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(
            "Your disc is translated to C# and compiled here. Leave this empty to use the default " +
            "location for your account. Nothing from the disc is copied -- only the translated code " +
            "and the assembly built from it.");
        ImGui.Spacing();
        ImGui.TextDisabled("About 20 MB. Kept between runs, so the translation only happens once.");
        ImGui.Spacing();
        ImGui.TextDisabled($"Default: {DefaultDir}");
        ImGui.Spacing();

        float browseW = 80;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - browseW - spacing);
        ImGui.InputText("##buildpath", _pathBuf, (uint)BufSize);
        ImGui.SameLine();
        if (ImGui.Button("Browse...", new Vector2(browseW, 0))) OpenBrowser();

        if (_error.Length > 0)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.3f, 0.3f, 1f));
            ImGui.TextWrapped(_error);
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        float btnW = 120;
        float total = btnW * 2 + spacing;
        ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - total) * 0.5f + ImGui.GetStyle().WindowPadding.X);

        if (ImGui.Button("Confirm", new Vector2(btnW, 0))) TryConfirm();
        ImGui.SameLine();
        if (ImGui.Button("Use Default", new Vector2(btnW, 0))) UseDefault();

        ImGui.Spacing();
        ImGui.EndPopup();
    }

    void OpenBrowser()
    {
        try
        {
            var current = Encoding.UTF8.GetString(_pathBuf).TrimEnd('\0').Trim();
            string? start = current.Length > 0 && Directory.Exists(current)
                ? Path.GetFullPath(current)
                : RecompOne.Runtime.Storage.UserData.Dir;

            var result = Dialog.FolderPicker(start);
            if (result.IsOk && !string.IsNullOrWhiteSpace(result.Path)) SetPathBuf(result.Path);
            else if (result.IsError) _error = $"Dialog error: {result.ErrorMessage}";
        }
        catch (Exception ex)
        {
            _error = $"Failed to open the native picker: {ex.Message}";
        }
    }

    void SetPathBuf(string path)
    {
        _pathBuf = new byte[BufSize];
        var bytes = Encoding.UTF8.GetBytes(path);
        Array.Copy(bytes, _pathBuf, Math.Min(bytes.Length, BufSize - 1));
        _error = "";
    }

    void TryConfirm()
    {
        var path = Encoding.UTF8.GetString(_pathBuf).TrimEnd('\0').Trim();
        if (path.Length == 0) { UseDefault(); return; }

        // Create and probe rather than refuse -- a path for a folder that does not exist yet is an
        // intention, not a mistake. It is also the only chance to find out the location is
        // unwritable BEFORE a multi-minute recompile discovers it the hard way.
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".recompone-write-test");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            _error = $"Cannot write to this folder: {ex.Message}";
            return;
        }

        ConfigManager.Game.BuildPath = Path.GetFullPath(path);
        ConfigManager.SaveGame();
        Close();
    }

    void UseDefault()
    {
        ConfigManager.Game.BuildPath = "";
        ConfigManager.SaveGame();
        Close();
    }

    void Close()
    {
        Answered = true;
        IsOpen = false;
        ImGui.CloseCurrentPopup();
    }
}
