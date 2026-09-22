namespace DiceyA11y.Launcher;

/// <summary>
/// Where the game's files are, per platform. <c>GameDirectory</c> is the folder the player
/// knows as "the game": on Windows the one holding diceydungeons.exe, on macOS the one
/// holding diceydungeons.app (and, for GOG, the mods folder). On macOS the executable and
/// lime.ndll live inside the bundle, in Contents/MacOS; the bridge swap happens there.
/// </summary>
public static class GamePaths
{
    public const string ProcessName = "diceydungeons";
    public const string OriginalLime = "lime_orig.dll";   // the same name on both platforms

    private static string Bundle(string gameDir) =>
        gameDir.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? gameDir
            : Path.Combine(gameDir, "diceydungeons.app");

    /// <summary>The folder holding lime.ndll and lime_orig.dll.</summary>
    public static string LimeDirectory(string gameDir) =>
        OperatingSystem.IsMacOS() ? Path.Combine(Bundle(gameDir), "Contents", "MacOS") : gameDir;

    public static string Executable(string gameDir) =>
        OperatingSystem.IsMacOS()
            ? Path.Combine(LimeDirectory(gameDir), "diceydungeons")
            : Path.Combine(gameDir, "diceydungeons.exe");

    public static string ExecutableName => OperatingSystem.IsMacOS() ? "diceydungeons.app" : "diceydungeons.exe";

    public static bool HasGame(string dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Executable(dir));

    /// <summary>Places a Mac install usually is, besides next to the mod.</summary>
    public static IEnumerable<string> MacCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return "/Applications/Dicey Dungeons";
        yield return Path.Combine(home, "Applications", "Dicey Dungeons");
        yield return "/Applications";   // a bare diceydungeons.app dropped into Applications
        var steam = Path.Combine(home, "Library", "Application Support", "Steam");
        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(m.Groups[1].Value);
        }
        foreach (var library in libraries)
        {
            var dir = Path.Combine(library, "steamapps", "common", "Dicey Dungeons");
            yield return dir;
            // Steam's Mac depots sometimes hold the .app one level down, or are the .app.
            yield return Path.Combine(dir, "Dicey Dungeons.app");
        }
    }
}
