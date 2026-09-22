using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// A card (elements.Equipment) described from game state rather than from the stage.
///
/// In battle the stage is better while a dice is hovered, because the card then shows live
/// numbers. Everywhere else - shops, rewards, the backpack, review lists - cards overlap,
/// scroll or are not drawn at all, so the text has to come from the object:
///
///   name + namemodifier                     internal English key, translated by the game
///   MyLocale.translatearray(fulldescription) the rules text as the card prints it, one
///                                           entry per line (verified live; Equipment.hx
///                                           builds the card face from the same call)
///   slots, needstotal, countdown            elements.DiceSlotType, from Equipment.hx
/// </summary>
public static class CardText
{
    public static string Name(ScreenContext ctx, string card)
    {
        var v = ctx.Bridge.Batch(card + ".name", card + ".namemodifier", card + ".displayname");
        if (!v[0].Exists) return "";
        return DecorateName(ctx, v[0].AsString(), v[1].AsString());
    }

    /// <summary>"Sword" + "+" is an upgraded Sword; "-" is a weakened one.</summary>
    /// <summary>
    /// Each character's special panel is an Equipment with a placeholder name that is never
    /// shown on screen (Fighter.hx: "Robot Calculate", "Inventor Gadgets", "Witch Spellbook"...).
    /// </summary>
    private static readonly Dictionary<string, string> PanelNames = new()
    {
        ["Robot Calculate"] = "panel.robot", ["Robot Request"] = "panel.robot", ["Robot Flip"] = "panel.robot",
        ["Inventor Gadgets"] = "panel.gadget", ["Jester Deck"] = "panel.deck", ["Witch Deck"] = "panel.deck",
        ["Witch Spellbook"] = "panel.spellbook", ["Thief Stolen Card"] = "card.stolen",
        ["Thief Reunion"] = "panel.skill", ["Warrior Reunion"] = "panel.skill", ["Witch Reunion"] = "panel.skill",
        ["Warrior Skillcard"] = "panel.skill", ["Witch Skillcard"] = "panel.skill",
    };

    public static string DecorateName(ScreenContext ctx, string name, string modifier)
    {
        if (PanelNames.TryGetValue(name, out var panel)) return Strings.T(panel);
        var text = ctx.Translate(name);
        if (modifier.Contains('+')) text += " " + Strings.T("card.plus");
        if (modifier.Contains('-')) text += " " + Strings.T("card.minus");
        return text;
    }

    /// <summary>The rules lines as the game holds them, markup included ("Do [fire]3 damage"): what the keyword lookup reads.</summary>
    public static List<string> RawDescriptionLines(ScreenContext ctx, string card)
    {
        // What the card's own panel last drew is the most faithful: it includes what an upgrade
        // adds ("+ 1", worked out at draw time - fulldescription never changes) and any text
        // substituted by scripts. A card that has never been drawn has no panel text yet.
        var panel = card + ".equipmentpanel.description";
        var count = ctx.Bridge.Eval(panel + ".length").AsInt();
        var lines = count > 0
            ? ctx.Bridge.Batch(Enumerable.Range(0, count).Select(i => $"{panel}[{i}].textfield._text").ToList())
                 .Where(l => l.Exists).Select(l => l.AsString()).Where(l => l.Trim().Length > 0).ToList()
            : new List<string>();
        if (lines.Count == 0)
            lines = ctx.Bridge.Dump("{MyLocale}.translatearray(" + card + ".fulldescription)")
                       .Where(l => l.Value.Exists).Select(l => l.Value.AsString()).Where(l => l.Trim().Length > 0).ToList();
        return lines;
    }

    public static string Description(ScreenContext ctx, string card)
    {
        var lines = RawDescriptionLines(ctx, card).Select(StageCleaner.Clean).Where(l => l.Length > 0);
        // A line break is either the end of an effect or just where the card wraps a sentence.
        var text = "";
        foreach (var line in lines)
        {
            var newSentence = text.Length > 0 && char.IsUpper(line[0]) && char.IsLetterOrDigit(text[^1]);
            text += text.Length == 0 ? line : (newSentence ? ". " : " ") + line;
        }
        return text;
    }

    /// <summary>"Burn: Sets the next dice on fire..." for every status effect the card's text names by its icon.</summary>
    public static List<string> Keywords(ScreenContext ctx, string card) => StatusKeywords.Lines(ctx, RawDescriptionLines(ctx, card));

    /// <summary>Rules text of a card known only by its internal name (a page of the Witch's book).</summary>
    public static string DescriptionByName(ScreenContext ctx, string name)
    {
        var lines = ctx.Bridge.Dump("{MyLocale}.translatearray({Gamedata}.getequipmenttemplate(" + Bridge.GameValue.Quote(name) + ").fulldescription)")
                       .Select(l => StageCleaner.Clean(l.Value.AsString())).Where(l => l.Length > 0);
        return string.Join(" ", lines);
    }

    public static string Slots(ScreenContext ctx, string card, bool withFilled = true)
    {
        var slots = ctx.Bridge.Dump(card + ".slots").Select(s => s.Value.TypeName).Where(n => n.Length > 0).ToList();
        var v = ctx.Bridge.Batch(card + ".remainingcountdown", card + ".needstotal", card + ".needsdoubles");
        if (slots.Count == 0 && v[1].AsInt() <= 0) return "";

        var filled = ctx.Bridge.Dump(card + ".assigneddice").Select(d => d.Value.Exists).ToList();
        var words = new List<string>();
        // Slots of type DOUBLES come in sets that must all show the same number: one phrase, not one word each.
        var matching = slots.Count(s => s == "DOUBLES");
        if (matching > 0) words.Add(Strings.T("slot.matching", Math.Max(matching, 2)));
        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i] == "DOUBLES") continue;
            if (slots[i].StartsWith("FREE") || slots[i].StartsWith("SPARE")) continue;   // not a requirement the player fills
            var word = SlotWord(slots[i], v[0].AsInt());
            if (slots[i] == "COMBINATION")
            {
                // The dice to collect are drawn as pictures; elements.Combination.getstring names them ("2", "135").
                var wanted = ctx.Bridge.Eval("{elements.Combination}.getstring(" + card + ")").AsString();
                var dice = wanted.All(char.IsDigit) ? string.Join(", ", wanted.ToCharArray()) : wanted.Replace("|", ", ");
                word = Strings.T("slot.COMBINATION", dice.Length > 0 ? dice : "?");
            }
            if (withFilled && i < filled.Count && filled[i]) word += " (" + Strings.T("slot.filled") + ")";
            words.Add(word);
        }
        if (v[1].AsInt() > 0) words.Add(Strings.T("slot.total", v[1].AsInt()));
        if (v[2].AsBool() && matching == 0) words.Add(Strings.T("slot.DOUBLES"));
        return words.Count == 0 ? "" : Strings.T("slot.needs", string.Join(", ", words));
    }

    /// <summary>Names follow elements.DiceSlotType, read from the published Equipment.hx.</summary>
    private static string SlotWord(string slot, int countdownLeft)
    {
        if (slot == "COUNTDOWN") return Strings.T("slot.COUNTDOWN", countdownLeft);
        if (Strings.Has("slot." + slot)) return Strings.T("slot." + slot);
        if (slot.Length > 3 && int.TryParse(slot[^1..], out var n))
        {
            if (slot.StartsWith("MAX")) return Strings.T("slot.MAX", n);
            if (slot.StartsWith("MIN")) return Strings.T("slot.MIN", n);
            if (slot.StartsWith("REQUIRE")) return Strings.T("slot.REQUIRE", n);
            if (slot.StartsWith("RANGE") && slot.Length == 7) return Strings.T("slot.RANGE", slot[5], slot[6]);
        }
        return slot.ToLowerInvariant();
    }

    /// <summary>Name, rules text, what it takes, and how much room it needs in the backpack.</summary>
    public static string Full(ScreenContext ctx, string card, bool withSize = false)
    {
        var name = Name(ctx, card);
        if (name.Length == 0) return "";
        var parts = new List<string> { name };
        var description = Description(ctx, card);
        if (description.Length > 0) parts.Add(description);
        var slots = Slots(ctx, card);
        if (slots.Length > 0) parts.Add(slots);
        if (withSize) parts.Add(Strings.T(ctx.Bridge.Eval(card + ".size").AsInt() >= 2 ? "card.size2" : "card.size1"));
        return string.Join(". ", parts);
    }
}
