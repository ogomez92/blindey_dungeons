using DiceyA11y.Bridge;
using DiceyA11y.Core;
using DiceyA11y.Game;
using DiceyA11y.Game.Buffers;
using DiceyA11y.Input;
using DiceyA11y.Launcher;
using DiceyA11y.Protocol;
using DiceyA11y.Screens;
using DiceyA11y.Speech;
using Keys = DiceyA11y.Input.HotkeyHook.Keys;

namespace DiceyA11y;

/// <summary>
/// DiceyA11y launcher.
///
/// Dicey Dungeons is a native (hxcpp) build with a sandboxed hscript modding API,
/// so there is no in-process plugin to load. This program is the mod's other half:
/// it starts the game, reads the state the Polymod mod prints on stdout, speaks it
/// through Prism, and owns the review hotkeys.
/// </summary>
public static class Program
{
    private static EventLog _log = null!;
    private static ScreenReaderOutput _speech = null!;
    private static BufferManager _buffers = null!;
    private static Narrator _narrator = null!;
    private static HotkeyHook _hotkeys = null!;
    private static BridgeClient _bridge = null!;
    private static VirtualPad _pad = null!;
    private static ScreenMonitor _screens = null!;

    private static string _lastAnnouncement = "";
    private static string _runCharacter = "";
    private static int _floor = -1;
    private static int _turn = -1;
    private static bool _modSeen;

    public static int Main(string[] args)
    {
        Console.Title = "Dicey Dungeons Accessibility";

        if (args.Contains("--selftest")) return SelfTest();

        var configPath = ArgValue(args, "--config") ?? Config.DefaultPath;
        var config = Config.Load(configPath);
        OverrideFromArgs(config, args);

        var baseDir = AppContext.BaseDirectory;
        _log = new EventLog(Path.Combine(baseDir, "diceya11y.log"), echoToConsole: true);
        _log.Write($"DiceyA11y starting. Config: {configPath}");

        _buffers = new BufferManager(config.MaxEventBuffer);
        _speech = new ScreenReaderOutput(config, _log.Write);
        var spokenLog = OpenSpokenLog(baseDir);
        _speech.EventSpoken += text =>
        {
            _lastAnnouncement = text;
            _buffers.Get(BufferManager.Events).Add(text);
            try { spokenLog?.WriteLine($"{DateTime.Now:HH:mm:ss}  {text}"); } catch { }
        };

        var speechReady = _speech.Initialize(baseDir);
        if (!speechReady)
        {
            _log.Write("Continuing without speech. The game will still run, and the log will "
                       + "still record what would have been said.");
        }

        _narrator = new Narrator(_speech, _buffers, config);

        var launcher = new GameLauncher(config, _log.Write);
        // Steam puts its own lime.ndll back after a game update; repair that before looking.
        BridgeInstaller.Ensure(config.GameDirectory, _log.Write);
        if (!launcher.Validate(out var problem))
        {
            _log.Write("ERROR: " + problem);
            _speech.Say(problem, interrupt: true);
            Pause();
            return 2;
        }

        var parser = new ProtocolParser();
        parser.ModBooted += version =>
        {
            _modSeen = true;
            _log.Write($"Accessibility mod reported in (protocol v{version}).");
            if (!_screens.Connected) _speech.SayEvent("Accessibility mod active.");
        };
        parser.RunFact += (key, value) =>
        {
            switch (key)
            {
                case "player":
                    _runCharacter = value;
                    if (!_screens.Connected) _speech.SayEvent($"Run started as {value}.");
                    break;
                case "floor":
                    // Seed silently. The run-scope floor arrives while the dungeon is
                    // still being generated, so announcing it would double up with the
                    // first battle's own floor reading a moment later.
                    if (int.TryParse(value, out var f)) _floor = f;
                    break;
            }
        };
        parser.Context += (key, value) =>
        {
            if (key == "turn" && int.TryParse(value, out var t)) _turn = t;
        };
        parser.SnapshotReady += snapshot =>
        {
            // With the bridge in place battles are narrated from live game state, in the
            // game's language. The stdout snapshots remain only as the no-bridge fallback.
            if (_screens.Connected) return;
            if (snapshot.Floor >= 0 && snapshot.Floor != _floor)
            {
                var first = _floor < 0;
                _floor = snapshot.Floor;
                if (!first) _speech.SayEvent($"Floor {_floor}.");
            }
            try { _narrator.OnSnapshot(snapshot); }
            catch (Exception ex) { _log.Write($"Narrator error on '{snapshot.Event}': {ex}"); }
        };

        launcher.LineReceived += line =>
        {
            try
            {
                if (parser.Feed(line)) return;
                if (config.SpeakGameLog) _log.Write("game: " + line);
            }
            catch (Exception ex)
            {
                _log.Write($"Failed to parse game output: {ex.Message}");
            }
        };

        _bridge = new BridgeClient(_log.Write);
        _pad = new VirtualPad(_bridge, _log.Write);
        _screens = new ScreenMonitor(_bridge, _pad, _speech, _buffers, config, _log.Write);

        _hotkeys = new HotkeyHook(_log.Write) { ScopeToGameWindow = config.ScopeHotkeysToGame };
        BindHotkeys();
        BindGamepadKeys();
        _hotkeys.Start();

        var exitCode = 0;
        var done = new ManualResetEventSlim(false);
        launcher.Exited += code =>
        {
            exitCode = code;
            _log.Write($"Game exited with code {code}.");
            done.Set();
        };

        try
        {
            if (!launcher.AttachIfRunning()) launcher.Start(Path.Combine(baseDir, "diceya11y-game.log"));
            _hotkeys.GameProcessId = launcher.ProcessId;
            _speech.Say(Strings.T("mod.ready"));
            if (BridgeInstalled(config)) _screens.Start();
            else
            {
                _log.Write("Bridge not installed: lime.ndll in the game folder is not the mod's. Run the installer again (or tools/install_bridge.ps1).");
                _speech.Say(Strings.T("mod.bridge_missing"));
            }
            if (args.Contains("--dev")) new DevPipe(DevCommand, _log.Write).Start();
            done.Wait();
        }
        catch (Exception ex)
        {
            _log.Write("ERROR launching the game: " + ex.Message);
            _speech.Say("Could not start Dicey Dungeons. " + ex.Message, interrupt: true);
            exitCode = 3;
        }

        if (!_modSeen && !BridgeInstalled(config))
        {
            _log.Write("WARNING: the accessibility mod never reported in. Check that the game was "
                       + "launched with mod=" + config.ModIds + " and that mods/" + config.ModIds
                       + " exists. Nothing from combat would have been spoken.");
        }

        _hotkeys.Dispose();
        _screens.Dispose();
        _bridge.Dispose();
        launcher.Dispose();
        spokenLog?.Dispose();
        _speech.Dispose();
        _log.Write("DiceyA11y stopped.");
        _log.Dispose();
        return exitCode;
    }

    /// <summary>
    /// Checks the text pipeline against real card strings captured from the game.
    /// Run with --selftest; no game or screen reader needed.
    /// </summary>
    private static int SelfTest()
    {
        (string Input, string Expected)[] cases =
        {
            // Captured verbatim from diceya11y-game.log.
            ("Do [sword]<d6> damage",            "Do dice value damage"),
            ("Do [sword]5 damage",               "Do 5 damage"),
            ("Add [shield]3 shield",             "Add 3 shield"),
            ("Reroll a dice^[gray](Reuseable)",  "Reroll a dice, (Reuseable)"),
            ("[fire]3",                          "3 burn"),
            ("Sword+",                           "Sword plus"),
            ("",                                 ""),
        };

        (string Input, string Expected)[] hints =
        {
            ("Desde aquí puedes reorganizar tu equipo.", "Desde aquí puedes reorganizar tu equipo. Press R to open the inventory."),
            ("If you want, you can see what the enemy's moves are here.", "If you want, you can see what the enemy's moves are here. Press Tab to hear them."),
            ("It's another enemy!", "It's another enemy!"),
        };
        (string[] Lines, string Expected)[] joins =
        {
            (new[] { "¡RECIBES", "UN DADO EXTRA!" }, "¡RECIBES UN DADO EXTRA!"),
            (new[] { "Choose a reward!", "Sword" }, "Choose a reward! Sword"),
            (new[] { "A line", "continues here" }, "A line continues here"),
        };
        Console.WriteLine(TextCleanup.JoinSentences("An extra dice!", "Press Enter to take") == "An extra dice! Press Enter to take" ? "ok    JoinSentences after !" : "FAIL  JoinSentences after !");

        var failures = 0;
        foreach (var (input, expected) in hints)
        {
            var actual = TutorialHints.Apply(input);
            if (actual != expected) { failures++; Console.WriteLine($"FAIL  hint \"{input}\" -> \"{actual}\""); }
            else Console.WriteLine($"ok    hint \"{input}\"");
        }
        foreach (var (lines, expected) in joins)
        {
            var actual = TextCleanup.JoinLines(lines);
            if (actual != expected) { failures++; Console.WriteLine($"FAIL  join -> \"{actual}\", expected \"{expected}\""); }
            else Console.WriteLine($"ok    join \"{actual}\"");
        }
        foreach (var (input, expected) in cases)
        {
            var actual = TextCleanup.Clean(input);
            var ok = actual == expected;
            if (!ok) failures++;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  \"{input}\"");
            if (!ok)
            {
                Console.WriteLine($"        expected \"{expected}\"");
                Console.WriteLine($"        actual   \"{actual}\"");
            }
        }

        Console.WriteLine(failures == 0 ? "All text pipeline cases passed." : $"{failures} case(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------- hotkeys

    private static void BindHotkeys()
    {
        // F1 makes help the current list, so Ctrl+Up walks it one key at a time
        // instead of forcing the player to sit through the whole thing.
        _hotkeys.Bind(Keys.F1, () =>
        {
            var header = _buffers.ShowHelp(HelpEntries());
            _speech.Say(header + ". " + _buffers.Current.Current(), interrupt: true);
        });

        _hotkeys.Bind(Keys.F1, () => _speech.Say(string.Join(". ", HelpEntries()), interrupt: true), control: true);
        _hotkeys.Bind(Keys.F2, () => _speech.Say(string.IsNullOrEmpty(_lastAnnouncement)
            ? "Nothing announced yet"
            : _lastAnnouncement, interrupt: true));

        _hotkeys.Bind(Keys.F3, () => _speech.Say(_screens.DescribeFocus(), interrupt: true));
        _hotkeys.Bind(Keys.F4, () => _speech.Say(_screens.DescribeScreen(), interrupt: true));
        _hotkeys.Bind(Keys.F5, () => _speech.Say(!_screens.Connected && _narrator.InCombat
            ? TextCleanup.Join(_narrator.DescribePlayer(), _narrator.DescribeEnemy(), _narrator.DescribeDice())
            : _screens.DescribeContext(), interrupt: true));

        // Direct jumps: make that buffer current, then read where the cursor is.
        BindBuffer(Keys.F6, BufferManager.YourCards);
        BindBuffer(Keys.F7, BufferManager.EnemyCards);
        BindBuffer(Keys.F8, BufferManager.Statuses);
        BindBuffer(Keys.F9, BufferManager.Events);

        // Ctrl + arrows drive the review model: up/down within a buffer, left/right
        // between buffers. The game itself uses bare arrows, never Ctrl+arrows.
        _hotkeys.Bind(Keys.Up, () => _speech.Say(_buffers.Current.MoveDeeper(), interrupt: true), control: true);
        _hotkeys.Bind(Keys.Down, () => _speech.Say(_buffers.Current.MoveTowardTop(), interrupt: true), control: true);
        _hotkeys.Bind(Keys.Right, () => _speech.Say(_buffers.NextBuffer(), interrupt: true), control: true);
        _hotkeys.Bind(Keys.Left, () => _speech.Say(_buffers.PreviousBuffer(), interrupt: true), control: true);

        // The screen cursor: a keyboard stand-in for the mouse, for the few things the game
        // gives neither the gamepad nor the mod's own lists.
        _hotkeys.Bind(Keys.PageDown, () => _speech.Say(_screens.MoveScreenCursor(+1), interrupt: true), control: true);
        _hotkeys.Bind(Keys.PageUp, () => _speech.Say(_screens.MoveScreenCursor(-1), interrupt: true), control: true);
        _hotkeys.Bind(Keys.Enter, () => _speech.Say(_screens.ClickScreenCursor(), interrupt: true), control: true);

        // At-a-glance status, from anywhere in a battle (health also on the map).
        _hotkeys.Bind(Keys.Letter('D'), () => _speech.Say(_screens.DescribeStatus(StatusKind.Dice), interrupt: true), control: true);
        _hotkeys.Bind(Keys.Letter('H'), () => _speech.Say(_screens.DescribeStatus(StatusKind.Health), interrupt: true), control: true);
        _hotkeys.Bind(Keys.Letter('E'), () => _speech.Say(_screens.DescribeStatus(StatusKind.Enemies), interrupt: true), control: true);
        _hotkeys.Bind(Keys.F12, () => _speech.Say(WhereAmI(), interrupt: true));
    }

    /// <summary>
    /// The game has no keyboard navigation, only mouse and gamepad, so these keys are a
    /// gamepad: each is held and released on the mod's virtual pad exactly as pressed, which
    /// keeps the game's own hold-to-repeat. Swallowed by the hook, so nothing acts twice.
    /// </summary>
    private static void BindGamepadKeys()
    {
        void Map(int key, PadButton button, NavKey nav) => _hotkeys.BindHeld(key, () => NavDown(nav), () => NavUp(nav));
        Map(Keys.Up, PadButton.Up, NavKey.Up);
        Map(Keys.Down, PadButton.Down, NavKey.Down);
        Map(Keys.Left, PadButton.Left, NavKey.Left);
        Map(Keys.Right, PadButton.Right, NavKey.Right);
        Map(Keys.Enter, PadButton.A, NavKey.Confirm);
        Map(Keys.Space, PadButton.A, NavKey.Snap);       // A everywhere; on the map it recentres the cursor
        Map(Keys.Escape, PadButton.B, NavKey.Back);
        Map(Keys.Letter('E'), PadButton.X, NavKey.X);
        Map(Keys.Letter('R'), PadButton.Y, NavKey.Y);
        Map(Keys.Letter('Q'), PadButton.LeftShoulder, NavKey.LeftShoulder);
        Map(Keys.Letter('W'), PadButton.RightShoulder, NavKey.RightShoulder);
        Map(Keys.F11, PadButton.Start, NavKey.Start);
        Map(Keys.Backspace, PadButton.Back, NavKey.Select);
        // Keys only the mod's own screens use (the map's cursor); they never reach the pad.
        Map(Keys.Comma, PadButton.A, NavKey.PreviousItem);
        Map(Keys.Period, PadButton.A, NavKey.NextItem);
        Map(Keys.Letter('P'), PadButton.A, NavKey.Path);

        // In battle Tab / Shift+Tab move between groups (the enemy's cards, the status effects,
        // your actions); Up and Down move between the items of a group while the card list has
        // the arrows (CombatHandler.OnKey). Tab used to hold the right stick up, which shows the
        // enemy's cards on screen: every card was read in one go and could not be browsed, and
        // the release handler slept on the key queue, so the next arrow waited behind it.
        Map(Keys.Tab, PadButton.A, NavKey.GroupNext);
        _hotkeys.BindHeld(Keys.Tab, () => NavDown(NavKey.GroupPrevious), () => NavUp(NavKey.GroupPrevious), shift: true);

        // Left Control silences speech, as it does in the screen reader itself; the key still
        // reaches the game and still works as the Control of the review shortcuts.
        _hotkeys.BindObserved(Keys.LeftControl, () => _speech.StopSpeech());
    }

    private static readonly Dictionary<NavKey, PadButton> PadFor = new()
    {
        [NavKey.Up] = PadButton.Up, [NavKey.Down] = PadButton.Down, [NavKey.Left] = PadButton.Left, [NavKey.Right] = PadButton.Right,
        [NavKey.Confirm] = PadButton.A, [NavKey.Back] = PadButton.B, [NavKey.X] = PadButton.X, [NavKey.Y] = PadButton.Y,
        [NavKey.LeftShoulder] = PadButton.LeftShoulder, [NavKey.RightShoulder] = PadButton.RightShoulder,
        [NavKey.Start] = PadButton.Start, [NavKey.Select] = PadButton.Back, [NavKey.Snap] = PadButton.A,
    };

    // A screen may claim a key for itself (the map does); only keys that went to the pad
    // are released on it, so a claimed key never leaves a button half pressed.
    private static readonly HashSet<NavKey> OnPad = new();

    private static void NavDown(NavKey nav)
    {
        _screens.NoteInput();
        if (_screens.HandleKey(nav)) return;
        if (!PadFor.ContainsKey(nav)) return;
        _screens.WakeGamepadMode();
        lock (OnPad) OnPad.Add(nav);
        _pad.Down(PadFor[nav]);
    }

    private static void NavUp(NavKey nav)
    {
        bool sent;
        lock (OnPad) sent = OnPad.Remove(nav);
        if (sent) _pad.Up(PadFor[nav]);
    }

    private static string DevCommand(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[0] == "key" && Enum.TryParse<NavKey>(parts[1], true, out var nav))
        {
            NavDown(nav);
            Thread.Sleep(60);
            NavUp(nav);
            return "ok";
        }
        if (line == "peek")
        {
            NavDown(NavKey.GroupNext);
            Thread.Sleep(60);
            NavUp(NavKey.GroupNext);
            return "ok";
        }
        if (line == "stop") { _speech.StopSpeech(); return "ok"; }
        if (parts.Length == 2 && parts[0] == "cursor")
            return parts[1] switch
            {
                "next" => _screens.MoveScreenCursor(+1),
                "prev" => _screens.MoveScreenCursor(-1),
                "click" => _screens.ClickScreenCursor(),
                _ => "unknown cursor command",
            };
        if (parts.Length == 2 && parts[0] == "status" && Enum.TryParse<StatusKind>(parts[1], true, out var kind))
            return _screens.DescribeStatus(kind);
        if (parts.Length == 2 && parts[0] == "review")
            return parts[1] switch
            {
                "up" => _buffers.Current.MoveDeeper(),
                "down" => _buffers.Current.MoveTowardTop(),
                "next" => _buffers.NextBuffer(),
                "prev" => _buffers.PreviousBuffer(),
                "help" => _buffers.ShowHelp(HelpEntries()),
                "all" => _buffers.Current.Title + ": " + _buffers.Current.ReadAll(),
                _ => "unknown review command",
            };
        if (parts.Length == 2 && parts[0] == "buffer")
            return _buffers.Get(parts[1].Replace('_', ' ')).ReadAll();
        if (parts.Length == 2 && parts[0] == "read")
            return parts[1] switch
            {
                "focus" => _screens.DescribeFocus(),
                "screen" => _screens.DescribeScreen(),
                "context" => _screens.DescribeContext(),
                "help" => string.Join(" | ", HelpEntries()),
                _ => "unknown read",
            };
        return "unknown command";
    }

    private static bool BridgeInstalled(Config config)
        => BridgeInstaller.IsInstalled(config.GameDirectory);

    private static void BindBuffer(int key, string bufferName)
    {
        _hotkeys.Bind(key, () =>
        {
            var header = _buffers.Select(bufferName);
            var buffer = _buffers.Current;
            // An empty buffer already says so in its header; do not say it twice.
            _speech.Say(buffer.IsEmpty ? header : header + ". " + buffer.Current(), interrupt: true);
        });
        _hotkeys.Bind(key, () => _speech.Say(_buffers.Get(bufferName).ReadAll(), interrupt: true), control: true);
    }

    private static string WhereAmI()
    {
        var parts = new List<string>();
        parts.Add(_screens.ScreenName());
        if (!string.IsNullOrEmpty(_runCharacter)) parts.Add("Playing as " + _runCharacter);
        if (_floor >= 0) parts.Add("floor " + _floor);
        if (_turn >= 0 && _narrator.InCombat) parts.Add("turn " + _turn);
        parts.Add(_narrator.InCombat ? "in battle" : "not in battle");
        if (_narrator.InCombat) parts.Add(_narrator.DescribeEnemy());
        parts.Add("speech backend " + _speech.BackendName);
        return string.Join(". ", parts);
    }

    private static IEnumerable<string> HelpEntries()
    {
        var entries = new List<string> { _screens.ScreenName() };
        entries.AddRange(_screens.Help());
        return entries;
    }

    // ---------------------------------------------------------------- misc

    private static StreamWriter? OpenSpokenLog(string baseDir)
    {
        try { return new StreamWriter(Path.Combine(baseDir, "diceya11y-spoken.log"), append: false) { AutoFlush = true }; }
        catch { return null; }
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    /// <summary>Command line wins over the config file, for testing and shortcuts.</summary>
    private static void OverrideFromArgs(Config config, string[] args)
    {
        if (ArgValue(args, "--game") is { } game) config.GameDirectory = game;
        if (ArgValue(args, "--mods") is { } mods) config.ModIds = mods;
        if (ArgValue(args, "--extra") is { } extra) config.ExtraGameArgs = extra;
        if (args.Contains("--no-hotkey-scope")) config.ScopeHotkeysToGame = false;
    }

    private static void Pause()
    {
        Console.WriteLine("Press Enter to close.");
        try { Console.ReadLine(); } catch { }
    }
}
