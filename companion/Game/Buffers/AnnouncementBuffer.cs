using DiceyA11y.Core;

namespace DiceyA11y.Game.Buffers;

/// <summary>
/// A named list of lines the player can step through on demand.
///
/// Items are held in **review order**: index 0 is the top — the newest event, or
/// the first line of a detail list. The cursor starts "before the top" so the
/// first move reads index 0.
///
/// Review features never change game state. A buffer only ever reads a copy of
/// what the mod already received.
/// </summary>
public sealed class AnnouncementBuffer
{
    private readonly List<string> _lines = new();
    private readonly int _capacity;
    private int _cursor = -1;

    public string Name { get; }

    /// <summary>What the player hears; <see cref="Name"/> stays the internal id.</summary>
    public string Title => Strings.Has("buffer." + Name) ? Strings.T("buffer." + Name) : Name;

    /// <summary>Left out of Ctrl+Left/Right until something shows it (help: only after F1).</summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// Rebuilds the contents from live state when the buffer is read. Returning
    /// null marks the buffer unavailable (for example board state outside battle).
    /// </summary>
    public Func<List<string>?>? Refresher { get; set; }

    /// <summary>Events-style buffers jump back to the newest item when focused.</summary>
    public bool FollowLatest { get; set; }

    public AnnouncementBuffer(string name, int capacity = 200)
    {
        Name = name;
        _capacity = Math.Max(1, capacity);
    }

    public int Count => _lines.Count;
    public bool IsEmpty => _lines.Count == 0;

    /// <summary>Adds a new top item, keeping a mid-review cursor on the same entry.</summary>
    public void Add(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        _lines.Insert(0, line);
        if (_cursor >= 0) _cursor++;            // stay on whatever was being read
        while (_lines.Count > _capacity) _lines.RemoveAt(_lines.Count - 1);
        if (_cursor >= _lines.Count) _cursor = _lines.Count - 1;
    }

    /// <summary>
    /// Replaces the contents wholesale (detail buffers rebuilt from state).
    /// The cursor is reset only when the content actually changed, so re-reading
    /// the same thing does not lose the player's position.
    /// </summary>
    public void Set(IEnumerable<string> lines)
    {
        var next = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (next.Count > _capacity) next = next.Take(_capacity).ToList();

        if (next.Count == _lines.Count && !next.Where((t, i) => t != _lines[i]).Any())
            return;                              // unchanged: keep the cursor

        _lines.Clear();
        _lines.AddRange(next);
        _cursor = -1;
    }

    public void Clear()
    {
        _lines.Clear();
        _cursor = -1;
    }

    /// <summary>Called when this buffer becomes the current one.</summary>
    public string Focus()
    {
        Refresh();
        if (FollowLatest) _cursor = -1;
        return _lines.Count == 0 ? Strings.T("buffer.empty", Title) : Strings.T("buffer.count", Title, _lines.Count);
    }

    /// <summary>Older events / further detail lines.</summary>
    public string MoveDeeper()
    {
        Refresh();
        if (_lines.Count == 0) return Strings.T("buffer.empty", Title);

        if (_cursor >= _lines.Count - 1)
        {
            _cursor = _lines.Count - 1;
            return Position();
        }
        _cursor++;
        return Position();
    }

    /// <summary>Back towards the newest event / the first detail line.</summary>
    public string MoveTowardTop()
    {
        Refresh();
        if (_lines.Count == 0) return Strings.T("buffer.empty", Title);

        if (_cursor <= 0)
        {
            _cursor = 0;
            return Position();
        }
        _cursor--;
        return Position();
    }

    public string Current()
    {
        Refresh();
        if (_lines.Count == 0) return Strings.T("buffer.empty", Title);
        if (_cursor < 0) _cursor = 0;
        return Position();
    }

    /// <summary>Reads everything at once, for players who prefer a single block.</summary>
    public string ReadAll()
    {
        Refresh();
        return _lines.Count == 0 ? Strings.T("buffer.empty", Title) : string.Join(". ", _lines.Select(l => l.TrimEnd('.', ' ')));
    }

    private void Refresh()
    {
        if (Refresher is null) return;
        var rebuilt = Refresher();
        if (rebuilt is not null) Set(rebuilt);
    }

    private string Position() => _lines[_cursor].TrimEnd('.') + ". " + Strings.T("of", _cursor + 1, _lines.Count);
}
