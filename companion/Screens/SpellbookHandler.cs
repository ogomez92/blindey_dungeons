using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// The Witch's spellbook, opened from the map with Y where other characters have the
/// inventory (states.ViewSpellbook). Observed live:
///
///   right side   the book: six numbered pages (elements.Spellbook.spells[0..5], a page is
///                cast in battle by playing a dice of that number). Up / Down move
///                selectedspellindex; selectedequipment is that page's card.
///   left side    the prepared spells, ready at the start of every fight
///                (precastequipment[0..3]; gamepad_slot is the one being edited).
///   A            prepares the selected page in the current prepared slot.
///   LB / RB      show what the spell becomes when upgraded (previewupgraded,
///                selectedupgradedequipment).
///
/// Everything drawn is card faces and captions for these, so the stage is muted and the
/// screen is spoken from state.
/// </summary>
public sealed class SpellbookHandler : ScreenHandler
{
    private const string View = "{states.ViewSpellbook}";
    private const string Book = "{elements.Spellbook}";

    private string _cursor = "";
    private string _prepared = "";
    private bool _upgradedView;

    public override bool Handles(string scene) => scene == "states.ViewSpellbook";

    public override void OnEnter(ScreenContext ctx)
    {
        _cursor = "";
        _prepared = "";
        _upgradedView = false;
        base.OnEnter(ctx);
    }

    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p) => false;

    protected override void OnEntryRead(ScreenContext ctx)
    {
        ctx.SayEvent(Summary(ctx));
        ctx.SayEvent(Strings.T("spellbook.keys"));
    }

    private static List<string> Prepared(ScreenContext ctx)
        => (ctx.Bridge.Pluck(View + ".precastequipment", "name") ?? new()).Select(r => r[0].Exists ? r[0].AsString() : "").ToList();

    private static string Summary(ScreenContext ctx)
    {
        var pages = (ctx.Bridge.Pluck(Book + ".spells", "name") ?? new())
            .Select((r, i) => Strings.T("spellbook.page", i + 1, r[0].AsString().Length > 0 ? TextCleanup.Join(ctx.Translate(r[0].AsString()), Double(ctx, i)) : Strings.T("spellbook.empty")));
        var prepared = Prepared(ctx).Where(n => n.Length > 0).Select(ctx.Translate).ToList();
        return Strings.T("spellbook.summary", prepared.Count == 0 ? Strings.T("pack.none") : string.Join(", ", prepared), string.Join("; ", pages));
    }

    protected override string CursorState(ScreenContext ctx)
        => string.Join("|", ctx.Bridge.Batch(View + ".selectedspellindex", View + ".gamepad_slot").Select(v => v.Raw));

    public override void OnPoll(ScreenContext ctx)
    {
        base.OnPoll(ctx);
        if (!EntryRead) return;

        var names = Prepared(ctx);
        var prepared = string.Join("|", names);
        if (_prepared.Length > 0 && prepared != _prepared)
        {
            var slot = ctx.Bridge.Eval(View + ".gamepad_slot").AsInt();
            if (slot < names.Count && names[slot].Length > 0)
                ctx.Say(Strings.T("spellbook.prepared", ctx.Translate(names[slot]), slot + 1), interrupt: true);
        }
        _prepared = prepared;

        var upgradedView = ctx.Bridge.Eval(View + ".previewupgraded").AsBool();
        if (upgradedView != _upgradedView)
        {
            _upgradedView = upgradedView;
            ctx.Say(upgradedView
                ? Strings.T("smith.becomes", CardText.Full(ctx, View + ".selectedupgradedequipment"))
                : DescribePage(ctx), interrupt: true);
        }

        var cursor = CursorState(ctx);
        if (cursor == _cursor) return;
        var slotChanged = _cursor.Length > 0 && _cursor.Split('|')[1] != cursor.Split('|')[1];
        _cursor = cursor;
        ctx.Say(slotChanged ? DescribeSlot(ctx) : DescribePage(ctx), interrupt: RecentInput(ctx));
    }

    private static string DescribePage(ScreenContext ctx)
    {
        var index = ctx.Bridge.Eval(View + ".selectedspellindex").AsInt();
        var upgraded = ctx.Bridge.Eval(View + ".previewupgraded").AsBool();
        var card = CardText.Full(ctx, View + (upgraded ? ".selectedupgradedequipment" : ".selectedequipment"));
        var name = ctx.Bridge.Eval($"{Book}.spells[{index}].name").AsString();
        if (name.Length == 0 || card.Length == 0) return Strings.T("spellbook.page", index + 1, Strings.T("spellbook.empty"));
        var isPrepared = Prepared(ctx).Contains(name);
        return TextCleanup.JoinSentences(Strings.T("spellbook.page", index + 1, card), Double(ctx, index), isPrepared ? Strings.T("spellbook.is_prepared") : null);
    }

    /// <summary>A double spell's page shows two dice of its number (Spell.requirements has two entries): it takes two dice to cast.</summary>
    private static string? Double(ScreenContext ctx, int index)
        => ctx.Bridge.Eval($"{Book}.spells[{index}].requirements.length").AsInt() == 2 ? Strings.T("witch.double", index + 1) : null;

    private static string DescribeSlot(ScreenContext ctx)
    {
        var slot = ctx.Bridge.Eval(View + ".gamepad_slot").AsInt();
        var names = Prepared(ctx);
        var name = slot < names.Count ? names[slot] : "";
        return Strings.T("spellbook.slot", slot + 1, name.Length > 0 ? ctx.Translate(name) : Strings.T("spellbook.empty"));
    }

    public override string DescribeFocus(ScreenContext ctx) => TextCleanup.JoinSentences(DescribePage(ctx), DescribeSlot(ctx));

    public override string DescribeContext(ScreenContext ctx) => TextCleanup.JoinSentences(Summary(ctx), DungeonHandler.PlayerStatus(ctx));

    public override IEnumerable<string> Help => new[] { Strings.T("spellbook.keys") }.Concat(base.Help);
}
