using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// "You found: ..." after a chest, a shop purchase or a level-up reward
/// (states.PickupEquipment). The cards shown are its static founditems (elements.Equipment,
/// observed live); the only button is Take.
/// </summary>
public sealed class PickupHandler : CardScreenHandler
{
    private const string Items = "{states.PickupEquipment}.founditems";

    public override bool Handles(string scene) => scene == "states.PickupEquipment";

    protected override IEnumerable<string> CardExpressions(ScreenContext ctx)
    {
        var count = ctx.Bridge.Eval(Items + ".length").AsInt();
        return Enumerable.Range(0, count).Select(i => $"{Items}[{i}]");
    }

    /// <summary>
    /// A sighted player sees the card fly to the equipped row or to the backpack. The screen
    /// already knows which: spaceinequipment / spaceinbackpack.
    /// </summary>
    protected override string CardNote(ScreenContext ctx, ShownCard card, int index)
    {
        if (index != Cards.Count - 1) return "";
        var v = ctx.Bridge.Batch("{states.PickupEquipment}.spaceinequipment", "{states.PickupEquipment}.spaceinbackpack");
        if (!v[0].Exists) return "";
        return Strings.T(v[0].AsBool() ? "pickup.equips" : v[1].AsBool() ? "pickup.to_pack" : "pickup.no_room");
    }
}
