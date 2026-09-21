using DiceyA11y.Bridge;
using DiceyA11y.Core;
using DiceyA11y.Game.Buffers;
using DiceyA11y.Input;
using DiceyA11y.Speech;

namespace DiceyA11y.Screens;

/// <summary>
/// Polls the running game through the bridge and keeps the right screen handler fed.
///
/// The active handler is chosen from game state on every poll (haxegon.Scene's current
/// scene), never from events: the game has no screen-change hook to patch, and state
/// cannot go stale the way an open/close tracker does.
/// </summary>
public sealed class ScreenMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(60);

    private const string Nav = "{ButtonGamepadNavigation}";
    private const string CurrentScene = "{haxegon.Scene}.scenelist[{haxegon.Scene}.currentscene]";

    private readonly BridgeClient _bridge;
    private readonly VirtualPad _pad;
    private readonly ScreenContext _ctx;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly List<ScreenHandler> _handlers;
    private readonly ScreenHandler _fallback = new();
    private readonly HashSet<string> _unhandledLogged = new();

    private ScreenHandler _active;
    private Thread? _thread;
    private volatile bool _running;
    private string _language = "";

    public ScreenMonitor(BridgeClient bridge, VirtualPad pad, ScreenReaderOutput speech, BufferManager buffers,
                         Config config, Action<string> log)
    {
        _bridge = bridge;
        _pad = pad;
        _log = log;
        _ctx = new ScreenContext
        {
            Bridge = bridge, Speech = speech, Buffers = buffers, Config = config, Log = log,
            Stage = new StageWatcher(TimeSpan.FromMilliseconds(320)),
        };
        _handlers = new List<ScreenHandler> { new DungeonHandler(pad), new CombatHandler(), new LevelUpHandler(), new BackpackHandler(), new PickupHandler(), new ShopHandler(), new BlacksmithHandler(), new BarterHandler(), new CharacterSelectHandler(), new ProgressEditorHandler(), new ModsHandler(), new SpellbookHandler(), new DeckHandler(), new ChallengeHandler(), new FinaleSelectHandler(), new RemixHandler(), new InventoryFullHandler() };
        _active = _fallback;
    }

    public bool Connected => _bridge.Connected;

    /// <summary>
    /// The game drops to mouse mode at launch and whenever the mouse moves, and in mouse mode
    /// the first press of some pad buttons (Start was measured) only switches the mode back.
    /// Telling the game "a gamepad was used" first makes every key count.
    /// </summary>
    public void WakeGamepadMode()
    {
        if (!_bridge.Connected || _ctx.State.GamepadMode) return;
        _bridge.Eval("{ControlMode}.notifyGamepadInput()");
    }
    public string Scene => _ctx.State.Scene;

    public void Start()
    {
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "DiceyA11y screen monitor" };
        _thread.Start();
    }

    /// <summary>Called by the input layer whenever a key is passed to the game.</summary>
    public void NoteInput() => _ctx.LastInput = DateTime.UtcNow;

    private void Run()
    {
        var announcedWaiting = false;
        while (_running)
        {
            try
            {
                if (!_bridge.Connected)
                {
                    if (!_bridge.Connect(1500))
                    {
                        if (!announcedWaiting) { _log("Waiting for the bridge pipe..."); announcedWaiting = true; }
                        Thread.Sleep(500);
                        continue;
                    }
                    announcedWaiting = false;
                    _log("Bridge connected.");
                    _pad.Reset();
                    lock (_gate) { _ctx.State = new GameState(); _ctx.ForgetTranslations(); _language = ""; }
                }

                lock (_gate) Poll();
            }
            catch (Exception ex)
            {
                _log("Screen monitor error: " + ex);
            }
            Thread.Sleep(PollInterval);
        }
    }

    private void Poll()
    {
        var head = _bridge.Batch(CurrentScene, "{ControlMode}.mode", "{MyLocale}.currentlanguage",
                                 Nav + ".activebutton.label", Nav + ".activebutton.x", Nav + ".activebutton.y");
        if (head[0].Kind != GameValueKind.Object) return;   // loading, or the game is not ticking
        _pad.Ensure();

        var language = head[2].AsString();
        if (language.Length > 0 && language != _language)
        {
            _language = language;
            Strings.SetLanguage(language);
            StageCleaner.Language = language;
            _ctx.ForgetTranslations();
            _log("Game language: " + language);
        }

        var buttons = ReadButtons();
        NavButton? focus = null;
        if (head[3].Kind == GameValueKind.String)
        {
            var raw = head[3].AsString();
            focus = new NavButton(raw, _ctx.Translate(raw), head[4].AsDouble(), head[5].AsDouble());
        }

        _ctx.Previous = _ctx.State;
        _ctx.State = new GameState
        {
            Scene = head[0].TypeName,
            GamepadMode = head[1].TypeName == "Gamepad",
            Focus = focus,
            Buttons = buttons,
        };

        var labels = new HashSet<string>(buttons.Select(b => StageCleaner.Key(b.Label)));
        _ctx.Stage.Update(_bridge.StageTexts(), labels);

        if (_ctx.State.Scene != _ctx.Previous.Scene)
        {
            _active.OnExit(_ctx);
            _active = _handlers.FirstOrDefault(h => h.Handles(_ctx.State.Scene)) ?? _fallback;
            if (ReferenceEquals(_active, _fallback) && _unhandledLogged.Add(_ctx.State.Scene))
                _log($"No dedicated handler for {_ctx.State.Scene}; using the generic one.");
            _log("Screen: " + _ctx.State.Scene);
            _active.OnEnter(_ctx);
        }

        _active.OnPoll(_ctx);
        RefreshTooltips();
    }

    // ------------------------------------------------------------ screen cursor (mouse fallback)

    private int _cursorIndex = -1;
    private string _cursorKey = "";

    private List<Paragraph> CursorItems()
        => _ctx.Stage.Current.Where(p => p.X >= 0 && p.Y >= 0).OrderBy(p => p.Y / 40).ThenBy(p => p.X).ToList();

    /// <summary>
    /// A last resort for what the game only gives to the mouse (the mod manager's upload pages
    /// and their popups were the cases found): every piece of text on screen can be stepped
    /// through and clicked. The click is delivered to OpenFL's stage exactly as Lime would
    /// (Stage.__onMouse: move, down, up), verified live on the title's buttons.
    /// </summary>
    public string MoveScreenCursor(int direction)
    {
        lock (_gate)
        {
            var items = CursorItems();
            if (items.Count == 0) return Strings.T("nothing.text");
            var current = items.FindIndex(p => p.Key + "@" + p.X + "," + p.Y == _cursorKey);
            _cursorIndex = current < 0 ? (direction > 0 ? 0 : items.Count - 1) : Math.Clamp(current + direction, 0, items.Count - 1);
            var item = items[_cursorIndex];
            _cursorKey = item.Key + "@" + item.X + "," + item.Y;
            return item.Text + ", " + Strings.T("of", _cursorIndex + 1, items.Count);
        }
    }

    public string ClickScreenCursor()
    {
        lock (_gate)
        {
            var item = CursorItems().FirstOrDefault(p => p.Key + "@" + p.X + "," + p.Y == _cursorKey);
            if (item == null) return Strings.T("cursor.none");

            const string Stage = "#0.parent.window.stage";
            var size = _bridge.Batch(Stage + ".stageWidth", Stage + ".stageHeight");
            if (!size[0].Exists) return Strings.T("cursor.none");
            // The 3840x2160 canvas is scaled to fit the window and centred.
            var scale = Math.Min(size[0].AsDouble() / 3840.0, size[1].AsDouble() / 2160.0);
            var x = (size[0].AsDouble() - 3840 * scale) / 2 + (item.X + (item.Width > 0 ? item.Width / 2.0 : 60)) * scale;
            var y = (size[1].AsDouble() - 2160 * scale) / 2 + (item.Y + (item.Height > 0 ? item.Height / 2.0 : 30)) * scale;
            var at = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{x:0.0}, {y:0.0}");

            _bridge.Eval($"{Stage}.__onMouse(\"mouseMove\", {at}, 0)");
            Thread.Sleep(120);
            _bridge.Eval($"{Stage}.__onMouse(\"mouseDown\", {at}, 0)");
            Thread.Sleep(90);
            _bridge.Eval($"{Stage}.__onMouse(\"mouseUp\", {at}, 0)");
            return Strings.T("cursor.clicked", item.Text);
        }
    }

    private DateTime _tooltipsReadAt;

    /// <summary>
    /// What a sighted player gets by hovering: displayobjects.TooltipManager.tooltips is a map
    /// of every tooltip the current screen registered (limit break, status icons, map prompts),
    /// each with its lines already translated. They go into a review list of their own.
    /// </summary>
    private void RefreshTooltips()
    {
        if (DateTime.UtcNow - _tooltipsReadAt < TimeSpan.FromSeconds(1.5)) return;
        _tooltipsReadAt = DateTime.UtcNow;
        const string Map = "{displayobjects.TooltipManager}.tooltips";
        var lines = new List<string>();
        foreach (var key in _bridge.Request("iter", Map + ".keys()").Split(BridgeClient.UnitSeparator).Select(k => new GameValue(k)).Where(k => k.Kind == GameValueKind.String))
        {
            var tip = Map + ".get(" + GameValue.Quote(key.AsString()) + ")";
            if (!_bridge.Eval(tip + ".canshow").AsBool(true)) continue;
            var text = string.Join(". ", _bridge.Dump(tip + ".translatedtext").Select(l => StageCleaner.Clean(l.Value.AsString())).Where(l => l.Length > 0));
            if (text.Length > 0 && !lines.Contains(text)) lines.Add(text);
        }
        _ctx.Buffers.Get(BufferManager.Tooltips).Set(lines);
    }

    /// <summary>
    /// The navigation buttons the game registered, in reading order. The game re-registers
    /// every button each frame and keeps the previous frame's records too, so each one
    /// appears twice; they are folded by label and position.
    /// </summary>
    private List<NavButton> ReadButtons()
    {
        var rows = _bridge.Pluck(Nav + ".buttonrecords", "label", "x", "y");
        var list = new List<NavButton>();
        if (rows == null) return list;
        var seen = new HashSet<string>();
        foreach (var r in rows)
        {
            if (r[0].Kind != GameValueKind.String) continue;
            var raw = r[0].AsString();
            double x = r[1].AsDouble(), y = r[2].AsDouble();
            if (!seen.Add($"{raw}@{x:0},{y:0}")) continue;
            // While a button slides in, its two records are a few pixels apart: still one button.
            if (list.Any(b => b.RawLabel == raw && Math.Abs(b.X - x) < 80 && Math.Abs(b.Y - y) < 80)) continue;
            list.Add(new NavButton(raw, _ctx.Translate(raw), x, y));
        }
        // Reading order. Buttons under cards of different heights sit at staggered heights
        // (1381 and 1525 seen) and are still one row; stacked menu buttons are 180 or more apart.
        list.Sort((a, b) => Math.Abs(a.Y - b.Y) > 150 ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        return list;
    }

    // ------------------------------------------------------------ on-demand reads (hotkeys)

    /// <summary>True when the active screen used the key itself and the game must not see it.</summary>
    public bool HandleKey(NavKey key)
    {
        if (!_bridge.Connected) return false;
        lock (_gate)
        {
            try { return _active.OnKey(_ctx, key); }
            catch (Exception ex) { _log("Key handling failed: " + ex.Message); return false; }
        }
    }

    public string DescribeFocus() { lock (_gate) return Guarded(() => _active.DescribeFocus(_ctx)); }
    public string DescribeScreen() { lock (_gate) return Guarded(() => _active.DescribeScreen(_ctx)); }
    public string DescribeContext() { lock (_gate) return Guarded(() => _active.DescribeContext(_ctx)); }
    public IEnumerable<string> Help() { lock (_gate) return _active.Help.ToList(); }

    public string ScreenName()
    {
        var key = "screen." + _ctx.State.Scene;
        return Strings.Has(key) ? Strings.T(key) : _ctx.State.Scene;
    }

    private string Guarded(Func<string> read)
    {
        if (!_bridge.Connected) return Strings.T("mod.waiting");
        try { return read(); }
        catch (Exception ex) { _log("Read failed: " + ex.Message); return Strings.T("nothing.text"); }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1000);
    }
}
