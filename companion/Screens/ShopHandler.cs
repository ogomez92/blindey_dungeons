using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// The shop (states.Shopstate). currentshop.contents is a list of elements.LevelUpReward,
/// each normally wrapping a card (.equipment); under every card sits a Buy button and a
/// "Cost: n gold" line. The game keeps the price only in that drawn line, so it is read
/// from the stage, from the column under the card. Observed live on floor 2.
///
/// On entry the player hears their gold and the list of wares with prices; moving between
/// the Buy buttons reads the whole card each time, because that is the decision being made.
/// </summary>
public sealed class ShopHandler : CardScreenHandler
{
    private const string Contents = "{states.Shopstate}.currentshop.contents";

    private int _gold = -1;
    private string _focusedWare = "";
    private string _focusedRaw = "";

    public override bool Handles(string scene) => scene == "states.Shopstate";

    protected override bool ReadCardsOnEntry => false;

    public override void OnEnter(ScreenContext ctx)
    {
        _gold = -1;
        base.OnEnter(ctx);
    }

    protected override IEnumerable<string> CardExpressions(ScreenContext ctx)
    {
        var count = ctx.Bridge.Eval(Contents + ".length").AsInt();
        return Enumerable.Range(0, count).Select(i => $"{Contents}[{i}].equipment");
    }

    protected override void OnEntryRead(ScreenContext ctx)
    {
        base.OnEntryRead(ctx);
        var wares = Enumerable.Range(0, Cards.Count)
            .Select(i => TextCleanup.Join(CardText.Name(ctx, Cards[i].Expression), Price(ctx, Cards[i]))).ToList();
        ctx.SayEvent(Strings.T("shop.summary", Gold(ctx), wares.Count == 0 ? Strings.T("pack.none") : string.Join("; ", wares)));
    }

    public override void OnPoll(ScreenContext ctx)
    {
        base.OnPoll(ctx);
        var gold = Gold(ctx);
        if (_gold >= 0 && gold != _gold) AnnouncePurchase(ctx, gold);
        _gold = gold;
        var focused = FocusedCard(ctx);
        if (focused >= 0)
        {
            _focusedWare = CardText.Name(ctx, Cards[focused].Expression);
            _focusedRaw = ctx.Bridge.Eval(Cards[focused].Expression + ".name").AsString();
        }
    }

    /// <summary>Buying usually closes the shop at once, so the purchase is often only noticed on the way out.</summary>
    public override void OnExit(ScreenContext ctx)
    {
        var gold = Gold(ctx);
        if (_gold >= 0 && gold < _gold) AnnouncePurchase(ctx, gold);
        base.OnExit(ctx);
    }

    private void AnnouncePurchase(ScreenContext ctx, int gold)
    {
        var text = gold < _gold && _focusedWare.Length > 0
            ? Strings.T("shop.bought", _focusedWare, gold)
            : Strings.T("shop.gold_left", gold);
        if (_focusedRaw.Length > 0 && BackpackHandler.AreaOf(ctx, _focusedRaw) is { Length: > 0 } area) text += " " + area + ".";
        ctx.SayEvent(text);
        _gold = gold;
    }

    private static int Gold(ScreenContext ctx) => ctx.Bridge.Eval("{Game}.player.gold").AsInt();

    /// <summary>The "Cost: 2 gold" line (or "Sold") drawn under a card, in its column.</summary>
    private static string Price(ScreenContext ctx, ShownCard card)
    {
        var labels = new HashSet<string>(ctx.State.Buttons.Select(b => StageCleaner.Key(b.Label)));
        var lines = ctx.Stage.Current.Where(p => p.Y >= card.Y + card.Height - 5 && p.Y < 1900
                                                 && p.X >= card.X - 40 && p.X <= card.X + card.Width
                                                 && !labels.Contains(p.Key))
                       .Select(p => p.Text);
        return string.Join(", ", lines);
    }

    protected override string CardNote(ScreenContext ctx, ShownCard card, int index)
    {
        var price = Price(ctx, card);
        var digits = new string(price.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        var tooDear = int.TryParse(digits, out var cost) && cost > Gold(ctx);
        return tooDear ? price + ", " + Strings.T("shop.cant_afford") : price;
    }

    // Price lines are spoken with their card, not as loose text.
    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p)
        => base.ShouldSpeak(ctx, p) && p.Y < 1900 && !Cards.Any(c => p.Y >= c.Y + c.Height - 5 && p.X >= c.X - 40 && p.X <= c.X + c.Width);

    public override string DescribeContext(ScreenContext ctx)
        => TextCleanup.JoinSentences(new[] { DungeonHandler.PlayerStatus(ctx) }
            .Concat(Enumerable.Range(0, Cards.Count).Select(i => DescribeCard(ctx, i))).ToArray());

    public override IEnumerable<string> Help => new[] { Strings.T("help.shop") }.Concat(base.Help);
}
