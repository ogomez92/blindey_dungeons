using DiceyA11y.Core;
using DiceyA11y.Game.Buffers;
using DiceyA11y.Protocol;
using DiceyA11y.Speech;

namespace DiceyA11y.Game;

/// <summary>
/// Turns snapshots into speech.
///
/// The mod cannot see the UI, only game state, so every announcement is derived
/// by diffing consecutive snapshots. That is also why the wording is built here
/// rather than in hscript: this is the part that gets tuned the most, and C# can
/// be changed without relaunching the game.
/// </summary>
public sealed class Narrator
{
    private readonly ScreenReaderOutput _speech;
    private readonly BufferManager _buffers;
    private readonly Config _config;

    private Snapshot? _previous;

    /// <summary>Latest complete state, used by the on-demand review hotkeys.</summary>
    public Snapshot? Current { get; private set; }

    public bool InCombat { get; private set; }

    public Narrator(ScreenReaderOutput speech, BufferManager buffers, Config config)
    {
        _speech = speech;
        _buffers = buffers;
        _config = config;
    }

    public void OnSnapshot(Snapshot snapshot)
    {
        var previous = _previous;
        Current = snapshot;
        RefreshBuffers(snapshot);

        switch (snapshot.Event)
        {
            case "startcombat":
                InCombat = true;
                _previous = snapshot;          // a new battle is its own baseline
                AnnounceBattleStart(snapshot);
                return;

            case "beforestartturn":
                // Fires just before onstartturn with the same state. Used only as a
                // baseline so start-of-turn effects (poison, burn) are attributed to
                // the turn rather than to the previous action.
                _previous = snapshot;
                return;

            case "onstartturn":
                AnnounceTurnStart(snapshot, previous);
                break;

            case "playerequipmentuse":
            case "enemyequipmentuse":
                AnnounceChanges(snapshot, previous);
                break;

            case "endturn":
                AnnounceChanges(snapshot, previous);
                break;

            case "aftercombat":
                AnnounceBattleEnd(snapshot);
                InCombat = false;
                _buffers.ClearBattleBuffers();
                break;
        }

        _previous = snapshot;
    }

    // ---------------------------------------------------------------- events

    private void AnnounceBattleStart(Snapshot s)
    {
        var enemy = s.Enemy;
        var player = s.Player;

        _speech.SayEvent(TextCleanup.Join(
            "Battle. " + enemy.Name,
            enemy.HealthPhrase,
            "you " + player.HealthPhrase,
            player.Level > 0 ? "level " + player.Level : null));

        if (_config.AnnounceEnemyEquipment && enemy.Equipment.Count > 0)
        {
            _speech.SayEvent(enemy.Name + " has " + TextCleanup.Plural(enemy.Equipment.Count, "card")
                             + ": " + string.Join(", ", enemy.Equipment.Select(e => e.DisplayName)));
        }

        var statuses = DescribeStatuses(s);
        if (statuses is not null) _speech.SayEvent(statuses);
    }

    private void AnnounceTurnStart(Snapshot s, Snapshot? previous)
    {
        // In the turn hooks, "self" is the fighter whose turn is starting.
        var whose = s.Self.IsPlayer ? "Your turn" : s.Self.Name + " turn";
        _speech.SayEvent(whose);

        AnnounceChanges(s, previous);

        if (!s.Self.IsPlayer) return;

        if (_config.AnnounceDice && s.Player.DicePool.Count > 0)
        {
            _speech.SayEvent("Dice: " + string.Join(", ", s.Player.DicePool.Select(d => d.Describe())));
        }

        var ready = s.Player.Equipment
            .Where(e => e.CountdownMax > 0 && e.Countdown <= 0)
            .Select(e => e.DisplayName)
            .ToList();
        if (ready.Count > 0) _speech.SayEvent("Ready: " + string.Join(", ", ready));
    }

    private void AnnounceBattleEnd(Snapshot s)
    {
        var player = s.Player;
        var enemy = s.Enemy;

        if (player.Hp <= 0)
            _speech.SayEvent("Defeated.");
        else if (enemy.Hp <= 0)
            _speech.SayEvent("Victory. " + enemy.Name + " defeated. You have " + player.HealthPhrase + ".");
        else
            _speech.SayEvent("Battle over. You have " + player.HealthPhrase + ".");
    }

    // --------------------------------------------------------------- diffing

    private void AnnounceChanges(Snapshot s, Snapshot? previous)
    {
        if (previous is null) return;

        AnnounceHealthChange(previous.Player, s.Player, "You");
        AnnounceHealthChange(previous.Enemy, s.Enemy, s.Enemy.Name);

        if (_config.AnnounceStatusChanges)
        {
            AnnounceStatusChange(previous.Player, s.Player, "You");
            AnnounceStatusChange(previous.Enemy, s.Enemy, s.Enemy.Name);
        }

        AnnounceCountdowns(previous.Player, s.Player);
        AnnounceCardChanges(previous.Player, s.Player, "You");
        AnnounceCardChanges(previous.Enemy, s.Enemy, s.Enemy.Name);
    }

    private void AnnounceCardChanges(FighterInfo before, FighterInfo after, string who)
    {
        var had = before.Equipment.Select(e => e.DisplayName).ToList();
        var has = after.Equipment.Select(e => e.DisplayName).ToList();

        var gained = has.Except(had, StringComparer.OrdinalIgnoreCase).ToList();
        var lost = had.Except(has, StringComparer.OrdinalIgnoreCase).ToList();

        if (gained.Count > 0) _speech.SayEvent(who + " gained " + string.Join(", ", gained));
        if (lost.Count > 0) _speech.SayEvent(who + " lost " + string.Join(", ", lost));
    }

    private void AnnounceHealthChange(FighterInfo before, FighterInfo after, string who)
    {
        if (before.MaxHp == 0 && after.MaxHp == 0) return;

        var delta = after.Hp - before.Hp;
        if (delta == 0) return;

        var amount = Math.Abs(delta);
        var isPlayer = who == "You";
        string subject;
        if (delta < 0) subject = isPlayer ? "You take " + amount + " damage" : who + " takes " + amount + " damage";
        else subject = isPlayer ? "You heal " + amount : who + " heals " + amount;

        _speech.SayEvent(TextCleanup.Join(subject, after.Hp + " of " + after.MaxHp + " left"));
    }

    private void AnnounceStatusChange(FighterInfo before, FighterInfo after, string who)
    {
        var beforeMap = ToMap(before);
        var afterMap = ToMap(after);

        foreach (var entry in afterMap)
        {
            if (!beforeMap.TryGetValue(entry.Key, out var old))
                _speech.SayEvent(who + " gained " + Status(entry.Key, entry.Value));
            else if (old != entry.Value)
                _speech.SayEvent(who + (entry.Value > old ? " gained " : " lost ") + entry.Key + ", now " + entry.Value);
        }

        foreach (var name in beforeMap.Keys)
        {
            if (!afterMap.ContainsKey(name)) _speech.SayEvent(who + " lost " + name);
        }
    }

    private static Dictionary<string, int> ToMap(FighterInfo f)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in f.VisibleStatuses) map[s.Name] = s.Value;
        return map;
    }

    private static string Status(string name, int value) => value > 1 ? name + " " + value : name;

    private void AnnounceCountdowns(FighterInfo before, FighterInfo after)
    {
        foreach (var card in after.Equipment)
        {
            if (card.CountdownMax <= 0) continue;
            var old = before.Equipment.FirstOrDefault(e => e.Index == card.Index && e.Name == card.Name);
            if (old is null) continue;
            if (old.Countdown > 0 && card.Countdown <= 0)
                _speech.SayEvent(card.DisplayName + " is ready");
        }
    }

    // --------------------------------------------------------------- buffers

    private void RefreshBuffers(Snapshot s)
    {
        _buffers.Get(BufferManager.YourCards).Set(s.Player.Equipment.Select(CardLine));
        _buffers.Get(BufferManager.EnemyCards).Set(s.Enemy.Equipment.Select(CardLine));
        _buffers.Get(BufferManager.Dice).Set(
            s.Player.DicePool.Select(d => "Die " + (d.Index + 1) + ": " + d.Describe()));

        var statuses = new List<string>();
        foreach (var x in s.Player.VisibleStatuses) statuses.Add("You: " + x.Describe());
        foreach (var x in s.Enemy.VisibleStatuses) statuses.Add(s.Enemy.Name + ": " + x.Describe());
        _buffers.Get(BufferManager.Statuses).Set(statuses);
    }

    private static string CardLine(EquipInfo card) =>
        string.IsNullOrWhiteSpace(card.Description)
            ? card.Describe()
            : card.Describe() + ". " + TextCleanup.Clean(card.Description);

    // ------------------------------------------------------------ on demand

    public string DescribePlayer()
    {
        var p = Current?.Player;
        if (p is null) return "No combat state yet";

        return TextCleanup.Join(
            p.Name,
            p.HealthPhrase,
            p.Level > 0 ? "level " + p.Level : null,
            p.Gold > 0 ? p.Gold + " gold" : null,
            // mana/maxmana read 100/100 for characters that do not use mana at all
            // (measured on the Warrior), so it is only worth saying when it has
            // actually been spent.
            p.MaxMana > 0 && p.Mana < p.MaxMana ? p.Mana + " of " + p.MaxMana + " mana" : null,
            p.LimitMax > 0 ? "limit break " + p.Limit + " of " + p.LimitMax : null,
            p.VisibleStatuses.Any()
                ? string.Join(", ", p.VisibleStatuses.Select(x => x.Describe()))
                : "no status effects");
    }

    public string DescribeEnemy()
    {
        var e = Current?.Enemy;
        if (e is null) return "No combat state yet";

        return TextCleanup.Join(
            e.Name,
            e.HealthPhrase,
            TextCleanup.Plural(e.Equipment.Count, "card"),
            e.VisibleStatuses.Any()
                ? string.Join(", ", e.VisibleStatuses.Select(x => x.Describe()))
                : "no status effects");
    }

    public string DescribeDice()
    {
        var p = Current?.Player;
        if (p is null || p.DicePool.Count == 0) return "No dice";
        return "Dice: " + string.Join(", ", p.DicePool.Select(d => d.Describe()));
    }

    private static string? DescribeStatuses(Snapshot s)
    {
        var parts = new List<string>();
        if (s.Player.VisibleStatuses.Any())
            parts.Add("You: " + string.Join(", ", s.Player.VisibleStatuses.Select(x => x.Describe())));
        if (s.Enemy.VisibleStatuses.Any())
            parts.Add(s.Enemy.Name + ": " + string.Join(", ", s.Enemy.VisibleStatuses.Select(x => x.Describe())));
        return parts.Count == 0 ? null : string.Join(". ", parts);
    }
}
