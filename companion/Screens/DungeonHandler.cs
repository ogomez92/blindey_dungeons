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
/// So on this screen the mod owns movement. Left/Right pick a destination - the exits from
/// here first, then everything of interest on the floor with how far away it is - and Up
/// takes one step toward it by pushing the virtual stick at the next node on the route.
/// What the player heard is therefore always where the step goes.
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
    private string _targetId = "";
    private bool _travelling;
    private DateTime _stepAt = DateTime.MinValue;   // when the last step was asked for
    private string _stepFrom = "";

    public DungeonHandler(VirtualPad pad) => _pad = pad;

    public override bool Handles(string scene) => scene == "states.Dungeon";

    public override void OnEnter(ScreenContext ctx)
    {
        _lastPosition = "";
        _targetId = "";
        _travelling = false;
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

        // A step that went nowhere must not be answered with silence. The game refuses to
        // walk past an enemy the player is standing on (observed), and says so only visually.
        if (_stepFrom == here.Id && DateTime.UtcNow - _stepAt > TimeSpan.FromSeconds(1.2))
        {
            _stepFrom = "";
            _travelling = false;
            ctx.Say(here.Type == "ENEMY" ? Strings.T("map.blocked_enemy", ctx.Translate(here.Enemy)) : Strings.T("map.blocked"), interrupt: true);
        }

        if (here.Id == _lastPosition) return;
        _stepFrom = "";
        var first = _lastPosition.Length == 0;
        _lastPosition = here.Id;

        string text;
        var target = _travelling ? FindTarget(map, here, _targetId) : null;
        if (target != null && target.Route.Count > 0)
            text = Strings.T("map.step", DescribeNode(ctx, here, false), target.Route.Count, DescribeNode(ctx, target.Node, false));
        else
        {
            _travelling = false;
            _targetId = "";
            text = DescribeHere(ctx, here) + " " + DescribeExits(ctx, map, here);
        }

        if (first) ctx.SayEvent(text); else ctx.Say(text, interrupt: RecentInput(ctx));
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
        switch (key)
        {
            case NavKey.Left: Cycle(ctx, -1); return true;
            case NavKey.Right: Cycle(ctx, +1); return true;
            case NavKey.Up: Step(ctx); return true;
            case NavKey.Down: ctx.Say(DescribeContext(ctx), interrupt: true); return true;
            default: return false;
        }
    }

    private void Cycle(ScreenContext ctx, int direction)
    {
        var map = ReadFloor(ctx, out var here);
        if (here == null) return;
        var targets = Targets(map, here);
        if (targets.Count == 0) { ctx.Say(Strings.T("map.no_exits"), interrupt: true); return; }

        var index = targets.FindIndex(t => t.Node.Id == _targetId);
        index = index < 0 ? (direction > 0 ? 0 : targets.Count - 1) : (index + direction + targets.Count) % targets.Count;
        _targetId = targets[index].Node.Id;
        _travelling = false;
        ctx.Say(DescribeTarget(ctx, here, targets[index]) + ", " + Strings.T("of", index + 1, targets.Count), interrupt: true);
    }

    private void Step(ScreenContext ctx)
    {
        var map = ReadFloor(ctx, out var here);
        if (here == null) return;
        var target = FindTarget(map, here, _targetId);
        if (target == null || target.Route.Count == 0) { ctx.Say(Strings.T("map.choose"), interrupt: true); return; }

        var next = target.Route[0];
        _travelling = target.Route.Count > 1;
        double dx = next.X - here.X, dy = next.Y - here.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return;
        _stepFrom = here.Id;
        _stepAt = DateTime.UtcNow;
        _pad.PushStick(dx / length, dy / length, 100);
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

    private static Target? FindTarget(Dictionary<string, MapNode> map, MapNode here, string id)
        => id.Length == 0 ? null : Targets(map, here).FirstOrDefault(t => t.Node.Id == id);

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
        ReadFloor(ctx, out var here);
        return here == null ? Strings.T("nothing.focus") : DescribeHere(ctx, here);
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

    /// <summary>Eight compass sectors, 0 = right, clockwise, in screen coordinates (y grows downward).</summary>
    private static int Sector(double dx, double dy)
    {
        var degrees = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        return (int)Math.Round(degrees / 45.0, MidpointRounding.AwayFromZero) & 7;
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
