using DiceyA11y.Bridge;
using DiceyA11y.Core;
using DiceyA11y.Input;

namespace DiceyA11y.Screens;

/// <summary>
/// The mod manager (states.Modmode). The game gives the gamepad only its three buttons
/// (Back to Title, Unload all, Load Mod(s)); the two lists - installed mods and mods that
/// will be loaded - answer to mouse clicks alone. They are plain string arrays (modlist,
/// loadlist) that the screen redraws from every frame (verified: moving an id from one to
/// the other shows up at once and is what Load Mod(s) then loads), so the mod adds the
/// missing list itself:
///
///   Down from the buttons   enters a list of every mod, each with whether it will be loaded
///   Up / Down               move through it; Up from the first row returns to the buttons
///   Enter                   moves the mod to the other list
/// </summary>
public sealed class ModsHandler : ScreenHandler
{
    private const string Scene = "{states.Modmode}";

    private bool _inList;
    private int _index;

    public override bool Handles(string scene) => scene == "states.Modmode";

    public override void OnEnter(ScreenContext ctx)
    {
        _inList = false;
        _index = 0;
        base.OnEnter(ctx);
    }

    protected override void OnEntryRead(ScreenContext ctx) => ctx.SayEvent(Strings.T("mods.intro", Mods(ctx).Count));

    /// <summary>The lists are read row by row; the mouse instructions do not apply.</summary>
    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p)
    {
        if (ctx.Bridge.Eval(Scene + ".currentscreen").AsString() != "browse") return true;
        return p.Y < 200 || p.X > 2300;
    }

    private sealed record Mod(string Id, bool Loading);

    private static List<Mod> Mods(ScreenContext ctx)
    {
        var list = ctx.Bridge.Dump(Scene + ".modlist").Select(m => new Mod(m.Value.AsString(), false))
            .Concat(ctx.Bridge.Dump(Scene + ".loadlist").Select(m => new Mod(m.Value.AsString(), true)))
            .Where(m => m.Id.Length > 0).OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
        return list;
    }

    public override bool OnKey(ScreenContext ctx, NavKey key)
    {
        if (ctx.Bridge.Eval(Scene + ".currentscreen").AsString() != "browse") return base.OnKey(ctx, key);
        var mods = Mods(ctx);
        if (!_inList)
        {
            if (key != NavKey.Down || mods.Count == 0) return base.OnKey(ctx, key);
            _inList = true;
            _index = 0;
            ctx.Say(Describe(ctx, mods, _index), interrupt: true);
            return true;
        }

        switch (key)
        {
            case NavKey.Up when _index == 0:
                _inList = false;
                ctx.Say(base.DescribeFocus(ctx), interrupt: true);
                return true;
            case NavKey.Up:
                _index--;
                break;
            case NavKey.Down:
                if (_index < mods.Count - 1) _index++;
                break;
            case NavKey.Confirm:
                Toggle(ctx, mods[Math.Min(_index, mods.Count - 1)]);
                mods = Mods(ctx);
                break;
            case NavKey.Left or NavKey.Right:
                // The game's "move up / move down" buttons (mouse only): the order mods load in.
                Reorder(ctx, mods[Math.Min(_index, mods.Count - 1)], key == NavKey.Left ? -1 : +1);
                break;
            default:
                return false;   // Escape and the rest still belong to the game
        }
        _index = Math.Clamp(_index, 0, mods.Count - 1);
        ctx.Say(Describe(ctx, mods, _index), interrupt: true);
        return true;
    }

    private static void Reorder(ScreenContext ctx, Mod mod, int direction)
    {
        if (!mod.Loading) return;
        var order = ctx.Bridge.Dump(Scene + ".loadlist").Select(m => m.Value.AsString()).ToList();
        var from = order.IndexOf(mod.Id);
        var to = from + direction;
        if (from < 0 || to < 0 || to >= order.Count) return;
        var id = GameValue.Quote(mod.Id);
        ctx.Bridge.Eval($"{Scene}.loadlist.remove({id})");
        ctx.Bridge.Eval($"{Scene}.loadlist.insert({to}, {id})");
    }

    private static void Toggle(ScreenContext ctx, Mod mod)
    {
        var id = GameValue.Quote(mod.Id);
        var (from, to) = mod.Loading ? ("loadlist", "modlist") : ("modlist", "loadlist");
        ctx.Bridge.Eval($"{Scene}.{from}.remove({id})");
        ctx.Bridge.Eval($"{Scene}.{to}.push({id})");
    }

    private static string Describe(ScreenContext ctx, List<Mod> mods, int index)
    {
        var mod = mods[index];
        var meta = $"{Scene}.getMod({GameValue.Quote(mod.Id)})";
        var v = ctx.Bridge.Batch(meta + ".title", meta + ".description", meta + ".author", meta + ".license", meta + ".modVersion.toString()");
        var title = v[0].AsString().Length > 0 ? v[0].AsString() : mod.Id;
        var order = ctx.Bridge.Dump(Scene + ".loadlist").Select(m => m.Value.AsString()).ToList();
        return TextCleanup.JoinSentences(
            title + ", " + Strings.T(mod.Loading ? "mods.will_load" : "mods.not_loaded") + ", " + Strings.T("of", index + 1, mods.Count),
            mod.Loading && order.Count > 1 ? Strings.T("mods.order", order.IndexOf(mod.Id) + 1, order.Count) : null,
            StageCleaner.Clean(v[1].AsString()),
            v[2].AsString().Length > 0 ? Strings.T("mods.author", v[2].AsString()) : null,
            v[4].AsString().Length > 0 ? Strings.T("mods.version", v[4].AsString()) : null,
            v[3].AsString().Length > 0 ? Strings.T("mods.license", v[3].AsString()) : null);
    }

    public override string DescribeFocus(ScreenContext ctx)
    {
        var mods = Mods(ctx);
        return _inList && mods.Count > 0 ? Describe(ctx, mods, Math.Clamp(_index, 0, mods.Count - 1)) : base.DescribeFocus(ctx);
    }

    public override string DescribeContext(ScreenContext ctx)
    {
        var mods = Mods(ctx);
        var loading = mods.Where(m => m.Loading).Select(m => m.Id).ToList();
        return Strings.T("mods.summary", mods.Count, loading.Count == 0 ? Strings.T("pack.none") : string.Join(", ", loading));
    }

    public override IEnumerable<string> Help => new[] { Strings.T("mods.help") }.Concat(base.Help);
}
