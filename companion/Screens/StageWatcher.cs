using DiceyA11y.Bridge;

namespace DiceyA11y.Screens;

/// <summary>A block of on-screen text: one or more stage text fields that read as a unit.</summary>
public sealed record Paragraph(int X, int Y, string Text)
{
    public string Key => StageCleaner.Key(Text);

    /// <summary>Size of the paragraph's first line, when the game reported it; for aiming a click.</summary>
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>
/// Turns the raw stage text into paragraphs and works out what is new.
///
/// The game draws text a line at a time and animates panels in, so the stage is read
/// every poll and only acted on once it has stopped changing for a moment. "New" is
/// judged by content, not position, because panels slide while they appear.
/// </summary>
public sealed class StageWatcher
{
    /// <summary>The game's canvas is 3840x2160 whatever the window size (Screen.canvaswidth/height).</summary>
    private const int CanvasWidth = 3840, CanvasHeight = 2160;

    private readonly TimeSpan _settle;
    private List<Paragraph> _current = new();
    private string _currentSignature = "";
    private DateTime _changedAt = DateTime.UtcNow;
    private Dictionary<string, int> _baseline = new();
    private bool _dirty;

    public StageWatcher(TimeSpan settle) => _settle = settle;

    public IReadOnlyList<Paragraph> Current => _current;
    public bool Settled => DateTime.UtcNow - _changedAt >= _settle;

    /// <summary>Text has changed and has not been handed out by <see cref="TakeNew"/> yet.</summary>
    public bool Pending => _dirty;

    /// <param name="standalone">
    /// Keys of text that is a control's label. Menu buttons are stacked as tightly as the
    /// lines of a paragraph, so geometry alone would glue a whole menu into one sentence.
    /// </param>
    public void Update(IReadOnlyList<StageText> stage, IReadOnlySet<string> standalone)
    {
        var paragraphs = BuildParagraphs(stage, standalone);
        var signature = string.Join("\n", paragraphs.Select(p => p.Key));
        if (signature == _currentSignature)
        {
            _current = paragraphs;   // positions may still drift; content is what matters
            return;
        }
        _current = paragraphs;
        _currentSignature = signature;
        _changedAt = DateTime.UtcNow;
        _dirty = true;
    }

    /// <summary>Treat everything now on screen as not yet seen.</summary>
    public void Reset()
    {
        _baseline = new Dictionary<string, int>();
        _dirty = true;
        _changedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Once the stage has settled after a change: the paragraphs that were not there at the
    /// previous settle, in reading order. Null while nothing new is ready.
    /// </summary>
    public List<Paragraph>? TakeNew()
    {
        if (!_dirty || !Settled) return null;
        _dirty = false;

        var remaining = new Dictionary<string, int>(_baseline);
        var fresh = new List<Paragraph>();
        foreach (var p in _current)
        {
            if (remaining.TryGetValue(p.Key, out var n) && n > 0) { remaining[p.Key] = n - 1; continue; }

            // A paragraph that grew by a line since it was last read: only the rest is new.
            // (Not from a single word: "Warrior fled..." is not the HUD's "Warrior" having grown.)
            var grownFrom = _baseline.Keys.Where(k => k.Contains(' ') && p.Key.Length > k.Length + 1 && (p.Key.StartsWith(k + " ", StringComparison.Ordinal)
                                                                                         || p.Key.StartsWith(k + ". ", StringComparison.Ordinal)))
                                          .OrderByDescending(k => k.Length).FirstOrDefault();
            fresh.Add(grownFrom == null || grownFrom.Length + 1 >= p.Text.Length
                ? p
                : p with { Text = p.Text[(grownFrom.Length + 1)..].TrimStart(' ', '.') });
        }

        _baseline = new Dictionary<string, int>();
        foreach (var p in _current) _baseline[p.Key] = _baseline.GetValueOrDefault(p.Key) + 1;
        return fresh;
    }

    private static List<Paragraph> BuildParagraphs(IReadOnlyList<StageText> stage, IReadOnlySet<string> standalone)
    {
        // Lines of one paragraph are separate text fields stacked closely, either sharing a
        // left edge or centred on the same axis. Input is already in reading order.
        var lines = new List<(StageText Source, string Text)>();
        foreach (var t in stage)
        {
            if (t.Y < -20 || t.Y > CanvasHeight || t.X < -200 || t.X > CanvasWidth) continue;   // sliding in or out
            var text = StageCleaner.Clean(t.Raw);
            if (text.Length > 0) lines.Add((t, text));
        }

        var used = new bool[lines.Count];
        var result = new List<Paragraph>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var first = lines[i].Source;
            var last = first;
            var text = lines[i].Text;

            var alone = standalone.Contains(StageCleaner.Key(text));
            for (var j = i + 1; j < lines.Count && !alone; j++)
            {
                if (used[j] || standalone.Contains(StageCleaner.Key(lines[j].Text))) continue;
                var next = lines[j].Source;
                // Text revealed gradually reports empty bounds; assume an ordinary line then.
                // Lines of one paragraph share a font size. A card's title (60 high) and the big
                // countdown number under it (129 high) are different things, however close.
                var lineHeight = last.Height >= 40 && next.Height >= 40 ? Math.Min(last.Height, next.Height) : Math.Max(last.Height, next.Height);
                if (lineHeight < 40) lineHeight = 84;
                if (last.Height >= 40 && next.Height >= 40 && Math.Max(last.Height, next.Height) > 1.6 * lineHeight) continue;
                var gap = next.Y - last.Y;
                if (gap <= 12 || gap > lineHeight * 1.45) continue;
                var aligned = Math.Abs(next.X - last.X) <= 12
                              || (last.Width > 0 && next.Width > 0 && Math.Abs(next.CenterX - last.CenterX) <= 24);
                if (!aligned) continue;
                used[j] = true;
                // A card lists its effects one per line with no full stop; a wrapped sentence
                // continues in lower case. Without the pause two effects read as one.
                // (A wrapped sentence can also continue with a SHOUTED word: "It turns out you / CAN get...".)
                var next2 = lines[j].Text.Length > 1 ? lines[j].Text[1] : ' ';
                var newSentence = char.IsUpper(lines[j].Text[0]) && !char.IsUpper(next2) && char.IsLetterOrDigit(text[^1]) && text.Any(char.IsLower);
                text += (newSentence ? ". " : " ") + lines[j].Text;
                last = next;
            }

            result.Add(new Paragraph(first.X, first.Y, text) { Width = first.Width, Height = first.Height });
        }
        return result;
    }
}
