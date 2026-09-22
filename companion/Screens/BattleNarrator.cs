using DiceyA11y.Bridge;
using DiceyA11y.Core;
using DiceyA11y.Game.Buffers;

namespace DiceyA11y.Screens;

/// <summary>
/// Says what happens in a fight by comparing consecutive reads of the two fighters.
///
/// Everything comes from live game objects through the bridge: Game.player and
/// Game.monster are elements.Fighter (hp, maxhp, name, status, equipment, dicepool - field
/// names from the published Fighter.hx, Equipment.hx, Dice.hx and StatusEffect.hx), and
/// states.Combat.state names the phase ("playerturn:allocatedice", ...). Names are internal
/// English keys, so they go through the game's own translation before being spoken.
///
/// This replaces the first version of the mod, which needed a Polymod script to print
/// state on stdout: that only worked when the game was started by the launcher, replaced a
/// game file other mods also replace, and spoke English whatever language the game was in.
/// </summary>
public sealed class BattleNarrator
{
    public sealed record StatusInfo(string Name, int Value, string Description = "");

    /// <summary>The fighters' statuses as of the last poll, for the Tab groups.</summary>
    public IReadOnlyList<StatusInfo> PlayerStatuses => _player?.Statuses ?? new List<StatusInfo>();
    public IReadOnlyList<StatusInfo> EnemyStatuses => _enemy?.Statuses ?? new List<StatusInfo>();
    public bool PlayerHasDeck => _player?.DeckStyle ?? false;

    /// <summary>"Shield 3" / "Shield 3. Blocks incoming damage" for one status.</summary>
    public static string DescribeStatus(StatusInfo s, bool withRule) => withRule ? DescribeFully(s) : Describe(s);
    private sealed record CardInfo(string Name, bool Ready, int CountdownLeft, int Countdown, int TimesUsed, bool Available, string Detail = "", bool InHand = true)
    {
        /// <summary>The card whatever state it is in: "Sword" for both Sword and the weakened Sword.</summary>
        public string BaseName { get; init; } = Name;
        public bool Weakened { get; init; }
        public bool Shocked { get; init; }
    }

    private sealed record DiceInfo(int Value, bool Locked, bool Frozen, bool Burning, bool Blind, string On, bool Consumed = false)
    {
        /// <summary>Still in the player's hand: not on a card and not used up (Dice.available, locked ones included).</summary>
        public bool Free => On.Length == 0 && !Consumed;
    }

    private sealed class FighterState
    {
        public string Name = "";
        public int Hp, MaxHp, Level, Gold, Limit, LimitMax;
        public bool DeckStyle;            // the Jester: Fighter.layout == DECK, equipment is a hand of cards
        public int CpuTotal, CpuTarget;   // the Robot: Fighter.roll_total against roll_target
        public bool CpuError;
        public List<StatusInfo> Statuses = new();
        public List<CardInfo> Cards = new();
        public List<DiceInfo> Dice = new();
    }

    private FighterState? _player, _enemy;
    private string _phaseOwner = "";
    private bool _started, _ended, _diceAnnounced;
    private List<string> _freeDice = new(), _pendingFreeDice = new();
    private DateTime _freeDiceChangedAt;
    private List<string> _enemyDice = new(), _pendingEnemyDice = new();
    private DateTime _enemyDiceChangedAt;

    /// <summary>Rules text of cards and statuses, for the review lists. Read once: it costs several requests.</summary>
    private readonly Dictionary<string, string> _details = new();

    public void Reset()
    {
        _player = _enemy = null;
        _phaseOwner = "";
        _started = _ended = _diceAnnounced = false;
        _freeDice = new(); _pendingFreeDice = new();
        _enemyDice = new(); _pendingEnemyDice = new();
        _details.Clear();
        _recentStatuses.Clear();
        _explained.Clear();
    }

    /// <summary>Statuses whose rules text has been said this battle: "Frozen" needs explaining once, not every turn.</summary>
    private readonly HashSet<string> _explained = new();

    public void Poll(ScreenContext ctx)
    {
        var head = ctx.Bridge.Batch("{states.Combat}.state", "{states.Combat}.turn");
        if (!head[0].Exists) return;
        var player = Read(ctx, "{Game}.player");
        var enemy = Read(ctx, "{Game}.monster");
        if (player == null || enemy == null) return;

        RefreshBuffers(ctx, player, enemy);
        foreach (var status in player.Statuses.Concat(enemy.Statuses)) _recentStatuses.Add(status.Name);

        if (!_started)
        {
            _started = true;
            _player = player; _enemy = enemy;
            AnnounceStart(ctx, player, enemy);
        }

        // Once somebody has fallen the board is cleared card by card; none of that is news.
        if (_ended) { _player = player; _enemy = enemy; return; }

        AnnounceEnemyCardUse(ctx, _enemy!, enemy);   // before its effects, so cause precedes result
        Diff(ctx, _player!, player, isPlayer: true);
        Diff(ctx, _enemy!, enemy, isPlayer: false);
        AnnounceTurn(ctx, head[0].AsString(), head[1].AsString(), player, enemy);
        AnnounceEnd(ctx, player, enemy);

        _player = player; _enemy = enemy;
    }

    // ------------------------------------------------------------------ reading

    private FighterState? Read(ScreenContext ctx, string f)
    {
        var v = ctx.Bridge.Batch(f + ".name", f + ".hp", f + ".maxhp", f + ".level", f + ".gold", f + ".limitvalue", f + ".limitmax",
                                 f + ".roll_total", f + ".roll_target", f + ".roll_error", f + ".layout");
        if (!v[0].Exists) return null;
        var s = new FighterState
        {
            Name = ctx.Translate(v[0].AsString()), Hp = v[1].AsInt(), MaxHp = v[2].AsInt(), Level = v[3].AsInt(),
            Gold = v[4].AsInt(), Limit = v[5].AsInt(), LimitMax = v[6].AsInt(),
            CpuTotal = v[7].AsInt(), CpuTarget = v[8].AsInt(), CpuError = v[9].AsBool(),
            DeckStyle = v[10].TypeName == "DECK",
        };

        var statuses = ctx.Bridge.Pluck(f + ".status", "name", "value", "invisible") ?? new();
        for (var i = 0; i < statuses.Count; i++)
        {
            var r = statuses[i];
            // A status spent this turn (burn moved onto a dice) stays in the list at zero until cleanup.
            if (!r[0].Exists || r[2].AsBool() || r[1].AsInt() <= 0) continue;
            var index = i;
            var description = Detail($"{f}:status:{r[0].AsString()}:{r[1].AsInt()}", () => string.Join(" ",
                ctx.Bridge.Dump($"{f}.status[{index}].description").Select(l => StageCleaner.Clean(l.Value.AsString())).Where(l => l.Length > 0)));
            s.Statuses.Add(new StatusInfo(StatusName(ctx, r[0].AsString()), r[1].AsInt(), description));
        }

        var cards = ctx.Bridge.Pluck(f + ".equipment", "name", "ready", "remainingcountdown", "countdown", "timesused", "availablethisturn", "namemodifier", "skillcard", "ty", "show", "shockedsetting", "reducecountdownby") ?? new();
        for (var i = 0; i < cards.Count; i++)
        {
            var r = cards[i];
            if (!r[0].Exists) continue;
            // A dice on a countdown slot is spent at once, but the number on the card ticks down
            // one step every 0.12 s (Equipment.reducecountdownby): what is left is the number
            // less what is still to tick, or every step would be an event of its own.
            var countdownLeft = Math.Max(0, r[2].AsInt() - r[11].AsInt());
            var card = $"{f}.equipment[{i}]";
            var name = CardText.DecorateName(ctx, r[0].AsString(), r[6].AsString());
            if (r[7].AsString() == "switchfighter") name = Strings.T("panel.switch");
            if (r[7].AsString() == "stolencard")
            {
                // The Thief's placeholder card stands for whatever was stolen this turn.
                var stolen = ctx.Bridge.Eval(f + ".stolencard.name");
                card = f + ".stolencard";
                name = Strings.T("card.stolen") + (stolen.Exists ? " " + ctx.Translate(stolen.AsString()) : "");
                if (!stolen.Exists) { s.Cards.Add(new CardInfo(name, r[1].AsBool(true), 0, 0, r[4].AsInt(), r[5].AsBool(true))); continue; }
            }
            var target = card;
            var detail = Detail($"{f}:card:{name}", () => TextCleanup.Join(CardText.Description(ctx, target), CardText.Slots(ctx, target, withFilled: false)));
            // With a deck, Fighter.equipment also keeps the cards already played or discarded this
            // turn, parked off screen (observed: ty far above or below the 2160-high canvas).
            var inHand = !s.DeckStyle || (r[1].AsBool(true) && r[9].AsBool(true) && r[8].AsDouble() > -200 && r[8].AsDouble() < 2100);
            s.Cards.Add(new CardInfo(name, r[1].AsBool(true), countdownLeft, r[3].AsInt(), r[4].AsInt(), r[5].AsBool(true), detail, inHand)
            {
                BaseName = r[7].AsString().Length > 0 ? name : CardText.DecorateName(ctx, r[0].AsString(), r[6].AsString().Replace("-", "")),
                Weakened = r[6].AsString().Contains('-'),
                Shocked = r[10].AsInt() > 0,
            });
        }

        foreach (var r in ctx.Bridge.Pluck(f + ".dicepool", "basevalue", "modifier", "locked", "frozen", "burn", "blind", "assigned.name", "consumed") ?? new())
            if (r[0].Exists)
                s.Dice.Add(new DiceInfo(r[0].AsInt() + r[1].AsInt(), r[2].AsBool(), r[3].AsBool(), r[4].AsBool(), r[5].AsBool(),
                                        r[6].Exists ? ctx.Translate(r[6].AsString()) : "", r[7].AsBool()));
        return s;
    }

    private string Detail(string key, Func<string> read)
    {
        if (_details.TryGetValue(key, out var cached)) return cached;
        var text = read();
        if (text.Length > 0) _details[key] = text;
        return text;
    }

    /// <summary>Status names are lower-case keys ("fire"); the translation table has them capitalised.</summary>
    private static string StatusName(ScreenContext ctx, string key)
    {
        if (key.Length == 0) return key;
        var capitalised = char.ToUpperInvariant(key[0]) + key[1..];
        var translated = ctx.Translate(capitalised);
        return translated.Length > 0 ? translated : key;
    }

    // ------------------------------------------------------------------ announcements

    private static string Health(FighterState f) => Strings.T("battle.health", f.Hp, f.MaxHp);

    private void AnnounceStart(ScreenContext ctx, FighterState player, FighterState enemy)
    {
        ctx.SayEvent(Strings.T("battle.start", enemy.Name, Health(enemy), Health(player)));
        if (ctx.Config.AnnounceEnemyEquipment && enemy.Cards.Count > 0)
            ctx.SayEvent(Strings.T("battle.enemy_cards", enemy.Name, string.Join(", ", enemy.Cards.Select(c => c.Name))));
        ctx.SayEvent(Strings.T("battle.groups_hint"));
        foreach (var (f, who) in new[] { (player, Strings.T("battle.you")), (enemy, enemy.Name) })
            if (f.Statuses.Count > 0) ctx.SayEvent(who + " " + string.Join(", ", f.Statuses.Select(Describe)));
    }

    private void AnnounceTurn(ScreenContext ctx, string phase, string turn, FighterState player, FighterState enemy)
    {
        // states.Combat.turn is "player" on the player's turn (observed); anything else is the enemy's.
        var owner = turn.Length == 0 ? "" : turn == "player" ? "player" : "enemy";
        if (owner.Length > 0 && owner != _phaseOwner)
        {
            _phaseOwner = owner;
            _diceAnnounced = false;
            _enemyDice = new(); _pendingEnemyDice = new();
            ctx.SayEvent(owner == "player" ? Strings.T("battle.your_turn") : Strings.T("battle.enemy_turn", enemy.Name));
        }

        // The dice exist only once they have been thrown and the player may act.
        if (_phaseOwner == "player" && !_diceAnnounced && phase.EndsWith("allocatedice", StringComparison.Ordinal) && player.Dice.Count > 0)
        {
            _diceAnnounced = true;
            _freeDice = _pendingFreeDice = FreeDice(player);
            if (ctx.Config.AnnounceDice)
                ctx.SayEvent(Strings.T("dice.list", string.Join(", ", _freeDice)));
        }
        else if (_phaseOwner == "player" && _diceAnnounced) AnnounceNewDice(ctx, player);
        else if (_phaseOwner == "enemy") AnnounceEnemyDice(ctx, enemy);
    }

    private static List<string> FreeDice(FighterState f) => f.Dice.Where(d => d.Free).Select(Describe).ToList();

    /// <summary>
    /// What the enemy rolled, which a sighted player watches land before the cards are played.
    /// Every dice in its pool counts, spent or not (the roll is the news, and by the time the
    /// pool has settled the first dice may already sit on a card); dice made later in the turn
    /// are announced the same way.
    /// </summary>
    private void AnnounceEnemyDice(ScreenContext ctx, FighterState enemy)
    {
        var dice = enemy.Dice.Select(Describe).ToList();
        if (!dice.SequenceEqual(_pendingEnemyDice)) { _pendingEnemyDice = dice; _enemyDiceChangedAt = DateTime.UtcNow; return; }
        if (dice.SequenceEqual(_enemyDice) || DateTime.UtcNow - _enemyDiceChangedAt < TimeSpan.FromMilliseconds(350)) return;

        var left = new List<string>(_enemyDice);
        var added = dice.Where(d => !left.Remove(d)).ToList();
        _enemyDice = dice;
        if (added.Count > 0 && ctx.Config.AnnounceDice)
            ctx.SayEvent(Strings.T("dice.enemy", enemy.Name, string.Join(", ", added)));
    }

    /// <summary>
    /// Cards that split, reroll, bump or duplicate a dice change what the player holds. A
    /// dice merely being spent is not news; a dice that was not there before is. Dice are
    /// created one at a time, so wait until the pool has stopped changing.
    /// </summary>
    private void AnnounceNewDice(ScreenContext ctx, FighterState player)
    {
        var free = FreeDice(player);
        if (!free.SequenceEqual(_pendingFreeDice)) { _pendingFreeDice = free; _freeDiceChangedAt = DateTime.UtcNow; return; }
        if (free.SequenceEqual(_freeDice) || DateTime.UtcNow - _freeDiceChangedAt < TimeSpan.FromMilliseconds(600)) return;

        var left = new List<string>(_freeDice);
        var added = free.Where(d => !left.Remove(d)).ToList();
        _freeDice = free;
        if (added.Count > 0 && ctx.Config.AnnounceDice)
            ctx.SayEvent(free.Count == 0 ? Strings.T("dice.none") : Strings.T("dice.list", string.Join(", ", free)));
    }

    private void Diff(ScreenContext ctx, FighterState before, FighterState after, bool isPlayer)
    {
        var who = isPlayer ? Strings.T("battle.you") : after.Name;

        // The finale's "switch fighter": another contestant steps in with their own health and cards.
        if (isPlayer && before.Name.Length > 0 && after.Name != before.Name)
        {
            ctx.SayEvent(Strings.T("battle.switched", after.Name, after.Hp, after.MaxHp, string.Join(", ", after.Cards.Where(c => c.InHand).Select(c => c.Name))));
            return;
        }

        var delta = after.Hp - before.Hp;
        if (delta < 0) ctx.SayEvent(Strings.T(isPlayer ? "battle.you_hurt" : "battle.hurt", who, -delta, after.Hp, after.MaxHp));
        else if (delta > 0) ctx.SayEvent(Strings.T(isPlayer ? "battle.you_heal" : "battle.heal", who, delta, after.Hp, after.MaxHp));

        // The Robot asks for dice one at a time; the running total against the CPU target decides
        // between carrying on, a jackpot (exactly the target) and an error (over it).
        if (isPlayer && after.CpuTarget > 0 && after.CpuTotal != before.CpuTotal && after.CpuTotal > 0)
            ctx.SayEvent(after.CpuError ? Strings.T("robot.error", after.CpuTotal, after.CpuTarget)
                       : after.CpuTotal == after.CpuTarget ? Strings.T("robot.jackpot", after.CpuTotal)
                       : Strings.T("robot.cpu", after.CpuTotal, after.CpuTarget));

        // The limit bar fills as the player is hurt; when full the game makes the button shake.
        if (isPlayer && after.LimitMax > 0 && after.Limit >= after.LimitMax && before.Limit < before.LimitMax)
            ctx.SayEvent(Strings.T("limit.ready", CombatHandler.DescribeLimitBreak(ctx)));

        if (ctx.Config.AnnounceStatusChanges)
        {
            var old = before.Statuses.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.Sum(s => s.Value));
            var now = after.Statuses.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.Sum(s => s.Value));
            foreach (var (name, value) in now)
            {
                if (!old.TryGetValue(name, out var was))
                {
                    // "You frozen" says nothing to someone who cannot see the icon's tooltip:
                    // the status's own rules text follows, the first time it appears in a battle.
                    var text = Strings.T("battle.status_gain", who, Describe(new StatusInfo(name, value)));
                    var rules = after.Statuses.FirstOrDefault(s => s.Name == name)?.Description ?? "";
                    if (rules.Length > 0 && _explained.Add(name)) text = TextCleanup.JoinSentences(text, rules);
                    ctx.SayEvent(text);
                }
                else if (was != value) ctx.SayEvent(Strings.T("battle.status_now", who, name, value));
            }
            foreach (var name in old.Keys.Where(n => !now.ContainsKey(n))) ctx.SayEvent(Strings.T("battle.status_lost", who, name));
        }

        // A countdown card comes down as dice land on it. The player put their own dice there and
        // only needs to hear when it is ready; the enemy's dice landing is otherwise invisible
        // (third play-test: "Spiky Potion is ready" came out of nowhere after three dice).
        foreach (var card in after.Cards.Where(c => c.Countdown > 0))
        {
            var was = before.Cards.FirstOrDefault(c => c.Name == card.Name);
            if (was == null || card.CountdownLeft >= was.CountdownLeft) continue;
            if (isPlayer) { if (card.CountdownLeft <= 0) ctx.SayEvent(Strings.T("battle.card_ready", card.Name)); continue; }
            // The dice just put there, when the game still has them on the card; else the amount.
            var had = before.Dice.Where(d => d.On == card.Name).Select(d => d.Value).ToList();
            var put = after.Dice.Where(d => d.On == card.Name && !d.Blind).Select(d => d.Value).Where(v => !had.Remove(v)).ToList();
            var dice = put.Count > 0 ? string.Join(", ", put) : (was.CountdownLeft - card.CountdownLeft).ToString();
            ctx.SayEvent(card.CountdownLeft > 0 ? Strings.T("battle.enemy_countdown", who, dice, card.Name, card.CountdownLeft)
                                                : Strings.T("battle.enemy_countdown_ready", who, dice, card.Name));
        }

        if (after.DeckStyle)
        {
            // A hand of cards: what matters is what was just drawn. Counted, because decks hold copies.
            var had = before.Cards.Where(c => c.InHand).Select(c => c.Name).ToList();
            var drawn = after.Cards.Where(c => c.InHand).Select(c => c.Name).Where(n => !had.Remove(n)).ToList();
            if (drawn.Count > 0 && before.Cards.Count > 0) ctx.SayEvent(Strings.T("jester.drawn", string.Join(", ", drawn)));
            return;
        }

        // A status that moves from the fighter onto the cards (shock, weaken) is one event per
        // card - "Sword weakened" - not the unweakened card lost and a weakened one gained.
        var wasCards = before.Cards.ToList();
        foreach (var card in after.Cards)
        {
            var was = wasCards.FirstOrDefault(c => c.BaseName == card.BaseName);
            if (was == null) continue;
            wasCards.Remove(was);
            if (card.Shocked && !was.Shocked) ctx.SayEvent(Strings.T("battle.card_shocked", who, card.BaseName));
            if (card.Weakened && !was.Weakened) ctx.SayEvent(Strings.T("battle.card_weakened", who, card.BaseName));
            if (!card.Weakened && was.Weakened) ctx.SayEvent(Strings.T("battle.card_restored", who, card.BaseName));
        }

        var gained = after.Cards.Select(c => c.BaseName).Except(before.Cards.Select(c => c.BaseName)).ToList();
        var lost = before.Cards.Select(c => c.BaseName).Except(after.Cards.Select(c => c.BaseName)).ToList();
        // The Thief's stolen card is a different enemy card every turn, not a card won and lost.
        var stolenPrefix = Strings.T("card.stolen");
        var stolen = gained.FirstOrDefault(n => n.StartsWith(stolenPrefix, StringComparison.Ordinal));
        if (stolen != null && stolen.Length > stolenPrefix.Length) ctx.SayEvent(Strings.T("battle.stolen", stolen[stolenPrefix.Length..].Trim()));
        gained.RemoveAll(n => n.StartsWith(stolenPrefix, StringComparison.Ordinal));
        lost.RemoveAll(n => n.StartsWith(stolenPrefix, StringComparison.Ordinal));
        if (gained.Count > 0) ctx.SayEvent(Strings.T("battle.cards_gained", who, string.Join(", ", gained)));
        if (lost.Count > 0) ctx.SayEvent(Strings.T("battle.cards_lost", who, string.Join(", ", lost)));
    }

    /// <summary>The player knows what they played; the enemy's moves are otherwise only visible.</summary>
    private static void AnnounceEnemyCardUse(ScreenContext ctx, FighterState before, FighterState after)
    {
        for (var i = 0; i < after.Cards.Count && i < before.Cards.Count; i++)
            if (after.Cards[i].Name == before.Cards[i].Name && after.Cards[i].TimesUsed > before.Cards[i].TimesUsed)
            {
                // The dice it was played with, when the game still has them on the card.
                var card = after.Cards[i].Name;
                var dice = after.Dice.Concat(before.Dice.Where(d => !after.Dice.Contains(d))).Where(d => d.On == card && !d.Blind).Select(d => d.Value.ToString()).Distinct().ToList();
                ctx.SayEvent(dice.Count > 0 ? Strings.T("battle.enemy_uses_dice", after.Name, card, string.Join(", ", dice))
                                            : Strings.T("battle.enemy_uses", after.Name, card));
            }
    }

    private void AnnounceEnd(ScreenContext ctx, FighterState player, FighterState enemy)
    {
        if (_ended) return;
        if (player.Hp <= 0) { _ended = true; ctx.SayEvent(Strings.T("battle.defeat")); }
        else if (enemy.Hp <= 0) { _ended = true; ctx.SayEvent(Strings.T("battle.victory", enemy.Name, Health(player))); }
    }

    // ------------------------------------------------------------------ descriptions

    private static string Describe(StatusInfo s) => s.Value > 1 ? s.Name + " " + s.Value : s.Name;

    private static string Describe(DiceInfo d)
    {
        var parts = new List<string> { d.Blind ? Strings.T("dice.hidden") : d.Value.ToString() };
        if (d.Locked) parts.Add(Strings.T("dice.locked"));
        if (d.Frozen) parts.Add(Strings.T("dice.frozen"));
        if (d.Burning) parts.Add(Strings.T("dice.burning"));
        return string.Join(" ", parts);
    }

    private static string Describe(CardInfo c)
    {
        var parts = new List<string> { c.Name };
        if (c.Countdown > 0) parts.Add(c.CountdownLeft > 0 ? Strings.T("slot.COUNTDOWN", c.CountdownLeft) : Strings.T("card.ready"));
        if (!c.Ready || !c.Available) parts.Add(Strings.T("card.unavailable"));
        return string.Join(", ", parts);
    }

    /// <summary>For the review lists: what hovering the card or the status icon would show.</summary>
    private static string DescribeFully(CardInfo c) => c.Detail.Length > 0 ? Describe(c) + ". " + c.Detail : Describe(c);
    private static string DescribeFully(StatusInfo s) => s.Description.Length > 0 ? Describe(s) + ". " + s.Description : Describe(s);

    private static void RefreshBuffers(ScreenContext ctx, FighterState player, FighterState enemy)
    {
        ctx.Buffers.Get(BufferManager.YourCards).Set(player.Cards.Where(c => c.InHand).Select(DescribeFully));
        ctx.Buffers.Get(BufferManager.EnemyCards).Set(enemy.Cards.Select(DescribeFully));
        // Only what can still be played: a dice on a card or used up is no longer the player's to place.
        ctx.Buffers.Get(BufferManager.Dice).Set(player.Dice.Where(d => d.Free).Select(Describe));
        var you = new List<string> { player.Name, Health(player) };
        if (player.LimitMax > 0) you.Add(Strings.T("battle.limit", player.Limit, player.LimitMax));
        if (player.CpuTarget > 0) you.Add(Strings.T("robot.cpu", player.CpuTotal, player.CpuTarget));
        you.AddRange(player.Statuses.Select(DescribeFully));
        you.Add(Strings.T("map.level", player.Level));
        you.Add(Strings.T("icon.gold", player.Gold));
        ctx.Buffers.Get(BufferManager.Player).Set(you);
        ctx.Buffers.Get(BufferManager.Statuses).Set(
            player.Statuses.Select(s => Strings.T("battle.you") + ": " + DescribeFully(s))
                  .Concat(enemy.Statuses.Select(s => enemy.Name + ": " + DescribeFully(s))));
    }

    /// <summary>True once either fighter has fallen.</summary>
    public bool Ended => _ended;

    /// <summary>Names as drawn in the battle HUD, which the narrator already covers.</summary>
    public IEnumerable<string> FighterNames => new[] { _player?.Name, _enemy?.Name }.Where(n => !string.IsNullOrEmpty(n))!;

    /// <summary>Status names as the game floats them over a fighter ("Burn 1"); the narrator has already said them.</summary>
    public bool IsStatusLabel(string text)
    {
        return _recentStatuses.Any(n => text.StartsWith(n, StringComparison.OrdinalIgnoreCase) && text[n.Length..].Trim().All(char.IsDigit));
    }

    private readonly HashSet<string> _recentStatuses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ctrl+D.</summary>
    public string DescribeDice()
    {
        if (_player == null) return Strings.T("status.battle_only");
        var free = FreeDice(_player);
        return free.Count == 0 ? Strings.T("dice.none") : Strings.T("dice.list", string.Join(", ", free));
    }

    /// <summary>Ctrl+H.</summary>
    public string DescribeHealth() => _player == null ? "" : Health(_player);

    /// <summary>Ctrl+E. The game only ever has one enemy at a time.</summary>
    public string DescribeEnemyHealth() => _enemy == null ? Strings.T("status.battle_only") : _enemy.Name + ", " + Health(_enemy);

    public string DescribeBoth()
    {
        if (_player == null || _enemy == null) return "";
        string One(string who, FighterState f) => TextCleanup.Join(who, Health(f),
            f.Statuses.Count > 0 ? string.Join(", ", f.Statuses.Select(Describe)) : null);
        return One(Strings.T("battle.you"), _player)
             + (_player.LimitMax > 0 ? ", " + Strings.T("battle.limit", _player.Limit, _player.LimitMax) : "")
             + (_player.CpuTarget > 0 ? ", " + Strings.T("robot.cpu", _player.CpuTotal, _player.CpuTarget) : "")
             + ". " + One(_enemy.Name, _enemy) + ".";
    }
}
