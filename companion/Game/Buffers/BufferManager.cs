using DiceyA11y.Core;

namespace DiceyA11y.Game.Buffers;

/// <summary>
/// Owns the ordered list of review buffers and the "current buffer" cursor.
///
/// Announcements stay short; the detail a sighted player gets by hovering lives
/// here and is read on demand. Ctrl+Up/Down move inside the current buffer,
/// Ctrl+Left/Right switch buffers, skipping empty ones.
///
/// The first buffer follows the game's focus: whenever a card, a dice, a button or a map
/// space gains focus it is filled with that item's details and becomes the current buffer,
/// so Ctrl+Up always starts from what the player is on. Tooltips (what hovering the mouse
/// would show) sit on top of it rather than in a list of their own.
/// </summary>
public sealed class BufferManager
{
    public const string Item = "item";
    public const string Events = "events";
    public const string Player = "player";
    public const string YourCards = "your cards";
    public const string EnemyCards = "enemy cards";
    public const string Dice = "dice";
    public const string Statuses = "status effects";
    public const string Help = "help";

    private readonly List<AnnouncementBuffer> _ordered = new();
    private readonly Dictionary<string, AnnouncementBuffer> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private List<string> _itemLines = new(), _tooltips = new();
    private int _current;

    public BufferManager(int eventCapacity)
    {
        Register(new AnnouncementBuffer(Item, 96));
        Register(new AnnouncementBuffer(Events, eventCapacity) { FollowLatest = true });
        Register(new AnnouncementBuffer(Player, 32));
        Register(new AnnouncementBuffer(YourCards, 64));
        Register(new AnnouncementBuffer(EnemyCards, 64));
        Register(new AnnouncementBuffer(Dice, 32));
        Register(new AnnouncementBuffer(Statuses, 64));
        // Help exists only between F1 and the moment the player moves away from it.
        Register(new AnnouncementBuffer(Help, 64) { Hidden = true });
    }

    private void Register(AnnouncementBuffer buffer)
    {
        _ordered.Add(buffer);
        _byName[buffer.Name] = buffer;
    }

    public AnnouncementBuffer Get(string name) =>
        _byName.TryGetValue(name, out var b) ? b : _ordered[0];

    public AnnouncementBuffer Current => _ordered[_current];

    public IEnumerable<AnnouncementBuffer> All => _ordered;

    /// <summary>Makes a buffer current and announces it. Used by the direct hotkeys.</summary>
    public string Select(string name)
    {
        lock (_lock)
        {
            var index = _ordered.FindIndex(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return $"No buffer named {name}";
            MoveTo(index);
            return _ordered[_current].Focus();
        }
    }

    /// <summary>F1: fills the help list, shows it and makes it current.</summary>
    public string ShowHelp(IEnumerable<string> entries)
    {
        lock (_lock)
        {
            var help = Get(Help);
            help.Set(entries);
            help.Hidden = false;
            MoveTo(_ordered.IndexOf(help));
            return help.Focus();
        }
    }

    /// <summary>
    /// Something gained focus in the game: its details become the item list, and that list
    /// becomes the current one, silently: the focus echo has already said what it is.
    /// </summary>
    public void FocusItem(IEnumerable<string> lines)
    {
        lock (_lock)
        {
            _itemLines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            RebuildItem();
            MoveTo(0);
        }
    }

    /// <summary>What hovering would show on this screen; kept above the focused item's own lines.</summary>
    public void SetTooltips(IEnumerable<string> lines)
    {
        lock (_lock)
        {
            var next = lines.ToList();
            if (next.SequenceEqual(_tooltips)) return;
            _tooltips = next;
            RebuildItem();
        }
    }

    private void RebuildItem() => Get(Item).Set(_tooltips.Concat(_itemLines));

    private void MoveTo(int index)
    {
        if (index != _current && Current.Name == Help) { Current.Hidden = true; Current.Clear(); }
        _current = index;
    }

    public string NextBuffer() => Step(+1);
    public string PreviousBuffer() => Step(-1);

    /// <summary>
    /// Walks to the next non-empty buffer. If every other buffer is empty it stays
    /// put and says so — a silent key press is indistinguishable from a broken mod.
    /// </summary>
    private string Step(int direction)
    {
        lock (_lock)
        {
            for (var i = 1; i <= _ordered.Count; i++)
            {
                var index = ((_current + direction * i) % _ordered.Count + _ordered.Count) % _ordered.Count;
                if (index == _current) break;
                if (_ordered[index].IsEmpty || _ordered[index].Hidden) continue;
                MoveTo(index);
                return _ordered[_current].Focus();
            }
            return Strings.T("buffer.only", Current.Title);
        }
    }

    /// <summary>
    /// Drops content that stops being true when a battle ends, so stale details are
    /// never read back as if they were current.
    ///
    /// Your own cards deliberately survive: this is a deckbuilder, your equipment
    /// carries between fights, and being able to review it on the map is useful.
    /// The events log survives too.
    /// </summary>
    public void ClearBattleBuffers()
    {
        Get(EnemyCards).Clear();
        Get(Dice).Clear();
        Get(Statuses).Clear();
    }
}
