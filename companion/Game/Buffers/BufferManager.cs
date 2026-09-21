namespace DiceyA11y.Game.Buffers;

/// <summary>
/// Owns the ordered list of review buffers and the "current buffer" cursor.
///
/// Announcements stay short; the detail a sighted player gets by hovering lives
/// here and is read on demand. Ctrl+Up/Down move inside the current buffer,
/// Ctrl+Left/Right switch buffers, skipping empty ones.
/// </summary>
public sealed class BufferManager
{
    public const string Events = "events";
    public const string YourCards = "your cards";
    public const string EnemyCards = "enemy cards";
    public const string Dice = "dice";
    public const string Statuses = "status effects";
    public const string Tooltips = "tooltips";
    public const string Help = "help";

    private readonly List<AnnouncementBuffer> _ordered = new();
    private readonly Dictionary<string, AnnouncementBuffer> _byName = new(StringComparer.OrdinalIgnoreCase);
    private int _current;

    public BufferManager(int eventCapacity)
    {
        Register(new AnnouncementBuffer(Events, eventCapacity) { FollowLatest = true });
        Register(new AnnouncementBuffer(YourCards, 64));
        Register(new AnnouncementBuffer(EnemyCards, 64));
        Register(new AnnouncementBuffer(Dice, 32));
        Register(new AnnouncementBuffer(Statuses, 64));
        Register(new AnnouncementBuffer(Tooltips, 64));
        Register(new AnnouncementBuffer(Help, 64));
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
        var index = _ordered.FindIndex(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return $"No buffer named {name}";
        _current = index;
        return _ordered[_current].Focus();
    }

    public string NextBuffer() => Step(+1);
    public string PreviousBuffer() => Step(-1);

    /// <summary>
    /// Walks to the next non-empty buffer. If every other buffer is empty it stays
    /// put and says so — a silent key press is indistinguishable from a broken mod.
    /// </summary>
    private string Step(int direction)
    {
        for (var i = 1; i <= _ordered.Count; i++)
        {
            var index = ((_current + direction * i) % _ordered.Count + _ordered.Count) % _ordered.Count;
            if (index == _current) break;
            if (_ordered[index].IsEmpty) continue;
            _current = index;
            return _ordered[_current].Focus();
        }
        return $"No other buffer has anything. Still on {Current.Name}";
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
