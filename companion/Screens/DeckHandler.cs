using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// The Jester's deck viewer (states.ViewDeck), opened from the map with Y. The deck is a
/// horizontal strip of cards; left and right scroll it (targetscrollposition, 0-based) and
/// the counter reads "3/9 CARDS". The strip's order is the viewer's own, not that of the
/// piles in elements.Deck, so the card under the cursor is read off the stage: it is the one
/// drawn in the middle column (observed: its title sits at x 1760, the next at 2720).
/// </summary>
public sealed class DeckHandler : ScreenHandler
{
    /// <summary>
    /// The finale's party view (states.ViewParty, "Group" on the map) is the same strip with
    /// the rescued contestants instead of cards: previewcards, same scroll statics.
    /// </summary>
    private string View = "{states.ViewDeck}";
    private bool _party;
    private const int ColumnLeft = 1450, ColumnRight = 2400, ColumnBottom = 1650;

    private int _position = -1;
    private int _pending = -1;

    /// <summary>
    /// And the Reunion Warrior's "Exercises" (states.ViewTraining, X on the map): the same strip
    /// of workout cards, or just two lines of explanation while there are none.
    /// </summary>
    private bool _training;

    public override bool Handles(string scene) => scene is "states.ViewDeck" or "states.ViewParty" or "states.ViewTraining";

    public override void OnEnter(ScreenContext ctx)
    {
        _position = _pending = -1;
        _party = ctx.State.Scene == "states.ViewParty";
        _training = ctx.State.Scene == "states.ViewTraining";
        View = "{" + ctx.State.Scene + "}";
        base.OnEnter(ctx);
    }

    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p) => _training && p.Y < 1900 && Face(ctx).Count == 0;

    protected override void OnEntryRead(ScreenContext ctx)
    {
        if (_training) { ctx.SayEvent(Strings.T("training.keys")); return; }
        Intro(ctx);
    }

    private void Intro(ScreenContext ctx) => ctx.SayEvent(Strings.T(_party ? "party.intro" : "deck.intro", Count(ctx)));

    private int Count(ScreenContext ctx)
        => _party ? ctx.Bridge.Eval(View + ".previewcards.length").AsInt() : new[] { "discardpile", "inplaypile", "drawpile" }.Sum(pile => (ctx.Bridge.Pluck("{elements.Deck}." + pile, "equipment.name") ?? new()).Count(r => r[0].Exists));

    protected override string CursorState(ScreenContext ctx) => ctx.Bridge.Eval(View + ".targetscrollposition").Raw;

    public override void OnPoll(ScreenContext ctx)
    {
        base.OnPoll(ctx);
        if (!EntryRead) return;
        var v = ctx.Bridge.Batch(View + ".targetscrollposition", View + ".actualscrollposition");
        var position = v[0].AsInt();
        if (position == _position) return;

        // The strip slides; the middle column only holds the right card once it has arrived.
        if (v[1].AsInt() != position || !ctx.Stage.Settled || position != _pending) { _pending = position; return; }
        _position = position;
        ctx.Say(DescribeFocus(ctx), interrupt: RecentInput(ctx));
    }

    private static List<string> Face(ScreenContext ctx)
        => ctx.Stage.Current.Where(p => p.X >= ColumnLeft && p.X <= ColumnRight && p.Y > 400 && p.Y < ColumnBottom).Select(p => p.Text).ToList();

    public override string DescribeFocus(ScreenContext ctx)
    {
        var face = Face(ctx);
        var position = ctx.Bridge.Eval(View + ".targetscrollposition").AsInt();
        if (_training) return face.Count == 0 ? Strings.T("training.none") : TextCleanup.JoinSentences(string.Join(". ", face), Strings.T("training.position", position + 1));
        return TextCleanup.JoinSentences(string.Join(". ", face), Strings.T("of", position + 1, Count(ctx)));
    }

    public override string DescribeContext(ScreenContext ctx)
    {
        if (_party) return TextCleanup.JoinSentences(DescribeFocus(ctx), DungeonHandler.PlayerStatus(ctx));
        var names = new[] { "discardpile", "inplaypile", "drawpile" }
            .SelectMany(pile => ctx.Bridge.Pluck("{elements.Deck}." + pile, "equipment.name", "equipment.namemodifier") ?? new())
            .Where(r => r[0].Exists).Select(r => CardText.DecorateName(ctx, r[0].AsString(), r[1].AsString()))
            .GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Count()} {g.Key}" : g.Key);
        return TextCleanup.JoinSentences(Strings.T("deck.list", string.Join(", ", names)), DungeonHandler.PlayerStatus(ctx));
    }

    public override IEnumerable<string> Help => new[] { Strings.T("deck.keys") }.Concat(base.Help);
}
