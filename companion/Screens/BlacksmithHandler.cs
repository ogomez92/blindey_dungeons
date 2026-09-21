using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// The upgrade anvil (states.Blacksmith). The player's cards are laid out in the same grid
/// as in the inventory and the cursor is a cell: gamepad_column / gamepad_row, over the
/// equipped cards (upgradefrom == "equipment") or the backpack ("backpack"; Y switches
/// between them). Beside the grid the game draws the card as it is and as it would become:
/// states.Blacksmith.upgradedversion is that second card. A upgrades the card under the
/// cursor. The shopkeeper's line is blacksmithtext_split, already translated. Observed live.
/// </summary>
public sealed class BlacksmithHandler : ScreenHandler
{
    /// <summary>
    /// The copy shop (states.Copyshop, in episodes that replace the anvil) is the same screen
    /// with the same statics; Enter duplicates the card instead and there is no "after" card.
    /// </summary>
    private string Scene = "{states.Blacksmith}";
    private bool _copy;

    private string _cursor = "";
    private bool _first;
    private bool _popup;

    public override bool Handles(string scene) => scene is "states.Blacksmith" or "states.Copyshop";

    public override void OnEnter(ScreenContext ctx)
    {
        _cursor = "";
        _first = true;
        _copy = ctx.State.Scene == "states.Copyshop";
        Scene = "{" + ctx.State.Scene + "}";
        base.OnEnter(ctx);
    }

    protected override void OnEntryRead(ScreenContext ctx)
    {
        var chatter = string.Join(" ", ctx.Bridge.Dump(Scene + ".blacksmithtext_split").Select(l => StageCleaner.Clean(l.Value.AsString())));
        if (chatter.Length > 0) ctx.SayEvent(chatter);
        ctx.SayEvent(Strings.T(_copy ? "copy.intro" : "smith.intro"));
    }

    /// <summary>Everything drawn here is either a card face or the chatter; both are spoken from state.</summary>
    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p) => false;

    public override void OnPoll(ScreenContext ctx)
    {
        base.OnPoll(ctx);
        if (!EntryRead) return;

        // After an upgrade the grid gives way to "Equipment upgraded!", the new card and an OK
        // that is drawn but is not a navigation button: numbought goes to 1 and A leaves.
        var popup = IsPopup(ctx);
        if (popup && !_popup) ctx.Say(DescribePopup(ctx), interrupt: true);
        _popup = popup;
        if (popup) { _cursor = ""; return; }

        var state = CursorState(ctx);
        if (state == _cursor) return;
        var modeChanged = !_first && _cursor.Split('|')[0] != state.Split('|')[0];
        _cursor = state;
        _first = false;
        var text = DescribeCell(ctx);
        if (modeChanged) text = Strings.T(InPack(ctx) ? "pack.area.pack" : "pack.area.equipped") + ". " + text;
        ctx.Say(text, interrupt: RecentInput(ctx));
    }

    protected override string CursorState(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch(Scene + ".upgradefrom", Scene + ".gamepad_column", Scene + ".gamepad_row", Scene + ".numbought");
        return string.Join("|", v.Select(x => x.Raw));
    }

    private bool InPack(ScreenContext ctx) => ctx.Bridge.Eval(Scene + ".upgradefrom").AsString() == "backpack";

    private string DescribeCell(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch(Scene + ".gamepad_column", Scene + ".gamepad_row");
        var container = InPack(ctx) ? "{Inventory}.backpack" : "{Inventory}.equipmentslots";
        var card = $"{container}.getitem({v[0].AsInt()},{v[1].AsInt()}).equipment";
        var place = Strings.T("pack.cell_short", v[0].AsInt() + 1, v[1].AsInt() + 1);
        var now = CardText.Full(ctx, card, withSize: true);
        if (now.Length == 0) return Strings.T("pack.empty") + ". " + place;
        if (_copy) return TextCleanup.JoinSentences(now, Strings.T("copy.hint"), place);

        var upgraded = Scene + ".upgradedversion";
        var same = ctx.Bridge.Eval(upgraded + ".name").AsString() == ctx.Bridge.Eval(card + ".name").AsString();
        var after = same ? CardText.Full(ctx, upgraded, withSize: true) : "";
        var already = ctx.Bridge.Eval(card + ".upgraded").AsBool();
        return TextCleanup.JoinSentences(now,
            already ? Strings.T("smith.already") : after.Length > 0 ? Strings.T("smith.becomes", after) : Strings.T("smith.cannot"),
            place);
    }

    private bool IsPopup(ScreenContext ctx) => ctx.Bridge.Eval(Scene + ".numbought").AsInt() > 0;

    private string DescribePopup(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch(Scene + ".gamepad_column", Scene + ".gamepad_row");
        var container = InPack(ctx) ? "{Inventory}.backpack" : "{Inventory}.equipmentslots";
        var card = CardText.Full(ctx, $"{container}.getitem({v[0].AsInt()},{v[1].AsInt()}).equipment", withSize: true);
        return TextCleanup.JoinSentences(Strings.T(_copy ? "copy.done" : "smith.done", card), Strings.T("smith.continue"));
    }

    public override string DescribeFocus(ScreenContext ctx) => IsPopup(ctx) ? DescribePopup(ctx) : DescribeCell(ctx);

    public override string DescribeContext(ScreenContext ctx)
        => TextCleanup.JoinSentences(Strings.T(_copy ? "copy.intro" : "smith.intro"), DungeonHandler.PlayerStatus(ctx));

    public override IEnumerable<string> Help => new[] { Strings.T("help.smith") }.Concat(base.Help);
}
