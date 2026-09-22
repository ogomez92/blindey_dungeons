using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// The trade node (states.Barterstate): one of your cards (yourdeal) for one of theirs
/// (theirdeal), both elements.Equipment. "Deal!" and "No deal" are drawn as buttons but are
/// not navigation buttons: A accepts and B refuses. Refusing can bring another offer
/// (currenttrade.rejected counts them), so a change of either card is announced as a new
/// offer. Observed live on floor 4.
/// </summary>
public sealed class BarterHandler : CardScreenHandler
{
    private const string Scene = "{states.Barterstate}";

    private string _offer = "";
    private string _theirs = "";
    private bool _ownedBefore;

    public override bool Handles(string scene) => scene == "states.Barterstate";

    protected override bool ReadCardsOnEntry => false;

    protected override IEnumerable<string> CardExpressions(ScreenContext ctx) => new[] { Scene + ".yourdeal", Scene + ".theirdeal" };

    public override void OnEnter(ScreenContext ctx)
    {
        _offer = "";
        base.OnEnter(ctx);
    }

    public override void OnPoll(ScreenContext ctx)
    {
        base.OnPoll(ctx);
        if (!EntryRead) return;
        var v = ctx.Bridge.Batch(Scene + ".yourdeal.name", Scene + ".theirdeal.name");
        var offer = v[0].Raw + "|" + v[1].Raw;
        if (offer == _offer) return;
        _offer = offer;
        _theirs = v[1].AsString();
        _ownedBefore = BackpackHandler.Owns(ctx, _theirs);
        if (v[0].Exists && v[1].Exists)
        {
            ctx.SayEvent(DescribeOffer(ctx));
            // The item list follows the offer (it kept the map's lines: Ctrl+Up here once read "your space, 1 of 2").
            ctx.FocusItem(new[]
            {
                Strings.T("barter.give", CardText.Full(ctx, Scene + ".yourdeal", withSize: true)),
                Strings.T("barter.get", CardText.Full(ctx, Scene + ".theirdeal", withSize: true)),
                Strings.T("barter.keys"),
            });
        }
    }

    /// <summary>The deal is struck with a handshake animation and straight back to the map: say how it ended.</summary>
    public override void OnExit(ScreenContext ctx)
    {
        if (_theirs.Length > 0)
        {
            var area = BackpackHandler.AreaOf(ctx, _theirs);
            ctx.SayEvent(area.Length > 0 && !_ownedBefore
                ? Strings.T("barter.done", ctx.Translate(_theirs), area)
                : Strings.T("barter.refused"));
        }
        base.OnExit(ctx);
    }

    private static string DescribeOffer(ScreenContext ctx)
        => TextCleanup.JoinSentences(
            Strings.T("barter.give", CardText.Full(ctx, Scene + ".yourdeal", withSize: true)),
            Strings.T("barter.get", CardText.Full(ctx, Scene + ".theirdeal", withSize: true)),
            Strings.T("barter.keys"));

    /// <summary>The two captions are not controls the cursor can reach; the key hint replaces them. The strip is the map's.</summary>
    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p)
        => base.ShouldSpeak(ctx, p) && p.Y < 1700;

    public override string DescribeFocus(ScreenContext ctx)
        => ctx.State.Focus != null ? base.DescribeFocus(ctx) : DescribeOffer(ctx);

    public override string DescribeContext(ScreenContext ctx)
        => TextCleanup.JoinSentences(DescribeOffer(ctx), DungeonHandler.PlayerStatus(ctx));

    public override IEnumerable<string> Help => new[] { Strings.T("barter.keys") }.Concat(base.Help);
}
