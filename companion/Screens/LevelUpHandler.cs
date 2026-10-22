using System.Text.RegularExpressions;
using DiceyA11y.Core;
using DiceyA11y.Input;

namespace DiceyA11y.Screens;

/// <summary>
/// The screen after a won battle (states.LevelUpScreen), which walks through several
/// states (its static "state", observed live):
///
///   gainxp                  rewards drawn as rows of star and coin pictures with only "x1"
///                           beside them, so the amounts come from lastbattleexpreward,
///                           lastbattlegoldreward and nextlevelexp
///   chooserequiredrewards   "You get an extra dice!" and a Take button
///   chooseoptionalrewards   "Choose a reward!": two or three columns, each a card or a
///                           picture with a caption, and a Take button under every one
///   inventorgadgets         the Inventor only, after every fight: "Choose an item to destroy
///                           for parts". Her cards (Game.player.equipment) stand in a row; under
///                           each is a button named after the gadget that card turns into
///                           (Equipment.gadget) and, below it, what the gadget does
///   finderskeepers          the Thief's second episode: "Choose a piece of equipment to keep",
///                           the enemy's cards (Game.monster.equipment) with a Steal button each
///   finished                back to the map
///
/// What is on offer is leveluprewards (elements.LevelUpReward: type, level, equipment)
/// filtered to the level just reached; cards are read from game state, the rest (an
/// upgrade, a dice) from the caption drawn in that column.
/// </summary>
public sealed partial class LevelUpHandler : CardScreenHandler
{
    private const string Screen = "{states.LevelUpScreen}";

    [GeneratedRegex(@"^x\s*\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex Multiplier();

    private string _state = "";
    private bool _leveled, _rewardsSaid;
    private DateTime _stateSince = DateTime.MinValue;

    /// <summary>The card and gadget of the Inventor's choice, said once the screen moves on.</summary>
    private (string Card, string Gadget)? _scrapped;
    private DateTime _scrappedAt;

    /// <summary>States that open with a heading ("Choose a reward!") and then buttons.</summary>
    private static readonly HashSet<string> ChoiceStates = new() { "chooseoptionalrewards", "inventorgadgets", "finderskeepers" };

    public override bool Handles(string scene) => scene == "states.LevelUpScreen";

    protected override bool ReadCardsOnEntry => false;

    /// <summary>Every visit starts with the battle's rewards; a level-up, if any, is announced when it happens.</summary>
    protected override string ScreenName(ScreenContext ctx) => Strings.T("screen.levelup.rewards");

    public override void OnEnter(ScreenContext ctx)
    {
        _state = "";
        _rewardsSaid = false;
        _scrapped = null;
        _leveled = ctx.Bridge.Eval(Screen + ".leveledup").AsBool();
        base.OnEnter(ctx);
    }

    protected override IEnumerable<string> CardExpressions(ScreenContext ctx)
    {
        var state = ctx.Bridge.Eval(Screen + ".state").AsString();
        if (state == "inventorgadgets")
        {
            var cards = ctx.Bridge.Pluck("{Game}.player.equipment", "skillcard", "x") ?? new();
            return Enumerable.Range(0, cards.Count)
                .Where(i => cards[i][0].AsString().Length == 0 && cards[i][1].AsDouble() > 0)
                .Select(i => $"{{Game}}.player.equipment[{i}]").ToList();
        }
        if (state == "finderskeepers")
        {
            // The Thief's "Finders Keepers" episode: keep one of the beaten enemy's cards.
            var theirs = ctx.Bridge.Pluck("{Game}.monster.equipment", "x") ?? new();
            return Enumerable.Range(0, theirs.Count).Where(i => theirs[i][0].AsDouble() > 0)
                .Select(i => $"{{Game}}.monster.equipment[{i}]").ToList();
        }
        if (state != "chooseoptionalrewards") return Array.Empty<string>();
        var level = ctx.Bridge.Eval("{Game}.player.level").AsInt();
        var rows = ctx.Bridge.Pluck(Screen + ".leveluprewards", "level", "equipment.name", "equipment.x") ?? new();
        return Enumerable.Range(0, rows.Count)
            .Where(i => rows[i][0].AsInt() == level && rows[i][1].Exists && rows[i][2].AsDouble() > 0)
            .Select(i => $"{Screen}.leveluprewards[{i}].equipment").ToList();
    }

    public override void OnPoll(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch(Screen + ".state", Screen + ".leveledup", Screen + ".giveexp",
                                 Screen + ".lastbattleexpreward", Screen + ".lastbattlegoldreward", Screen + ".nextlevelexp");
        var state = v[0].AsString();
        if (state != _state) _stateSince = DateTime.UtcNow;
        _state = state;   // before the base poll: ShouldSpeak and DescribeFocus go by it

        if (_scrapped is { } scrapped && (state != "inventorgadgets" || DateTime.UtcNow - _scrappedAt > TimeSpan.FromSeconds(3)))
        {
            if (state != "inventorgadgets") ctx.SayEvent(Strings.T("scrap.done", scrapped.Card, scrapped.Gadget));
            _scrapped = null;
        }
        if (!Holding(ctx)) base.OnPoll(ctx);

        // The stars are handed over one by one; what is still needed is only right once they all are.
        if (state == "gainxp" && !_rewardsSaid && v[2].AsInt() == 0 && v[3].Exists)
        {
            _rewardsSaid = true;
            ctx.SayEvent(Strings.T("levelup.rewards", v[3].AsInt(), v[4].AsInt()));
            if (!v[1].AsBool() && v[5].AsInt() > 0) ctx.SayEvent(Strings.T("levelup.next", v[5].AsInt()));
        }

        if (v[1].AsBool() && !_leveled) { _leveled = true; ctx.SayEvent(Strings.T("levelup.leveled")); }
    }

    /// <summary>
    /// Nothing is read for a moment while the screen changes over. A new state's heading
    /// ("Choose a reward!") slides in after its buttons register, and was heard after the first
    /// Take. And once a gadget is chosen, its buttons stay focused while they slide away: the
    /// choice was read again as "1 of 6" before the rewards came, and as "Pea Gun, 1 of 3" on the
    /// way back to the map.
    /// </summary>
    private bool Holding(ScreenContext ctx)
    {
        if (_scrapped != null || _state == "finished") return true;   // finished: the map follows at once
        if (!ChoiceStates.Contains(_state)) return false;
        var since = DateTime.UtcNow - _stateSince;
        return since < TimeSpan.FromMilliseconds(600) || since < TimeSpan.FromMilliseconds(1500) && !ctx.Stage.Settled;
    }

    /// <summary>A gadget's button destroys its card at once: remember which, to say it when the screen moves on.</summary>
    public override bool OnKey(ScreenContext ctx, NavKey key)
    {
        if (key == NavKey.Confirm && _state == "inventorgadgets" && ctx.State.Focus is { } button && FocusedCard(ctx) is var index and >= 0)
        {
            _scrapped = (CardText.Name(ctx, Cards[index].Expression), button.Label);
            _scrappedAt = DateTime.UtcNow;
        }
        return base.OnKey(ctx, key);
    }

    /// <summary>A reward that is not a card: the caption drawn in the focused button's column.</summary>
    private static string ColumnCaption(ScreenContext ctx, NavButton focus, double halfWidth = 450, int top = 400)
    {
        var labels = new HashSet<string>(ctx.State.Buttons.Select(b => StageCleaner.Key(b.Label)));
        var lines = ctx.Stage.Current.Where(p => p.Y > top && p.Y < focus.Y - 20 && Math.Abs(p.X - focus.X) < halfWidth && !labels.Contains(p.Key))
                       .Select(p => p.Text);
        return TextCleanup.JoinLines(lines);
    }

    /// <summary>
    /// "You get an extra dice!" with its lone Take button: the words are the reward and the
    /// button is what Enter does, so they are one announcement, in that order. Read as loose
    /// text plus a focus echo, the echo jumped the speech queue ("You get. Take. An extra dice!").
    /// </summary>
    private bool RequiredReward => _state == "chooserequiredrewards";

    /// <summary>What the gadget named on the focused button does: the lines drawn under that button.</summary>
    private static string BelowButton(ScreenContext ctx, NavButton focus)
    {
        var labels = new HashSet<string>(ctx.State.Buttons.Select(b => StageCleaner.Key(b.Label)));
        var lines = ctx.Stage.Current.Where(p => p.Y > focus.Y + 30 && p.Y < focus.Y + 330 && Math.Abs(p.X - focus.X) < 500 && !labels.Contains(p.Key))
                       .Select(p => p.Text);
        return string.Join(". ", lines);
    }

    public override string DescribeFocus(ScreenContext ctx)
    {
        if (ctx.State.Focus is { } button && _state == "inventorgadgets")
        {
            var index = FocusedCard(ctx);
            var buttons = ctx.State.Buttons;
            var position = buttons.ToList().FindIndex(b => b.RawLabel == button.RawLabel) + 1;
            return TextCleanup.JoinSentences(
                index >= 0 ? Strings.T("scrap.destroy", DescribeCard(ctx, index)) : null,
                Strings.T("scrap.gadget", button.Label, BelowButton(ctx, button)),
                buttons.Count > 1 && position > 0 ? Strings.T("of", position, buttons.Count) : null);
        }

        // The finale reuses the Finders Keepers state when a beaten contestant joins the group:
        // the "card" is the contestant and the button says Welcome.
        if (ctx.State.Focus is { RawLabel: "Welcome!" } welcome && FocusedCard(ctx) is var joined and >= 0)
            return TextCleanup.JoinSentences(CardText.Name(ctx, Cards[joined].Expression), welcome.Label);

        var focus = ctx.State.Focus;
        if (focus != null && RequiredReward && ctx.State.Buttons.Count == 1)
            return TextCleanup.JoinSentences(ColumnCaption(ctx, focus, halfWidth: 1200, top: 150), PressEnterTo(focus.Label));
        if (focus == null || Cards.Count == 0 || FocusedCard(ctx) >= 0) return base.DescribeFocus(ctx);
        return TextCleanup.JoinSentences(ColumnCaption(ctx, focus), base.DescribeFocus(ctx));
    }

    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p)
        => base.ShouldSpeak(ctx, p) && !Multiplier().IsMatch(p.Text) && p.Y < 2100   // below: the map's status strip
           && !(_state == "inventorgadgets" && p.Y > 1400)                              // gadget captions: read with their button
           && !(RequiredReward && p.Y > 150 && p.Y < 1700)                                 // the reward's words: read with Take (the button registers a poll after them)
           && !p.Text.TrimEnd().EndsWith(':')                                           // "Next level:" labels a row of stars
           && !(_leveled && Letters(p.Text) == Letters(Strings.T("levelup.leveled"))); // the drawn "LEVEL UP": said already

    private static string Letters(string text) => new(text.Where(char.IsLetter).Select(char.ToUpperInvariant).ToArray());

    public override string DescribeContext(ScreenContext ctx)
        => TextCleanup.JoinSentences(DungeonHandler.PlayerStatus(ctx), base.DescribeContext(ctx));
}
