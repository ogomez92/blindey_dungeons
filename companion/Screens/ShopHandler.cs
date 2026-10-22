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
///
/// Not every ware is a card: an apple ("Restore 10 HP", type "apple") and an upgrade coupon
/// ("Upgrade Equipment", type "upgrade") have no equipment. Their caption is the ware's own
/// textfield[0] (a displayobjects.Print, centred at _x over its Buy button). Left out, their
/// Buy said only "Buy, 1 of 3", the entry counted only the cards, and buying one named the
/// card last focused instead (play-test, 2026-10-22).
/// </summary>
public sealed class ShopHandler : CardScreenHandler
{
    private const string Contents = "{states.Shopstate}.currentshop.contents";

    private int _gold = -1;
    private int _hp = -1;
    private string _focusedWare = "";
    private string _focusedRaw = "";

    /// <summary>A ware that is not a card: its caption, and where it is drawn (centre x, top y).</summary>
    private sealed record Ware(string Text, double X, double Y);

    private List<Ware> _wares = new();

    public override bool Handles(string scene) => scene == "states.Shopstate";

    protected override bool ReadCardsOnEntry => false;

    public override void OnEnter(ScreenContext ctx)
    {
        _gold = _hp = -1;
        _wares = new List<Ware>();
        base.OnEnter(ctx);
    }

    protected override IEnumerable<string> CardExpressions(ScreenContext ctx)
    {
        var count = ctx.Bridge.Eval(Contents + ".length").AsInt();
        return Enumerable.Range(0, count).Select(i => $"{Contents}[{i}].equipment");
    }

    /// <summary>Gold and how much is on offer; the wares themselves are met with the arrows (reading them all first was too long).</summary>
    protected override void OnEntryRead(ScreenContext ctx)
    {
        base.OnEntryRead(ctx);
        ctx.SayEvent(Strings.T("shop.summary", Gold(ctx), Cards.Count + _wares.Count) + " " + Strings.T("shop.browse"));
    }

    public override void OnPoll(ScreenContext ctx)
    {
        _wares = ReadWares(ctx);
        base.OnPoll(ctx);
        var gold = Gold(ctx);
        if (_gold >= 0 && gold != _gold) AnnouncePurchase(ctx, gold);
        _gold = gold;
        _hp = ctx.Bridge.Eval("{Game}.player.hp").AsInt();
        var focused = FocusedCard(ctx);
        if (focused >= 0)
        {
            _focusedWare = CardText.Name(ctx, Cards[focused].Expression);
            _focusedRaw = ctx.Bridge.Eval(Cards[focused].Expression + ".name").AsString();
        }
        else if (FocusedWare(ctx) is { } ware)
        {
            _focusedWare = ware.Text;
            _focusedRaw = "";
        }
    }

    private List<Ware> ReadWares(ScreenContext ctx)
    {
        var count = ctx.Bridge.Eval(Contents + ".length").AsInt();
        var paths = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var f = $"{Contents}[{i}].textfield[0]";
            paths.AddRange(new[] { $"{Contents}[{i}].equipment.name", f + ".textfield._text", f + "._x", f + "._y" });
        }
        var v = paths.Count == 0 ? Array.Empty<Bridge.GameValue>() : ctx.Bridge.Batch(paths);
        var wares = new List<Ware>();
        for (var i = 0; i + 3 < v.Length; i += 4)
        {
            if (v[i].Exists) continue;   // a card: described whole by CardScreenHandler
            var text = StageCleaner.Clean(v[i + 1].AsString());
            if (text.Length > 0) wares.Add(new Ware(text, v[i + 2].AsDouble(), v[i + 3].AsDouble()));
        }
        return wares;
    }

    /// <summary>The non-card ware whose Buy button has the focus (the button sits under its caption).</summary>
    private Ware? FocusedWare(ScreenContext ctx)
    {
        var focus = ctx.State.Focus;
        if (focus == null || FocusedCard(ctx) >= 0) return null;
        return _wares.Where(w => Math.Abs(w.X - focus.X) < WareColumn).OrderBy(w => Math.Abs(w.X - focus.X)).FirstOrDefault();
    }

    /// <summary>Half a ware's column: shop columns stand ~900 apart (seen: Buy at x 486, 1386, 2226).</summary>
    private const double WareColumn = 350;

    private static double CenterX(Paragraph p) => p.Width > 0 ? p.X + p.Width / 2.0 : p.X;

    /// <summary>The price line drawn under a non-card ware, in its column.</summary>
    private static string WarePrice(ScreenContext ctx, Ware ware)
    {
        var labels = new HashSet<string>(ctx.State.Buttons.Select(b => StageCleaner.Key(b.Label)));
        return string.Join(", ", ctx.Stage.Current.Where(p => InPriceBand(p, ware) && !labels.Contains(p.Key)).Select(p => p.Text));
    }

    private static bool InPriceBand(Paragraph p, Ware ware)
        => p.Y > ware.Y + 40 && p.Y < 1900 && Math.Abs(CenterX(p) - ware.X) < WareColumn && p.Key != StageCleaner.Key(ware.Text);

    public override string DescribeFocus(ScreenContext ctx)
    {
        if (FocusedWare(ctx) is not { } ware) return base.DescribeFocus(ctx);
        return TextCleanup.JoinSentences(ware.Text, PriceNote(ctx, WarePrice(ctx, ware)), base.DescribeFocus(ctx));
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
        // An apple heals at once, and the shop closes over it: the map would never say so.
        var hp = ctx.Bridge.Batch("{Game}.player.hp", "{Game}.player.maxhp");
        if (_hp >= 0 && hp[0].AsInt() > _hp) text += " " + Strings.T("map.healed", hp[0].AsInt() - _hp, hp[0].AsInt(), hp[1].AsInt());
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

    protected override string CardNote(ScreenContext ctx, ShownCard card, int index) => PriceNote(ctx, Price(ctx, card));

    private static string PriceNote(ScreenContext ctx, string price)
    {
        var digits = new string(price.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        var tooDear = int.TryParse(digits, out var cost) && cost > Gold(ctx);
        return tooDear ? price + ", " + Strings.T("shop.cant_afford") : price;
    }

    // Price lines are spoken with their card, not as loose text.
    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p)
        => base.ShouldSpeak(ctx, p) && p.Y < 1900 && !Cards.Any(c => p.Y >= c.Y + c.Height - 5 && p.X >= c.X - 40 && p.X <= c.X + c.Width)
           && !_wares.Any(w => p.Key == StageCleaner.Key(w.Text) || InPriceBand(p, w));

    public override string DescribeContext(ScreenContext ctx)
        => TextCleanup.JoinSentences(new[] { DungeonHandler.PlayerStatus(ctx) }
            .Concat(Enumerable.Range(0, Cards.Count).Select(i => DescribeCard(ctx, i)))
            .Concat(_wares.Select(w => TextCleanup.Join(w.Text, PriceNote(ctx, WarePrice(ctx, w))))).ToArray());

    public override IEnumerable<string> Help => new[] { Strings.T("help.shop") }.Concat(base.Help);
}
