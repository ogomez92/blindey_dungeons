using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// "You found: ..." after a chest, a shop purchase or a level-up reward
/// (states.PickupEquipment). The cards shown are its static founditems (elements.Equipment,
/// observed live); the only button is Take.
///
/// The Witch finds spells instead (witchmode): the screen draws her book on the right, six
/// pages (elements.Spellbook.spells) with "Empty Slot" or the spell's name, and Up / Down
/// move states.ViewSpellbook.selectedspellindex over them (the game renders it through
/// ViewSpellbook.spellbookrender_pickupequipment). No navigation button is registered: A is
/// "Learn spell" (writes the spell on that page, replacing what is there, and returns to the
/// map), B "Ignore it", LB shows the upgraded spell (witchupgradeditem, drawn where the found
/// card is: only the "+" of the drawn title tells them apart). Read as loose text, the pages
/// came out as bare names with no number, and an empty page as nothing at all ("Empty Slot"
/// had already been said), so a player could not tell which page the spell would go on.
/// </summary>
public sealed class PickupHandler : CardScreenHandler
{
    private const string Scene = "{states.PickupEquipment}";
    private const string Items = Scene + ".founditems";
    private const string Book = "{elements.Spellbook}";
    private const string Page = "{states.ViewSpellbook}.selectedspellindex";
    private const int BookLeft = 1900;     // observed: the pages are drawn from x 2090, the found spell ends near 1300
    private const int ButtonsTop = 1600;   // "LEARN SPELL" / "IGNORE IT", drawn text at y 1682

    private bool _witch;
    private int _page = -1;
    private bool _upgradedShown;
    private string _spell = "";

    public override bool Handles(string scene) => scene == "states.PickupEquipment";

    public override void OnEnter(ScreenContext ctx)
    {
        _witch = false;
        _page = -1;
        _upgradedShown = false;
        _spell = "";
        base.OnEnter(ctx);
    }

    protected override IEnumerable<string> CardExpressions(ScreenContext ctx)
    {
        var count = ctx.Bridge.Eval(Items + ".length").AsInt();
        return Enumerable.Range(0, count).Select(i => $"{Items}[{i}]");
    }

    /// <summary>
    /// A sighted player sees the card fly to the equipped row or to the backpack. The screen
    /// already knows which: spaceinequipment / spaceinbackpack. A spell goes in the book instead.
    /// </summary>
    protected override string CardNote(ScreenContext ctx, ShownCard card, int index)
    {
        if (_witch || index != Cards.Count - 1) return "";
        var v = ctx.Bridge.Batch(Scene + ".spaceinequipment", Scene + ".spaceinbackpack");
        if (!v[0].Exists) return "";
        return Strings.T(v[0].AsBool() ? "pickup.equips" : v[1].AsBool() ? "pickup.to_pack" : "pickup.no_room");
    }

    public override void OnPoll(ScreenContext ctx)
    {
        // Before the base poll: ShouldSpeak goes by both.
        var v = ctx.Bridge.Batch(Scene + ".witchmode", Items + "[0].name", Page);
        _witch = v[0].AsBool();
        _spell = _witch ? v[1].AsString() : "";
        base.OnPoll(ctx);
        if (!_witch || !EntryRead) return;

        var page = v[2].AsInt(-1);
        if (page >= 0 && page != _page)
        {
            var first = _page < 0;
            _page = page;
            if (first) ctx.SayEvent(DescribePage(ctx, page)); else ctx.Say(DescribePage(ctx, page), interrupt: true);
        }

        var upgraded = Cards.Count > 0 && ctx.Stage.Current.Any(p => Cards[0].Contains(p) && p.Text.TrimEnd().EndsWith('+'));
        if (upgraded != _upgradedShown)
        {
            _upgradedShown = upgraded;
            ctx.Say(upgraded ? Strings.T("smith.becomes", CardText.Full(ctx, Scene + ".witchupgradeditem")) : DescribeCard(ctx, 0), interrupt: true);
        }
    }

    protected override void OnEntryRead(ScreenContext ctx)
    {
        base.OnEntryRead(ctx);
        if (_witch) ctx.SayEvent(Strings.T("pickup.spell_keys"));
    }

    /// <summary>The spell is only in the book once the screen has closed; say which page it went on.</summary>
    public override void OnExit(ScreenContext ctx)
    {
        if (!_witch || _page < 0 || _spell.Length == 0) return;
        if (ctx.Bridge.Eval($"{Book}.spells[{_page}].name").AsString() == _spell)
            ctx.SayEvent(Strings.T("pickup.spell_learned", ctx.Translate(_spell), _page + 1));
    }

    /// <summary>"Page 5 of 6, empty" or "Page 2 of 6: Infliction. Do 3 damage": a page is cast with the dice of its number.</summary>
    private string DescribePage(ScreenContext ctx, int page)
    {
        var v = ctx.Bridge.Batch(Book + ".spells.length", $"{Book}.spells[{page}].name");
        var number = Strings.T("of", page + 1, v[0].AsInt());
        var name = v[1].AsString();
        var spell = ctx.Translate(_spell);
        if (name.Length == 0) return Strings.T("pickup.spell_page_empty", number, spell);
        var what = TextCleanup.JoinSentences(ctx.Translate(name), CardText.DescriptionByName(ctx, name), SpellbookHandler.Double(ctx, page));
        return Strings.T("pickup.spell_page", number, what, spell);
    }

    protected override string CursorState(ScreenContext ctx) => _witch ? ctx.Bridge.Eval(Page).Raw : base.CursorState(ctx);

    protected override string DescribeFocusBrief(ScreenContext ctx)
        => _witch && _page >= 0 ? DescribePage(ctx, _page) : base.DescribeFocusBrief(ctx);

    public override string DescribeFocus(ScreenContext ctx)
        => _witch && _page >= 0 && Cards.Count > 0 ? TextCleanup.JoinSentences(DescribeCard(ctx, 0), DescribePage(ctx, _page)) : base.DescribeFocus(ctx);

    /// <summary>
    /// The book's pages and the two captions under the spell are spoken from state, with their
    /// number and key; the spell's name drawn again under its card is already in the card.
    /// </summary>
    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p)
        => base.ShouldSpeak(ctx, p)
           && !(_witch && (p.X > BookLeft || p.Y > ButtonsTop || p.Key == StageCleaner.Key(ctx.Translate(_spell))));

    public override IEnumerable<string> Help
        => _witch ? new[] { Strings.T("pickup.spell_keys") }.Concat(base.Help) : base.Help;
}
