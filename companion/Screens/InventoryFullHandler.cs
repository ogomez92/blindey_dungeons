using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// "You are carrying too much" (states.InventoryFull): shown when a found or bought card
/// fits neither the equipped grid nor the backpack. Some of the player's cards stand in a
/// row, each with a Discard button; discarding one makes room for the new card
/// (states.InventoryFull.newequipment). The cards are read with their buttons by the
/// generic "column above the button" rule, so only the headings are read on entry.
/// Reached live by telling states.PickupEquipment there was no room.
/// </summary>
public sealed class InventoryFullHandler : ScreenHandler
{
    public override bool Handles(string scene) => scene == "states.InventoryFull";

    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p) => p.Y < 300;

    protected override void OnEntryRead(ScreenContext ctx)
    {
        var card = CardText.Name(ctx, "{states.InventoryFull}.newequipment");
        if (card.Length > 0) ctx.SayEvent(Strings.T("full.new", card));
    }

    public override IEnumerable<string> Help => new[] { Strings.T("full.keys") }.Concat(base.Help);
}
