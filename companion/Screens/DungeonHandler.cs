using DiceyA11y.Bridge;
using DiceyA11y.Core;
using DiceyA11y.Input;

namespace DiceyA11y.Screens;

/// <summary>
/// The dungeon map. The game holds each floor as a graph: states.Dungeon.floor[Game.currentfloor]
/// .nodes are elements.GraphNode with x, y, type (elements.DungeonBlockType), enemytemplate,
/// visited and connections; states.Dungeon.playerposition is the node the player is on.
///
/// Measured behaviour this design rests on:
///  - Nothing happens on arriving at a node. The chest opens or the fight starts only when
///    A is pressed there, so walking is always safe.
///  - The d-pad cannot reach every neighbour. Neighbours sit on diagonals, so "up" is a tie
///    between up-left and up-right and the game then does not move at all. The tutorial
///    tells players to use the stick, and the stick works: a short push at the exact angle
///    of a neighbour moves exactly one step to exactly that neighbour.
///
/// So on this screen the mod owns movement, through a cursor of its own that explores the
/// map without moving the player: the arrows move it from space to space, comma and period
/// jump it between the points of interest, Space brings it back to the player, P says how to
/// walk to it. Enter walks to the space under the cursor when it is one step away (by
/// pushing the virtual stick at exactly that neighbour), and on the player's own space it is
/// the game's A: open the chest, start the fight.
/// </summary>
public sealed class DungeonHandler : ScreenHandler
{
    private const string Position = "{states.Dungeon}.playerposition";
    private const string Nodes = "{states.Dungeon}.floor[{Game}.currentfloor].nodes";
    private const int MaxLinks = 6;
    private const int EnemyPenalty = 50;   // route around a fight when there is another way

    private sealed record MapNode(string Id, double X, double Y, string Type, string Enemy, bool Visited, List<string> Links);
    private sealed record Target(MapNode Node, List<MapNode> Route);

    private readonly VirtualPad _pad;
    private string _lastPosition = "";
    private string _cursorId = "";
    private DateTime _stepAt = DateTime.MinValue;   // when the last step was asked for
    private string _stepFrom = "";

    public DungeonHandler(VirtualPad pad) => _pad = pad;

    public override bool Handles(string scene) => scene == "states.Dungeon";

    public override void OnEnter(ScreenContext ctx)
    {
        _lastPosition = "";
        _cursorId = "";
        _hp = _gold = -1;
        base.OnEnter(ctx);
    }

    private int _hp = -1, _gold = -1;

    /// <summary>An apple, a purchase, a trap: on the map these only show as the strip's numbers changing.</summary>
    private void AnnounceVitals(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch("{Game}.player.hp", "{Game}.player.maxhp", "{Game}.player.gold");
        if (!v[0].Exists) return;
        int hp = v[0].AsInt(), gold = v[2].AsInt();
        if (_hp >= 0 && hp != _hp) ctx.SayEvent(Strings.T(hp > _hp ? "map.healed" : "map.hurt", Math.Abs(hp - _hp), hp, v[1].AsInt()));
        if (_gold >= 0 && gold != _gold) ctx.SayEvent(Strings.T("map.gold", gold));
        _hp = hp; _gold = gold;
    }

    public static string PlayerStatus(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch("{Game}.player.hp", "{Game}.player.maxhp", "{Game}.player.gold", "{Game}.player.level",
                                 "{Game}.player.dice", "{Game}.currentfloor", "{states.LevelUpScreen}.nextlevelexp");
        if (!v[0].Exists) return "";
        return Strings.T("map.status", v[0].AsInt(), v[1].AsInt(), v[2].AsInt(), v[3].AsInt(), v[4].AsInt(), v[5].AsInt() + 1);
    }

    public override void OnPoll(ScreenContext ctx)
    {
        base.OnPoll(ctx);
        AnnounceVitals(ctx);

        var map = ReadFloor(ctx, out var here);
        if (here == null) return;
        ctx.Buffers.Get(Game.Buffers.BufferManager.Player).Set(PlayerLines(ctx));

        // A step that went nowhere must not be answered with silence. The game refuses to
        // walk past an enemy the player is standing on (observed), and says so only visually.
        if (_stepFrom == here.Id && DateTime.UtcNow - _stepAt > TimeSpan.FromSeconds(1.2))
        {
            _stepFrom = "";
            _walkTo = "";
            // The enemy on this space is what Enter must act on now, so the cursor comes back
            // here: left on the far destination of the walk, Enter only said "that is 4 steps
            // away" and the promised fight never started.
            if (here.Type == "ENEMY") { _cursorId = here.Id; _blockedOn = here.Id; }
            ctx.Say(here.Type == "ENEMY" ? Strings.T("map.blocked_enemy", ctx.Translate(here.Enemy)) : Strings.T("map.blocked"), interrupt: true);
        }

        if (here.Id != _lastPosition)
        {
            _stepFrom = "";
            _blockedOn = "";
            _arrivedAt = DateTime.UtcNow;
            var first = _lastPosition.Length == 0;
            _lastPosition = here.Id;
            _fork.Clear();

            // On a walk the cursor stays on the destination and each space passed is only named.
            var route = _walkTo.Length > 0 && _walkTo != here.Id ? Routes(map, here).GetValueOrDefault(_walkTo) : null;
            if (route != null)
                ctx.Say(Strings.T("map.step", DescribeNode(ctx, here, false), route.Count, DescribeNode(ctx, map[_walkTo], false)), interrupt: false);
            else
            {
                _walkTo = "";
                _cursorId = here.Id;   // the cursor travels with the player
                var text = DescribeHere(ctx, here) + " " + DescribeExits(ctx, map, here);
                if (first) ctx.SayEvent(text); else ctx.Say(text, interrupt: RecentInput(ctx));
                if (ctx.State.Focus == null) ctx.FocusItem(CursorLines(ctx, map, here, here));
            }
        }

        ContinueWalk(ctx, map, here);
    }

    private string _walkTo = "";
    private string _blockedOn = "";   // the enemy space the game refused to walk on from
    private DateTime _arrivedAt = DateTime.MinValue;

    /// <summary>
    /// P walks to the space under the cursor, one stick push per step, each sent once the
    /// previous step has landed. It ends on arrival, when the game refuses a step (an enemy
    /// the player stands on blocks the way on: the 1.2 s check above says so), when a menu
    /// opens, or at the player's next key.
    /// </summary>
    private void ContinueWalk(ScreenContext ctx, Dictionary<string, MapNode> map, MapNode here)
    {
        if (_walkTo.Length == 0 || _stepFrom.Length > 0) return;
        if (ctx.State.Focus != null || !map.ContainsKey(_walkTo) || _walkTo == here.Id) { _walkTo = ""; return; }
        if (DateTime.UtcNow - _arrivedAt < TimeSpan.FromMilliseconds(250)) return;   // let the step's animation finish
        var route = Routes(map, here).GetValueOrDefault(_walkTo);
        if (route == null || route.Count == 0) { _walkTo = ""; return; }
        PushToward(here, route[0]);
    }

    private void PushToward(MapNode here, MapNode next)
    {
        double dx = next.X - here.X, dy = next.Y - here.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return;
        _stepFrom = here.Id;
        _stepAt = DateTime.UtcNow;
        _pad.PushStick(dx / length, dy / length, 100);
    }

    /// <summary>
    /// The strip along the bottom (health, level, gold, dice, floor) is on screen the whole
    /// run. Read aloud on every return to the map it is noise; F4 still reads it on demand.
    /// </summary>
    protected override bool ShouldSpeak(ScreenContext ctx, Paragraph p)
        => p.Y < StatusBarTop && !(p.Text.Length < 6 && p.Text.Any(char.IsDigit) && "+-".Contains(p.Text[0]));   // floating "+5": AnnounceVitals says it

    private const int StatusBarTop = 1900;   // observed: the strip's text sits at y 1954-2046 of 2160

    // ------------------------------------------------------------------ keys

    public override bool OnKey(ScreenContext ctx, NavKey key)
    {
        if (ctx.State.Focus != null) return false;   // a menu is open over the map: let the game have the keys
        // Any key ends a walk in progress: the player is never carried further than they want.
        if (_walkTo.Length > 0)
        {
            _walkTo = "";
            if (key is NavKey.Path or NavKey.Confirm) { ctx.Say(Strings.T("map.walk_stopped"), interrupt: true); return true; }
        }
        switch (key)
        {
            case NavKey.Left: case NavKey.Right: case NavKey.Up: case NavKey.Down: MoveCursor(ctx, key); return true;
            case NavKey.PreviousItem: Cycle(ctx, -1); return true;
            case NavKey.NextItem: Cycle(ctx, +1); return true;
            case NavKey.Snap: Snap(ctx); return true;
            case NavKey.Path: StartWalk(ctx); return true;
            case NavKey.Confirm: return Walk(ctx);
            default: return false;
        }
    }

    /// <summary>The node under the cursor; the player's own when the cursor has not been moved (or its node is gone).</summary>
    private MapNode CursorNode(Dictionary<string, MapNode> map, MapNode here)
        => map.TryGetValue(_cursorId, out var node) ? node : here;

    private void SetCursor(ScreenContext ctx, Dictionary<string, MapNode> map, MapNode here, MapNode node, string? suffix = null)
    {
        _cursorId = node.Id;
        var text = DescribeCursor(ctx, map, here, node);
        ctx.Say(suffix == null ? text : text + ", " + suffix, interrupt: true);
        ctx.FocusItem(CursorLines(ctx, map, here, node));
    }

    /// <summary>What the arrows will do next when the last one met two paths: key -> space.</summary>
    private readonly Dictionary<NavKey, string> _fork = new();

    /// <summary>
    /// The arrows follow the paths, and nothing else. Paths only ever run in six directions
    /// (counted over every floor of a run with tools/mapoffsets.py: (+-480, +-240) and
    /// (0, +-480)), so a space has at most one neighbour in each, and:
    ///
    ///   Up / Down      the space straight above / below if a path leads there; otherwise the
    ///                  one on that side (up left or up right) if there is only one
    ///   Left / Right   the one on that side (up or down) if there is only one
    ///   two that way   both are named, and the other pair of arrows picks: after Right,
    ///                  Up takes "up right" and Down "down right"
    ///
    /// "Up right" is therefore Up and Right in either order, exactly as it is spoken. The
    /// first version searched a cone for the nearest space, and an exit announced as "up
    /// right" (it lies 27 degrees above the horizontal) did not answer to Up at all.
    /// </summary>
    private void MoveCursor(ScreenContext ctx, NavKey key)
    {
        var map = ReadFloor(ctx, out var here);
        if (here == null) return;
        var from = CursorNode(map, here);

        if (_fork.Remove(key, out var chosen) && map.TryGetValue(chosen, out var picked) && from.Links.Contains(chosen))
        {
            _fork.Clear();
            SetCursor(ctx, map, here, picked);
            return;
        }
        _fork.Clear();

        var vertical = key is NavKey.Up or NavKey.Down;
        var sign = key is NavKey.Up or NavKey.Left ? -1 : 1;
        var side = from.Links.Where(map.ContainsKey).Select(id => map[id])
                       .Where(n => Math.Sign(Math.Round(vertical ? n.Y - from.Y : n.X - from.X)) == sign).ToList();
        var straight = vertical ? side.FirstOrDefault(n => Math.Abs(n.X - from.X) < 1) : null;

        if (straight != null || side.Count == 1) { SetCursor(ctx, map, here, straight ?? side[0]); return; }

        var way = Strings.T("dir." + (key == NavKey.Right ? 0 : key == NavKey.Down ? 2 : key == NavKey.Left ? 4 : 6));
        if (side.Count == 0)
        {
            var exits = from.Links.Where(map.ContainsKey).Select(id => map[id]).OrderBy(n => Sector(n.X - from.X, n.Y - from.Y))
                            .Select(n => Strings.T("dir." + Sector(n.X - from.X, n.Y - from.Y))).ToList();
            ctx.Say(Strings.T("map.no_path", way, string.Join(", ", exits)), interrupt: true);
            return;
        }

        // Two paths that way: one of the other two arrows decides.
        var options = new List<string>();
        foreach (var n in side.OrderBy(n => vertical ? n.X : n.Y))
        {
            var pick = vertical ? (n.X < from.X ? NavKey.Left : NavKey.Right) : (n.Y < from.Y ? NavKey.Up : NavKey.Down);
            _fork[pick] = n.Id;
            options.Add(Strings.T("map.exit", Strings.T("dir." + (pick == NavKey.Right ? 0 : pick == NavKey.Down ? 2 : pick == NavKey.Left ? 4 : 6)), DescribeNode(ctx, n, true)));
        }
        ctx.Say(Strings.T(vertical ? "map.fork_vertical" : "map.fork_horizontal", way, string.Join("; ", options)), interrupt: true);
    }

    private void Cycle(ScreenContext ctx, int direction)
    {
        var map = ReadFloor(ctx, out var here);
        if (here == null) return;
        // Points of interest only: an empty space next door is an exit, not a point of interest.
        var targets = Targets(map, here).Where(t => t.Node.Type != "EMPTY").ToList();
        if (targets.Count == 0) { ctx.Say(Strings.T("map.no_exits"), interrupt: true); return; }

        var index = targets.FindIndex(t => t.Node.Id == _cursorId);
        index = index < 0 ? (direction > 0 ? 0 : targets.Count - 1) : (index + direction + targets.Count) % targets.Count;
        SetCursor(ctx, map, here, targets[index].Node, Strings.T("of", index + 1, targets.Count));
    }

    private void Snap(ScreenContext ctx)
    {
        var map = ReadFloor(ctx, out var here);
        if (here != null) SetCursor(ctx, map, here, here);
    }

    /// <summary>P: walk to the space under the cursor. The route (fights avoided where possible) is said first.</summary>
    private void StartWalk(ScreenContext ctx)
    {
        var map = ReadFloor(ctx, out var here);
        if (here == null) return;
        var target = CursorNode(map, here);
        if (target.Id == here.Id) { ctx.Say(Strings.T("map.path_here"), interrupt: true); return; }
        if (!Routes(map, here).ContainsKey(target.Id)) { ctx.Say(Strings.T("map.cursor_unreachable", DescribeNode(ctx, target, true)), interrupt: true); return; }
        ctx.Say(Strings.T("map.walking", DescribeNode(ctx, target, false), DescribePath(ctx, map, here, target)), interrupt: true);
        _walkTo = target.Id;
    }

    /// <summary>Enter. False hands the key to the game as A: the player is on the space under the cursor.</summary>
    private bool Walk(ScreenContext ctx)
    {
        var map = ReadFloor(ctx, out var here);
        if (here == null) return false;
        var target = CursorNode(map, here);
        if (target.Id == here.Id) return false;
        // Standing on an enemy that blocks the way: Enter is the fight, wherever the cursor is.
        if (here.Type == "ENEMY" && _blockedOn == here.Id) { _cursorId = here.Id; return false; }

        if (!here.Links.Contains(target.Id))
        {
            // Not one step away: nothing happens in the game, and the player is told why.
            var route = Routes(map, here).GetValueOrDefault(target.Id);
            ctx.Say(route == null ? Strings.T("map.cursor_unreachable", DescribeNode(ctx, target, true)) : Strings.T("map.too_far", route.Count), interrupt: true);
            return true;
        }

        PushToward(here, target);
        return true;
    }

    // ------------------------------------------------------------------ reading the floor

    private static Dictionary<string, MapNode> ReadFloor(ScreenContext ctx, out MapNode? here)
    {
        here = null;
        var paths = new List<string> { "x", "y", "type", "enemytemplate.name", "visited", "connections.length" };
        for (var i = 0; i < MaxLinks; i++) { paths.Add($"connections[{i}].x"); paths.Add($"connections[{i}].y"); }

        var map = new Dictionary<string, MapNode>();
        var rows = ctx.Bridge.Pluck(Nodes, paths.ToArray());
        var at = ctx.Bridge.Batch(Position + ".x", Position + ".y");
        if (rows == null || !at[0].Exists) return map;

        foreach (var r in rows)
        {
            if (!r[0].Exists || r[2].TypeName == "INVISIBLE") continue;
            var links = new List<string>();
            var count = Math.Min(r[5].AsInt(), MaxLinks);
            for (var i = 0; i < count; i++)
                if (r[6 + i * 2].Exists) links.Add(Id(r[6 + i * 2].AsDouble(), r[7 + i * 2].AsDouble()));
            var node = new MapNode(Id(r[0].AsDouble(), r[1].AsDouble()), r[0].AsDouble(), r[1].AsDouble(),
                                   r[2].TypeName, r[3].AsString(), r[4].AsBool(), links);
            map[node.Id] = node;
        }
        map.TryGetValue(Id(at[0].AsDouble(), at[1].AsDouble()), out here);
        return map;
    }

    private static string Id(double x, double y) => $"{x:0},{y:0}";

    /// <summary>Exits first, clockwise from the right; then everything else worth walking to, nearest first.</summary>
    private static List<Target> Targets(Dictionary<string, MapNode> map, MapNode here)
    {
        var routes = Routes(map, here);
        var exits = here.Links.Where(map.ContainsKey).Select(id => map[id])
            .OrderBy(n => Sector(n.X - here.X, n.Y - here.Y))
            .Select(n => new Target(n, new List<MapNode> { n })).ToList();

        var far = map.Values
            .Where(n => n.Type != "EMPTY" && n.Id != here.Id && !here.Links.Contains(n.Id) && routes.ContainsKey(n.Id))
            .Select(n => new Target(n, routes[n.Id]))
            .OrderBy(t => t.Route.Count).ThenBy(t => t.Node.X).ToList();
        return exits.Concat(far).ToList();
    }

    /// <summary>Cheapest route to every node. Fights cost extra, so a way round is preferred.</summary>
    private static Dictionary<string, List<MapNode>> Routes(Dictionary<string, MapNode> map, MapNode start)
    {
        var cost = new Dictionary<string, int> { [start.Id] = 0 };
        var previous = new Dictionary<string, string>();
        var queue = new PriorityQueue<string, int>();
        queue.Enqueue(start.Id, 0);
        while (queue.TryDequeue(out var id, out var c))
        {
            if (c > cost[id]) continue;
            foreach (var link in map[id].Links.Where(map.ContainsKey))
            {
                var next = c + 1 + (map[link].Type == "ENEMY" ? EnemyPenalty : 0);
                if (cost.TryGetValue(link, out var known) && known <= next) continue;
                cost[link] = next;
                previous[link] = id;
                queue.Enqueue(link, next);
            }
        }

        var routes = new Dictionary<string, List<MapNode>>();
        foreach (var id in cost.Keys.Where(k => k != start.Id))
        {
            var path = new List<MapNode>();
            for (var at = id; at != start.Id; at = previous[at]) path.Add(map[at]);
            path.Reverse();
            routes[id] = path;
        }
        return routes;
    }

    // ------------------------------------------------------------------ wording

    public override string DescribeContext(ScreenContext ctx)
    {
        var map = ReadFloor(ctx, out var here);
        if (here == null) return base.DescribeContext(ctx);
        var parts = new List<string> { PlayerStatus(ctx), DescribeHere(ctx, here), DescribeExits(ctx, map, here) };
        var far = Targets(map, here).Where(t => t.Route.Count > 1).Select(t => DescribeTarget(ctx, here, t)).ToList();
        if (far.Count > 0) parts.Add(Strings.T("map.elsewhere", string.Join("; ", far)));
        return string.Join(" ", parts);
    }

    public override string DescribeFocus(ScreenContext ctx)
    {
        if (ctx.State.Focus != null) return base.DescribeFocus(ctx);
        var map = ReadFloor(ctx, out var here);
        return here == null ? Strings.T("nothing.focus") : DescribeCursor(ctx, map, here, CursorNode(map, here));
    }

    /// <summary>The space under the cursor and how it relates to the player: theirs, one step away, or how far.</summary>
    private static string DescribeCursor(ScreenContext ctx, Dictionary<string, MapNode> map, MapNode here, MapNode node)
    {
        var what = DescribeNode(ctx, node, true);
        if (node.Id == here.Id) return Strings.T("map.cursor_here", what);
        if (here.Links.Contains(node.Id)) return Strings.T("map.cursor_adjacent", what, Strings.T("dir." + Sector(node.X - here.X, node.Y - here.Y)));
        var route = Routes(map, here).GetValueOrDefault(node.Id);
        return route == null ? Strings.T("map.cursor_unreachable", what) : Strings.T("map.cursor_far", what, route.Count);
    }

    /// <summary>The item list for the space under the cursor: what it is, what it joins, how to get there.</summary>
    private static List<string> CursorLines(ScreenContext ctx, Dictionary<string, MapNode> map, MapNode here, MapNode node)
    {
        var lines = new List<string> { DescribeCursor(ctx, map, here, node) };
        var joined = node.Links.Where(map.ContainsKey).Select(id => map[id]).OrderBy(n => Sector(n.X - node.X, n.Y - node.Y))
                         .Select(n => Strings.T("map.exit", Strings.T("dir." + Sector(n.X - node.X, n.Y - node.Y)), DescribeNode(ctx, n, true))).ToList();
        if (joined.Count > 0) lines.Add(Strings.T("map.joins", string.Join("; ", joined)));
        if (node.Id != here.Id) lines.Add(DescribePath(ctx, map, here, node));
        return lines;
    }

    /// <summary>P: the walk from the player to the cursor, step by step, with runs of one direction folded.</summary>
    private static string DescribePath(ScreenContext ctx, Dictionary<string, MapNode> map, MapNode here, MapNode node)
    {
        if (node.Id == here.Id) return Strings.T("map.path_here");
        var route = Routes(map, here).GetValueOrDefault(node.Id);
        if (route == null) return Strings.T("map.cursor_unreachable", DescribeNode(ctx, node, true));

        var steps = new List<(int Sector, int Count)>();
        var at = here;
        foreach (var next in route)
        {
            var sector = Sector(next.X - at.X, next.Y - at.Y);
            if (steps.Count > 0 && steps[^1].Sector == sector) steps[^1] = (sector, steps[^1].Count + 1);
            else steps.Add((sector, 1));
            at = next;
        }
        var words = steps.Select(s => s.Count == 1 ? Strings.T("dir." + s.Sector) : Strings.T("map.path_times", Strings.T("dir." + s.Sector), s.Count));
        var text = Strings.T("map.path", route.Count, string.Join(", ", words));
        var fights = route.Take(route.Count - 1).Where(n => n.Type == "ENEMY").Select(n => ctx.Translate(n.Enemy)).ToList();
        return fights.Count == 0 ? text : text + ". " + Strings.T("map.path_fights", string.Join(", ", fights));
    }

    /// <summary>The player list on the map: the status strip a sighted player sees along the bottom, line by line.</summary>
    private static List<string> PlayerLines(ScreenContext ctx)
    {
        var v = ctx.Bridge.Batch("{Game}.player.hp", "{Game}.player.maxhp", "{Game}.player.gold", "{Game}.player.level",
                                 "{Game}.player.dice", "{Game}.currentfloor", "{Game}.player.name");
        if (!v[0].Exists) return new List<string>();
        var lines = new List<string>
        {
            ctx.Translate(v[6].AsString()), Strings.T("battle.health", v[0].AsInt(), v[1].AsInt()), Strings.T("icon.gold", v[2].AsInt()),
            Strings.T("map.level", v[3].AsInt()), Strings.T("dice.count", v[4].AsInt()), Strings.T("map.floor", v[5].AsInt() + 1),
        };
        var cards = (ctx.Bridge.Pluck("{Game}.player.equipment", "name", "namemodifier", "skillcard") ?? new())
            .Where(r => r[0].Exists && r[2].AsString().Length == 0).Select(r => CardText.DecorateName(ctx, r[0].AsString(), r[1].AsString())).ToList();
        if (cards.Count > 0) lines.Add(Strings.T("cards.yours", string.Join(", ", cards)));
        return lines;
    }

    private static string DescribeHere(ScreenContext ctx, MapNode node) => Strings.T("map.here", DescribeNode(ctx, node, false));

    private static string DescribeExits(ScreenContext ctx, Dictionary<string, MapNode> map, MapNode here)
    {
        var exits = Targets(map, here).Where(t => t.Route.Count == 1).Select(t => DescribeTarget(ctx, here, t)).ToList();
        return exits.Count == 0 ? Strings.T("map.no_exits") : Strings.T("map.exits", string.Join("; ", exits));
    }

    private static string DescribeTarget(ScreenContext ctx, MapNode here, Target t)
    {
        var first = t.Route[0];
        var direction = Strings.T("dir." + Sector(first.X - here.X, first.Y - here.Y));
        if (t.Route.Count == 1) return Strings.T("map.exit", direction, DescribeNode(ctx, t.Node, true));

        var text = Strings.T("map.far", DescribeNode(ctx, t.Node, true), t.Route.Count, direction);
        var fights = t.Route.Take(t.Route.Count - 1).Where(n => n.Type == "ENEMY").Select(n => ctx.Translate(n.Enemy)).ToList();
        return fights.Count == 0 ? text : text + ", " + Strings.T("map.through", string.Join(", ", fights));
    }

    /// <summary>
    /// Direction words: 0 = right, clockwise, y growing downward. By the signs of the offset,
    /// not its angle: a path to (+480, -240) is "up right" because it is reached with Up and
    /// Right, although it lies much nearer the horizontal.
    /// </summary>
    private static int Sector(double dx, double dy)
    {
        int sx = Math.Abs(dx) < 1 ? 0 : Math.Sign(dx), sy = Math.Abs(dy) < 1 ? 0 : Math.Sign(dy);
        return (sx, sy) switch
        {
            (1, 0) => 0, (1, 1) => 1, (0, 1) => 2, (-1, 1) => 3, (-1, 0) => 4, (-1, -1) => 5, (0, -1) => 6, (1, -1) => 7, _ => 0,
        };
    }

    private static string DescribeNode(ScreenContext ctx, MapNode node, bool withVisited)
    {
        // The finale: a contestant under Lady Luck's spell. Stepping on the space is a fight; winning frees them.
        if (node.Type == "TEAMMATE" && node.Enemy.Length > 0)
            return Strings.T("map.teammate", ctx.Translate(node.Enemy)) + (withVisited && node.Visited ? ", " + Strings.T("map.visited") : "");

        var key = "map.node." + node.Type;
        var what = Strings.Has(key) ? Strings.T(key) : node.Type.ToLowerInvariant();

        // The enemy is drawn on the map, so naming it is fair. A chest's contents are not
        // shown until it is opened, so GraphNode.item is deliberately never read.
        if (node.Type == "ENEMY" && node.Enemy.Length > 0) what += " " + ctx.Translate(node.Enemy);
        if (withVisited && node.Visited && node.Type == "EMPTY") what += ", " + Strings.T("map.visited");
        return what;
    }

    public override IEnumerable<string> Help => new[] { Strings.T("help.map"), Strings.T("help.map.pack"), Strings.T("help.map.rules"), Strings.T("help.map.training") }.Concat(base.Help);
}
