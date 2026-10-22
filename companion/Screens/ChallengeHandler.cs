using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// The challenges screen (states.ChallengeScreen, from character select once unlocked). A
/// strip of tiles along the bottom, one per enemy (challengepanel, 53 of them); left and
/// right move currentmonsterindex and the middle of the screen then shows that challenge:
/// its task, whether it is done, a Take prompt when a reward is waiting, and the enemy's
/// fact sheet once known. The tiles have no buttons, so the position comes from state and
/// the details from the text drawn above the strip. Observed live.
/// </summary>
public sealed class ChallengeHandler : ScreenHandler
{
    private const string Scene = "{states.ChallengeScreen}";
    private const int StripTop = 1600;

    private int _index = -1;
    private string _details = "";
    private bool _opening;   // the first challenge is read whole, position and details together

    public override bool Handles(string scene) => scene == "states.ChallengeScreen";

    public override void OnEnter(ScreenContext ctx)
    {
        _index = -1;
        _details = "";
        _opening = true;
        base.OnEnter(ctx);
    }

    /// <summary>The strip repeats "???" for every unknown enemy; the details are read with the position.</summary>
    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p) => false;

    protected override void OnEntryRead(ScreenContext ctx) => ctx.SayEvent(Strings.T("challenge.keys"));

    protected override string CursorState(ScreenContext ctx) => ctx.Bridge.Eval(Scene + ".currentmonsterindex").Raw;

    /// <summary>
    /// The enemy and its place are said the moment the cursor moves, from state; the challenge
    /// drawn in the middle follows once its panel has slid in and the text is still. Waiting for
    /// the panel before saying anything cost over half a second per arrow (measured 530 ms; 70 ms
    /// elsewhere), and players found the menu sluggish.
    /// </summary>
    public override void OnPoll(ScreenContext ctx)
    {
        base.OnPoll(ctx);
        if (!EntryRead) return;
        var index = ctx.Bridge.Eval(Scene + ".currentmonsterindex").AsInt(-1);
        if (index < 0) return;
        if (index != _index)
        {
            var first = _index < 0;
            _index = index;
            if (!first) { _opening = false; ctx.Say(Position(ctx), interrupt: RecentInput(ctx)); }
            return;
        }

        // Taking a reward keeps the cursor where it is but swaps the panel for the fact sheet.
        // The previous challenge's text, should it still be drawn, is what was said last.
        if (!ctx.Stage.Settled) return;
        var details = Details(ctx);
        if (details == _details) return;
        _details = details;
        ctx.Say(_opening ? DescribeFocus(ctx) : details, interrupt: false);
        _opening = false;
    }

    public override string DescribeFocus(ScreenContext ctx) => TextCleanup.JoinSentences(Position(ctx), Details(ctx));

    /// <summary>"Frog, 3 of 53", from state: known as soon as the cursor moves.</summary>
    private static string Position(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch(Scene + ".currentmonsterindex", Scene + ".currentmonster.name", Scene + ".currentmonster.unlocked",
                                 Scene + ".challengepanel.length");
        var name = v[2].AsBool() && v[1].Exists ? ctx.Translate(v[1].AsString()) : Strings.T("challenge.unknown");
        return name + ", " + Strings.T("of", v[0].AsInt() + 1, v[3].AsInt());
    }

    /// <summary>Everything above the strip except the Back prompt in the top left corner and the enemy's name, already said.</summary>
    private static string Details(ScreenContext ctx)
        => string.Join(". ", ctx.Stage.Current.Where(p => p.Y > 100 && p.Y < StripTop && p.X > -50 && !(p.X < 700 && p.Y < 250))
                                .Select(p => p.Text.TrimEnd('.')));

    public override IEnumerable<string> Help => new[] { Strings.T("challenge.keys") }.Concat(base.Help);
}
