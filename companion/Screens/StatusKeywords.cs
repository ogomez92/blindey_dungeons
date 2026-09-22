using System.Text.RegularExpressions;
using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// The status effects a card's text names only by their icon ("Do [fire]3 damage"): a
/// sighted player hovers the icon for the rule, a blind one gets it as a keyword line
/// under the card. The table is the game's own data/text/statuseffects.csv (Name,
/// Displayed As, Symbol, Description; no quoted fields), and the words go through the
/// game's translation like everything else. Symbols shared by several effects (shield is
/// also Pierce's icon, lock is also the counters') resolve to the effect named after the
/// symbol; the "dice" symbol is decoration, not an effect.
/// </summary>
public static partial class StatusKeywords
{
    [GeneratedRegex(@"\[([a-z_]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Icon();

    private sealed record Effect(string Name, string DisplayedAs, string Symbol, string Description);

    private static Dictionary<string, Effect>? _bySymbol;
    private static string _loadedFrom = "";

    private static Dictionary<string, Effect> Table(string gameDirectory)
    {
        if (_bySymbol != null && _loadedFrom == gameDirectory) return _bySymbol;
        var table = new Dictionary<string, Effect>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(gameDirectory, "data", "text", "statuseffects.csv");
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                var f = line.Split(',');
                if (f.Length < 4 || f[0].Length == 0 || f[2].Length == 0 || f[2] == "dice") continue;
                var effect = new Effect(f[0], f[1].Replace('_', ' '), f[2], f[3]);
                // The effect named after its symbol wins ("shield" over "pierce"); otherwise the first.
                if (!table.TryGetValue(effect.Symbol, out var known) || (known.Name != known.Symbol && effect.Name == effect.Symbol))
                    table[effect.Symbol] = effect;
            }
        }
        catch (Exception) { /* no data folder: no keywords, nothing else lost */ }
        _bySymbol = table;
        _loadedFrom = gameDirectory;
        return table;
    }

    /// <summary>One "Name: rule" line per distinct status icon in the raw lines, in order of appearance.</summary>
    public static List<string> Lines(ScreenContext ctx, IEnumerable<string> rawLines)
    {
        var table = Table(ctx.Config.GameDirectory);
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in rawLines)
            foreach (Match m in Icon().Matches(raw))
            {
                var symbol = m.Groups[1].Value;
                if (!seen.Add(symbol) || !table.TryGetValue(symbol, out var effect)) continue;
                var name = ctx.Translate(effect.DisplayedAs);
                var rule = ctx.Translate(effect.Description);
                if (name.Length > 0 && rule.Length > 0) lines.Add(Strings.T("keyword", name, rule));
            }
        return lines;
    }
}
