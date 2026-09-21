using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// Base for the screens that lay cards out next to ordinary buttons: item found, shop,
/// level-up rewards, upgrade, trade. A card face on the stage is a handful of loose text
/// fields (title, slot words, rules lines) that the generic reading would speak out of
/// order and mixed with the button labels, so here the faces are muted and each card is
/// described whole from game state (<see cref="CardText"/>).
///
/// Which card belongs to the focused button follows the game's own rule, published in
/// Equipment.showgamepadhighlight(): the card whose horizontal span contains
/// ButtonGamepadNavigation.activebutton.x.
/// </summary>
public abstract class CardScreenHandler : ScreenHandler
{
    protected sealed record ShownCard(string Expression, double X, double Y, double Width, double Height)
    {
        public bool Contains(Paragraph p) => p.X >= X - 20 && p.X <= X + Width + 20 && p.Y >= Y - 20 && p.Y < Y + Height - 5;
        public bool Spans(double x) => x >= X - 10 && x <= X + Width + 10;
    }

    private List<ShownCard> _cards = new();
    private bool _cardsRead;

    /// <summary>Expressions of the elements.Equipment objects drawn on this screen right now.</summary>
    protected abstract IEnumerable<string> CardExpressions(ScreenContext ctx);

    /// <summary>Extra words for one card: a price, "sold", what it would become.</summary>
    protected virtual string CardNote(ScreenContext ctx, ShownCard card, int index) => "";

    /// <summary>False where the cards are met one by one through the buttons under them.</summary>
    protected virtual bool ReadCardsOnEntry => true;

    protected IReadOnlyList<ShownCard> Cards => _cards;

    /// <summary>Cards are described whole from game state here, not from the text over the button.</summary>
    protected override bool ReadColumnAboveTwins => false;

    public override void OnEnter(ScreenContext ctx)
    {
        _cards = new List<ShownCard>();
        _cardsRead = false;
        base.OnEnter(ctx);
    }

    public override void OnPoll(ScreenContext ctx)
    {
        _cards = ReadCards(ctx);
        base.OnPoll(ctx);
        if (EntryRead) ReadCardsOnce(ctx);   // cards that were not there yet when the text was read
    }

    protected override void OnEntryRead(ScreenContext ctx) => ReadCardsOnce(ctx);

    private void ReadCardsOnce(ScreenContext ctx)
    {
        if (_cardsRead || _cards.Count == 0) return;
        _cardsRead = true;
        if (ReadCardsOnEntry)
            for (var i = 0; i < _cards.Count; i++) ctx.SayEvent(DescribeCard(ctx, i));
    }

    private List<ShownCard> ReadCards(ScreenContext ctx)
    {
        var list = new List<ShownCard>();
        foreach (var e in CardExpressions(ctx))
        {
            var v = ctx.Bridge.Batch(e + ".name", e + ".x", e + ".y", e + ".width", e + ".height");
            if (v[0].Exists) list.Add(new ShownCard(e, v[1].AsDouble(), v[2].AsDouble(), v[3].AsDouble(), v[4].AsDouble()));
        }
        return list;
    }

    protected string DescribeCard(ScreenContext ctx, int index)
    {
        var card = _cards[index];
        return TextCleanup.JoinSentences(CardText.Full(ctx, card.Expression, withSize: true), CardNote(ctx, card, index));
    }

    /// <summary>The card standing over the focused button, or -1.</summary>
    protected int FocusedCard(ScreenContext ctx)
    {
        var focus = ctx.State.Focus;
        if (focus == null) return -1;
        return _cards.FindIndex(c => c.Spans(focus.X) || c.Spans(focus.X + 150));
    }

    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p) => !_cards.Any(c => c.Contains(p));

    /// <summary>"Buy" alone says nothing: name the card it belongs to first.</summary>
    public override string DescribeFocus(ScreenContext ctx)
    {
        var label = base.DescribeFocus(ctx);
        var index = FocusedCard(ctx);
        if (index < 0 || _cards.Count < 2 && ReadCardsOnEntry) return label;
        return TextCleanup.JoinSentences(DescribeCard(ctx, index), label);
    }

    public override string DescribeScreen(ScreenContext ctx)
    {
        var parts = new List<string>();
        var name = ScreenName(ctx);
        if (name.Length > 0) parts.Add(name);
        parts.AddRange(ctx.Stage.Current.Where(p => !_cards.Any(c => c.Contains(p))).Select(p => p.Text));
        for (var i = 0; i < _cards.Count; i++) parts.Add(DescribeCard(ctx, i));
        return parts.Count == 0 ? Strings.T("nothing.text") : string.Join(". ", parts);
    }
}
