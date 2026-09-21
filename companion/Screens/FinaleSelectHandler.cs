using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// "Final showdown": choosing who goes through the last door (states.FinaleCharacterSelection,
/// reached from the seventh entry of the character select once every episode is won). The
/// six contestants stand in a row as pictures only: charlist holds their names in order and
/// characterselection is the cursor. A chooses; the chosen one then speaks a few lines
/// (ordinary text, read by the generic path, A advances) before the run starts.
/// Observed live.
/// </summary>
public sealed class FinaleSelectHandler : ScreenHandler
{
    private const string Scene = "{states.FinaleCharacterSelection}";

    private int _selected = -1;

    public override bool Handles(string scene) => scene == "states.FinaleCharacterSelection";

    public override void OnEnter(ScreenContext ctx)
    {
        _selected = -1;
        base.OnEnter(ctx);
    }

    protected override void OnEntryRead(ScreenContext ctx) => ctx.SayEvent(Strings.T("finale.keys"));

    protected override string CursorState(ScreenContext ctx)
        => string.Join("|", ctx.Bridge.Batch(Scene + ".characterselection", Scene + ".state").Select(v => v.Raw));

    public override void OnPoll(ScreenContext ctx)
    {
        base.OnPoll(ctx);
        if (!EntryRead) return;
        var v = ctx.Bridge.Batch(Scene + ".characterselection", Scene + ".state");
        if (v[1].AsInt() != 0) { _selected = -1; return; }   // the chosen contestant is talking
        if (v[0].AsInt() == _selected) return;
        _selected = v[0].AsInt();
        ctx.Say(DescribeFocus(ctx), interrupt: RecentInput(ctx));
    }

    public override string DescribeFocus(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch(Scene + ".characterselection", Scene + ".charlist.length", Scene + ".state");
        if (v[2].AsInt() != 0) return base.DescribeScreen(ctx);
        var name = ctx.Bridge.Eval($"{Scene}.charlist[{v[0].AsInt()}]");
        return (name.Exists ? ctx.Translate(name.AsString()) : "?") + ", " + Strings.T("of", v[0].AsInt() + 1, v[1].AsInt());
    }

    public override IEnumerable<string> Help => new[] { Strings.T("finale.keys") }.Concat(base.Help);
}
