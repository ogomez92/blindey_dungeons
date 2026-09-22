using System.Collections.Concurrent;
using DiceyA11y.Core;

namespace DiceyA11y.Speech;

/// <summary>
/// The only class in the mod that speaks: through Prism on Windows, AVSpeech on macOS
/// (<see cref="ISpeechEngine"/>).
///
/// A Prism backend instance is not thread safe, so every call is funnelled onto a
/// single dedicated speech thread. Callers (the stdout pump, the hotkey hook, the
/// narrator) only ever enqueue.
/// </summary>
public sealed class ScreenReaderOutput : IDisposable
{
    private sealed record Utterance(string Text, bool Interrupt, bool IsEvent);

    // Two queues, not one. An answer the player explicitly asked for must not wait
    // behind a burst of combat narration, but that narration must not be thrown
    // away either - so on-demand speech jumps the queue instead of clearing it.
    private readonly ConcurrentQueue<Utterance> _priority = new();
    private readonly ConcurrentQueue<Utterance> _events = new();
    private readonly SemaphoreSlim _pending = new(0);
    private readonly Config _config;
    private readonly Action<string> _log;
    private Thread? _thread;

    private ISpeechEngine? _engine;
    private DateTime _lastRecovery = DateTime.MinValue;
    private volatile bool _running;

    public string BackendName => _engine?.Name ?? "(none)";
    public bool Available => _engine != null;

    /// <summary>Raised for unsolicited announcements so buffers/logs can record them.</summary>
    public event Action<string>? EventSpoken;

    public ScreenReaderOutput(Config config, Action<string> log)
    {
        _config = config;
        _log = log;
    }

    public bool Initialize(string nativeDirectory)
    {
        _engine = OperatingSystem.IsMacOS()
            ? AvSpeechEngine.Create(_config.SpeechRate, _config.MacVoice, _log)
            : PrismEngine.Create(nativeDirectory, _config.Braille, _log);
        if (_engine == null) return false;

        _running = true;
        _thread = new Thread(Pump) { IsBackground = true, Name = "DiceyA11y speech" };
        _thread.Start();
        return true;
    }

    /// <summary>True when the mod sets the rate (AVSpeech); a screen reader keeps its own.</summary>
    public bool OwnsRate => _engine?.Rate != null;

    /// <summary>Moves the rate by <paramref name="delta"/> percent and returns the new value, or null.</summary>
    public int? ChangeRate(int delta)
    {
        if (_engine?.Rate is not { } rate) return null;
        _engine.Rate = rate + delta;
        return _engine.Rate;
    }

    /// <summary>On-demand readouts and focus echoes. Not recorded as an event.</summary>
    public void Say(string text, bool interrupt = false) => Enqueue(text, interrupt, inEventQueue: false, record: false);

    /// <summary>
    /// A focus echo that must come after the events already queued: the screen's own text
    /// was just announced and the button belongs after it ("You get an extra dice. Take"),
    /// not jumping ahead of it through the priority queue. Not recorded as an event.
    /// </summary>
    public void SayAfterEvents(string text) => Enqueue(text, interrupt: false, inEventQueue: true, record: false);

    /// <summary>
    /// Unsolicited announcements (combat, screen changes, story). Recorded in the
    /// Events buffer and the event log so bug reports have something to point at.
    /// </summary>
    public void SayEvent(string text) => Enqueue(text, interrupt: false, inEventQueue: true, record: true);

    private void Enqueue(string text, bool interrupt, bool inEventQueue, bool record)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var cleaned = TextCleanup.Clean(text);
        if (string.IsNullOrWhiteSpace(cleaned)) return;

        if (record) EventSpoken?.Invoke(cleaned);
        if (!_running) return;

        // Never interrupt by default: combat events arrive in bursts and interrupting
        // throws away information the player has no other way to get.
        var utterance = new Utterance(cleaned, interrupt && _config.AllowInterrupt, record);
        if (inEventQueue) _events.Enqueue(utterance); else _priority.Enqueue(utterance);
        _pending.Release();
    }

    /// <summary>Silences the current utterance and drops anything already queued.</summary>
    public void StopSpeech()
    {
        while (_priority.TryDequeue(out _)) { }
        while (_events.TryDequeue(out _)) { }
        try { _engine?.Stop(); } catch { /* best effort */ }
    }

    private void Pump()
    {
        while (_running)
        {
            try
            {
                if (!_pending.Wait(250)) continue;
            }
            catch (ObjectDisposedException) { return; }

            if (!_running) return;
            if (_priority.TryDequeue(out var utterance) || _events.TryDequeue(out utterance))
                Speak(utterance);
        }
    }

    private void Speak(Utterance utterance)
    {
        if (_engine == null) return;

        bool ok;
        string error;
        try
        {
            ok = _engine.Speak(utterance.Text, utterance.Interrupt, out error);
        }
        catch (Exception ex)
        {
            _log($"Speech call threw: {ex.Message}");
            return;
        }

        if (ok)
        {
            if (_config.LogSpokenLines) _log($"Spoke: {utterance.Text}");
            return;
        }

        _log($"Speech failed ({error}) for: {utterance.Text}");
        TryRecover();
    }

    /// <summary>
    /// Rate-limited engine re-creation. This is what lets the mod come back to life
    /// when the player starts or restarts their screen reader after the game.
    /// </summary>
    private void TryRecover()
    {
        if ((DateTime.UtcNow - _lastRecovery).TotalSeconds < 5) return;
        _lastRecovery = DateTime.UtcNow;

        try
        {
            if (_engine?.Recover() == true) _log("Re-acquired a speech backend.");
        }
        catch (Exception ex)
        {
            _log($"Speech recovery failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _pending.Release(); } catch { }
        _thread?.Join(1500);
        _pending.Dispose();
        _engine?.Dispose();
        _engine = null;
    }
}
