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
///
/// A large card (Equipment.size 2) fills two cells of a column. It is stored in its top cell
/// only (contents[c][r]); getitem(c, r + 1) returns the same card. The game's cursor can
/// stand on either half (Up wraps from row 1 to the lower half), so both halves are named
/// "column 1, rows 1 and 2": read cell by cell, a player heard the Sword twice as if it took
/// two slots, and could not work out how many slots there were.
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
    private string _lastCard = "";   // area, column and top row of the card last described
    private HashSet<string> _cardNames = new();

    public override bool Handles(string scene) => scene == "states.Backpack";

    public override void OnEnter(ScreenContext ctx)
    {
        _cursor = "";
        _held = "";
        _lastCard = "";
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
                var cards = AllCards(ctx);
                var placed = cards.FirstOrDefault(c => c.RawName == was && c.InPack == inPack && c.Column == v[0].AsInt());
                var text = Strings.T("pack.dropped", ctx.Translate(was), placed != null ? Where(placed) : Where(inPack, v[0].AsInt(), v[1].AsInt(), 1));
                if (_under.Length > 0 && _under != was)
                {
                    var now = cards.FirstOrDefault(c => c.RawName == _under);
                    if (now != null) text += " " + Strings.T("pack.displaced", now.Name, Where(now));
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

        // The other half of the large card just read: the same card, not another slot.
        var cell = Cell(ctx, inPack, v[0].AsInt(), v[1].AsInt());
        var card = cell.Name.Length > 0 ? $"{inPack}:{v[0].Raw}:{cell.Top}" : "";
        var sameCard = card.Length > 0 && card == _lastCard && !areaChanged;
        _lastCard = card;
        ctx.Say(sameCard ? Strings.T("pack.same_card", ctx.Translate(cell.Name), Place(cell, v[0].AsInt(), false))
                         : DescribeCell(ctx, inPack, v[0].AsInt(), v[1].AsInt(), areaChanged), interrupt: RecentInput(ctx));
    }

    protected override string CursorState(ScreenContext ctx)
        => Key(ctx.Bridge.Batch(Scene + ".gamepad_column", Scene + ".gamepad_row", Scene + ".gamepad_inbackpack")) + _held;

    private static string Key(Bridge.GameValue[] v) => $"{v[2].Raw}:{v[0].Raw}:{v[1].Raw}";

    private static string Area(bool inPack) => Strings.T(inPack ? "pack.area.pack" : "pack.area.equipped");

    /// <summary>"Equipped, column 1, row 2", or "column 1, rows 1 and 2" for a large card.</summary>
    private static string Where(bool inPack, int column, int top, int size)
        => size > 1 ? Strings.T("pack.cell_large", Area(inPack), column + 1, top + 1, top + 2) : Strings.T("pack.cell", Area(inPack), column + 1, top + 1);

    private static string Where(Stored card) => Where(card.InPack, card.Column, card.Row, card.Size);

    /// <summary>What a cell holds: the card's internal name (empty if none), its size, and the row it starts on.</summary>
    private sealed record CellInfo(string Name, int Size, int Top, bool InPack);

    private static CellInfo Cell(ScreenContext ctx, bool inPack, int column, int row)
    {
        var container = inPack ? Pack : Equipped;
        var v = ctx.Bridge.Batch($"{container}.getitem({column},{row}).equipment.name", $"{container}.getitem({column},{row}).equipment.size",
                                 $"{container}.contents[{column}][{row}].equipment.name");
        if (!v[0].Exists) return new CellInfo("", 1, row, inPack);
        var size = Math.Max(1, v[1].AsInt());
        // Not stored in this cell, yet the cell holds a card: the lower half of the large card above.
        return new CellInfo(v[0].AsString(), size, v[2].Exists ? row : Math.Max(0, row - size + 1), inPack);
    }

    private static string Place(CellInfo cell, int column, bool withArea)
    {
        if (withArea) return Where(cell.InPack, column, cell.Top, cell.Name.Length > 0 ? cell.Size : 1);
        return cell.Name.Length > 0 && cell.Size > 1
            ? Strings.T("pack.cell_large_short", column + 1, cell.Top + 1, cell.Top + 2)
            : Strings.T("pack.cell_short", column + 1, cell.Top + 1);
    }

    private string DescribeCell(ScreenContext ctx, bool inPack, int column, int row, bool sayArea)
    {
        var item = $"{(inPack ? Pack : Equipped)}.getitem({column},{row})";
        var cell = Cell(ctx, inPack, column, row);
        var card = CardText.Full(ctx, item + ".equipment", withSize: true);
        var holding = _held.Length > 0 ? ctx.Translate(_held) : "";
        var content = card.Length > 0
            ? (holding.Length > 0 ? card + ". " + Strings.T("pack.swap_hint", holding) : card)
            : holding.Length > 0 ? Strings.T("pack.empty_drop", holding) : Strings.T("pack.empty");
        var place = Place(cell, column, sayArea);
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

    private sealed record Stored(string Name, bool InPack, int Column, int Row, string RawName = "", int Size = 1);

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
                {
                    cells.Add($"{container}.contents[{column}][{row}].equipment.name");
                    cells.Add($"{container}.contents[{column}][{row}].equipment.size");
                }
            var values = ctx.Bridge.Batch(cells);
            for (var i = 0; i < values.Length / 2; i++)
                if (values[i * 2].Exists)
                    list.Add(new Stored(ctx.Translate(values[i * 2].AsString()), inPack, i % size[0].AsInt(), i / size[0].AsInt(),
                                        values[i * 2].AsString(), Math.Max(1, values[i * 2 + 1].AsInt())));
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
        return TextCleanup.JoinSentences(Strings.T("pack.summary", List(false), List(true)), Capacity(ctx));
    }

    /// <summary>"Equipped has 3 columns of 2 cells, the backpack 4 of 5": a sighted player sees the empty slots.</summary>
    private static string? Capacity(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch(Equipped + ".width", Equipped + ".height", Pack + ".width", Pack + ".height");
        return v[0].Exists && v[2].Exists ? Strings.T("pack.capacity", v[0].AsInt(), v[1].AsInt(), v[2].AsInt(), v[3].AsInt()) : null;
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
        var summary = Summary(ctx);
        return _held.Length > 0 ? TextCleanup.JoinSentences(summary, Strings.T("pack.holding", ctx.Translate(_held))) : summary;
    }

    /// <summary>
    /// Card names and the preview of the hovered card are what the cursor reading covers;
    /// the status bar along the bottom is the map's. The two grid headings ("IN BACKPACK" at
    /// 848,176 and "EQUIPPED" at 2723,1056, observed) came after the first cell and are said
    /// with every cell anyway. Instructions stay.
    /// </summary>
    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p)
    {
        if (p.Y > 1900) return false;
        if (p.X > 2300 && p.Y < 1000) return false;   // the enlarged copy of the card under the cursor
        if (p.X < 1500 && p.Y < 300 || p.X > 2300 && Math.Abs(p.Y - 1056) < 60) return false;   // the grid headings
        return !_cardNames.Contains(p.Key);
    }

    /// <summary>A dead arrow (Down on the top half of a large card) repeats only the card and where it is.</summary>
    protected override string DescribeFocusBrief(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch(Scene + ".gamepad_column", Scene + ".gamepad_row", Scene + ".gamepad_inbackpack");
        if (!v[0].Exists) return Strings.T("nothing.focus");
        var cell = Cell(ctx, v[2].AsBool(), v[0].AsInt(), v[1].AsInt());
        return cell.Name.Length == 0 ? Strings.T("pack.empty") + ". " + Place(cell, v[0].AsInt(), false)
                                     : Strings.T("pack.same_card", ctx.Translate(cell.Name), Place(cell, v[0].AsInt(), false));
    }

    public override IEnumerable<string> Help => new[] { Strings.T("help.pack") }.Concat(base.Help);
}
