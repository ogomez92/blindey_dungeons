namespace DiceyA11y.Core;

/// <summary>
/// Tutorial lines that point at something on screen ("you can rearrange your equipment
/// from here") mean nothing without the picture they point at. The mod adds the key that
/// opens that thing. Lines come from data/text/locale/*/cutscenes.csv (the "|" is the
/// game's line break, spoken as a space), one per language the game ships; matched on the
/// cleaned text with spaces ignored (the Chinese and Japanese lines break mid-sentence).
/// </summary>
public static class TutorialHints
{
    private static readonly (string[] Lines, string HintKey)[] Hints =
    {
        (new[]
        {
            "You can rearrange your equipment from here.",
            "يمكنك ترتيب أدواتك من هنا.",
            "Você pode reorganizar seus equipamentos aqui.",
            "Aquí pots reorganitzar l'equipament que portes.",
            "Gelli di aildrefnu dy offer yma.",
            "Hier kannst du deine Ausrüstung neu ordnen.",
            "Desde aquí puedes reorganizar tu equipo.",
            "Ici, vous pouvez réarranger votre équipement.",
            "Is féidir atheagar a chur ar d'fhearas anseo.",
            "Puoi riordinare il tuo equipaggiamento qui.",
            "ここで装備品を 変更できる。",
            "이곳에서 장비를 재배치할 수 있습니다.",
            "Vanaf hier kun je je uitrusting wijzigen.",
            "Tutaj możesz zmienić ułożenie swojego wyposażenia.",
            "Pode reorganizar os seus equipamentos aqui.",
            "Здесь можно упорядочить свое оснащение.",
            "你可以在这里 整理你的装备。",
            "Tukej poradzisz pōmiynić rozłożynie swojich geryjtōw.",
            "你可以在這裡 整理你的裝備。",
            "Ekipmanını burada düzenleyebilirsin.",
        }, "hint.rearrange"),
        (new[]
        {
            "If you want, you can see what the enemy's moves are here.",
            "إن شئت، يمكنك تفقد حركات الأعداء من هنا .",
            "Se quiser, pode ver quais são os movimentos do inimigo aqui.",
            "Si vols, aquí pots veure quines accions pot fer l'enemic.",
            "Os wyt ti eisiau, mae modd gweld symudiadau'r gelyn yma.",
            "Wenn du willst, kannst du dir hier die Fähigkeiten deines Gegners ansehen.",
            "Si quieres, aquí puedes ver los movimientos enemigos.",
            "Au besoin, vous pouvez voir les capacités de l'ennemi ici.",
            "Más maith leat, féadann tú gluaiseachtaí na namhad a fheiscint anseo.",
            "Se vuoi, puoi vedere le mosse del nemico qui.",
            "ここで敵の装備やスキルを 確認できる。",
            "이곳을 누르면, 적이 다음 턴에 사용할 패를 확인할 수 있습니다.",
            "Eventueel kun je hier zien wat de acties van de vijand zijn.",
            "Jeśli chcesz, tutaj możesz zobaczyć ruchy przeciwnika.",
            "При желании здесь можно увидеть ходы соперника.",
            "你可以在这里看到 敌人的行动。",
            "Eli chcesz, tukej poradzisz ôbejrzeć ruchy ôpacznika.",
            "你可以在這裡看到 敵人的行動。",
            "İstersen burada düşman hamlelerini görebilirsin.",
        }, "hint.enemy_moves"),
    };

    private static string Key(string text) => string.Concat(Screens.StageCleaner.Key(text).Where(c => !char.IsWhiteSpace(c)));

    /// <summary>The text with the mod's hint appended when it is one of the known lines.</summary>
    public static string Apply(string text)
    {
        var key = Key(text);
        foreach (var (lines, hint) in Hints)
            if (lines.Any(l => key.StartsWith(Key(l), StringComparison.Ordinal)))
                return text.TrimEnd() + " " + Strings.T(hint);
        return text;
    }
}
