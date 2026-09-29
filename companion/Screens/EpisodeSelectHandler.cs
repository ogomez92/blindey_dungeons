using DiceyA11y.Bridge;
using DiceyA11y.Core;

namespace DiceyA11y.Screens;

/// <summary>
/// Episode select (states.Episodestate): a row of doors, six to a page. The door's title,
/// wins and rules are ordinary text read by the generic path; what the screen only shows
/// with pictures is added here. Locked doors are skipped by the game's own cursor, so
/// without this the player never learns they exist: the entry names each locked episode
/// and what opens it, and every move says where the door is in the whole row.
///
/// Read live: selecteddoor (door on this page), episodespage / totalpages (the second page
/// is "Extra Episodes", only there when a mod gives a contestant more than six; the right
/// and left shoulders, W and Q, turn it), episodeavailable indexed by episode level - 1,
/// character.name. The conditions are ProgressTracking.isunlocked in the exe: episodes 2
/// and 3 open for everyone once five contestants have played, and a contestant's episodes
/// 4, 5 and 6 once that contestant has won 2, 3 and 4 of theirs (numepisodeswon).
/// </summary>
public sealed class EpisodeSelectHandler : ScreenHandler
{
    private const string Scene = "{states.Episodestate}";

    private string _cursor = "";
    private int _page = -1;

    public override bool Handles(string scene) => scene == "states.Episodestate";

    public override void OnEnter(ScreenContext ctx)
    {
        _cursor = "";
        _page = -1;
        base.OnEnter(ctx);
    }

    protected override void OnEntryRead(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch(Scene + ".episodespage", Scene + ".totalpages");
        _page = v[0].AsInt();
        _cursor = CursorState(ctx);
        ctx.SayEvent(Summary(ctx));
        if (v[1].AsInt() > 1) ctx.SayEvent(Strings.T("episode.pages", _page + 1, v[1].AsInt()));
    }

    protected override string CursorState(ScreenContext ctx)
        => string.Join("|", ctx.Bridge.Batch(Scene + ".episodespage", Scene + ".selecteddoor").Select(v => v.Raw));

    public override void OnPoll(ScreenContext ctx)
    {
        base.OnPoll(ctx);
        if (!EntryRead) return;
        var cursor = CursorState(ctx);
        if (cursor == _cursor) return;
        _cursor = cursor;
        var v = ctx.Bridge.Batch(Scene + ".episodespage", Scene + ".totalpages");
        if (v[0].AsInt() != _page)
        {
            _page = v[0].AsInt();
            ctx.Say(TextCleanup.Join(Strings.T("episode.page", _page + 1, v[1].AsInt()), Position(ctx)), interrupt: RecentInput(ctx));
            var locked = LockedOnPage(ctx);
            if (locked.Length > 0) ctx.SayEvent(locked);
            return;
        }
        ctx.Say(Position(ctx), interrupt: RecentInput(ctx));
    }

    /// <summary>"episode 4 of 6": where the door stands in the contestant's whole row.</summary>
    private static string Position(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch(Scene + ".episodespage", Scene + ".selecteddoor", Scene + ".doors.length", Scene + ".character.name");
        var total = Episodes(ctx, v[3].AsString()).Count;
        var number = v[0].AsInt() * Math.Max(1, v[2].AsInt()) + v[1].AsInt() + 1;
        return total > 0 ? Strings.T("episode.position", number, Math.Max(total, number)) : "";
    }

    /// <summary>The contestant's episodes in door order, with whether each is open.</summary>
    private static List<(int Level, string Name, bool Open)> Episodes(ScreenContext ctx, string character)
    {
        if (character.Length == 0) return new();
        var rows = ctx.Bridge.Pluck("{Gamedata}.getepisodes(" + GameValue.Quote(character) + ")", "level", "episodename") ?? new();
        var open = ctx.Bridge.Dump(Scene + ".episodeavailable").Select(e => e.Value.AsBool()).ToList();
        return rows.Select(r => (Level: r[0].AsInt(), Name: r[1].AsString()))
                   .Where(r => r.Level > 0)
                   .Select(r => (r.Level, r.Name, r.Level - 1 < open.Count && open[r.Level - 1]))
                   .OrderBy(r => r.Level).ToList();
    }

    /// <summary>Spoken on entry: how many doors are open and what the closed ones need.</summary>
    private static string Summary(ScreenContext ctx)
    {
        var character = ctx.Bridge.Eval(Scene + ".character.name").AsString();
        var episodes = Episodes(ctx, character);
        if (episodes.Count == 0) return "";
        var open = episodes.Count(e => e.Open);
        var head = open == episodes.Count ? Strings.T("episode.all_open", open) : Strings.T("episode.open", open, episodes.Count);
        var locked = Locked(ctx, character, episodes.Where(e => !e.Open));
        return locked.Length > 0 ? head + " " + locked : head;
    }

    private static string LockedOnPage(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch(Scene + ".episodespage", Scene + ".doors.length", Scene + ".character.name");
        var perPage = Math.Max(1, v[1].AsInt());
        var first = v[0].AsInt() * perPage + 1;
        var episodes = Episodes(ctx, v[2].AsString());
        return Locked(ctx, v[2].AsString(), episodes.Where(e => !e.Open && e.Level >= first && e.Level < first + perPage));
    }

    /// <summary>"Locked: Parallel Universe, win 3 Warrior episodes; ... You have won 2."</summary>
    private static string Locked(ScreenContext ctx, string character, IEnumerable<(int Level, string Name, bool Open)> locked)
    {
        var list = locked.ToList();
        if (list.Count == 0) return "";
        var who = ctx.Translate(character);
        var byWins = false;
        var parts = list.Select(e =>
        {
            var name = ctx.Translate(e.Name);
            switch (e.Level)
            {
                case 2 or 3:
                    return Strings.T("episode.needs", name, ctx.Translate("Play a game with five contestants"));
                case >= 4 and <= 6:
                    byWins = true;
                    return Strings.T("episode.needs_wins", name, e.Level - 2, who);
                default:
                    return name;
            }
        });
        var text = Strings.T("episode.locked", string.Join("; ", parts));
        if (!byWins) return text;
        var won = ctx.Bridge.Eval("{ProgressTracking}.numepisodeswon(" + GameValue.Quote(character) + ")").AsInt();
        return text + " " + Strings.T("episode.won", won, who);
    }

    /// <summary>F3 and dead keys: the selected episode and where it is.</summary>
    public override string DescribeFocus(ScreenContext ctx)
    {
        var name = ctx.Bridge.Eval(Scene + ".activeepisode.episodename");
        var position = Position(ctx);
        if (!name.Exists) return position.Length > 0 ? position : base.DescribeFocus(ctx);
        return TextCleanup.Join(ctx.Translate(name.AsString()), position);
    }

    public override string DescribeScreen(ScreenContext ctx)
    {
        var summary = Summary(ctx);
        var screen = base.DescribeScreen(ctx);
        return summary.Length > 0 ? screen + ". " + summary : screen;
    }

    public override IEnumerable<string> Help => new[] { Strings.T("help.episodes") }.Concat(base.Help);
}
