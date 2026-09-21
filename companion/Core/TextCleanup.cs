using System.Text;
using System.Text.RegularExpressions;

namespace DiceyA11y.Core;

/// <summary>
/// Turns raw Dicey Dungeons card text into something a speech synthesiser reads well.
///
/// Card descriptions are authored for the screen. They contain two kinds of inline
/// markup, both observed in the game's own data:
///   [name]   - an icon or a styling directive (data/text/symbols.csv)
///   &lt;d6&gt;    - a value placeholder substituted at display time
/// and they use '|' as a line break. The mod rewrites '|' to '^' on the wire so the
/// record stays parseable, and it is turned back into a clause break here.
/// </summary>
public static partial class TextCleanup
{
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex CollapseSpaces();

    [GeneratedRegex(@"\[([a-zA-Z0-9_?]+)\]")]
    private static partial Regex BracketToken();

    [GeneratedRegex(@"<([a-zA-Z]+)(\d*)>")]
    private static partial Regex AngleToken();

    [GeneratedRegex(@"\s+([,.;:!?])")]
    private static partial Regex SpaceBeforePunctuation();

    /// <summary>An icon immediately followed by the value it labels: "[sword]&lt;d6&gt;", "[shield]3".</summary>
    [GeneratedRegex(@"\[([a-zA-Z0-9_?]+)\]\s*(<[^>]+>|\d+)")]
    private static partial Regex IconBeforeValue();

    /// <summary>The same word twice in a row, which the icon reordering can produce.</summary>
    [GeneratedRegex(@"\b(\w+)(?:\s+\1\b)+", RegexOptions.IgnoreCase)]
    private static partial Regex DuplicateWord();

    /// <summary>
    /// symbols.csv entries that only change colour or size. They carry no meaning
    /// for a listener, so they are dropped rather than spoken.
    /// </summary>
    private static readonly HashSet<string> Cosmetic = new(StringComparer.OrdinalIgnoreCase)
    {
        "gray", "grey", "white", "blue", "red", "green", "yellow", "purple", "orange",
        "lessthan", "verysmall", "small", "smallish", "medium", "normal", "bigish", "big", "huge",
        "uru_d", "uru_g", "uru_n", "uru_b", "uru_h", "uru_m", "uru_t",
    };

    /// <summary>Icon tokens that do carry meaning, mapped to the word a player would say.</summary>
    private static readonly Dictionary<string, string> Icons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sword"] = "damage",
        ["shield"] = "shield",
        ["fire"] = "burn",
        ["ice"] = "freeze",
        ["shock"] = "shock",
        ["poison"] = "poison",
        ["weaken"] = "weaken",
        ["curse"] = "curse",
        ["blind"] = "blind",
        ["lock"] = "lock",
        ["reduce"] = "reduce",
        ["silence"] = "silence",
        ["dodge"] = "dodge",
        ["fury"] = "fury",
        ["recycle"] = "recycle",
        ["thorns"] = "thorns",
        ["confuse"] = "confuse",
        ["vanish"] = "vanish",
        ["haunted"] = "haunted",
        ["jinx"] = "jinx",
        ["heal"] = "heal",
        ["heart"] = "health",
        ["gold"] = "gold",
        ["mana"] = "mana",
        ["potion"] = "potion",
        ["star"] = "star",
        ["cog"] = "cog",
        ["dice"] = "dice",
        ["dice1"] = "one",
        ["dice2"] = "two",
        ["dice3"] = "three",
        ["dice4"] = "four",
        ["dice5"] = "five",
        ["dice6"] = "six",
        ["dice?"] = "any dice",
        ["d6"] = "dice value",
    };

    /// <summary>&lt;...&gt; placeholders substituted by the game at display time.</summary>
    private static readonly Dictionary<string, string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["d"] = "dice value",
        ["d6"] = "dice value",
        ["x"] = "value",
    };

    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var s = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

        // '^' is the mod's stand-in for the game's line break inside card text.
        s = s.Replace("^", ", ");

        // Card text writes the icon before the number it labels and then repeats the
        // word: "Do [sword]<d6> damage". Read literally that is "damage dice value
        // damage". Moving the icon word after its value turns it into a duplicate,
        // which the pass below collapses - and an icon with no trailing word, like
        // "[fire]3", still reads correctly as "3 burn".
        var reordered = false;
        s = IconBeforeValue().Replace(s, m =>
        {
            reordered = true;
            var key = m.Groups[1].Value;
            var value = m.Groups[2].Value;
            if (Cosmetic.Contains(key)) return " " + value + " ";
            return Icons.TryGetValue(key, out var word)
                ? " " + value + " " + word + " "
                : " " + value + " ";
        });

        s = BracketToken().Replace(s, m =>
        {
            var key = m.Groups[1].Value;
            if (Cosmetic.Contains(key)) return " ";
            return Icons.TryGetValue(key, out var word) ? " " + word + " " : " ";
        });

        s = AngleToken().Replace(s, m =>
        {
            var full = m.Groups[1].Value + m.Groups[2].Value;
            if (Placeholders.TryGetValue(full, out var spoken)) return " " + spoken + " ";
            if (Placeholders.TryGetValue(m.Groups[1].Value, out var baseSpoken)) return " " + baseSpoken + " ";
            return " " + m.Groups[1].Value + " ";
        });

        s = s.Replace("•", ", ").Replace("·", ", ");

        // '+' is the upgrade marker on card names ("Sword+") and an operator in card text
        // ("d6 + 2"); both read correctly as the word for plus in the game's language.
        s = s.Replace("+", " " + Strings.T("card.plus") + " ");

        s = CollapseSpaces().Replace(s, " ");
        // Only where the reordering above could have produced the duplicate: names such as
        // "Boop Boop" and dice lists such as "1 1" are meant to be said twice.
        if (reordered) s = DuplicateWord().Replace(s, "$1");
        s = SpaceBeforePunctuation().Replace(s, "$1");
        s = s.Replace(", ,", ",").Trim();
        s = s.Trim(' ', ',');
        return s;
    }

    /// <summary>Joins parts, dropping empties, for "Name, 3 of 5" style readouts.</summary>
    /// <summary>Joins whole sentences: "A. B. C", whatever punctuation the parts came with.</summary>
    public static string JoinSentences(params string?[] parts)
        => string.Join(". ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim().TrimEnd('.')));

    public static string Join(params string?[] parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(p.Trim());
        }
        return sb.ToString();
    }

    /// <summary>"1 card" / "2 cards"</summary>
    public static string Plural(int n, string singular, string? plural = null)
        => n == 1 ? n + " " + singular : n + " " + (plural ?? singular + "s");
}
