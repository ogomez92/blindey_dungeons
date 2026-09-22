using System.Collections.Concurrent;
using DiceyA11y.Core;

namespace DiceyA11y.Speech;

/// <summary>
/// The only class in the mod that touches <see cref="PrismNative"/>.
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

    private IntPtr _ctx = IntPtr.Zero;
    private IntPtr _backend = IntPtr.Zero;
    private ulong _features;
    private DateTime _lastRecovery = DateTime.MinValue;
    private volatile bool _running;

    public string BackendName { get; private set; } = "(none)";
    public bool Available => _backend != IntPtr.Zero;

    /// <summary>Raised for unsolicited announcements so buffers/logs can record them.</summary>
    public event Action<string>? EventSpoken;

    public ScreenReaderOutput(Config config, Action<string> log)
    {
        _config = config;
        _log = log;
    }

    public bool Initialize(string nativeDirectory)
    {
        if (!PrismNative.Load(nativeDirectory, out var detail))
        {
            _log($"Speech unavailable: {detail}");
            return false;
        }
        _log($"Loaded native library: {detail}");

        try
        {
            // prism_init(NULL) is valid and uses the global backend registry.
            _ctx = PrismNative.prism_init(IntPtr.Zero);
            if (_ctx == IntPtr.Zero)
            {
                _log("Speech unavailable: prism_init returned NULL");
                return false;
            }

            if (!CreateBackend())
            {
                _log("Speech unavailable: no screen reader or TTS backend could be created. "
                     + "Start NVDA/JAWS (or make sure a SAPI voice is installed) and restart.");
                return false;
            }
        }
        catch (DllNotFoundException ex)
        {
            _log($"Speech unavailable: prism.dll could not be loaded ({ex.Message})");
            return false;
        }
        catch (BadImageFormatException)
        {
            _log("Speech unavailable: prism.dll is the wrong architecture. A 64-bit build is required.");
            return false;
        }

        _running = true;
        _thread = new Thread(Pump) { IsBackground = true, Name = "DiceyA11y speech" };
        _thread.Start();
        return true;
    }

    private bool CreateBackend()
    {
        // create_best returns the highest-priority backend that initializes,
        // ALREADY initialized - do not call prism_backend_initialize on it.
        _backend = PrismNative.prism_registry_create_best(_ctx);
        if (_backend == IntPtr.Zero) return false;

        BackendName = PrismNative.PtrToString(PrismNative.prism_backend_name(_backend));
        _features = PrismNative.prism_backend_get_features(_backend);
        _log($"Speech backend: {BackendName} (features 0x{_features:X})");
        return true;
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
        if (_backend != IntPtr.Zero && (_features & PrismNative.FeatureStop) != 0)
        {
            try { PrismNative.prism_backend_stop(_backend); } catch { /* best effort */ }
        }
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
        if (_backend == IntPtr.Zero) return;

        var payload = PrismNative.Utf8Z(utterance.Text);
        int result;
        try
        {
            result = (_features & PrismNative.FeatureOutput) != 0
                ? PrismNative.prism_backend_output(_backend, payload, utterance.Interrupt)
                : PrismNative.prism_backend_speak(_backend, payload, utterance.Interrupt);

            if (result == PrismNative.PrismOk
                && _config.Braille
                && (_features & PrismNative.FeatureOutput) == 0
                && (_features & PrismNative.FeatureBraille) != 0)
            {
                PrismNative.prism_backend_braille(_backend, payload);
            }
        }
        catch (Exception ex)
        {
            _log($"Speech call threw: {ex.Message}");
            return;
        }

        if (result == PrismNative.PrismOk)
        {
            if (_config.LogSpokenLines) _log($"Spoke: {utterance.Text}");
            return;
        }

        _log($"Speech failed ({PrismNative.ErrorText(result)}) for: {utterance.Text}");
        TryRecover();
    }

    /// <summary>
    /// Rate-limited backend re-creation. This is what lets the mod come back to life
    /// when the player starts or restarts their screen reader after the game.
    /// </summary>
    private void TryRecover()
    {
        if ((DateTime.UtcNow - _lastRecovery).TotalSeconds < 5) return;
        _lastRecovery = DateTime.UtcNow;

        try
        {
            if (_backend != IntPtr.Zero)
            {
                PrismNative.prism_backend_free(_backend);
                _backend = IntPtr.Zero;
            }
            if (CreateBackend()) _log("Re-acquired a speech backend.");
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

        if (_backend != IntPtr.Zero)
        {
            try
            {
                if ((_features & PrismNative.FeatureStop) != 0) PrismNative.prism_backend_stop(_backend);
                PrismNative.prism_backend_free(_backend);
            }
            catch { }
            _backend = IntPtr.Zero;
        }
        if (_ctx != IntPtr.Zero)
        {
            try { PrismNative.prism_shutdown(_ctx); } catch { }
            _ctx = IntPtr.Zero;
        }
    }
}
