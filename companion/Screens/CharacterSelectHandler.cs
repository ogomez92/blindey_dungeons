using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// Character select (states.CharacterSelect). Left and right walk the six contestants
/// (selectedcharacter); the name, biography or unlock condition are ordinary text and are
/// read by the generic handler. What the screen only draws is added here: the position in
/// the row, whether the contestant is unlocked (charavailable), the difficulty, which is a
/// row of pictures (availablecharactertemplates[i].difficulty) and how many episodes are
/// done (numepisodes). Observed live.
/// </summary>
public sealed class CharacterSelectHandler : ScreenHandler
{
    private const string Scene = "{states.CharacterSelect}";

    private int _selected = -1;

    public override bool Handles(string scene) => scene == "states.CharacterSelect";

    public override void OnEnter(ScreenContext ctx)
    {
        _selected = -1;
        base.OnEnter(ctx);
    }

    public override void OnPoll(ScreenContext ctx)
    {
        var selected = ctx.Bridge.Eval(Scene + ".selectedcharacter").AsInt(-1);
        if (selected != _selected && selected >= 0)
        {
            _selected = selected;
            ctx.Say(Describe(ctx, selected), interrupt: RecentInput(ctx));
        }
        base.OnPoll(ctx);
    }

    private static string Describe(ScreenContext ctx, int index)
    {
        var t = $"{Scene}.availablecharactertemplates[{index}]";
        var v = ctx.Bridge.Batch(t + ".name", t + ".difficulty", $"{Scene}.charavailable[{index}]", $"{Scene}.numepisodes[{index}]",
                                 Scene + ".availablecharactertemplates.length");
        if (!v[0].Exists) return "";
        var available = v[2].AsBool();
        return TextCleanup.Join(
            available ? ctx.Translate(v[0].AsString()) : Strings.T("charselect.locked"),
            Strings.T("of", index + 1, v[4].AsInt()),
            available ? Strings.T("charselect.difficulty", v[1].AsInt()) : null,
            available && v[3].AsInt() > 0 ? Strings.T("charselect.episodes", v[3].AsInt()) : null);
    }

    protected override string CursorState(ScreenContext ctx) => ctx.Bridge.Eval(Scene + ".selectedcharacter").Raw + ctx.State.FocusKey;

    public override string DescribeFocus(ScreenContext ctx)
    {
        var text = Describe(ctx, ctx.Bridge.Eval(Scene + ".selectedcharacter").AsInt());
        return ctx.State.Focus != null ? text + ". " + base.DescribeFocus(ctx) : text;
    }

    /// <summary>The name is part of the position announcement; "DIFFICULTY:" is followed by pictures only.</summary>
    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p)
    {
        if (p.Text.TrimEnd().EndsWith(':')) return false;
        var name = ctx.Bridge.Eval($"{Scene}.availablecharactertemplates[{_selected}].name");
        return !name.Exists || StageCleaner.Key(ctx.Translate(name.AsString())) != p.Key;
    }

    public override IEnumerable<string> Help => new[] { Strings.T("help.charselect") }.Concat(base.Help);
}
