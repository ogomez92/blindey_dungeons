using System.Diagnostics;
using System.Text;

namespace DiceyA11y.Launcher;

/// <summary>
/// Keeps the bridge in place from the launcher's side.
///
/// A release carries the bridge in <c>bridge\lime.ndll</c> next to the launcher. Steam puts
/// its own lime.ndll back after every game update or file verification, which used to mean
/// running the install script again; now the next launch repairs it. The rules are the ones
/// of tools/install_bridge.ps1 and the installer: the two libraries are told apart by the
/// bridge's marker export, and nothing that could be the bridge, or is too small to be the
/// real Lime, is ever saved as lime_orig.dll. (An early install script got that wrong and
/// destroyed the original; see CLAUDE.md §7.)
/// </summary>
public static class BridgeInstaller
{
    private const string Marker = "diceya11y_bridge_marker";
    private const long MinOriginalSize = 3 * 1024 * 1024;

    public static string PayloadPath => Path.Combine(AppContext.BaseDirectory, "bridge", "lime.ndll");

    /// <summary>True when lime.ndll is the bridge and the original it forwards to is there.</summary>
    public static bool IsInstalled(string gameDir)
    {
        try
        {
            var dir = GamePaths.LimeDirectory(gameDir);
            return File.Exists(Path.Combine(dir, GamePaths.OriginalLime))
                   && IsBridge(Path.Combine(dir, "lime.ndll"));
        }
        catch { return false; }
    }

    /// <summary>Installs or refreshes the bridge if this copy of the mod carries one. Never throws.</summary>
    public static void Ensure(string gameDir, Action<string> log)
    {
        try
        {
            var dir = GamePaths.LimeDirectory(gameDir);
            var lime = Path.Combine(dir, "lime.ndll");
            var orig = Path.Combine(dir, GamePaths.OriginalLime);
            var payload = PayloadPath;

            // A developer build has no payload next to it; the dev scripts install the bridge.
            if (!File.Exists(payload) || !File.Exists(lime)) return;
            // The running game holds lime.ndll open.
            if (Process.GetProcessesByName(GamePaths.ProcessName).Length > 0) return;
            if (!IsBridge(payload))
            {
                log($"{payload} is not a bridge build (marker missing); leaving the game folder alone.");
                return;
            }

            if (IsBridge(lime))
            {
                if (!File.Exists(orig))
                {
                    log("lime.ndll is the bridge but lime_orig.dll is missing. Verify the game files in Steam, then start the mod again.");
                    return;
                }
                if (SameContent(payload, lime)) return;
                File.Copy(payload, lime, overwrite: true);
                Unquarantine(lime);
                log("Bridge updated to the one shipped with this version.");
                return;
            }

            // What is in place is the game's own Lime: first run, or Steam restored it.
            if (new FileInfo(lime).Length < MinOriginalSize)
            {
                log("lime.ndll is neither the bridge nor plausibly the real Lime (too small). Not touching it; verify the game files in Steam.");
                return;
            }
            File.Copy(lime, orig, overwrite: true);
            File.Copy(payload, lime, overwrite: true);
            Unquarantine(lime);
            log("Bridge installed; the game's lime.ndll is kept as lime_orig.dll.");
        }
        catch (Exception ex)
        {
            log("Could not install the bridge: " + ex.Message);
        }
    }

    /// <summary>
    /// A bridge that came out of a downloaded zip carries the quarantine flag, and Gatekeeper
    /// would then refuse to let the game load it. macOS only.
    /// </summary>
    private static void Unquarantine(string path)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try { removexattr(path, "com.apple.quarantine", 0); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("libc", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern int removexattr(string path, string name, int options);

    // Export names are plain ASCII in the file, so a byte search is enough.
    private static bool IsBridge(string path) =>
        File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.ASCII.GetBytes(Marker)) >= 0;

    private static bool SameContent(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length
        && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
}
