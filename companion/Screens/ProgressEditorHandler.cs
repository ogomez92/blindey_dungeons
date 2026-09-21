using System.Text.RegularExpressions;
using DiceyA11y.Core;
using DiceyA11y.Input;

namespace DiceyA11y.Screens;

/// <summary>
/// "Reset progress" in the pause menu opens the game's Progress Editor
/// (states.ProgressEditor), not a confirmation box. Observed live:
///
///   intro page    a checkbox "I understand!" (accepteditorwarning), then X starts
///   editor        one row per contestant. X unlocks the next locked contestant
///                 (scorecard_unlocked). Each unlocked row has dice-shaped toggles whose
///                 navigation label is only "diceradio&lt;x&gt;_&lt;y&gt;"; A toggles "episode
///                 completed" (written to the save on leaving). Y asks to reset all progress
///                 and must be pressed four times (resetconfirm); B leaves.
///
/// Rows are 230 px apart from y 488, in the order of the character select.
/// </summary>
public sealed partial class ProgressEditorHandler : ScreenHandler
{
    private const string Scene = "{states.ProgressEditor}";

    [GeneratedRegex(@"^diceradio(\d+)_(\d+)$")]
    private static partial Regex Radio();

    private readonly Dictionary<string, bool> _ticked = new();
    private string _unlocked = "";
    private int _resetConfirm;
    private bool _intro = true;

    public override bool Handles(string scene) => scene == "states.ProgressEditor";

    public override void OnEnter(ScreenContext ctx)
    {
        _ticked.Clear();
        _unlocked = "";
        _resetConfirm = 0;
        _intro = true;
        base.OnEnter(ctx);
    }

    public override void OnPoll(ScreenContext ctx)
    {
        base.OnPoll(ctx);
        var v = ctx.Bridge.Batch(Scene + ".showingintro", Scene + ".resetconfirm", Scene + ".accepteditorwarning");
        var intro = v[0].AsBool();
        if (_intro && !intro) ctx.SayEvent(Strings.T("editor.keys"));
        _intro = intro;

        var unlocked = string.Join("", ctx.Bridge.Dump(Scene + ".scorecard_unlocked").Select(u => u.Value.AsBool() ? "1" : "0"));
        if (_unlocked.Length > 0 && unlocked != _unlocked)
        {
            var index = Enumerable.Range(0, Math.Min(unlocked.Length, _unlocked.Length)).FirstOrDefault(i => unlocked[i] != _unlocked[i], -1);
            if (index >= 0 && unlocked[index] == '1') ctx.SayEvent(Strings.T("editor.unlocked", CharacterName(ctx, index)));
        }
        _unlocked = unlocked;

        if (v[1].AsInt() < _resetConfirm) ctx.SayEvent(Strings.T("editor.reset_cancelled"));
        _resetConfirm = v[1].AsInt();
    }

    public override bool OnKey(ScreenContext ctx, NavKey key)
    {
        // The toggle's state is only drawn. Track it from what the save said on entry.
        if (key == NavKey.Confirm && ctx.State.Focus is { } focus && Radio().Match(focus.RawLabel) is { Success: true } m)
        {
            var id = focus.RawLabel;
            _ticked[id] = !Ticked(ctx, id, m);
            ctx.Say(Strings.T(_ticked[id] ? "editor.done" : "editor.not_done"), interrupt: true);
        }
        return base.OnKey(ctx, key);
    }

    private bool Ticked(ScreenContext ctx, string id, Match m)
    {
        if (_ticked.TryGetValue(id, out var known)) return known;
        var (row, column) = Cell(m);
        var name = ctx.Bridge.Eval($"{{states.CharacterSelect}}.availablecharactertemplates[{row}].name");
        var won = name.Exists ? ctx.Bridge.Eval("{ProgressTracking}.numepisodeswon(" + Bridge.GameValue.Quote(name.AsString()) + ")").AsInt() : 0;
        return _ticked[id] = won > column;
    }

    private static (int Row, int Column) Cell(Match m)
        => ((int)Math.Round((int.Parse(m.Groups[2].Value) - 488) / 230.0), Math.Max(0, (int)Math.Round((int.Parse(m.Groups[1].Value) - 1482) / 230.0)));

    private static string CharacterName(ScreenContext ctx, int row)
    {
        var name = ctx.Bridge.Eval($"{{states.CharacterSelect}}.availablecharactertemplates[{row}].name");
        return name.Exists ? ctx.Translate(name.AsString()) : Strings.T("editor.row", row + 1);
    }

    public override string DescribeFocus(ScreenContext ctx)
    {
        var focus = ctx.State.Focus;
        if (focus == null || Radio().Match(focus.RawLabel) is not { Success: true } m) return base.DescribeFocus(ctx);
        var (row, column) = Cell(m);
        return Strings.T("editor.radio", CharacterName(ctx, row), column + 1,
                         Strings.T(Ticked(ctx, focus.RawLabel, m) ? "editor.done" : "editor.not_done"));
    }

    public override IEnumerable<string> Help => new[] { Strings.T("editor.keys") }.Concat(base.Help);
}
