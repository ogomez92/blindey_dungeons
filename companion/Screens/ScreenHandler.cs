using DiceyA11y.Core;
using DiceyA11y.Input;

namespace DiceyA11y.Screens;

/// <summary>
/// Behaviour for one game screen. This base class is also the generic fallback, so a screen
/// nobody wrote a handler for is still named, read out and navigable:
///
///   on entry      the screen's name, then whatever text is on it, then the focused control
///   on focus move the focused control's label, then any text that changed because of it
///                 (descriptions and prices that follow the cursor)
///   otherwise     any text that newly appears (popups, tutorial hints, dialogue)
///
/// Control labels are left out of the automatic reading: the player meets them by moving
/// through them. F4 reads everything, labels included.
/// </summary>
public class ScreenHandler
{
    private bool _entryRead;
    private DateTime _enteredAt;
    private string _announcedFocus = "";
    private string _announcedLabel = "";
    private double _announcedX, _announcedY;
    private string _heldFocus = "";
    private DateTime _heldFocusSince;
    private string _toggleSeen = "";

    /// <summary>
    /// Checkboxes are drawn as a picture, so their state has to come from the game. Keyed by
    /// the button's internal label; the value is where the game keeps that setting.
    /// Only entries verified in a live run belong here.
    /// </summary>
    private static readonly Dictionary<string, string> ToggleState = new()
    {
        ["Relaxed mode"] = "{Game}.relaxedmode",
        ["Disable screen effects"] = "{Screen}.disablescreeneffects",
        ["Disable shaders"] = "{Screen}.disableshaders",
        ["Fullscreen"] = "{Screen}.fullscreen",
        ["I understand!"] = "{states.ProgressEditor}.accepteditorwarning",
    };

    /// <summary>The game labels every slider button just "slider"; its name and value are the text beside it.</summary>
    private const string SliderLabel = "slider";

    public virtual bool Handles(string scene) => true;

    /// <summary>True once the screen's opening text has been spoken (or it has waited long enough).</summary>
    protected bool EntryRead => _entryRead;

    /// <summary>Spoken when the screen opens. Empty to stay quiet (e.g. a cutscene).</summary>
    protected virtual string ScreenName(ScreenContext ctx)
    {
        var key = "screen." + ctx.State.Scene;
        return Strings.Has(key) ? Strings.T(key) : "";
    }

    /// <summary>Keeps or drops a paragraph from automatic reading.</summary>
    protected virtual bool ShouldSpeak(ScreenContext ctx, Paragraph paragraph) => true;

    public virtual void OnEnter(ScreenContext ctx)
    {
        _entryRead = false;
        _enteredAt = DateTime.UtcNow;
        _announcedFocus = "";
        _announcedLabel = "";
        ctx.Stage.Reset();
        ctx.SayEvent(ScreenName(ctx));
    }

    public virtual void OnExit(ScreenContext ctx) { }

    /// <summary>
    /// A navigation key went down. Return true to keep it from the game: the screen then
    /// owns that key (the map does this, because the game's own d-pad handling cannot reach
    /// every node). Returning false passes it to the virtual gamepad as usual.
    /// </summary>
    public virtual bool OnKey(ScreenContext ctx, NavKey key)
    {
        // Left and right on a slider change its value; the value is the answer, not the focus again.
        var onSlider = ctx.State.Focus?.RawLabel == SliderLabel && key is NavKey.Left or NavKey.Right;
        if (key is NavKey.Up or NavKey.Down or NavKey.Left or NavKey.Right && !onSlider)
        {
            _deadKeyPending = true;
            _deadKeyAt = DateTime.UtcNow;
            _deadKeyState = CursorState(ctx);
        }
        return false;
    }

    private bool _deadKeyPending;
    private DateTime _deadKeyAt;
    private string _deadKeyState = "";

    /// <summary>Whatever identifies where the game's cursor is; compared before and after an arrow key.</summary>
    protected virtual string CursorState(ScreenContext ctx) => ctx.State.FocusKey;

    /// <summary>What to repeat when an arrow key moved nothing.</summary>
    protected virtual string DescribeFocusBrief(ScreenContext ctx) => DescribeFocus(ctx);

    /// <summary>
    /// An arrow key that moves nothing (the edge of a row, a card two cells tall, a single
    /// dice) must not be answered with silence: a blind player cannot tell that from a key
    /// the mod failed to pass on. Where the cursor still is gets repeated instead.
    /// </summary>
    protected void AnswerDeadKey(ScreenContext ctx)
    {
        if (!_deadKeyPending || DateTime.UtcNow - _deadKeyAt < TimeSpan.FromMilliseconds(DeadKeyDelayMs)) return;
        _deadKeyPending = false;
        if (CursorState(ctx) != _deadKeyState) return;
        var text = DescribeFocusBrief(ctx);
        if (text.Length > 0 && text != Strings.T("nothing.focus")) ctx.Say(text, interrupt: true);
    }

    protected virtual int DeadKeyDelayMs => 450;

    public virtual void OnPoll(ScreenContext ctx)
    {
        AnswerDeadKey(ctx);
        var fresh = ctx.Stage.TakeNew();

        // Hold the first focus echo until the screen's own text has been read, so the order
        // is "name, contents, where you are". A screen still animating after a moment must
        // not leave the player without a focus announcement.
        if (!_entryRead && fresh == null && DateTime.UtcNow - _enteredAt < TimeSpan.FromSeconds(1.2)) return;

        var spokeText = fresh != null && SpeakParagraphs(ctx, fresh);
        if (!_entryRead) { _entryRead = true; OnEntryRead(ctx); }

        if (ctx.State.FocusKey != _announcedFocus)
        {
            // A panel that brings its own text and its own button: read the text first, so the
            // player hears what "Take" refers to before hearing "Take". Not for long, though -
            // text that never settles must not swallow the focus echo.
            if (ctx.Stage.Pending && !RecentInput(ctx))
            {
                if (_heldFocus != ctx.State.FocusKey) { _heldFocus = ctx.State.FocusKey; _heldFocusSince = DateTime.UtcNow; }
                if (DateTime.UtcNow - _heldFocusSince < TimeSpan.FromMilliseconds(900)) return;
            }
            if (SameButtonMoving(ctx)) return;
            _heldFocus = "";
            _announcedFocus = ctx.State.FocusKey;
            _toggleSeen = ReadToggle(ctx);
            _sliderValue = SliderValue(ctx);
            if (ctx.State.Focus is { } focus)
            {
                var moved = focus.RawLabel == _announcedLabel && !ButtonAt(ctx, _announcedLabel, _announcedX, _announcedY, except: focus);
                _announcedLabel = focus.RawLabel;
                _announcedX = focus.X;
                _announcedY = focus.Y;
                // The same button come to rest somewhere else (a popup that slid away with its
                // OK still focused) is not news; a twin of it is ("Buy, 2 of 3").
                if (moved) return;
                var text = DescribeFocus(ctx);
                // Text that was just announced comes first; the button belongs after it, so it
                // must not jump the queue the way an answer to a key press does.
                if (spokeText && !RecentInput(ctx)) ctx.SayAfterEvents(text); else ctx.Say(text, interrupt: RecentInput(ctx));
                ctx.FocusItem(FocusLines(ctx, text));
            }
            // Focus left every button (a battle card without one): coming back to the same
            // caption is a new arrival, not that button sliding somewhere (seen: a card's
            // Unshock button went silent the second time the cursor reached it).
            else _announcedLabel = "";
        }
        else
        {
            // A slider being moved: only the new value, at once, cutting off the previous one.
            var value = SliderValue(ctx);
            if (value.Length > 0 && value != _sliderValue) ctx.Say(value, interrupt: true);
            _sliderValue = value;

            // Same control, new state: the player just flipped it.
            var toggle = ReadToggle(ctx);
            if (toggle.Length > 0 && toggle != _toggleSeen) ctx.Say(toggle, interrupt: true);
            _toggleSeen = toggle;
        }
    }

    /// <summary>Once per visit, after the opening text and before the first focus echo.</summary>
    protected virtual void OnEntryRead(ScreenContext ctx) { }

    /// <summary>
    /// A focused button with the same caption as the last one announced, at another place,
    /// and still on the move between two polls: a popup sliding away with its OK, or the
    /// game's button gliding into position. Announced once it rests (see OnPoll), never
    /// per frame ("Ok, 1 of 2. Ok, 1 of 2. Ok" was heard after every fight).
    /// </summary>
    private bool SameButtonMoving(ScreenContext ctx)
    {
        var focus = ctx.State.Focus;
        var before = ctx.Previous.Focus;
        if (focus == null || before == null || focus.RawLabel != _announcedLabel || before.RawLabel != focus.RawLabel) return false;
        return Math.Abs(before.X - focus.X) > 1 || Math.Abs(before.Y - focus.Y) > 1;
    }

    /// <summary>Whether a button with this caption still stands near (x, y), other than the one focused now.</summary>
    private static bool ButtonAt(ScreenContext ctx, string label, double x, double y, NavButton except)
        => ctx.State.Buttons.Any(b => b.RawLabel == label && Math.Abs(b.X - x) < 80 && Math.Abs(b.Y - y) < 80
                                      && (Math.Abs(b.X - except.X) > 1 || Math.Abs(b.Y - except.Y) > 1));

    /// <summary>True when at least one paragraph was spoken.</summary>
    protected bool SpeakParagraphs(ScreenContext ctx, List<Paragraph> paragraphs)
    {
        var labels = new HashSet<string>(ctx.State.Buttons.Select(b => StageCleaner.Key(b.Label)));
        var spoke = false;
        foreach (var p in paragraphs)
        {
            if (labels.Contains(p.Key)) continue;
            if (ctx.State.Focus?.RawLabel == SliderLabel && IsSliderValue(p.Text)) continue;   // said with the slider
            if (!ShouldSpeak(ctx, p)) continue;
            ctx.SayEvent(TutorialHints.Apply(p.Text));
            spoke = true;
        }
        return spoke;
    }

    /// <summary>A focus change the player caused may cut off the previous focus echo.</summary>
    protected static bool RecentInput(ScreenContext ctx) => DateTime.UtcNow - ctx.LastInput < TimeSpan.FromMilliseconds(700);

    private static string ReadToggle(ScreenContext ctx)
    {
        var focus = ctx.State.Focus;
        if (focus == null || !ToggleState.TryGetValue(focus.RawLabel, out var expression)) return "";
        var v = ctx.Bridge.Eval(expression);
        return v.Kind != Bridge.GameValueKind.Bool ? "" : Strings.T(v.AsBool() ? "control.on" : "control.off");
    }

    private string _sliderValue = "";

    private static bool IsSliderValue(string text) => text.Length is > 0 and < 6 && text.All(c => char.IsDigit(c) || c == '%');

    /// <summary>The number drawn on the focused slider's row.</summary>
    private static string SliderValue(ScreenContext ctx) => SliderValueText(ctx)?.Text ?? "";

    private static Paragraph? SliderValueText(ScreenContext ctx)
    {
        var focus = ctx.State.Focus;
        if (focus == null || focus.RawLabel != SliderLabel) return null;
        return ctx.Stage.Current.Where(p => Math.Abs(p.Y - focus.Y) < 120 && IsSliderValue(p.Text))
                  .OrderBy(p => Math.Abs(p.Y - focus.Y)).FirstOrDefault();
    }

    /// <summary>
    /// A slider's caption is drawn on the same line as its number, to the left of it (measured:
    /// caption y 684, number y 687, the slider itself y 636). Going by the number's line keeps
    /// out what merely shares the row - the cards of the battle the pause menu lies over.
    /// </summary>
    private static string SliderCaption(ScreenContext ctx)
    {
        var value = SliderValueText(ctx);
        if (value == null) return "";
        var labels = new HashSet<string>(ctx.State.Buttons.Select(b => StageCleaner.Key(b.Label)));
        return string.Join(" ", ctx.Stage.Current.Where(p => Math.Abs(p.Y - value.Y) < 20 && p.X < value.X && !IsSliderValue(p.Text) && !labels.Contains(p.Key))
                                   .Select(p => p.Text.TrimEnd(':', ' ')));
    }

    /// <summary>What the item list holds for the focused control. One line unless a screen knows better.</summary>
    protected virtual IEnumerable<string> FocusLines(ScreenContext ctx, string spoken) => new[] { spoken };

    /// <summary>Text on the same row as the focused control that is not itself a control's label.</summary>
    private static string RowText(ScreenContext ctx, NavButton focus)
    {
        var labels = new HashSet<string>(ctx.State.Buttons.Select(b => StageCleaner.Key(b.Label)));
        // A slider's caption is to its left or over it. The options open over the title screen,
        // whose own menu is still drawn further right on the same rows.
        var row = ctx.Stage.Current.Where(p => p.Y > focus.Y - 120 && p.Y < focus.Y + 70 && p.X < focus.X + 300 && !labels.Contains(p.Key)).Select(p => p.Text);
        return string.Join(" ", row);
    }

    /// <summary>F3: the focused control, in full.</summary>
    public virtual string DescribeFocus(ScreenContext ctx)
    {
        var focus = ctx.State.Focus;
        if (focus == null) return Strings.T("nothing.focus");
        if (focus.RawLabel == SliderLabel)
        {
            var value = SliderValue(ctx);
            var caption = SliderCaption(ctx);
            var row = caption.Length > 0 ? caption + ": " + value : RowText(ctx, focus);
            return (row.Length > 0 ? row + ", " : "") + Strings.T("control.slider");
        }
        var state = ReadToggle(ctx);
        if (state.Length > 0) return focus.Label + ", " + Strings.T("control.checkbox") + ", " + state;
        var buttons = ctx.State.Buttons;
        var index = buttons.ToList().FindIndex(b => b.RawLabel == focus.RawLabel && Math.Abs(b.X - focus.X) < 1 && Math.Abs(b.Y - focus.Y) < 1);
        var label = index >= 0 && buttons.Count > 1
            ? focus.Label + ", " + Strings.T("of", index + 1, buttons.Count)
            : PressEnterTo(focus.Label);
        var column = ReadColumnAboveTwins ? ColumnAbove(ctx, focus) : "";
        return column.Length > 0 ? column + ". " + label : label;
    }

    /// <summary>
    /// The only button on a screen is what Enter does there ("Take", "Continue", "Open"):
    /// said as the action it is, so the player knows the key as well as the word.
    /// </summary>
    protected static string PressEnterTo(string label)
    {
        var word = label.Trim();
        if (word.Length == 0) return label;
        // "OK" is not a verb: "OK, press Enter" rather than "press Enter to ok".
        return word.Length <= 3 || !word.Any(char.IsLetter)
            ? Strings.T("focus.press_enter_short", word)
            : Strings.T("focus.press_enter", word.ToLowerInvariant());
    }

    /// <summary>Screens that describe what stands over their buttons themselves turn this off.</summary>
    protected virtual bool ReadColumnAboveTwins => true;

    /// <summary>
    /// Several buttons with the same caption in a row ("Discard", "Discard", "Discard") each
    /// belong to what is drawn over them - a card, a name, a picture with a caption. Without
    /// it the caption says nothing, so the text in the button's column is read first. The
    /// column is half the distance to the nearest button of the same name on either side.
    /// </summary>
    private static string ColumnAbove(ScreenContext ctx, NavButton focus)
    {
        var twins = ctx.State.Buttons.Where(b => b.RawLabel == focus.RawLabel && Math.Abs(b.X - focus.X) > 100 && Math.Abs(b.Y - focus.Y) < 150).ToList();
        if (twins.Count == 0) return "";
        var half = twins.Min(b => Math.Abs(b.X - focus.X)) / 2;
        var labels = new HashSet<string>(ctx.State.Buttons.Select(b => StageCleaner.Key(b.Label)));
        var lines = ctx.Stage.Current.Where(p => p.Y > 300 && p.Y < focus.Y - 20 && p.X > focus.X - half && p.X < focus.X + half - 60 && !labels.Contains(p.Key))
                       .Select(p => p.Text.TrimEnd('.'));
        return string.Join(". ", lines);
    }

    /// <summary>F4: everything on screen, in reading order.</summary>
    public virtual string DescribeScreen(ScreenContext ctx)
    {
        var parts = new List<string>();
        var name = ScreenName(ctx);
        if (name.Length > 0) parts.Add(name);
        parts.AddRange(ctx.Stage.Current.Select(p => p.Text));
        return parts.Count == 0 ? Strings.T("nothing.text") : string.Join(". ", parts);
    }

    /// <summary>F5: whatever a sighted player would take in at a glance on this screen.</summary>
    public virtual string DescribeContext(ScreenContext ctx) => DescribeScreen(ctx);

    /// <summary>Ctrl+D / Ctrl+H / Ctrl+E. Health is known wherever there is a run; the rest is the battle's.</summary>
    public virtual string DescribeStatus(ScreenContext ctx, StatusKind kind)
    {
        if (kind != StatusKind.Health) return Strings.T("status.battle_only");
        var v = ctx.Bridge.Batch("{Game}.player.hp", "{Game}.player.maxhp");
        return v[0].Exists && v[1].AsInt() > 0 ? Strings.T("battle.health", v[0].AsInt(), v[1].AsInt()) : Strings.T("status.no_run");
    }

    public virtual IEnumerable<string> Help => new[]
    {
        Strings.T("help.nav"), Strings.T("help.read"), Strings.T("help.pad"),
        Strings.T("help.buffers"), Strings.T("help.cursor"), Strings.T("help.where"),
    }.Concat(OperatingSystem.IsMacOS() ? new[] { Strings.T("help.rate") } : Array.Empty<string>());
}
