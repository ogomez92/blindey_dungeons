using System.Text.RegularExpressions;
using DiceyA11y.Bridge;
using DiceyA11y.Core;
using DiceyA11y.Input;

namespace DiceyA11y.Screens;

/// <summary>
/// Battle. What happens in a fight (damage, statuses, turns) is narrated from the combat
/// hooks by Game/Narrator; this handler covers the other half, which is knowing what the
/// cursor is on so the player can actually play.
///
/// The game's gamepad model, from states.Combat's statics (names verified against the
/// published elements/Equipment.hx, behaviour observed live):
///
///   gamepad_selectedequipment   the highlighted card; the d-pad moves between cards
///   A                           gamepad_dicemode = true: now choosing a dice for that card
///   gamepad_hoverdice           the highlighted dice while in dice mode
///   A again                     plays that dice on the card;  B backs out of dice mode
///   X                           ends the turn
///
/// A card's name and rules text are read off the stage from inside the card's rectangle,
/// so they are in the player's language and show live values ("Do 5 damage" once a dice
/// is hovered). The dice slots are pictures, so those come from Equipment.slots.
/// </summary>
public sealed partial class CombatHandler : ScreenHandler
{
    private const string Combat = "{states.Combat}";
    private const string Selected = Combat + ".gamepad_selectedequipment";
    private const string Hover = Combat + ".gamepad_hoverdice";
    private const string LimitPrompt = "{displayobjects.LimitBreakPrompt}.showing";

    /// <summary>A number, or the counters of several status icons read as one line ("1 1 1").</summary>
    [GeneratedRegex(@"^[-+]?\d+(\s+[-+]?\d+)*$")]
    private static partial Regex BareNumber();

    /// <summary>The HUD's "2 dice" beside each name, after cleaning, in either supported language.</summary>
    [GeneratedRegex(@"^\d+ (dice|dados)$", RegexOptions.IgnoreCase)]
    private static partial Regex DiceCountText();

    [GeneratedRegex(@"^\d+\s*/\s*\d+\s")]
    private static partial Regex HealthLead();

    [GeneratedRegex(@"^\d+\s*/\s*\d+$")]
    private static partial Regex HealthFraction();

    private sealed record Card(string Name, double X, double Y, double Width, double Height, bool Ready,
                               bool Available, int Shocked, int CountdownLeft, int Countdown, int NeedsTotal, bool Weakened);

    private sealed record Rect(double X, double Y, double Width, double Height)
    {
        // Text is placed by its top left corner, and cards are stacked a few pixels apart:
        // any slack at the bottom edge lets in the title of the card below.
        public bool Contains(Paragraph p) => p.X >= X - 20 && p.X <= X + Width + 20 && p.Y >= Y - 20 && p.Y < Y + Height - 5;
    }

    private string _focusKey = "";
    private string _pendingKey = "";
    private bool _previewing, _previewSpoken;
    private bool _limitPrompt, _fleePrompt, _fleeing;
    private DateTime _previewSince;
    private readonly HashSet<string> _spokenThisBattle = new();
    private readonly BattleNarrator _narrator = new();
    private readonly VirtualPad _pad;

    public CombatHandler(VirtualPad pad) => _pad = pad;
    private string _phase = "";
    private List<Rect> _cardRects = new();

    public override bool Handles(string scene) => scene == "states.Combat";

    public override void OnEnter(ScreenContext ctx)
    {
        _focusKey = "";
        _pendingKey = "";
        _previewing = _previewSpoken = false;
        _limitPrompt = _fleePrompt = _fleeing = false;
        _reunionStolen = ctx.Bridge.Eval("{Reunion}.thiefcard_selected") is { Exists: true } kept ? kept.AsString() : "";
        _spellSlot = -2;
        _wanted = null;
        _refused.Clear();
        _choicesSaid = "";
        _dialog = null;
        _paused = false;
        _pauseLabels.Clear();
        _pauseClosedAt = DateTime.MinValue;
        _enemyCard = -1;
        _phase = "";
        _spokenThisBattle.Clear();
        _narrator.Reset();
        ctx.Buffers.ClearBattleBuffers();
        base.OnEnter(ctx);
    }

    public override void OnPoll(ScreenContext ctx)
    {
        _cardRects = ReadCardRects(ctx);
        var paused = PauseMenuOpen(ctx);
        if (_paused && !paused) _pauseClosedAt = DateTime.UtcNow;
        _paused = paused;
        base.OnPoll(ctx);
        _narrator.Poll(ctx);

        // The pause menu (F11) is a screen of its own laid over the battle. The card cursor is
        // frozen beneath it, and must stay silent while the player moves through the options.
        if (_paused)
        {
            foreach (var b in ctx.State.Buttons) _pauseLabels.Add(b.RawLabel);
            _focusKey = "";
            return;
        }

        AnnounceEnemyPreview(ctx);
        AnnounceChoices(ctx);
        AnnounceReunionSteal(ctx);
        if (AnnouncePrompts(ctx)) return;   // a prompt is up: the card cursor is frozen beneath it
        if (AnnounceSpellSlot(ctx)) return;

        // Identity uses where a card or dice is going (tx/ty, homex/homey), not where it is:
        // they glide into place, and a position that changes every frame is not a new focus.
        var v = ctx.Bridge.Batch(Combat + ".state", Combat + ".gamepad_dicemode",
                                 Selected + ".name", Selected + ".tx", Selected + ".ty",
                                 Hover + ".basevalue", Hover + ".modifier", Hover + ".homex", Hover + ".homey", Hover + ".id",
                                 Selected + ".skillcard");
        if (v[0].IsError) return;

        var phase = v[0].AsString();
        var playerActing = phase.StartsWith("playerturn", StringComparison.Ordinal) && !_narrator.Ended;
        _phase = phase;
        if (!playerActing) { _focusKey = ""; _wanted = null; _refused.Clear(); return; }
        if (!v[1].AsBool() && AwaitingListMove(ctx, v[2], v[3], v[4])) return;

        var diceMode = v[1].AsBool();
        var key = diceMode
            ? $"dice:{v[5].Raw}:{v[6].Raw}:{v[7].AsDouble():0},{v[8].AsDouble():0}:{v[9].Raw}"
            : $"card:{v[2].Raw}:{v[3].AsDouble():0},{v[4].AsDouble():0}";

        // Described on every poll, not only when the cursor moves: a read made in one poll is
        // sent ahead with the next poll's (BridgeClient.OpenScope), so by the time the cursor
        // does move everything needed to speak is already here. This is what removed the
        // second of silence after each arrow key.
        var spoken = diceMode ? DiceParts(ctx, out _) : CardParts(ctx, Selected);
        var lines = diceMode ? spoken.Concat(CardParts(ctx, Selected)).ToList() : spoken;
        var (listed, listedAt) = diceMode ? (new List<ListCard>(), -1) : ListCards(ctx);
        if (key == _focusKey) return;

        // A panel whose button has the game's focus (gadget, Calculate) was just read as a button.
        // On a plain card a focused button is either a choice laid over the battle or the state
        // of a moment ago (the cursor just left a panel): the card is still news once it is gone.
        if (ctx.State.Focus != null && !diceMode) { if (v[10].AsString().Length > 0) _focusKey = key; return; }

        // After a key press a card is spoken at once. A dice waits one more poll, because the
        // card's live text ("Do 5 damage") is redrawn a frame after the hover moves; anything
        // that moved on its own waits for the stage to settle.
        // E ends the turn, but the phase stays "allocatedice" for two more seconds (measured)
        // while the cards slide away; nothing under the cursor is news during that.
        if (DateTime.UtcNow < _quietUntil) { _pendingKey = key; return; }

        // Enter on a dice plays it, and the card then redraws itself over a few frames (both
        // the old and the new text were once read together): only arrows and Escape leave a
        // card at rest, and only they get the immediate echo in card mode.
        var fromKey = DateTime.UtcNow - _navKeyAt < TimeSpan.FromMilliseconds(700) && (diceMode || _navKey != NavKey.Confirm);
        var ready = fromKey && !diceMode || key == _pendingKey && (fromKey || ctx.Stage.Settled);
        if (!ready) { _pendingKey = key; return; }
        _focusKey = key;

        var text = string.Join(diceMode ? ". " : ", ", spoken);
        var position = ListPosition(listed.Count, listedAt);
        if (position.Length > 0) text = text.TrimEnd('.', ' ') + ", " + position;
        // Cards are dealt again each turn and land in new places: the same card under a cursor
        // nobody moved is not news, however its position changed.
        var repeat = !fromKey && string.Equals(text, _focusSaid, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - _focusSaidAt < TimeSpan.FromSeconds(4);
        if (text.Length > 0 && !repeat) ctx.Say(text, interrupt: fromKey);
        _focusSaid = text;
        _focusSaidAt = DateTime.UtcNow;
        ctx.FocusItem(lines);
    }

    /// <summary>
    /// Whether the game has a speech or tutorial box up. Boxes are displayobjects.Textbox
    /// (field "dismissed"): the tutorial keeps its own as TextboxCommand in Tutorial.textboxes
    /// (actualtextbox), everything else lives in the Art.textboxmap string map. Both were
    /// found empty in a battle without speech (so labels such as a burning dice's "Costs 2
    /// health" do not count). HYPOTHESIS: not yet observed while a box was actually open.
    /// </summary>
    private static bool TextboxOpen(ScreenContext ctx)
    {
        var tutorial = ctx.Bridge.Pluck("{Tutorial}.textboxes", "actualtextbox.dismissed");
        if (tutorial != null && tutorial.Any(r => r[0].Exists && !r[0].AsBool())) return true;
        var keys = ctx.Bridge.Request("iter", "{Art}.textboxmap.keys()");
        return keys.Length > 0 && !keys.StartsWith('!');
    }

    private bool _paused;
    private Paragraph? _dialog;
    private DateTime _navKeyAt, _focusSaidAt, _quietUntil;
    private NavKey _navKey;
    private string _focusSaid = "";

    public override bool OnKey(ScreenContext ctx, NavKey key)
    {
        // Only keys that move the cursor earn an immediate echo. E (end turn) sends the cards
        // sliding away, and each frame of that would otherwise count as a new focus.
        if (key is NavKey.Up or NavKey.Down or NavKey.Left or NavKey.Right or NavKey.Confirm or NavKey.Back) { _navKeyAt = DateTime.UtcNow; _navKey = key; }
        if (key == NavKey.X && ctx.State.Focus == null && !_limitPrompt && !_fleePrompt) _quietUntil = DateTime.UtcNow.AddSeconds(2.5);
        if (key is NavKey.Up or NavKey.Down or NavKey.Left or NavKey.Right && MoveCardCursor(ctx, key is NavKey.Left or NavKey.Up ? -1 : 1)) return true;
        if (key is NavKey.EnemyNext or NavKey.EnemyPrevious) { BrowseEnemyCards(ctx, key == NavKey.EnemyNext ? 1 : -1); return true; }
        return base.OnKey(ctx, key);
    }

    private int _enemyCard = -1;

    /// <summary>
    /// Tab: the enemy's cards one at a time, with their place in the list; Shift+Tab goes back.
    /// Read from the card objects, never from the stage: off the preview the enemy's cards
    /// are parked over the player's, and the text inside their rectangles is the player's.
    /// </summary>
    private void BrowseEnemyCards(ScreenContext ctx, int step)
    {
        var rows = ctx.Bridge.Pluck("{Game}.monster.equipment", "name", "ready", "availablethisturn", "remainingcountdown", "countdown", "namemodifier") ?? new();
        var cards = rows.Select((r, i) => (Row: r, Index: i)).Where(c => c.Row[0].Exists).ToList();
        if (cards.Count == 0) { ctx.Say(Strings.T("cards.enemy_none"), interrupt: true); return; }
        _enemyCard = _enemyCard < 0 ? (step > 0 ? 0 : cards.Count - 1) : (_enemyCard + step + cards.Count) % cards.Count;
        var (row, index) = cards[_enemyCard];
        var card = $"{{Game}}.monster.equipment[{index}]";
        var parts = new List<string> { CardText.Full(ctx, card) };
        if (row[4].AsInt() > 0) parts.Add(row[3].AsInt() > 0 ? Strings.T("slot.COUNTDOWN", row[3].AsInt()) : Strings.T("card.ready"));
        if (!row[1].AsBool(true) || !row[2].AsBool(true)) parts.Add(Strings.T("card.unavailable"));
        var text = TextCleanup.JoinSentences(parts.ToArray()) + ", " + Strings.T("of", _enemyCard + 1, cards.Count);
        ctx.Say(_enemyCard == 0 && step > 0 ? Strings.T("cards.enemy_one", text) : text, interrupt: true);
        ctx.FocusItem(parts);
    }

    /// <summary>
    /// The game lays cards out in columns and stacks two small ones in the same column
    /// (Matchstick over Bump), so its d-pad needs Down to reach the lower one: a card a blind
    /// player only finds by chance. The mod owns the card cursor instead. The cards are one
    /// list, left to right and top to bottom within a column; Left/Up is the previous card,
    /// Right/Down the next, and the ends do not wrap.
    /// False leaves the key to the game: dice mode, a panel's buttons, prompts, dialogue.
    /// Verified per character: Warrior, Thief (stolen card), Robot (Calculate, jackpot),
    /// Inventor (gadget), Witch (four spell slots and the book), Jester (hand).
    /// </summary>
    private bool MoveCardCursor(ScreenContext ctx, int step)
    {
        if (_paused || _limitPrompt || _fleePrompt || _previewing || _spellSlot != -2 || _narrator.Ended) return false;
        var v = ctx.Bridge.Batch(Combat + ".state", Combat + ".gamepad_dicemode", Combat + ".gamepad_buttonmode", Selected + ".skillcard");
        if (!v[0].AsString().StartsWith("playerturn", StringComparison.Ordinal) || v[1].AsBool() || v[2].AsBool()) return false;

        // A panel card under the cursor focuses its button before A is pressed (seen: the Robot's
        // Calculate), and the game's own arrows then wrap from the panel to some other card. One
        // button is just the panel's face and the list goes on; several buttons (jackpot prizes,
        // the six request dice) are a choice, and the arrows belong to it.
        if (ctx.State.Focus is { } focused
            && (v[3].AsString().Length == 0 || focused.RawLabel == "dicebutton" || ctx.State.Buttons.Select(b => b.RawLabel).Distinct().Count() > 1)) return false;
        if (TextboxOpen(ctx)) return false;

        var (cards, at) = ListCards(ctx);
        if (at < 0) return false;   // the cursor is on something this list does not know: the game's own keys still work

        var to = at + step;
        if (to < 0 || to >= cards.Count)
        {
            // The end of the list must not be silent (see AnswerDeadKey): say where the cursor still is.
            var here = TextCleanup.Join(DescribeFocusBrief(ctx).TrimEnd('.', ' '), ListPosition(cards.Count, at));
            if (here.Length > 0) ctx.Say(here, interrupt: true);
            return true;
        }

        // The move itself is the game's: one d-pad press, so its click, its highlight and its
        // own cursor state all follow (writing the static alone moved the cursor in silence,
        // which the player noticed at once). Within a column that is Up or Down. Across columns
        // it is Left or Right, and gamepad_preferredrow = the card's displayrow says which of
        // two stacked cards the game lands on (verified: Left from a tall card onto the lower
        // of two, the Witch's lower slots). OnPoll checks where the cursor ended up
        // (AwaitingListMove).
        var (from, target) = (cards[at], cards[to]);
        var sameColumn = Math.Abs(from.X - target.X) < 1;
        var button = sameColumn ? (target.Y > from.Y ? PadButton.Down : PadButton.Up) : (target.X > from.X ? PadButton.Right : PadButton.Left);
        if (!ctx.State.GamepadMode) ctx.Bridge.Eval("{ControlMode}.notifyGamepadInput()");
        _wanted = new WantedCard(target.Index, target.Name, target.X, target.Y, DateTime.UtcNow, button, sameColumn ? -1 : target.Row);
        Press(ctx, _wanted);
        return true;   // the poll speaks the new card: its key changed and an arrow was just pressed
    }

    /// <summary>One press of the d-pad button that leads to the wanted card, with the row hint the game picks stacked cards by.</summary>
    private void Press(ScreenContext ctx, WantedCard want)
    {
        if (want.PreferredRow >= 0) ctx.Bridge.SetField(Combat, "gamepad_preferredrow", want.PreferredRow.ToString());
        _pad.Down(want.Button);
        _ = Task.Run(async () => { await Task.Delay(60); _pad.Up(want.Button); });
    }

    private sealed record ListCard(int Index, string Name, double X, double Y, int Row, string Skill);

    /// <summary>
    /// What the cursor can land on, in list order, and where the cursor is in it (-1: nowhere
    /// known). Cards still in play and on screen: a used card is not ready, and the Jester's
    /// played cards stay in Fighter.equipment parked off screen. The Jester's deck panel is not
    /// in equipment at all (the game's own arrows wrap past it).
    /// Reunion's skill panels ARE in the list (an earlier note said the cursor never lands on
    /// them, and a first version left them out): the Thief's holds a playable enemy card, the
    /// Witch's takes a dice ("requires 5: 10 damage"), and the raw d-pad lands on both. The
    /// Warrior's refuses the cursor while his Sword is in play, and takes it by itself once the
    /// Sword is used: what the game refuses is learned by trying (AwaitingListMove, _refused).
    /// </summary>
    private (List<ListCard> Cards, int At) ListCards(ScreenContext ctx)
    {
        var rows = ctx.Bridge.Pluck("{Game}.player.equipment", "name", "tx", "ty", "ready", "show", "skillcard", "displayrow");
        var v = ctx.Bridge.Batch(Selected + ".name", Selected + ".tx", Selected + ".ty");
        if (rows == null || !v[0].Exists) return (new List<ListCard>(), -1);
        var cards = rows.Select((r, i) => (Row: r, Index: i))
                        .Where(c => c.Row[0].Exists && c.Row[3].AsBool(true) && c.Row[4].AsBool(true)
                                    && c.Row[1].AsDouble() > -200 && c.Row[1].AsDouble() < 3840 && c.Row[2].AsDouble() > -200 && c.Row[2].AsDouble() < 2100)
                        .Select(c => new ListCard(c.Index, c.Row[0].AsString(), c.Row[1].AsDouble(), c.Row[2].AsDouble(), c.Row[6].AsInt(), c.Row[5].AsString()))
                        .OrderBy(c => Math.Round(c.X)).ThenBy(c => c.Y).ToList();
        bool Selected_(ListCard c) => c.Name == v[0].AsString() && Math.Abs(c.X - v[1].AsDouble()) < 1 && Math.Abs(c.Y - v[2].AsDouble()) < 1;
        // Known to refuse (so the count is right from the first key); anything else is learned.
        // Either way a card the game itself put the cursor on is in the list.
        cards.RemoveAll(c => (_refused.Contains(c.Index) || c.Skill == "warriorreunion") && !Selected_(c));
        return (cards, cards.FindIndex(Selected_));
    }

    /// <summary>Panels that only show Fighter.stolencard: the Thief's third card, and his Reunion panel.</summary>
    private static bool ShowsStolenCard(string skillcard) => skillcard is "stolencard" or "thiefreunion";

    /// <summary>"2 of 5": tells two copies of a card apart and says when the list has ended.</summary>
    private static string ListPosition(int count, int at) => at >= 0 && count > 1 ? Strings.T("of", at + 1, count) : "";

    private sealed record WantedCard(int Index, string Name, double X, double Y, DateTime At, PadButton Button, int PreferredRow, int Attempt = 1);

    private WantedCard? _wanted;

    /// <summary>
    /// After a list move: true while the game's cursor has not reached the card the player
    /// asked for. A press the game let pass (it happens in the moment after a card is played,
    /// while the used card slides away: Combat Roll was lost from the list that way in the
    /// second play-test) is made once more. A card the game will not give the cursor to even
    /// then (seen: Reunion's Warrior panel while he still has a card to play - the game even
    /// undoes a direct write of the cursor every frame, which is why there is no such write
    /// here) leaves the list for the rest of the turn, and the player hears where the cursor
    /// still is instead of silence.
    /// </summary>
    private bool AwaitingListMove(ScreenContext ctx, GameValue name, GameValue tx, GameValue ty)
    {
        if (_wanted is not { } want) return false;
        if (name.AsString() == want.Name && Math.Abs(tx.AsDouble() - want.X) < 1 && Math.Abs(ty.AsDouble() - want.Y) < 1) { _wanted = null; return false; }
        if (DateTime.UtcNow - want.At < TimeSpan.FromMilliseconds(want.Attempt == 1 ? 300 : 400)) return true;
        if (want.Attempt == 1)
        {
            ctx.Log($"Card list: the cursor is still on {name.AsString()}, not {want.Name}; pressing again.");
            _wanted = want with { At = DateTime.UtcNow, Attempt = 2 };
            Press(ctx, _wanted);
            return true;
        }
        _wanted = null;
        _refused.Add(want.Index);
        ctx.Log($"Card list: the game kept the cursor on {name.AsString()} and off {want.Name} twice; left out of the list this turn.");
        var (cards, at) = ListCards(ctx);
        var here = TextCleanup.Join(DescribeFocusBrief(ctx).TrimEnd('.', ' '), ListPosition(cards.Count, at));
        if (here.Length > 0) ctx.Say(here, interrupt: true);
        return false;
    }

    /// <summary>Equipment indexes the game refused to move the cursor to this turn.</summary>
    private readonly HashSet<int> _refused = new();

    /// <summary>
    /// Automatic reading skips what the cursor and the narrator already cover: card faces,
    /// floating damage numbers and the health readouts. Dialogue and tutorial prompts stay.
    /// </summary>
    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p)
    {
        if (_paused) return true;
        if (_narrator.IsStatusLabel(p.Text)) return false;   // "Burn 1" floating over a fighter: the narrator said it
        if (BareNumber().IsMatch(p.Text) || HealthFraction().IsMatch(p.Text)) return false;
        if (HealthLead().IsMatch(p.Text)) return false;   // "15/24 FURY": the health bar and its label
        if (_cardRects.Any(r => r.Contains(p))) return false;
        if (InFleePanel(ctx, p)) return false;
        if (_narrator.FighterNames.Any(n => StageCleaner.Key(n) == p.Key)) return false;
        if (DiceCountText().IsMatch(p.Text)) return false;

        // The rest of the HUD (fighter names, dice counts, the end-turn and enemy-moves
        // prompts) is redrawn every turn. Once per battle is orientation; every turn is noise.
        if (!_spokenThisBattle.Add(p.Key)) return false;
        // Speech and tutorial lines are sentences; the HUD's own labels are all capitals.
        if (p.Text.Any(char.IsLower)) _dialog = p;
        return true;
    }

    protected override string CursorState(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch(Combat + ".gamepad_dicemode", Selected + ".name", Selected + ".tx", Selected + ".ty",
                                 Hover + ".basevalue", Hover + ".x", Combat + ".state", Combat + ".gamepad_selectedslot");
        return ctx.State.FocusKey + "|" + string.Join("|", v.Select(x => x.Raw));
    }

    protected override int DeadKeyDelayMs => 300;

    protected override bool ReadColumnAboveTwins => false;

    protected override string DescribeFocusBrief(ScreenContext ctx)
    {
        // A menu or panel with the game's focus answers for itself, never the card under it.
        if (ctx.State.Focus != null || _paused) return DescribeFocus(ctx);
        // While somebody is talking the arrows move nothing: say the line again, not a card.
        // (Before the dice are thrown there is no card to act on, so a line on screen is the answer.)
        if (_dialog != null && ctx.Stage.Current.Any(p => p.Key == _dialog.Key)
            && (!_phase.EndsWith("allocatedice", StringComparison.Ordinal) || TextboxOpen(ctx))) return _dialog.Text;
        if (!_phase.StartsWith("playerturn", StringComparison.Ordinal)) return "";
        if (_limitPrompt || _fleePrompt) return DescribeFocus(ctx);
        return ctx.Bridge.Eval(Combat + ".gamepad_dicemode").AsBool() ? string.Join(". ", DiceParts(ctx, out _)) : DescribeSelectedCard(ctx, brief: true);
    }

    /// <summary>
    /// Battle panels with real buttons (the Robot's Calculate and jackpot prizes, the Witch's
    /// spell slots...) print what a button does right under its caption, inside the area of
    /// a card, where the automatic reading is muted. Read it with the button.
    /// </summary>
    private static string ButtonDetail(ScreenContext ctx, NavButton focus)
    {
        var labels = new HashSet<string>(ctx.State.Buttons.Select(b => StageCleaner.Key(b.Label)));
        var next = ctx.State.Buttons.Where(b => b.Y > focus.Y + 60 && Math.Abs(b.X - focus.X) < 500).Select(b => b.Y).DefaultIfEmpty(focus.Y + 330).Min();
        var lines = ctx.Stage.Current.Where(p => p.Y > focus.Y + 20 && p.Y < next - 60 && Math.Abs(p.X - focus.X) < 500 && !labels.Contains(p.Key))
                       .Select(p => p.Text);
        return string.Join(". ", lines);
    }

    /// <summary>The pause menu (F11) is a layer over the battle with buttons of its own.</summary>
    private static bool PauseMenuOpen(ScreenContext ctx) => ctx.Bridge.Eval("{states.Settings}.screens.length").AsInt() > 0;

    private string _choicesSaid = "";
    private readonly HashSet<string> _pauseLabels = new();
    private DateTime _pauseClosedAt = DateTime.MinValue;

    /// <summary>
    /// A set of buttons appearing mid-battle is a choice the player has to notice: the Robot's
    /// jackpot. Not the pause menu's own buttons, which the game keeps registered for a
    /// moment after Continue closes it (heard as "Choose a prize: Continue, Options...").
    /// </summary>
    private void AnnounceChoices(ScreenContext ctx)
    {
        if (PauseMenuOpen(ctx) || DateTime.UtcNow - _pauseClosedAt < TimeSpan.FromSeconds(1.5)) return;
        var choices = ctx.State.Buttons.Where(b => b.RawLabel != "Calculate" && !_pauseLabels.Contains(b.RawLabel)).GroupBy(b => b.RawLabel).Select(g => g.First()).ToList();
        var key = string.Join("|", choices.Select(b => b.RawLabel));
        if (key == _choicesSaid) return;
        _choicesSaid = key;
        if (choices.Count >= 2)
            ctx.SayEvent(Strings.T("battle.choices", string.Join(", ", choices.Select(b => b.Label))));
    }

    public override string DescribeFocus(ScreenContext ctx)
    {
        // The Robot's "request" panel (episode You Choose, You Lose): six buttons showing dice
        // faces, all labelled "dicebutton", laid out 1 2 / 3 4 / 5 6 (observed).
        if (ctx.State.Focus is { RawLabel: "dicebutton" } wanted)
        {
            var faces = ctx.State.Buttons.Where(b => b.RawLabel == "dicebutton").ToList();
            var value = faces.FindIndex(b => Math.Abs(b.X - wanted.X) < 1 && Math.Abs(b.Y - wanted.Y) < 1) + 1;
            return Strings.T("robot.request", value);
        }

        // The finale's fighter chooser: a "Choose" button under each contestant's name and health.
        // (Only the focused one is registered as a button; the caption is drawn under every fighter.)
        if (ctx.State.Focus is { } choose && !PauseMenuOpen(ctx)
            && ctx.Stage.Current.Count(p => p.Key == StageCleaner.Key(choose.Label)) > 1)
        {
            var labels = new HashSet<string>(ctx.State.Buttons.Select(b => StageCleaner.Key(b.Label)));
            var column = ctx.Stage.Current.Where(p => Math.Abs(p.X - choose.X) < 330 && p.Y > 500 && p.Y < 1500 && !labels.Contains(p.Key)).Select(p => p.Text);
            return TextCleanup.JoinSentences(string.Join(", ", column), base.DescribeFocus(ctx));
        }

        if (ctx.State.Focus != null)
            return PauseMenuOpen(ctx) ? base.DescribeFocus(ctx) : TextCleanup.JoinSentences(base.DescribeFocus(ctx), ButtonDetail(ctx, ctx.State.Focus));
        if (_limitPrompt) return DescribeLimitPrompt(ctx);
        if (_spellSlot != -2) return Strings.T("witch.choose_slot", DescribeSpellSlot(ctx, _spellSlot));
        if (_fleePrompt) return Strings.T("flee.prompt");
        var diceMode = ctx.Bridge.Eval(Combat + ".gamepad_dicemode").AsBool();
        var text = diceMode
            ? string.Join(". ", DiceParts(ctx, out _)) + ". " + DescribeSelectedCard(ctx, brief: false)
            : DescribeSelectedCard(ctx, brief: false);
        return text.Trim(' ', '.').Length == 0 ? Strings.T("nothing.focus") : text;
    }

    // ------------------------------------------------------------------ prompts

    /// <summary>
    /// Two small panels open over the cards and take A / B for themselves: the limit break
    /// (right shoulder, once the bar is full - displayobjects.LimitBreakPrompt.showing) and
    /// fleeing (states.Combat.flee_showprompt). Their text sits inside a card's rectangle, so
    /// the automatic reading would drop it; they are announced from game state instead.
    /// </summary>
    private bool AnnouncePrompts(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch(LimitPrompt, Combat + ".flee_showprompt");
        var limit = v[0].AsBool();
        var flee = v[1].AsBool();

        // A flee attempt stops twice on a message ("X tries to flee!", "X fled...") until A is pressed.
        var fleeing = ctx.Bridge.Eval(Combat + ".attempttoflee").AsBool();
        if (fleeing && !_fleeing) ctx.SayEvent(Strings.T("flee.trying"));
        _fleeing = fleeing;

        if (limit && !_limitPrompt) ctx.Say(DescribeLimitPrompt(ctx), interrupt: true);
        if (flee && !_fleePrompt) ctx.Say(Strings.T("flee.prompt"), interrupt: true);
        if ((!limit && _limitPrompt) || (!flee && _fleePrompt)) _focusKey = "";   // say where the cursor is again

        _limitPrompt = limit;
        _fleePrompt = flee;
        return limit || flee;
    }

    /// <summary>The flee panel's own two lines; the announcement above already covers them.</summary>
    private bool InFleePanel(ScreenContext ctx, Paragraph p)
    {
        if (!_fleePrompt && !ctx.Bridge.Eval(Combat + ".flee_showprompt").AsBool()) return false;
        var v = ctx.Bridge.Batch(Combat + ".flee_backing.x", Combat + ".flee_backing.y", Combat + ".flee_backing.width", Combat + ".flee_backing.height");
        return v[0].Exists && new Rect(v[0].AsDouble(), v[1].AsDouble(), v[2].AsDouble(), v[3].AsDouble()).Contains(p);
    }

    /// <summary>The prompt also opens while the bar is still filling; then it only informs ("ready in 8 HP").</summary>
    private static string DescribeLimitPrompt(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch("{Game}.player.limitvalue", "{Game}.player.limitmax");
        var missing = v[1].AsInt() - v[0].AsInt();
        return missing > 0
            ? Strings.T("limit.prompt_charging", DescribeLimitBreak(ctx), missing)
            : Strings.T("limit.prompt", DescribeLimitBreak(ctx));
    }

    public static string DescribeLimitBreak(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch("{Game}.player.limitbreak.name", "{Game}.player.limitbreak.description");
        if (!v[0].Exists) return "";
        return TextCleanup.Join(ctx.Translate(v[0].AsString()), ctx.Translate(v[1].AsString()));
    }

    // ------------------------------------------------------------------ cards

    /// <summary>
    /// The Y button swaps the view to the enemy's cards (states.Combat.previewingequipment).
    /// Card faces are normally left to the cursor, but here there is no cursor: reading them
    /// is the whole point of the view.
    /// </summary>
    private void AnnounceEnemyPreview(ScreenContext ctx)
    {
        var previewing = ctx.Bridge.Eval(Combat + ".previewingequipment").AsBool();
        if (!previewing) { if (_previewing) _focusKey = ""; _previewing = false; _previewSpoken = false; return; }
        if (!_previewing) { _previewing = true; _previewSince = DateTime.UtcNow; }
        if (_previewSpoken) return;

        // The enemy's cards slide in over the player's. Until the first one's own name is
        // inside its rectangle, the text found there still belongs to the player's cards.
        var rows = ctx.Bridge.Pluck("{Game}.monster.equipment", "name", "x", "y", "width", "height");
        if (rows == null) return;
        var timedOut = DateTime.UtcNow - _previewSince > TimeSpan.FromSeconds(2);
        if (rows.Count > 0 && !timedOut)
        {
            var first = rows[0];
            var rect = new Rect(first[1].AsDouble(), first[2].AsDouble(), first[3].AsDouble(), first[4].AsDouble());
            var name = StageCleaner.Key(ctx.Translate(first[0].AsString()));
            if (!ctx.Stage.Current.Any(p => rect.Contains(p) && p.Key.StartsWith(name, StringComparison.Ordinal))) return;
        }

        _previewSpoken = true;
        var cards = Enumerable.Range(0, rows.Count).Select(i => DescribeCard(ctx, $"{{Game}}.monster.equipment[{i}]", brief: false))
                              .Where(t => t.Length > 0).ToList();
        ctx.Say(cards.Count == 0 ? Strings.T("cards.enemy_none") : Strings.T("cards.enemy", string.Join(". ", cards)), interrupt: true);
    }

    private static List<Rect> ReadCardRects(ScreenContext ctx)
    {
        var rects = new List<Rect>();
        foreach (var owner in new[] { "{Game}.player.equipment", "{Game}.monster.equipment" })
        {
            var rows = ctx.Bridge.Pluck(owner, "x", "y", "width", "height");
            if (rows == null) continue;
            rects.AddRange(rows.Where(r => r[0].Exists).Select(r => new Rect(r[0].AsDouble(), r[1].AsDouble(), r[2].AsDouble(), r[3].AsDouble())));
        }
        return rects;
    }

    private string DescribeSelectedCard(ScreenContext ctx, bool brief) => DescribeCard(ctx, Selected, brief);

    private static string DescribeCard(ScreenContext ctx, string Selected, bool brief)
        => string.Join(brief ? ", " : ". ", CardParts(ctx, Selected));

    /// <summary>A card piece by piece: name and rules text, what it takes, the state it is in.</summary>
    private static List<string> CardParts(ScreenContext ctx, string Selected)
    {
        if (ctx.Bridge.Eval(Selected + ".skillcard").AsString() == "witch") return new List<string> { DescribeSpellbook(ctx) };

        // The Thief's third card is a placeholder (skillcard "stolencard"); what it shows and
        // plays is Fighter.stolencard, this turn's copy of one of the enemy's cards.
        var stolen = false;
        var holder = ctx.Bridge.Eval(Selected + ".skillcard").AsString();
        if (ShowsStolenCard(holder))
        {
            if (!ctx.Bridge.Eval("{Game}.player.stolencard.name").Exists) return new List<string> { Strings.T("card.stolen_none") };
            Selected = "{Game}.player.stolencard";
            stolen = true;
        }

        var v = ctx.Bridge.Batch(Selected + ".name", Selected + ".x", Selected + ".y", Selected + ".width", Selected + ".height",
                                 Selected + ".ready", Selected + ".availablethisturn", Selected + ".shockedsetting",
                                 Selected + ".remainingcountdown", Selected + ".countdown", Selected + ".needstotal", Selected + ".weakened");
        if (!v[0].Exists) return new List<string>();
        var card = new Card(v[0].AsString(), v[1].AsDouble(), v[2].AsDouble(), v[3].AsDouble(), v[4].AsDouble(),
                            v[5].AsBool(true), v[6].AsBool(true), v[7].AsInt(), v[8].AsInt(), v[9].AsInt(), v[10].AsInt(), v[11].AsBool());

        // Name and rules text as drawn, from inside the card.
        var rect = new Rect(card.X, card.Y, card.Width, card.Height);
        var slots = CardText.Slots(ctx, Selected);
        var slotBoxes = slots.Length == 0 ? new List<Rect>() : SlotBoxes(ctx, Selected, card);
        var hud = HudKeys(ctx);
        var face = ctx.Stage.Current.Where(p => rect.Contains(p) && !slotBoxes.Any(b => b.Contains(p)) && !IsHud(hud, p)).Select(p => p.Text).ToList();
        var parts = new List<string>();
        if (stolen) parts.Add(Strings.T(holder == "thiefreunion" ? "card.enemy_loan" : "card.stolen"));
        // A weakened card prints its name as "SWORD-"; "weakened" is said with its state below.
        if (face.Count > 0) { face[0] = face[0].TrimEnd('-', ' '); parts.AddRange(face); }
        else
        {
            // Not drawn yet (cards are still sliding in) or covered: the same text, from the card itself.
            parts.Add(CardText.Name(ctx, Selected));
            var description = CardText.Description(ctx, Selected);
            if (description.Length > 0) parts.Add(description);
        }

        // A Reunion skill panel has no name of its own on its face: say what it is first.
        if (holder.EndsWith("reunion", StringComparison.Ordinal) && holder != "thiefreunion" && parts.Count > 0)
        {
            var face_ = string.Join(", ", parts);
            parts.Clear();
            parts.Add(Strings.T("skill.panel", face_));
        }

        if (slots.Length > 0) parts.Add(slots);

        // A panel card (the Robot's Calculate / jackpot) is operated through buttons inside it.
        if (ctx.State.Focus == null && ctx.State.Buttons.Count >= 2 && ctx.Bridge.Eval(Selected + ".skillcard").AsString().Length > 0)
            parts.Add(Strings.T("battle.panel_hint"));

        // Reunion's Warrior: a dice put on a card stays there until the card is fired with a second A ("PLAY").
        var loaded = ctx.Bridge.Dump(Selected + ".assigneddice").Select(d => d.Value.Exists).ToList();
        if (card.Ready && loaded.Count > 0 && loaded.All(f => f)) parts.Add(Strings.T("card.loaded"));

        // Reunion's Thief: R keeps the borrowed card for good, once per battle.
        if (holder == "thiefreunion" && ctx.Bridge.Eval("{Reunion}.thiefcard_created").AsBool()
            && ctx.Bridge.Eval("{Reunion}.thiefcard_selected").AsString().Length == 0) parts.Add(Strings.T("card.enemy_loan_steal"));

        if (card.Shocked > 0) parts.Add(Strings.T("card.shocked"));
        if (card.Weakened) parts.Add(Strings.T("card.weakened"));
        if (!card.Available || !card.Ready) parts.Add(Strings.T("card.unavailable"));

        return parts;
    }

    /// <summary>
    /// The HUD's fighter name and "3 dice" are drawn at the bottom left, inside the rectangle
    /// of a card in a low slot (seen: the Witch's third spell slot read "Infliction, Do 3
    /// damage, Witch, 3 dice"). They are not part of any card.
    /// </summary>
    private static HashSet<string> HudKeys(ScreenContext ctx)
        => ctx.Bridge.Batch("{Game}.player.name", "{Game}.monster.name").Where(n => n.Exists)
              .Select(n => StageCleaner.Key(ctx.Translate(n.AsString()))).ToHashSet();

    private static bool IsHud(HashSet<string> names, Paragraph p) => names.Contains(p.Key) || DiceCountText().IsMatch(p.Text);

    /// <summary>
    /// Where the card draws its dice slots. The words printed inside one ("max 3", "even", a
    /// countdown) repeat what the slot description already says.
    /// </summary>
    private static List<Rect> SlotBoxes(ScreenContext ctx, string Selected, Card card)
    {
        const int SlotSize = 264;   // a card is 792 wide: three slot widths
        var rows = ctx.Bridge.Pluck(Selected + ".slotpositions", "x", "y");
        if (rows == null) return new List<Rect>();
        return rows.Where(r => r[0].Exists)
                   .Select(r => new Rect(card.X + r[0].AsDouble(), card.Y + r[1].AsDouble(), SlotSize - 20, SlotSize)).ToList();
    }

    // ------------------------------------------------------------------ Reunion skill panels

    private string _reunionStolen = "";

    /// <summary>
    /// Reunion's Thief: Y on his panel keeps the enemy card shown there ("Steal - keep this
    /// card", once per battle). The game only hides the button; Reunion.thiefcard_selected
    /// then names the card.
    /// </summary>
    private void AnnounceReunionSteal(ScreenContext ctx)
    {
        var selected = ctx.Bridge.Eval("{Reunion}.thiefcard_selected");
        var name = selected.Exists ? selected.AsString() : "";
        if (name == _reunionStolen) return;
        _reunionStolen = name;
        if (name.Length > 0) ctx.SayEvent(Strings.T("reunion.stolen", ctx.Translate(name)));
    }

    /// <summary>
    /// In the Reunion episodes the Warrior, Thief and Witch carry a skill panel (skillcard
    /// "warriorreunion", "thiefreunion", "witchreunion") fired with Y. The Thief's is also a card the
    /// cursor lands on (see CursorSkips). What it does is
    /// fired with Y. What it does is only drawn on it, so it is read from inside its rectangle.
    /// </summary>
    private static string DescribeSkillPanel(ScreenContext ctx)
    {
        var rows = ctx.Bridge.Pluck("{Game}.player.equipment", "skillcard", "x", "y", "width", "height") ?? new();
        var panel = rows.FirstOrDefault(r => r[0].AsString().EndsWith("reunion", StringComparison.Ordinal));
        if (panel == null) return "";
        var rect = new Rect(panel[1].AsDouble(), panel[2].AsDouble(), panel[3].AsDouble(), panel[4].AsDouble());
        var text = string.Join(", ", ctx.Stage.Current.Where(rect.Contains).Select(p => p.Text));
        if (panel[0].AsString() == "thiefreunion")
            return text.Length == 0 ? "" : Strings.T(ctx.Bridge.Eval("{Reunion}.thiefcard_created").AsBool() ? "reunion.thief_panel" : "reunion.thief_panel_used", text);
        return text.Length == 0 ? "" : Strings.T("skill.panel", text);
    }

    // ------------------------------------------------------------------ the Jester

    /// <summary>
    /// The deck panel: "Next" lists the cards about to be drawn (the end of
    /// elements.Deck.drawpile comes first, observed) and the count left; Y discards every
    /// group of identical cards in the hand and draws replacements.
    /// </summary>
    private static string DescribeDeck(ScreenContext ctx, List<string> hand)
    {
        var pile = (ctx.Bridge.Pluck("{elements.Deck}.drawpile", "equipment.name") ?? new()).Where(r => r[0].Exists).Select(r => r[0].AsString()).ToList();
        var next = pile.AsEnumerable().Reverse().Take(3).Select(ctx.Translate).ToList();
        var pair = hand.GroupBy(n => n).FirstOrDefault(g => g.Count() > 1)?.Key;
        return TextCleanup.JoinSentences(
            next.Count > 0 ? Strings.T("jester.next", string.Join(", ", next), pile.Count) : Strings.T("jester.empty"),
            pair != null ? Strings.T("jester.pair", ctx.Translate(pair)) : null);
    }

    // ------------------------------------------------------------------ the Witch

    /// <summary>
    /// The Witch's panel (skillcard "witch") takes any dice. A dice whose number is a written
    /// page of elements.Spellbook.spells summons that spell as a card; any other number is
    /// simply thrown at the enemy for 1 damage. The panel lists the pages as pictures of dice.
    /// </summary>
    private static List<string> SpellPages(ScreenContext ctx)
        => (ctx.Bridge.Pluck("{elements.Spellbook}.spells", "name") ?? new()).Select(r => r[0].AsString()).ToList();

    private static string DescribeSpellbook(ScreenContext ctx)
    {
        var pages = SpellPages(ctx).Select((name, i) => name.Length > 0 ? Strings.T("spellbook.page", i + 1, ctx.Translate(name)) : null).Where(p => p != null);
        return Strings.T("witch.book", string.Join(", ", pages));
    }

    private static string SpellForDice(ScreenContext ctx, int value)
    {
        var pages = SpellPages(ctx);
        var name = value >= 1 && value <= pages.Count ? pages[value - 1] : "";
        return name.Length == 0
            ? Strings.T("witch.throw")
            : Strings.T("witch.summons", ctx.Translate(name), CardText.DescriptionByName(ctx, name));
    }

    private int _spellSlot = -2;

    /// <summary>
    /// After a dice summons a spell the Witch chooses which of her four spell slots it goes
    /// in (states.Combat.selectspellslotmode, cursor gamepad_selectedslot, contents
    /// elements.Spellbook.equipmentslots). A taken slot is replaced. B cancels.
    /// </summary>
    private bool AnnounceSpellSlot(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch(Combat + ".selectspellslotmode", Combat + ".gamepad_selectedslot");
        if (!v[0].AsBool()) { if (_spellSlot != -2) _focusKey = ""; _spellSlot = -2; return false; }
        var slot = v[1].AsInt();
        if (slot == _spellSlot) return true;
        var first = _spellSlot == -2;
        _spellSlot = slot;
        var text = DescribeSpellSlot(ctx, slot);
        ctx.Say(first ? Strings.T("witch.choose_slot", text) : text, interrupt: true);
        return true;
    }

    private static string DescribeSpellSlot(ScreenContext ctx, int slot)
    {
        var slots = ctx.Bridge.Pluck("{elements.Spellbook}.equipmentslots", "name") ?? new();
        var name = slot >= 0 && slot < slots.Count && slots[slot][0].Exists ? slots[slot][0].AsString() : "";
        return Strings.T("witch.slot", slot + 1, Math.Max(slots.Count, 1),
                         name.Length > 0 ? Strings.T("witch.slot_taken", ctx.Translate(name)) : Strings.T("spellbook.empty"));
    }

    // ------------------------------------------------------------------ dice

    /// <summary>
    /// While a dice is hovered the card shows what it would do with that dice ("Do 5
    /// damage"). That is the line a sighted player checks before committing.
    /// </summary>
    private static string DicePreview(ScreenContext ctx)
    {
        var Selected = CombatHandler.Selected;
        if (ctx.Bridge.Eval(Selected + ".skillcard").AsString() == "witch")
        {
            var dice = ctx.Bridge.Batch(Hover + ".basevalue", Hover + ".modifier", Hover + ".blind");
            return dice[2].AsBool() ? "" : ". " + SpellForDice(ctx, dice[0].AsInt() + dice[1].AsInt());
        }
        if (ShowsStolenCard(ctx.Bridge.Eval(Selected + ".skillcard").AsString()) && ctx.Bridge.Eval("{Game}.player.stolencard.name").Exists)
            Selected = "{Game}.player.stolencard";
        var v = ctx.Bridge.Batch(Selected + ".x", Selected + ".y", Selected + ".width", Selected + ".height");
        if (!v[0].Exists) return "";
        var rect = new Rect(v[0].AsDouble(), v[1].AsDouble(), v[2].AsDouble(), v[3].AsDouble());
        var card = new Card("", rect.X, rect.Y, rect.Width, rect.Height, true, true, 0, 0, 0, 0, false);
        var slotBoxes = SlotBoxes(ctx, Selected, card);
        var hud = HudKeys(ctx);
        var face = ctx.Stage.Current.Where(p => rect.Contains(p) && !slotBoxes.Any(b => b.Contains(p)) && !IsHud(hud, p))
                      .Select(p => p.Text).Skip(1).ToList();   // first is the card's name
        return face.Count == 0 ? "" : ". " + string.Join(". ", face);
    }

    /// <summary>
    /// The hovered dice and what it would do on the chosen card - or that the card will not
    /// take it, which the game shows by not lighting the slot up. <paramref name="fits"/> is
    /// false in that case.
    /// </summary>
    private static List<string> DiceParts(ScreenContext ctx, out bool fits)
    {
        fits = true;
        var dice = DescribeHoverDice(ctx);
        if (dice.Length == 0) return new List<string>();

        var card = Selected;
        if (ShowsStolenCard(ctx.Bridge.Eval(Selected + ".skillcard").AsString()) && ctx.Bridge.Eval("{Game}.player.stolencard.name").Exists)
            card = "{Game}.player.stolencard";
        var v = ctx.Bridge.Batch(Hover + ".basevalue", Hover + ".modifier", Hover + ".blind");
        // A blind dice hides its number, so whether it fits must stay hidden too.
        if (!v[2].AsBool() && !DiceFits(ctx, card, v[0].AsInt() + v[1].AsInt()))
        {
            fits = false;
            return new List<string> { Strings.T("dice.unfit", dice, CardText.Name(ctx, card)) };
        }
        var parts = new List<string> { dice };
        var preview = DicePreview(ctx).TrimStart('.', ' ');
        if (preview.Length > 0) parts.Add(preview);
        return parts;
    }

    /// <summary>
    /// Whether any open slot of the card takes a dice of this value: Game.slotcheckvalue's
    /// rules, from the constructor names of elements.DiceSlotType (Equipment.hx). A shocked
    /// card takes any dice (it unlocks it); a character's panel decides for itself.
    /// </summary>
    private static bool DiceFits(ScreenContext ctx, string card, int value)
    {
        var v = ctx.Bridge.Batch(card + ".skillcard", card + ".shockedsetting");
        if (v[0].AsString().Length > 0 || v[1].AsInt() > 0) return true;
        var slots = ctx.Bridge.Dump(card + ".slots").Select(s => s.Value.TypeName).ToList();
        var filled = ctx.Bridge.Dump(card + ".assigneddice").Select(d => d.Value.Exists).ToList();
        var open = 0;
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (i < filled.Count && filled[i]) continue;
            if (slot.Length == 0 || slot.StartsWith("FREE") || slot.StartsWith("LOCKED") || slot.StartsWith("SPARE") || slot == "SKILL") continue;
            open++;
            if (SlotAccepts(slot, value)) return true;
        }
        return slots.Count == 0 || (open == 0 && filled.Count == 0);
    }

    private static bool SlotAccepts(string slot, int value)
    {
        if (slot == "EVEN") return value % 2 == 0;
        if (slot == "ODD") return value % 2 != 0;
        if (slot.StartsWith("RANGE") && slot.Length == 7 && char.IsDigit(slot[5]) && char.IsDigit(slot[6]))
            return value >= slot[5] - '0' && value <= slot[6] - '0';
        if (slot.Length > 3 && char.IsDigit(slot[^1]))
        {
            var n = slot[^1] - '0';
            if (slot.StartsWith("MAX")) return value <= n;
            if (slot.StartsWith("MIN")) return value >= n;
            if (slot.StartsWith("REQUIRE")) return value == n;
        }
        return true;   // NORMAL, COUNTDOWN, DOUBLES, COMBINATION and anything not known: the game decides
    }

    private static string DescribeHoverDice(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch(Hover + ".basevalue", Hover + ".modifier", Hover + ".locked", Hover + ".frozen", Hover + ".burn", Hover + ".blind");
        if (!v[0].Exists) return "";
        // A blind dice hides its face from the player; saying the number would be cheating.
        var parts = new List<string> { v[5].AsBool() ? Strings.T("dice.hidden") : Strings.T("dice.one", v[0].AsInt() + v[1].AsInt()) };
        if (v[2].AsBool()) parts.Add(Strings.T("dice.locked"));
        if (v[3].AsBool()) parts.Add(Strings.T("dice.frozen"));
        if (v[4].AsBool()) parts.Add(Strings.T("dice.burning"));
        return string.Join(", ", parts);
    }

    /// <summary>F5: every dice you hold and every card on both sides.</summary>
    public override string DescribeContext(ScreenContext ctx)
    {
        var parts = new List<string>();
        var both = _narrator.DescribeBoth();
        if (both.Length > 0) parts.Add(both.TrimEnd('.'));
        var dice = ctx.Bridge.Pluck("{Game}.player.dicepool", "basevalue", "modifier", "assigned.name");
        if (dice != null)
        {
            var free = dice.Where(d => d[0].Exists && !d[2].Exists).Select(d => (d[0].AsInt() + d[1].AsInt()).ToString()).ToList();
            parts.Add(free.Count == 0 ? Strings.T("dice.none") : Strings.T("dice.list", string.Join(", ", free)));
        }
        var skill = DescribeSkillPanel(ctx);
        if (skill.Length > 0) parts.Add(skill);
        foreach (var (owner, label) in new[] { ("{Game}.player.equipment", "cards.yours"), ("{Game}.monster.equipment", "cards.enemy") })
        {
            var rows = ctx.Bridge.Pluck(owner, "name", "ready", "availablethisturn", "skillcard", "ty", "show");
            if (rows == null || rows.Count == 0) continue;
            if (owner.Contains("player") && ctx.Bridge.Eval("{Game}.player.layout").TypeName == "DECK")
            {
                // The Jester: only the hand, then what the deck panel shows.
                rows = rows.Where(r => r[1].AsBool(true) && r[5].AsBool(true) && r[4].AsDouble() > -200 && r[4].AsDouble() < 2100).ToList();
                parts.Add(DescribeDeck(ctx, rows.Where(r => r[3].AsString().Length == 0).Select(r => r[0].AsString()).ToList()));
            }
            var stolenName = ctx.Bridge.Eval("{Game}.player.stolencard.name");
            var names = rows.Where(r => r[0].Exists).Select(r => (r[3].AsString() == "stolencard"
                    ? Strings.T("card.stolen") + (stolenName.Exists ? " " + ctx.Translate(stolenName.AsString()) : "")
                    : r[3].AsString() == "switchfighter" ? Strings.T("panel.switch") : CardText.DecorateName(ctx, r[0].AsString(), ""))
                + (r[1].AsBool(true) && r[2].AsBool(true) ? "" : " (" + Strings.T("card.unavailable") + ")"));
            parts.Add(Strings.T(label, string.Join(", ", names)));
        }
        return parts.Count == 0 ? base.DescribeContext(ctx) : string.Join(". ", parts);
    }

    public override string DescribeStatus(ScreenContext ctx, StatusKind kind) => kind switch
    {
        StatusKind.Dice => _narrator.DescribeDice(),
        StatusKind.Enemies => _narrator.DescribeEnemyHealth(),
        _ => _narrator.DescribeHealth() is { Length: > 0 } health ? health : base.DescribeStatus(ctx, kind),
    };

    public override IEnumerable<string> Help => new[] { Strings.T("help.combat.play"), Strings.T("help.combat"), Strings.T("help.combat.special") }.Concat(base.Help);
}
