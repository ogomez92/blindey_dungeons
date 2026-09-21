using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// "Choose a rule change" (states.Remixstate), between floors in the Bonus Round episodes.
/// The offers are remixoptions (elements.templates.RemixTemplate: name, fulldescription;
/// when lastoneisrandom the last is "a different rule chosen at random"), the cursor is
/// highlightedremix (up / down), A takes it. The rules already in force are remixes.
/// The options are panels, not buttons, so they are read from state. Observed live.
/// </summary>
public sealed class RemixHandler : ScreenHandler
{
    private const string Scene = "{states.Remixstate}";

    private int _highlighted = -2;

    public override bool Handles(string scene) => scene == "states.Remixstate";

    public override void OnEnter(ScreenContext ctx)
    {
        _highlighted = -2;
        base.OnEnter(ctx);
    }

    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p) => false;

    protected override void OnEntryRead(ScreenContext ctx)
    {
        var current = Current(ctx);
        if (Reviewing(ctx)) { ctx.SayEvent(Strings.T("remix.review", current.Length > 0 ? current : Strings.T("pack.none"))); return; }
        ctx.SayEvent(Strings.T("remix.intro", Count(ctx)) + (current.Length > 0 ? " " + Strings.T("remix.current", current) : ""));
    }

    /// <summary>The same scene, opened from the map with X just to look at the rules in force (previewmode).</summary>
    private static bool Reviewing(ScreenContext ctx) => ctx.Bridge.Eval(Scene + ".previewmode").AsBool();

    private static int Count(ScreenContext ctx) => ctx.Bridge.Eval(Scene + ".remixoptions.length").AsInt();

    private static string Describe(ScreenContext ctx, string template)
    {
        var v = ctx.Bridge.Batch(template + ".name", template + ".fulldescription");
        if (!v[0].Exists || v[0].AsString().Length == 0) return "";
        return TextCleanup.JoinSentences(ctx.Translate(v[0].AsString()), ctx.Translate(v[1].AsString()));
    }

    private static string Current(ScreenContext ctx)
    {
        var count = ctx.Bridge.Eval(Scene + ".remixes.length").AsInt();
        return string.Join("; ", Enumerable.Range(0, count).Select(i => Describe(ctx, $"{Scene}.remixes[{i}]")).Where(t => t.Length > 0));
    }

    protected override string CursorState(ScreenContext ctx) => ctx.Bridge.Eval(Scene + ".highlightedremix").Raw;

    public override void OnPoll(ScreenContext ctx)
    {
        base.OnPoll(ctx);
        if (!EntryRead) return;
        var v = ctx.Bridge.Batch(Scene + ".highlightedremix", Scene + ".state");
        if (Reviewing(ctx) || v[1].AsString() != "selection" || v[0].AsInt() == _highlighted) return;
        _highlighted = v[0].AsInt();
        ctx.Say(DescribeFocus(ctx), interrupt: RecentInput(ctx));
    }

    public override string DescribeFocus(ScreenContext ctx)
    {
        if (Reviewing(ctx)) return Strings.T("remix.review", Current(ctx));
        var index = ctx.Bridge.Eval(Scene + ".highlightedremix").AsInt();
        var count = Count(ctx);
        if (index < 0 || index >= count) return Strings.T("nothing.focus");
        var text = Describe(ctx, $"{Scene}.remixoptions[{index}]");
        return TextCleanup.JoinSentences(text, Strings.T("of", index + 1, count));
    }

    public override string DescribeContext(ScreenContext ctx)
    {
        if (Reviewing(ctx)) return Strings.T("remix.review", Current(ctx));
        var options = Enumerable.Range(0, Count(ctx)).Select(i => Describe(ctx, $"{Scene}.remixoptions[{i}]"));
        var current = Current(ctx);
        return TextCleanup.JoinSentences(Strings.T("remix.options", string.Join("; ", options)),
                                         current.Length > 0 ? Strings.T("remix.current", current) : null);
    }

    public override IEnumerable<string> Help => new[] { Strings.T("remix.keys") }.Concat(base.Help);
}
