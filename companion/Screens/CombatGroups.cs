using DiceyA11y.Core;
using DiceyA11y.Input;

namespace DiceyA11y.Screens;

/// <summary>
/// The battle's "groups": everything a sighted player takes in by looking around the
/// board, in four lists that Tab / Shift+Tab move between and Page Down / Page Up move
/// within. Each item is spoken with its place in the group and fills the review list
/// with its details, then the keywords its text names (the status effects behind its
/// icons), so Ctrl+Up explains "Do [fire]3 damage" without hunting for the icon.
///
///   enemy cards          the enemy's equipment, from the card objects (never the stage:
///                        off the preview those cards are parked over the player's)
///   enemy status         its status effects, each with the game's rule for it
///   your status          yours
///   your actions         the limit break (its charge, W when ready), a Reunion skill panel,
///                        the Jester's discard, fleeing
///
/// The item index is kept per group, clamped to whatever the group holds now: cards are
/// used up and statuses come and go between one look and the next.
/// </summary>
public sealed partial class CombatHandler
{
    private sealed record GroupItem(string Text, List<string> Lines);
    private sealed record Group(string Title, List<GroupItem> Items);

    private const int GroupCount = 4;
    private int _group = -1;
    private readonly int[] _groupAt = new int[GroupCount];

    private void ResetGroups()
    {
        _group = -1;
        Array.Clear(_groupAt);
    }

    private bool OnGroupKey(ScreenContext ctx, NavKey key)
    {
        switch (key)
        {
            case NavKey.GroupNext: MoveGroup(ctx, +1); return true;
            case NavKey.GroupPrevious: MoveGroup(ctx, -1); return true;
            case NavKey.ItemNext: MoveItem(ctx, +1); return true;
            case NavKey.ItemPrevious: MoveItem(ctx, -1); return true;
            default: return false;
        }
    }

    private void MoveGroup(ScreenContext ctx, int step)
    {
        var groups = ReadGroups(ctx);
        _group = _group < 0 ? (step > 0 ? 0 : groups.Count - 1) : (_group + step + groups.Count) % groups.Count;
        var group = groups[_group];
        if (group.Items.Count == 0) { ctx.Say(Strings.T("group.none", group.Title), interrupt: true); ctx.FocusItem(new[] { Strings.T("group.none", group.Title) }); return; }
        _groupAt[_group] = Math.Clamp(_groupAt[_group], 0, group.Items.Count - 1);
        SpeakItem(ctx, group, Strings.T("group.intro", group.Title, group.Items.Count));
    }

    private void MoveItem(ScreenContext ctx, int step)
    {
        if (_group < 0) { MoveGroup(ctx, +1); return; }
        var group = ReadGroups(ctx)[_group];
        if (group.Items.Count == 0) { ctx.Say(Strings.T("group.none", group.Title), interrupt: true); return; }
        // The ends do not wrap: the last item is repeated with its place, like the card list.
        _groupAt[_group] = Math.Clamp(_groupAt[_group] + step, 0, group.Items.Count - 1);
        SpeakItem(ctx, group, null);
    }

    private void SpeakItem(ScreenContext ctx, Group group, string? prefix)
    {
        var at = _groupAt[_group];
        var item = group.Items[at];
        var text = group.Items.Count > 1 ? item.Text.TrimEnd('.', ' ') + ", " + Strings.T("of", at + 1, group.Items.Count) : item.Text;
        ctx.Say(prefix == null ? text : prefix + ": " + text, interrupt: true);
        ctx.FocusItem(item.Lines);
    }

    private List<Group> ReadGroups(ScreenContext ctx) => new()
    {
        new Group(Strings.T("group.enemy_cards"), EnemyCardItems(ctx)),
        new Group(Strings.T("group.enemy_status"), StatusItems(_narrator.EnemyStatuses)),
        new Group(Strings.T("group.your_status"), StatusItems(_narrator.PlayerStatuses)),
        new Group(Strings.T("group.actions"), ActionItems(ctx)),
    };

    private static List<GroupItem> EnemyCardItems(ScreenContext ctx)
    {
        var rows = ctx.Bridge.Pluck("{Game}.monster.equipment", "name", "ready", "availablethisturn", "remainingcountdown", "countdown") ?? new();
        var items = new List<GroupItem>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (!r[0].Exists) continue;
            var card = $"{{Game}}.monster.equipment[{i}]";
            var lines = new List<string> { CardText.Name(ctx, card) };
            var description = CardText.Description(ctx, card);
            if (description.Length > 0) lines.Add(description);
            var slots = CardText.Slots(ctx, card, withFilled: false);
            if (slots.Length > 0) lines.Add(slots);
            if (r[4].AsInt() > 0) lines.Add(r[3].AsInt() > 0 ? Strings.T("slot.COUNTDOWN", r[3].AsInt()) : Strings.T("card.ready"));
            if (!r[1].AsBool(true) || !r[2].AsBool(true)) lines.Add(Strings.T("card.unavailable"));
            var text = TextCleanup.JoinSentences(lines.ToArray());
            lines.AddRange(CardText.Keywords(ctx, card));
            items.Add(new GroupItem(text, lines));
        }
        return items;
    }

    private static List<GroupItem> StatusItems(IReadOnlyList<BattleNarrator.StatusInfo> statuses)
        => statuses.Select(s => new GroupItem(BattleNarrator.DescribeStatus(s, withRule: true),
                                              new List<string> { BattleNarrator.DescribeStatus(s, withRule: false), s.Description }))
                   .ToList();

    private List<GroupItem> ActionItems(ScreenContext ctx)
    {
        var items = new List<GroupItem>();
        var v = ctx.Bridge.Batch("{Game}.player.limitvalue", "{Game}.player.limitmax", "{Game}.player.limitbreak.name", "{Game}.player.limitbreak.description");
        if (v[1].AsInt() > 0 && v[2].Exists)
        {
            var name = ctx.Translate(v[2].AsString());
            var rule = ctx.Translate(v[3].AsString());
            var ready = v[0].AsInt() >= v[1].AsInt();
            var text = ready ? Strings.T("action.limit_ready", name, rule) : Strings.T("action.limit_charging", name, v[0].AsInt(), v[1].AsInt(), rule);
            var lines = new List<string> { name + ", " + Strings.T("battle.limit", v[0].AsInt(), v[1].AsInt()), rule };
            // The rule as the game translated it, icons and all, for the keyword lookup.
            var raw = ctx.Bridge.Eval("{MyLocale}.translate(" + Bridge.GameValue.Quote(v[3].AsString()) + ")");
            lines.AddRange(StatusKeywords.Lines(ctx, new[] { raw.Exists ? raw.AsString() : v[3].AsString() }));
            items.Add(new GroupItem(text, lines));
        }
        var skill = DescribeSkillPanel(ctx);
        if (skill.Length > 0) items.Add(new GroupItem(skill, new List<string> { skill }));
        if (_narrator.PlayerHasDeck) items.Add(new GroupItem(Strings.T("action.jester"), new List<string> { Strings.T("action.jester") }));
        items.Add(new GroupItem(Strings.T("action.flee"), new List<string> { Strings.T("action.flee") }));
        return items;
    }
}
