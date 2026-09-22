namespace DiceyA11y.Core;

/// <summary>
/// Tutorial lines that point at something on screen ("you can rearrange your equipment
/// from here") mean nothing without the picture they point at. The mod adds the key that
/// opens that thing. Lines come from data/text/locale/*/cutscenes.csv (the "|" is the
/// game's line break, spoken as a space); matched on the cleaned text, so only the
/// languages listed here get the hint.
/// </summary>
public static class TutorialHints
{
    private static readonly (string[] Lines, string HintKey)[] Hints =
    {
        (new[] { "You can rearrange your equipment from here.", "Desde aquí puedes reorganizar tu equipo." }, "hint.rearrange"),
        (new[] { "If you want, you can see what the enemy's moves are here.", "Si quieres, aquí puedes ver los movimientos enemigos." }, "hint.enemy_moves"),
    };

    /// <summary>The text with the mod's hint appended when it is one of the known lines.</summary>
    public static string Apply(string text)
    {
        var key = Screens.StageCleaner.Key(text);
        foreach (var (lines, hint) in Hints)
            if (lines.Any(l => key.StartsWith(Screens.StageCleaner.Key(l), StringComparison.Ordinal)))
                return text.TrimEnd() + " " + Strings.T(hint);
        return text;
    }
}
