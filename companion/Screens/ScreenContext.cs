using DiceyA11y.Bridge;
using DiceyA11y.Core;
using DiceyA11y.Game.Buffers;
using DiceyA11y.Speech;

namespace DiceyA11y.Screens;

/// <summary>One navigation button as the game registered it this frame.</summary>
public sealed record NavButton(string RawLabel, string Label, double X, double Y);

/// <summary>What one poll of the game found. Immutable; handlers compare consecutive ones.</summary>
public sealed class GameState
{
    public string Scene { get; init; } = "";
    public bool GamepadMode { get; init; }
    public NavButton? Focus { get; init; }
    public IReadOnlyList<NavButton> Buttons { get; init; } = Array.Empty<NavButton>();

    /// <summary>
    /// Identity of the focused control. The label alone when it is unique, because buttons
    /// slide into place and would otherwise count as a new focus on every frame of the
    /// animation; label and place when several share it, so two "Buy" buttons differ.
    /// </summary>
    public string FocusKey
    {
        get
        {
            if (Focus == null) return "";
            var twins = Buttons.Count(b => b.RawLabel == Focus.RawLabel);
            return twins > 1 ? $"{Focus.RawLabel}@{Focus.X:0},{Focus.Y:0}" : Focus.RawLabel;
        }
    }
}

/// <summary>Everything a screen handler needs, so handlers stay small and testable.</summary>
public sealed class ScreenContext
{
    private readonly Dictionary<string, string> _translations = new();

    public required BridgeClient Bridge { get; init; }
    public required ScreenReaderOutput Speech { get; init; }
    public required BufferManager Buffers { get; init; }
    public required Config Config { get; init; }
    public required Action<string> Log { get; init; }
    public required StageWatcher Stage { get; init; }

    public GameState State { get; set; } = new();
    public GameState Previous { get; set; } = new();

    /// <summary>When the player last pressed a key that reaches the game.</summary>
    public DateTime LastInput { get; set; } = DateTime.MinValue;

    /// <summary>Focus echoes and on-demand reads: not recorded as events.</summary>
    public void Say(string text, bool interrupt = false)
    {
        if (!string.IsNullOrWhiteSpace(text)) Speech.Say(text, interrupt);
    }

    /// <summary>
    /// Something gained focus: its details fill the item list, which becomes the current
    /// review list, so Ctrl+Up reads about what the player is on without hunting for it.
    /// </summary>
    public void FocusItem(IEnumerable<string> lines) => Buffers.FocusItem(lines);

    /// <summary>Things that happened on their own: recorded in the events list.</summary>
    public void SayEvent(string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) Speech.SayEvent(text);
    }

    /// <summary>
    /// The game's own translation of one of its internal names (button labels, enemy and
    /// equipment names are English keys internally). Cached: the language only changes on
    /// the language screens, where <see cref="ForgetTranslations"/> is called.
    /// </summary>
    public string Translate(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (_translations.TryGetValue(key, out var cached)) return cached;
        var v = Bridge.Eval("{MyLocale}.translate(" + GameValue.Quote(key) + ")");
        if (!v.Exists) return StageCleaner.Clean(key);   // not cached: the bridge may just be busy
        var text = StageCleaner.Clean(v.AsString());
        // Level packs tag their own variants of a card or enemy ("Bump@reunionspacemarine",
        // "Rocket Launch@6"); only the part before the @ has a translation.
        var at = key.IndexOf('@');
        if (at > 0 && v.AsString() == key)
        {
            var plain = Bridge.Eval("{MyLocale}.translate(" + GameValue.Quote(key[..at]) + ")");
            text = StageCleaner.Clean(plain.Exists ? plain.AsString() : key[..at]);
        }
        // The game sometimes translates the name and keeps the tag ("Juicio@green").
        if (text.IndexOf('@') is > 0 and var tag) text = text[..tag].Trim();
        if (text.Length == 0) text = StageCleaner.Clean(key);
        _translations[key] = text;
        return text;
    }

    public void ForgetTranslations() => _translations.Clear();
}
