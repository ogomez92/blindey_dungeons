using System.Text.RegularExpressions;
using DiceyA11y.Core;

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

    public override bool Handles(string scene) => scene == "states.LevelUpScreen";

    protected override bool ReadCardsOnEntry => false;

    /// <summary>Every visit starts with the battle's rewards; a level-up, if any, is announced when it happens.</summary>
    protected override string ScreenName(ScreenContext ctx) => Strings.T("screen.levelup.rewards");

    public override void OnEnter(ScreenContext ctx)
    {
        _state = "";
        _rewardsSaid = false;
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
        base.OnPoll(ctx);

        var v = ctx.Bridge.Batch(Screen + ".state", Screen + ".leveledup", Screen + ".giveexp",
                                 Screen + ".lastbattleexpreward", Screen + ".lastbattlegoldreward", Screen + ".nextlevelexp");
        var state = v[0].AsString();

        // The stars are handed over one by one; what is still needed is only right once they all are.
        if (state == "gainxp" && !_rewardsSaid && v[2].AsInt() == 0 && v[3].Exists)
        {
            _rewardsSaid = true;
            ctx.SayEvent(Strings.T("levelup.rewards", v[3].AsInt(), v[4].AsInt()));
            if (!v[1].AsBool() && v[5].AsInt() > 0) ctx.SayEvent(Strings.T("levelup.next", v[5].AsInt()));
        }

        if (v[1].AsBool() && !_leveled) { _leveled = true; ctx.SayEvent(Strings.T("levelup.leveled")); }
        _state = state;
    }

    /// <summary>A reward that is not a card: the caption drawn in the focused button's column.</summary>
    private static string ColumnCaption(ScreenContext ctx, NavButton focus)
    {
        var labels = new HashSet<string>(ctx.State.Buttons.Select(b => StageCleaner.Key(b.Label)));
        var lines = ctx.Stage.Current.Where(p => p.Y > 400 && p.Y < focus.Y - 20 && Math.Abs(p.X - focus.X) < 450 && !labels.Contains(p.Key))
                       .Select(p => p.Text);
        return string.Join(". ", lines);
    }

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
        if (focus == null || Cards.Count == 0 || FocusedCard(ctx) >= 0) return base.DescribeFocus(ctx);
        return TextCleanup.JoinSentences(ColumnCaption(ctx, focus), base.DescribeFocus(ctx));
    }

    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p)
        => base.ShouldSpeak(ctx, p) && !Multiplier().IsMatch(p.Text) && p.Y < 2100   // below: the map's status strip
           && !(_state == "inventorgadgets" && p.Y > 1400)                              // gadget captions: read with their button
           && !p.Text.TrimEnd().EndsWith(':');                                          // "Next level:" labels a row of stars

    public override string DescribeContext(ScreenContext ctx)
        => TextCleanup.JoinSentences(DungeonHandler.PlayerStatus(ctx), base.DescribeContext(ctx));
}
