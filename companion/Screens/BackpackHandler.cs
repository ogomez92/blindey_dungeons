using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// The inventory (states.Backpack), opened from the map with Y. Also the screen the
/// upgrade anvil and the copy shop use to pick a card.
///
/// Two grids of cells (elements.ItemContainer, contents[column][row] of Collectible):
/// Inventory.backpack on the left and Inventory.equipmentslots, what is taken into battle,
/// on the right. The game's gamepad cursor is a cell: states.Backpack.gamepad_column /
/// gamepad_row / gamepad_inbackpack; moving left off the equipped grid enters the backpack.
/// A picks the card under the cursor up (Game.active_collectible), A again puts it down in
/// the cell under the cursor. All of it observed live.
///
/// Nothing here is a button, so the generic reading has nothing to follow: the cell under
/// the cursor is described from the containers.
/// </summary>
public sealed class BackpackHandler : ScreenHandler
{
    private const string Scene = "{states.Backpack}";
    private const string Pack = "{Inventory}.backpack";
    private const string Equipped = "{Inventory}.equipmentslots";
    private const string Held = "{Game}.active_collectible";

    private string _cursor = "";
    private string _held = "";
    private bool _inPack;
    private bool _first;
    private string _under = "";   // internal name of the card in the cell under the cursor
    private HashSet<string> _cardNames = new();

    public override bool Handles(string scene) => scene == "states.Backpack";

    public override void OnEnter(ScreenContext ctx)
    {
        _cursor = "";
        _held = "";
        _first = true;
        base.OnEnter(ctx);
        ctx.SayEvent(Summary(ctx));
    }

    public override void OnPoll(ScreenContext ctx)
    {
        _cardNames = AllCards(ctx).Select(c => StageCleaner.Key(c.Name)).ToHashSet();
        if (_held.Length > 0) _cardNames.Add(StageCleaner.Key(ctx.Translate(_held)));   // drawn under the cursor while carried
        base.OnPoll(ctx);

        var v = ctx.Bridge.Batch(Scene + ".gamepad_column", Scene + ".gamepad_row", Scene + ".gamepad_inbackpack", Held + ".equipment.name");
        if (!v[0].Exists) return;
        var inPack = v[2].AsBool();
        var held = v[3].Exists ? v[3].AsString() : "";

        if (held != _held)
        {
            var was = _held;
            _held = held;
            if (held.Length > 0) ctx.Say(Strings.T("pack.picked", ctx.Translate(held)), interrupt: true);
            else
            {
                // Putting a card down on another one swaps them: the other goes where there is room.
                var text = Strings.T("pack.dropped", ctx.Translate(was), Where(inPack, v[0].AsInt(), v[1].AsInt()));
                if (_under.Length > 0 && _under != was)
                {
                    var now = AllCards(ctx).FirstOrDefault(c => c.RawName == _under);
                    if (now != null) text += " " + Strings.T("pack.displaced", now.Name, Where(now.InPack, now.Column, now.Row));
                }
                ctx.Say(text, interrupt: true);
            }
            _cursor = Key(v);
            return;
        }
        _under = ctx.Bridge.Eval($"{(inPack ? Pack : Equipped)}.getitem({v[0].AsInt()},{v[1].AsInt()}).equipment.name") is { Exists: true } u ? u.AsString() : "";

        if (Key(v) == _cursor) return;
        _cursor = Key(v);
        var areaChanged = _first || inPack != _inPack;
        _first = false;
        _inPack = inPack;
        ctx.Say(DescribeCell(ctx, inPack, v[0].AsInt(), v[1].AsInt(), areaChanged), interrupt: RecentInput(ctx));
    }

    protected override string CursorState(ScreenContext ctx)
        => Key(ctx.Bridge.Batch(Scene + ".gamepad_column", Scene + ".gamepad_row", Scene + ".gamepad_inbackpack")) + _held;

    private static string Key(Bridge.GameValue[] v) => $"{v[2].Raw}:{v[0].Raw}:{v[1].Raw}";

    private static string Area(bool inPack) => Strings.T(inPack ? "pack.area.pack" : "pack.area.equipped");

    private static string Where(bool inPack, int column, int row) => Strings.T("pack.cell", Area(inPack), column + 1, row + 1);

    private string DescribeCell(ScreenContext ctx, bool inPack, int column, int row, bool sayArea)
    {
        var item = $"{(inPack ? Pack : Equipped)}.getitem({column},{row})";
        var card = CardText.Full(ctx, item + ".equipment", withSize: true);
        var holding = _held.Length > 0 ? ctx.Translate(_held) : "";
        var content = card.Length > 0
            ? (holding.Length > 0 ? card + ". " + Strings.T("pack.swap_hint", holding) : card)
            : holding.Length > 0 ? Strings.T("pack.empty_drop", holding) : Strings.T("pack.empty");
        var place = sayArea ? Where(inPack, column, row) : Strings.T("pack.cell_short", column + 1, row + 1);
        return sayArea ? place + ". " + content : content + ". " + place;
    }

    // ------------------------------------------------------------------ lists

    /// <summary>True when the player owns a card with this internal name, equipped or stored.</summary>
    public static bool Owns(ScreenContext ctx, string rawName) => AllCards(ctx).Any(c => c.RawName == rawName);

    /// <summary>"Equipped" or "Backpack" for a card the player owns, empty otherwise.</summary>
    public static string AreaOf(ScreenContext ctx, string rawName)
    {
        var card = AllCards(ctx).FirstOrDefault(c => c.RawName == rawName);
        return card == null ? "" : Area(card.InPack);
    }

    private sealed record Stored(string Name, bool InPack, int Column, int Row, string RawName = "");

    private static List<Stored> AllCards(ScreenContext ctx)
    {
        var list = new List<Stored>();
        foreach (var (container, inPack) in new[] { (Equipped, false), (Pack, true) })
        {
            var size = ctx.Bridge.Batch(container + ".width", container + ".height");
            if (!size[0].Exists) continue;
            var cells = new List<string>();
            for (var row = 0; row < size[1].AsInt(); row++)
                for (var column = 0; column < size[0].AsInt(); column++)
                    cells.Add($"{container}.contents[{column}][{row}].equipment.name");
            var names = ctx.Bridge.Batch(cells);
            for (var i = 0; i < names.Length; i++)
                if (names[i].Exists)
                    list.Add(new Stored(ctx.Translate(names[i].AsString()), inPack, i % size[0].AsInt(), i / size[0].AsInt(), names[i].AsString()));
        }
        return list;
    }

    private string Summary(ScreenContext ctx)
    {
        var cards = AllCards(ctx);
        string List(bool inPack)
        {
            var names = cards.Where(c => c.InPack == inPack).Select(c => c.Name).ToList();
            return names.Count == 0 ? Strings.T("pack.none") : string.Join(", ", names);
        }
        return Strings.T("pack.summary", List(false), List(true));
    }

    public override string DescribeFocus(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch(Scene + ".gamepad_column", Scene + ".gamepad_row", Scene + ".gamepad_inbackpack");
        if (!v[0].Exists) return Strings.T("nothing.focus");
        var text = DescribeCell(ctx, v[2].AsBool(), v[0].AsInt(), v[1].AsInt(), sayArea: true);
        return _held.Length > 0 ? Strings.T("pack.holding", ctx.Translate(_held)) + ". " + text : text;
    }

    public override string DescribeContext(ScreenContext ctx)
    {
        var parts = new List<string> { Summary(ctx).TrimEnd('.') };
        foreach (var (container, inPack) in new[] { (Equipped, false), (Pack, true) })
        {
            var size = ctx.Bridge.Batch(container + ".width", container + ".height");
            if (size[0].Exists) parts.Add(Strings.T("pack.grid", Area(inPack), size[0].AsInt(), size[1].AsInt()));
        }
        if (_held.Length > 0) parts.Add(Strings.T("pack.holding", ctx.Translate(_held)));
        return string.Join(". ", parts);
    }

    /// <summary>
    /// Card names and the preview of the hovered card are what the cursor reading covers;
    /// the status bar along the bottom is the map's. Headings and instructions stay.
    /// </summary>
    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p)
    {
        if (p.Y > 1900) return false;
        if (p.X > 2300 && p.Y < 1000) return false;   // the enlarged copy of the card under the cursor
        return !_cardNames.Contains(p.Key);
    }

    public override IEnumerable<string> Help => new[] { Strings.T("help.pack") }.Concat(base.Help);
}
