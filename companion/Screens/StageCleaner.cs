using System.Text.RegularExpressions;
using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// Cleans text read from the game's stage for speech. This text is already localized and
/// written for the screen, so it carries the game's inline markup (observed in a live run):
///
///   [force_ltr] [rtl] [/rtl] [force_shaping]      layout directives
///   [yellow] [gray] [white] []                    colour on / colour off
///   [90%] [150%]                                  size
///   [sword] [dice] [d6] ...                       icons (data/text/symbols.csv)
///   [a_button] [x_button] ...                     gamepad glyphs in prompts
///   [lang/lang_ko] ...                            language names drawn as images
///
/// Icons are dropped rather than named: on screen they sit next to the word they
/// illustrate ("Hace [sword]5 de daño"), so naming them would say it twice.
/// Gamepad glyphs become the keyboard key the mod binds to that button, and tutorial lines
/// that teach the stick are reworded to the arrow keys - the player has no gamepad.
/// </summary>
public static partial class StageCleaner
{
    /// <summary>"[heart]2 PS", "8 PS", "5 HP": an abbreviation a screen reader spells out letter by letter.</summary>
    [GeneratedRegex(@"(?:\[heart\]\s*(?:\[\])?\s*)?(\d+)\s*(?:\[\])?\s*(?:PS|HP)\b")]
    private static partial Regex HealthAbbreviation();

    [GeneratedRegex(@"\[dice\]\s*x\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DiceCount();

    /// <summary>Icons that stand in for a word: "[gold]12" is how the game writes "12 gold".</summary>
    [GeneratedRegex(@"\[(gold|star|heart)\]\s*(?:\[\])?\s*(\d+)(?!\d*\s*/)", RegexOptions.IgnoreCase)]
    private static partial Regex CountedIcon();

    /// <summary>
    /// An element icon in front of a damage amount is the only place the element is stated:
    /// "Deal [fire]5 damage". Elsewhere the icon repeats a word ("Burn [fire]1 dice").
    /// </summary>
    [GeneratedRegex(@"\[(fire|ice|shock|poison)\]((?:\[\])?\s*(?:\[d6\]|<d6>|\d+)\s*(?:de\s+)?)(damage|daño)", RegexOptions.IgnoreCase)]
    private static partial Regex ElementDamage();

    [GeneratedRegex(@"\[([a-z])_button\]", RegexOptions.IgnoreCase)]
    private static partial Regex FaceButton();

    /// <summary>
    /// The title's "(Esc) Options" hint, the one keyboard key the game names. The mod makes
    /// Escape the B button, so the key that opens the options is the one bound to Start.
    /// Seen across the locales: (Esc), (ESC), (esc), (Échap.), （Esc）, (Esc键), （Esc鍵）.
    /// </summary>
    [GeneratedRegex(@"[(（]\s*(?:esc|échap)[^()（）]{0,4}[)）]", RegexOptions.IgnoreCase)]
    private static partial Regex EscapeHint();

    [GeneratedRegex(@"\[lang/lang_([a-z]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageImage();

    [GeneratedRegex(@"\[[^\[\]]*\]")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"<([a-z0-9_]+)>", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s+([,.;:!?])")]
    private static partial Regex SpaceBeforePunctuation();

    private static readonly Dictionary<string, string[]> UnitWords = new()
    {
        ["gold"] = new[] { "gold", "de oro", "oro" },
        // "PS" is the Spanish game's abbreviation of "puntos de salud", its HP.
        ["heart"] = new[] { "health", "hp", "ps", "de salud", "de vida", "salud", "vida" },
        ["star"] = new[] { "xp", "experience", "de experiencia" },
    };

    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ko"] = "한국어", ["sc"] = "简体中文", ["tc"] = "繁體中文", ["ja"] = "日本語", ["ar"] = "العربية",
    };

    /// <summary>Glyph names other than the face buttons, as they appear in prompts.</summary>
    private static readonly Dictionary<string, string> OtherGlyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["start_button"] = "key.Start", ["back_button"] = "key.Back", ["select_button"] = "key.Back",
        ["lb_button"] = "key.LeftShoulder", ["rb_button"] = "key.RightShoulder",
        ["l_button"] = "key.LeftShoulder", ["r_button"] = "key.RightShoulder",
        ["left_stick"] = "key.arrows", ["dpad"] = "key.arrows",
    };

    /// <summary>Per-language rewording of instructions that name gamepad hardware.</summary>
    private static readonly (string Language, Regex Pattern, string KeyString)[] HardwareWording =
    {
        ("es", new Regex(@"\bla\s+palanca\s+izquierda\b", RegexOptions.IgnoreCase), "key.arrows"),
        ("es", new Regex(@"\bel\s+stick\s+izquierdo\b", RegexOptions.IgnoreCase), "key.arrows"),
        ("en", new Regex(@"\bthe\s+left\s+stick\b", RegexOptions.IgnoreCase), "key.arrows"),
        ("en", new Regex(@"\bleft\s+stick\b", RegexOptions.IgnoreCase), "key.arrows"),
    };

    public static string Language { get; set; } = "en";

    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Replace('\n', ' ').Replace('\r', ' ').Replace('|', ' ');

        s = HealthAbbreviation().Replace(s, m => m.Groups[1].Value + " " + Strings.T("unit.health"));
        s = DiceCount().Replace(s, m => " " + Strings.T("dice.count", m.Groups[1].Value) + " ");
        s = CountedIcon().Replace(s, m =>
        {
            // "Cost: [gold]2 gold" names the unit itself; "[gold]61" on its own does not.
            var rest = s[(m.Index + m.Length)..].TrimStart(' ', '[', ']').ToLowerInvariant();
            var named = UnitWords[m.Groups[1].Value.ToLowerInvariant()].Any(w => rest.StartsWith(w, StringComparison.Ordinal));
            return " " + (named ? m.Groups[2].Value : Strings.T("icon." + m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value)) + " ";
        });
        // [l_button] and [r_button] are the shoulders, not face buttons (the tutorial's
        // "Press [r_button] to use Fury" once came out as "key.R").
        s = FaceButton().Replace(s, m => m.Groups[1].Value.ToUpperInvariant() switch
        {
            "L" => " " + Strings.T("key.LeftShoulder") + " ",
            "R" => " " + Strings.T("key.RightShoulder") + " ",
            var face => Strings.Has("key." + face) ? " " + Strings.T("key." + face) + " " : m.Value,
        });
        s = EscapeHint().Replace(s, " " + Strings.T("key.Start") + " ");
        s = LanguageImage().Replace(s, m => LanguageNames.TryGetValue(m.Groups[1].Value, out var n) ? n : m.Groups[1].Value);
        s = ElementDamage().Replace(s, m => m.Groups[2].Value + Strings.T("damage." + m.Groups[1].Value.ToLowerInvariant()));
        s = s.Replace("[comma]", ",", StringComparison.OrdinalIgnoreCase).Replace("[;]", ",");   // the game's escape for a literal comma
        s = s.Replace("[d6]", " " + Strings.T("dice.value") + " ", StringComparison.OrdinalIgnoreCase);
        // Card text read from game state still has its placeholders: <d6>, <double>, <myshield>...
        s = Placeholder().Replace(s, m => " " + (Strings.Has("var." + m.Groups[1].Value.ToLowerInvariant())
            ? Strings.T("var." + m.Groups[1].Value.ToLowerInvariant()) : m.Groups[1].Value) + " ");

        s = AnyTag().Replace(s, m =>
        {
            var key = m.Value[1..^1];
            return OtherGlyphs.TryGetValue(key, out var k) ? " " + Strings.T(k) + " " : " ";
        });

        foreach (var (language, pattern, keyString) in HardwareWording)
            if (Language.StartsWith(language, StringComparison.OrdinalIgnoreCase))
                s = pattern.Replace(s, Strings.T(keyString));

        // The language list writes "(Chino_simplificado)": underscores are layout glue.
        s = s.Replace('_', ' ');
        s = Spaces().Replace(s, " ");
        s = SpaceBeforePunctuation().Replace(s, "$1");
        while (s.Contains(",,")) s = s.Replace(",,", ",");
        s = s.Replace(".,", ",");   // "máx.[;]" -> "máx.," reads as a stumble
        return s.Trim();
    }

    /// <summary>Comparison key: what two pieces of text have in common regardless of case and markup.</summary>
    public static string Key(string cleaned) => cleaned.Trim().ToUpperInvariant();
}
