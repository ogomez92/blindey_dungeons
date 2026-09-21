using System.Text.RegularExpressions;
using DiceyA11y.Bridge;
using DiceyA11y.Core;

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

    [GeneratedRegex(@"^[-+]?\d+$")]
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
        _choicesSaid = "";
        _phase = "";
        _spokenThisBattle.Clear();
        _narrator.Reset();
        ctx.Buffers.ClearBattleBuffers();
        base.OnEnter(ctx);
    }

    public override void OnPoll(ScreenContext ctx)
    {
        _cardRects = ReadCardRects(ctx);
        base.OnPoll(ctx);
        _narrator.Poll(ctx);

        AnnounceEnemyPreview(ctx);
        AnnounceChoices(ctx);
        AnnounceReunionSteal(ctx);
        if (AnnouncePrompts(ctx)) return;   // a prompt is up: the card cursor is frozen beneath it
        if (AnnounceSpellSlot(ctx)) return;

        var v = ctx.Bridge.Batch(Combat + ".state", Combat + ".gamepad_dicemode",
                                 Selected + ".name", Selected + ".x", Selected + ".y",
                                 Hover + ".basevalue", Hover + ".modifier", Hover + ".x", Hover + ".y");
        if (v[0].IsError) return;

        var phase = v[0].AsString();
        var playerActing = phase.StartsWith("playerturn", StringComparison.Ordinal) && !_narrator.Ended;
        _phase = phase;
        if (!playerActing) { _focusKey = ""; return; }

        var diceMode = v[1].AsBool();
        var key = diceMode
            ? $"dice:{v[5].Raw}:{v[6].Raw}:{v[7].AsDouble():0},{v[8].AsDouble():0}"
            : $"card:{v[2].Raw}:{v[3].AsDouble():0},{v[4].AsDouble():0}";
        if (key == _focusKey) return;

        // A panel whose button has the game's focus (gadget, Calculate) was just read as a button.
        if (ctx.State.Focus != null && !diceMode) { _focusKey = key; return; }

        // Cards and dice glide into place, so the same one reports a new position every
        // frame for a moment. Speak only once it has come to rest.
        if (key != _pendingKey || !ctx.Stage.Settled) { _pendingKey = key; return; }
        _focusKey = key;

        var text = diceMode ? DescribeHoverDice(ctx) + DicePreview(ctx) : DescribeSelectedCard(ctx, brief: true);
        if (text.Length > 0) ctx.Say(text, interrupt: RecentInput(ctx));
    }

    /// <summary>
    /// Automatic reading skips what the cursor and the narrator already cover: card faces,
    /// floating damage numbers and the health readouts. Dialogue and tutorial prompts stay.
    /// </summary>
    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p)
    {
        if (BareNumber().IsMatch(p.Text) || HealthFraction().IsMatch(p.Text)) return false;
        if (HealthLead().IsMatch(p.Text)) return false;   // "15/24 FURY": the health bar and its label
        if (_cardRects.Any(r => r.Contains(p))) return false;
        if (InFleePanel(ctx, p)) return false;
        if (_narrator.FighterNames.Any(n => StageCleaner.Key(n) == p.Key)) return false;
        if (DiceCountText().IsMatch(p.Text)) return false;

        // The rest of the HUD (fighter names, dice counts, the end-turn and enemy-moves
        // prompts) is redrawn every turn. Once per battle is orientation; every turn is noise.
        return _spokenThisBattle.Add(p.Key);
    }

    protected override string CursorState(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch(Combat + ".gamepad_dicemode", Selected + ".name", Selected + ".tx", Selected + ".ty",
                                 Hover + ".basevalue", Hover + ".x", Combat + ".state", Combat + ".gamepad_selectedslot");
        return string.Join("|", v.Select(x => x.Raw));
    }

    protected override int DeadKeyDelayMs => 650;

    protected override bool ReadColumnAboveTwins => false;

    protected override string DescribeFocusBrief(ScreenContext ctx)
    {
        if (!_phase.StartsWith("playerturn", StringComparison.Ordinal)) return "";
        if (_limitPrompt || _fleePrompt) return DescribeFocus(ctx);
        return ctx.Bridge.Eval(Combat + ".gamepad_dicemode").AsBool() ? DescribeHoverDice(ctx) : DescribeSelectedCard(ctx, brief: true);
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

    /// <summary>A set of buttons appearing mid-battle is a choice the player has to notice: the Robot's jackpot.</summary>
    private void AnnounceChoices(ScreenContext ctx)
    {
        if (PauseMenuOpen(ctx)) return;
        var choices = ctx.State.Buttons.Where(b => b.RawLabel != "Calculate").GroupBy(b => b.RawLabel).Select(g => g.First()).ToList();
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
            ? DescribeHoverDice(ctx) + ". " + DescribeSelectedCard(ctx, brief: false)
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
    {
        if (ctx.Bridge.Eval(Selected + ".skillcard").AsString() == "witch") return DescribeSpellbook(ctx);

        // The Thief's third card is a placeholder (skillcard "stolencard"); what it shows and
        // plays is Fighter.stolencard, this turn's copy of one of the enemy's cards.
        var stolen = false;
        if (ctx.Bridge.Eval(Selected + ".skillcard").AsString() == "stolencard")
        {
            if (!ctx.Bridge.Eval("{Game}.player.stolencard.name").Exists) return Strings.T("card.stolen_none");
            Selected = "{Game}.player.stolencard";
            stolen = true;
        }

        var v = ctx.Bridge.Batch(Selected + ".name", Selected + ".x", Selected + ".y", Selected + ".width", Selected + ".height",
                                 Selected + ".ready", Selected + ".availablethisturn", Selected + ".shockedsetting",
                                 Selected + ".remainingcountdown", Selected + ".countdown", Selected + ".needstotal", Selected + ".weakened");
        if (!v[0].Exists) return "";
        var card = new Card(v[0].AsString(), v[1].AsDouble(), v[2].AsDouble(), v[3].AsDouble(), v[4].AsDouble(),
                            v[5].AsBool(true), v[6].AsBool(true), v[7].AsInt(), v[8].AsInt(), v[9].AsInt(), v[10].AsInt(), v[11].AsBool());

        // Name and rules text as drawn, from inside the card.
        var rect = new Rect(card.X, card.Y, card.Width, card.Height);
        var slots = CardText.Slots(ctx, Selected);
        var slotBoxes = slots.Length == 0 ? new List<Rect>() : SlotBoxes(ctx, Selected, card);
        var face = ctx.Stage.Current.Where(p => rect.Contains(p) && !slotBoxes.Any(b => b.Contains(p))).Select(p => p.Text).ToList();
        var parts = new List<string>();
        if (stolen) parts.Add(Strings.T("card.stolen"));
        if (face.Count > 0) parts.AddRange(face);
        else
        {
            // Not drawn yet (cards are still sliding in) or covered: the same text, from the card itself.
            parts.Add(CardText.Name(ctx, Selected));
            var description = CardText.Description(ctx, Selected);
            if (description.Length > 0) parts.Add(description);
        }

        if (slots.Length > 0) parts.Add(slots);

        // A panel card (the Robot's Calculate / jackpot) is operated through buttons inside it.
        if (ctx.State.Focus == null && ctx.State.Buttons.Count >= 2 && ctx.Bridge.Eval(Selected + ".skillcard").AsString().Length > 0)
            parts.Add(Strings.T("battle.panel_hint"));

        // Reunion's Warrior: a dice put on a card stays there until the card is fired with a second A ("PLAY").
        var loaded = ctx.Bridge.Dump(Selected + ".assigneddice").Select(d => d.Value.Exists).ToList();
        if (card.Ready && loaded.Count > 0 && loaded.All(f => f)) parts.Add(Strings.T("card.loaded"));

        if (card.Shocked > 0) parts.Add(Strings.T("card.shocked"));
        if (card.Weakened) parts.Add(Strings.T("card.weakened"));
        if (!card.Available || !card.Ready) parts.Add(Strings.T("card.unavailable"));

        return string.Join(brief ? ", " : ". ", parts);
    }

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
    /// "warriorreunion", "thiefreunion", "witchreunion") that the cursor never lands on: it is
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
        if (ctx.Bridge.Eval(Selected + ".skillcard").AsString() == "stolencard" && ctx.Bridge.Eval("{Game}.player.stolencard.name").Exists)
            Selected = "{Game}.player.stolencard";
        var v = ctx.Bridge.Batch(Selected + ".x", Selected + ".y", Selected + ".width", Selected + ".height");
        if (!v[0].Exists) return "";
        var rect = new Rect(v[0].AsDouble(), v[1].AsDouble(), v[2].AsDouble(), v[3].AsDouble());
        var card = new Card("", rect.X, rect.Y, rect.Width, rect.Height, true, true, 0, 0, 0, 0, false);
        var slotBoxes = SlotBoxes(ctx, Selected, card);
        var face = ctx.Stage.Current.Where(p => rect.Contains(p) && !slotBoxes.Any(b => b.Contains(p)))
                      .Select(p => p.Text).Skip(1).ToList();   // first is the card's name
        return face.Count == 0 ? "" : ". " + string.Join(". ", face);
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

    public override IEnumerable<string> Help => new[] { Strings.T("help.combat.play"), Strings.T("help.combat"), Strings.T("help.combat.special") }.Concat(base.Help);
}
