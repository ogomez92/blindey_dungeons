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
    /// <summary>Started by the bridge (the game came up without us): the console is minimized, nobody reads it.</summary>
    private static bool _startedByGame;

    public static int Main(string[] args)
    {
        try { Console.Title = "Dicey Dungeons Accessibility"; } catch { /* no console: the Mac app */ }

        if (args.Contains("--selftest")) return SelfTest();
        _startedByGame = args.Contains("--started-by-game");

        var configPath = ArgValue(args, "--config") ?? Config.DefaultPath;
        var config = Config.Load(configPath);
        OverrideFromArgs(config, args);

        var baseDir = AppContext.BaseDirectory;
        var dataDir = Config.DataDirectory;   // baseDir on Windows; outside the app bundle on macOS
        _log = new EventLog(Path.Combine(dataDir, "diceya11y.log"), echoToConsole: true);
        _log.Write($"DiceyA11y starting. Config: {configPath}");
        if (_startedByGame) _log.Write("Started by the bridge: the game was launched without the mod (Steam's Play button without launch options?).");

        _buffers = new BufferManager(config.MaxEventBuffer);
        _speech = new ScreenReaderOutput(config, _log.Write);
        var spokenLog = OpenSpokenLog(dataDir);
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

        _hotkeys = new HotkeyHook(_log.Write)
        {
            ScopeToGameWindow = config.ScopeHotkeysToGame,
            NeedsPermission = () => _speech.Say(Strings.T("mod.keys_permission"), interrupt: true),
            Ready = () => _speech.Say(Strings.T("mod.keys_ready"), interrupt: true),
        };
        BindHotkeys();
        BindRateKeys(config, configPath);
        BindGamepadKeys();
        BindController(config);
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
            if (!launcher.AttachIfRunning()) launcher.Start(Path.Combine(dataDir, "diceya11y-game.log"));
            _hotkeys.GameProcessId = launcher.ProcessId;
            _speech.Say(Strings.T("mod.ready"));
            if (BridgeInstalled(config)) { _screens.Start(); _controller.Start(); }
            else
            {
                _log.Write("Bridge not installed: lime.ndll in the game folder is not the mod's. Run the installer again (or tools/install_bridge.ps1).");
                _speech.Say(Strings.T("mod.bridge_missing"));
            }
            if (args.Contains("--dev")) new DevPipe(DevCommand, _log.Write).Start();
            if (OperatingSystem.IsMacOS()) Mac.MacNative.WaitOnMainLoop(done);
            else done.Wait();
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
        _controller.Dispose();   // gives the controller back to the game while the bridge is still up
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

        foreach (var problem in Strings.CheckTables())
        {
            // An untranslated line falls back to English and is reported, not failed.
            if (!problem.EndsWith("not translated", StringComparison.Ordinal)) failures++;
            Console.WriteLine((problem.EndsWith("not translated", StringComparison.Ordinal) ? "note  " : "FAIL  ") + problem);
        }
        Console.WriteLine($"ok    language tables: {string.Join(" ", Strings.EmbeddedLanguages().OrderBy(x => x))}");

        Console.WriteLine(failures == 0 ? "All text pipeline cases passed." : $"{failures} case(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------- hotkeys

    // What the mod's own keys do; the keyboard and the controller (BindController) share them.
    private static void Speak(string text) => _speech.Say(text, interrupt: true);

    // Help becomes the current list, so the review keys walk it one line at a time instead of
    // forcing the player to sit through the whole thing.
    private static void SayHelp() => Speak(_buffers.ShowHelp(HelpEntries()) + ". " + _buffers.Current.Current());
    private static void SayLast() => Speak(string.IsNullOrEmpty(_lastAnnouncement) ? Strings.T("nothing.announced") : _lastAnnouncement);
    private static void SayFocus() => Speak(_screens.DescribeFocus());
    private static void SayScreen() => Speak(_screens.DescribeScreen());
    private static void SayContext() => Speak(!_screens.Connected && _narrator.InCombat
        ? TextCleanup.Join(_narrator.DescribePlayer(), _narrator.DescribeEnemy(), _narrator.DescribeDice())
        : _screens.DescribeContext());
    private static void SayStatus(StatusKind kind) => Speak(_screens.DescribeStatus(kind));
    private static void ReviewDeeper() => Speak(_buffers.Current.MoveDeeper());
    private static void ReviewTowardTop() => Speak(_buffers.Current.MoveTowardTop());
    private static void ReviewNextList() => Speak(_buffers.NextBuffer());
    private static void ReviewPreviousList() => Speak(_buffers.PreviousBuffer());
    private static void ScreenCursor(int direction) => Speak(_screens.MoveScreenCursor(direction));
    private static void ScreenCursorClick() => Speak(_screens.ClickScreenCursor());

    /// <summary>Make that list current, then read where its cursor is.</summary>
    private static void JumpToBuffer(string bufferName)
    {
        var header = _buffers.Select(bufferName);
        var buffer = _buffers.Current;
        // An empty buffer already says so in its header; do not say it twice.
        Speak(buffer.IsEmpty ? header : header + ". " + buffer.Current());
    }

    private static void BindHotkeys()
    {
        _hotkeys.Bind(Keys.F1, SayHelp);
        _hotkeys.Bind(Keys.F1, () => Speak(string.Join(". ", HelpEntries())), control: true);
        _hotkeys.Bind(Keys.F2, SayLast);
        _hotkeys.Bind(Keys.F3, SayFocus);
        _hotkeys.Bind(Keys.F4, SayScreen);
        _hotkeys.Bind(Keys.F5, SayContext);

        // Direct jumps: make that buffer current, then read where the cursor is.
        BindBuffer(Keys.F6, BufferManager.YourCards);
        BindBuffer(Keys.F7, BufferManager.EnemyCards);
        BindBuffer(Keys.F8, BufferManager.Statuses);
        BindBuffer(Keys.F9, BufferManager.Events);

        // Ctrl + arrows drive the review model: up/down within a buffer, left/right
        // between buffers. The game itself uses bare arrows, never Ctrl+arrows.
        _hotkeys.Bind(Keys.Up, ReviewDeeper, control: true);
        _hotkeys.Bind(Keys.Down, ReviewTowardTop, control: true);
        _hotkeys.Bind(Keys.Right, ReviewNextList, control: true);
        _hotkeys.Bind(Keys.Left, ReviewPreviousList, control: true);

        // The screen cursor: a keyboard stand-in for the mouse, for the few things the game
        // gives neither the gamepad nor the mod's own lists.
        _hotkeys.Bind(Keys.PageDown, () => ScreenCursor(+1), control: true);
        _hotkeys.Bind(Keys.PageUp, () => ScreenCursor(-1), control: true);
        _hotkeys.Bind(Keys.Enter, ScreenCursorClick, control: true);

        // At-a-glance status, from anywhere in a battle (health also on the map).
        _hotkeys.Bind(Keys.Letter('D'), () => SayStatus(StatusKind.Dice), control: true);
        _hotkeys.Bind(Keys.Letter('H'), () => SayStatus(StatusKind.Health), control: true);
        _hotkeys.Bind(Keys.Letter('E'), () => SayStatus(StatusKind.Enemies), control: true);
        _hotkeys.Bind(Keys.F12, () => Speak(WhereAmI()));
    }

    /// <summary>
    /// Speech rate, where the mod owns it (AVSpeech on macOS; a screen reader has its own).
    /// Saved to the config, so the next start keeps it.
    /// </summary>
    private static Func<int, string> _changeRate = _ => Strings.T("rate.fixed");

    private static void BindRateKeys(Config config, string configPath)
    {
        if (!_speech.OwnsRate) return;
        _changeRate = delta =>
        {
            if (_speech.ChangeRate(delta) is not { } rate) return Strings.T("rate.fixed");
            config.SpeechRate = rate;
            try { Config.Save(config, configPath); } catch (Exception ex) { _log.Write("Could not save the rate: " + ex.Message); }
            var said = Strings.T("speech.rate", rate);
            _speech.Say(said, interrupt: true);
            return said;
        };
        _hotkeys.Bind(Keys.Plus, () => _changeRate(+5), control: true);
        _hotkeys.Bind(Keys.Plus, () => _changeRate(+5), shift: true, control: true);   // Ctrl + the "+" itself
        _hotkeys.Bind(Keys.Minus, () => _changeRate(-5), control: true);
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

    /// <summary>
    /// The player's own controller (Input/ControllerInput takes it from the game). Its buttons go
    /// through NavDown / NavUp exactly as the keys standing in for them do, so the mod's card
    /// list, map cursor and battle groups work on it, and every press interrupts speech the way a
    /// key does. What the keyboard has on keys of its own:
    ///   d-pad or left stick = arrows; A B X Y LB RB Start as in the game; Back tapped = Back.
    ///   Triggers: battle groups (Tab, Shift+Tab); on the map the points of interest (period, comma).
    ///   Left stick click: map walk (P). Right stick click: map recentre (Space), elsewhere stop speech.
    ///   Right stick: review lists (Control + arrows).
    ///   Back held + A focus (F3), B last announcement (F2), X screen (F4), Y at a glance (F5),
    ///   Start help (F1), up health, down dice, right enemy health, left where am I (F12),
    ///   LB your cards (F6), RB enemy cards (F7), left trigger statuses (F8), right trigger
    ///   events (F9), right stick left/right/click the screen cursor (Control+Page Up/Down/Enter).
    /// The help lines that name these are the ".pad" twins in Strings.Pad.cs.
    /// </summary>
    private static void BindController(Config config)
    {
        _controller = new ControllerInput(_bridge, _pad, _log.Write) { Enabled = config.ControllerSupport };
        _controller.Changed += (control, down) => _hotkeys.RunOrdered(() => OnController(control, down));
        _controller.Connection += connected =>
        {
            if (!connected) { Strings.PadMode = false; return; }
            Strings.PadMode = true;
            _speech.Say(Strings.T("pad.connected"), interrupt: true);
        };
        _hotkeys.KeyboardUsed = () => Strings.PadMode = false;
    }

    private static ControllerInput _controller = null!;
    private static readonly Dictionary<PadControl, NavKey> PadNavHeld = new();
    private static bool _backHeld, _backChord;

    private static bool OnMap => _screens.Scene == "states.Dungeon";

    private static void OnController(PadControl control, bool down)
    {
        if (down) Strings.PadMode = true;

        // A press that went to the game is released there, whatever Back did meanwhile.
        if (!down && PadNavHeld.Remove(control, out var held)) { NavUp(held); return; }

        if (control == PadControl.Back)
        {
            // Back is a button of the game (flee, close) and the mod's shift key: tapped alone it
            // is Back, held with another control it reads.
            if (down) { _backHeld = true; _backChord = false; return; }
            _backHeld = false;
            if (!_backChord) { NavDown(NavKey.Select); Thread.Sleep(60); NavUp(NavKey.Select); }
            return;
        }
        if (!down) return;

        if (_backHeld)
        {
            _backChord = true;
            Action? read = control switch
            {
                PadControl.A => SayFocus,
                PadControl.B => SayLast,
                PadControl.X => SayScreen,
                PadControl.Y => SayContext,
                PadControl.Start => SayHelp,
                PadControl.DpadUp or PadControl.LeftStickUp => () => SayStatus(StatusKind.Health),
                PadControl.DpadDown or PadControl.LeftStickDown => () => SayStatus(StatusKind.Dice),
                PadControl.DpadRight or PadControl.LeftStickRight => () => SayStatus(StatusKind.Enemies),
                PadControl.DpadLeft or PadControl.LeftStickLeft => () => Speak(WhereAmI()),
                PadControl.LeftShoulder => () => JumpToBuffer(BufferManager.YourCards),
                PadControl.RightShoulder => () => JumpToBuffer(BufferManager.EnemyCards),
                PadControl.LeftTrigger => () => JumpToBuffer(BufferManager.Statuses),
                PadControl.RightTrigger => () => JumpToBuffer(BufferManager.Events),
                PadControl.RightStickLeft => () => ScreenCursor(-1),
                PadControl.RightStickRight => () => ScreenCursor(+1),
                PadControl.RightStickClick => ScreenCursorClick,
                PadControl.RightStickUp => ReviewDeeper,
                PadControl.RightStickDown => ReviewTowardTop,
                _ => null,
            };
            read?.Invoke();
            return;
        }

        NavKey? nav = control switch
        {
            PadControl.DpadUp or PadControl.LeftStickUp => NavKey.Up,
            PadControl.DpadDown or PadControl.LeftStickDown => NavKey.Down,
            PadControl.DpadLeft or PadControl.LeftStickLeft => NavKey.Left,
            PadControl.DpadRight or PadControl.LeftStickRight => NavKey.Right,
            PadControl.A => NavKey.Confirm,
            PadControl.B => NavKey.Back,
            PadControl.X => NavKey.X,
            PadControl.Y => NavKey.Y,
            PadControl.LeftShoulder => NavKey.LeftShoulder,
            PadControl.RightShoulder => NavKey.RightShoulder,
            PadControl.Start => NavKey.Start,
            PadControl.LeftTrigger => OnMap ? NavKey.PreviousItem : NavKey.GroupPrevious,
            PadControl.RightTrigger => OnMap ? NavKey.NextItem : NavKey.GroupNext,
            PadControl.LeftStickClick when OnMap => NavKey.Path,
            PadControl.RightStickClick when OnMap => NavKey.Snap,
            _ => null,
        };
        if (nav is { } key)
        {
            PadNavHeld[control] = key;
            NavDown(key);
            return;
        }
        switch (control)
        {
            case PadControl.RightStickUp: ReviewDeeper(); break;
            case PadControl.RightStickDown: ReviewTowardTop(); break;
            case PadControl.RightStickLeft: ReviewPreviousList(); break;
            case PadControl.RightStickRight: ReviewNextList(); break;
            case PadControl.RightStickClick: _speech.StopSpeech(); break;
        }
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
        // "press Right 40": a key on the keyboard hook's own path (its queue and ordering), held
        // that long, without waiting for the handler. Several in a row are a fast typist.
        if (parts.Length == 2 && parts[0] == "press")
        {
            var a = parts[1].Split(' ');
            var vk = a[0] switch { "Left" => Keys.Left, "Right" => Keys.Right, "Up" => Keys.Up, "Down" => Keys.Down, "Enter" => Keys.Enter, "Escape" => Keys.Escape, "Tab" => Keys.Tab, _ => 0 };
            if (vk == 0) return "unknown key";
            var hold = a.Length > 1 ? int.Parse(a[1]) : 40;
            _hotkeys.OnKey(vk, true, () => (false, false, false));
            Thread.Sleep(hold);
            _hotkeys.OnKey(vk, false, () => (false, false, false));
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
        if (parts.Length == 2 && parts[0] == "rate")
            return _changeRate(parts[1] == "up" ? +5 : -5);
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
        _hotkeys.Bind(key, () => JumpToBuffer(bufferName));
        _hotkeys.Bind(key, () => Speak(_buffers.Get(bufferName).ReadAll()), control: true);
    }

    private static string WhereAmI()
    {
        var parts = new List<string>();
        parts.Add(_screens.ScreenName());
        if (!string.IsNullOrEmpty(_runCharacter)) parts.Add(Strings.T("where.playing_as", _runCharacter));
        if (_floor >= 0) parts.Add(Strings.T("where.floor", _floor));
        if (_turn >= 0 && _narrator.InCombat) parts.Add(Strings.T("where.turn", _turn));
        parts.Add(Strings.T(_narrator.InCombat ? "where.in_battle" : "where.not_in_battle"));
        if (_narrator.InCombat) parts.Add(_narrator.DescribeEnemy());
        parts.Add(Strings.T("where.speech", _speech.BackendName));
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
        // The Mac app has no console: give the spoken error time to finish instead.
        if (Console.IsInputRedirected || _startedByGame)
        {
            if (OperatingSystem.IsMacOS()) Mac.MacNative.dm_main_loop_run(10);
            else Thread.Sleep(10000);
            return;
        }
        Console.WriteLine("Press Enter to close.");
        try { Console.ReadLine(); } catch { }
    }
}
