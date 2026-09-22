using System.Globalization;

namespace DiceyA11y.Core;

/// <summary>
/// Plain key=value settings file, written next to the executable on first run.
/// Deliberately not JSON: a blind user editing this in Notepad should not be able
/// to break it with a trailing comma.
/// </summary>
public sealed class Config
{
    public string GameDirectory { get; set; } = DetectGameDirectory();
    /// <summary>Polymod mods to load. The mod itself needs none: it works through the bridge.</summary>
    public string ModIds { get; set; } = "";
    public string ExtraGameArgs { get; set; } = "";

    public bool AllowInterrupt { get; set; } = true;   // only honoured for explicit interrupt requests
    public bool Braille { get; set; } = true;
    public bool LogSpokenLines { get; set; } = true;
    /// <summary>macOS only (AVSpeech): 0-100, 50 is the system's normal rate. Also on Ctrl+Plus / Ctrl+Minus.</summary>
    public int SpeechRate { get; set; } = 50;
    /// <summary>macOS only: a voice name ("Samantha"), identifier or language ("es-ES"); empty = system voice.</summary>
    public string MacVoice { get; set; } = "";
    public bool SpeakGameLog { get; set; } = false;    // echo raw game stdout (debugging)

    public bool AnnounceDice { get; set; } = true;
    public bool AnnounceEnemyEquipment { get; set; } = true;
    public bool AnnounceStatusChanges { get; set; } = true;
    public int MaxEventBuffer { get; set; } = 200;

    /// <summary>Hotkeys only act while Dicey Dungeons is the foreground window.</summary>
    public bool ScopeHotkeysToGame { get; set; } = true;

    public static string DefaultPath =>
        Path.Combine(DataDirectory, "DiceyA11y.cfg");

    /// <summary>
    /// Where the config and logs go: next to the executable on Windows; on macOS the
    /// executable is inside an app bundle, so ~/Library/Application Support/DiceyA11y.
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            if (!OperatingSystem.IsMacOS()) return AppContext.BaseDirectory;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                   "Library", "Application Support", "DiceyA11y");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static Config Load(string? path = null)
    {
        path ??= DefaultPath;
        var config = new Config();
        if (!File.Exists(path))
        {
            try { Save(config, path); } catch { /* read-only install dir is survivable */ }
            return config;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            var split = line.IndexOf('=');
            if (split <= 0) continue;

            var key = line[..split].Trim();
            var value = line[(split + 1)..].Trim();
            Apply(config, key, value);
        }
        // A game moved to another Steam library leaves a stale path behind.
        if (!HasGame(config.GameDirectory)) config.GameDirectory = DetectGameDirectory();
        return config;
    }

    private static bool HasGame(string dir) => Launcher.GamePaths.HasGame(dir);

    /// <summary>
    /// The installer puts the launcher in a folder inside the game folder, so the parent is
    /// the first guess; after that, every Steam library.
    /// </summary>
    private static string DetectGameDirectory()
    {
        if (!OperatingSystem.IsWindows()) return DetectMacGameDirectory();
        const string fallback = @"C:\Program Files (x86)\Steam\steamapps\common\Dicey Dungeons";
        try
        {
            var parent = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\', '/'));
            if (parent != null && HasGame(parent)) return parent;

            var steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                        ?? @"C:\Program Files (x86)\Steam";
            var libraries = new List<string> { steam };
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            foreach (var library in libraries)
            {
                var dir = Path.Combine(library, "steamapps", "common", "Dicey Dungeons");
                if (HasGame(dir)) return Path.GetFullPath(dir);
            }
        }
        catch { /* fall through to the usual place */ }
        return fallback;
    }

    /// <summary>
    /// The mod's folder (or its .app) usually sits inside the game folder, as on Windows;
    /// then the usual install places.
    /// </summary>
    private static string DetectMacGameDirectory()
    {
        try
        {
            var dir = AppContext.BaseDirectory.TrimEnd('/');
            for (var i = 0; i < 5 && dir != null; i++, dir = Path.GetDirectoryName(dir))
                if (HasGame(dir)) return dir;
            foreach (var candidate in Launcher.GamePaths.MacCandidates())
                if (HasGame(candidate)) return candidate;
        }
        catch { /* fall through */ }
        return "/Applications/Dicey Dungeons";
    }

    private static void Apply(Config c, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "gamedirectory": c.GameDirectory = value; break;
            case "modids": c.ModIds = value; break;
            case "extragameargs": c.ExtraGameArgs = value; break;
            case "allowinterrupt": c.AllowInterrupt = Bool(value, c.AllowInterrupt); break;
            case "braille": c.Braille = Bool(value, c.Braille); break;
            case "logspokenlines": c.LogSpokenLines = Bool(value, c.LogSpokenLines); break;
            case "speechrate": c.SpeechRate = Math.Clamp(Int(value, c.SpeechRate), 0, 100); break;
            case "macvoice": c.MacVoice = value; break;
            case "speakgamelog": c.SpeakGameLog = Bool(value, c.SpeakGameLog); break;
            case "announcedice": c.AnnounceDice = Bool(value, c.AnnounceDice); break;
            case "announceenemyequipment": c.AnnounceEnemyEquipment = Bool(value, c.AnnounceEnemyEquipment); break;
            case "announcestatuschanges": c.AnnounceStatusChanges = Bool(value, c.AnnounceStatusChanges); break;
            case "maxeventbuffer": c.MaxEventBuffer = Int(value, c.MaxEventBuffer); break;
            case "scopehotkeystogame": c.ScopeHotkeysToGame = Bool(value, c.ScopeHotkeysToGame); break;
        }
    }

    private static bool Bool(string v, bool fallback) =>
        v.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback
        };

    private static int Int(string v, int fallback) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

    public static void Save(Config c, string? path = null)
    {
        path ??= DefaultPath;
        var text = $"""
            # DiceyA11y settings. Edit with any text editor, then restart the launcher.
            # Lines starting with # are ignored.

            # Where Dicey Dungeons is installed.
            GameDirectory={c.GameDirectory}

            # Mods to load, comma separated and in load order. Keep diceya11y in the list.
            ModIds={c.ModIds}

            # Anything extra to put on the game's command line (advanced).
            ExtraGameArgs={c.ExtraGameArgs}

            # Speech
            AllowInterrupt={c.AllowInterrupt}
            Braille={c.Braille}
            LogSpokenLines={c.LogSpokenLines}

            # macOS speech (AVSpeech). Rate 0-100, 50 is normal; Ctrl+Plus and Ctrl+Minus
            # change it in game and save it here. MacVoice: a voice name such as Samantha,
            # or a language such as es-ES; empty uses the system voice.
            SpeechRate={c.SpeechRate}
            MacVoice={c.MacVoice}
            SpeakGameLog={c.SpeakGameLog}

            # What to announce automatically
            AnnounceDice={c.AnnounceDice}
            AnnounceEnemyEquipment={c.AnnounceEnemyEquipment}
            AnnounceStatusChanges={c.AnnounceStatusChanges}
            MaxEventBuffer={c.MaxEventBuffer}

            # Only let the review hotkeys fire while Dicey Dungeons is focused.
            ScopeHotkeysToGame={c.ScopeHotkeysToGame}
            """;
        File.WriteAllText(path, text);
    }
}
