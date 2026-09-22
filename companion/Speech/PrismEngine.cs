namespace DiceyA11y.Speech;

/// <summary>Speech through Prism: whichever screen reader (or SAPI) Prism finds. Windows.</summary>
internal sealed class PrismEngine : ISpeechEngine
{
    private readonly bool _braille;
    private readonly Action<string> _log;
    private IntPtr _ctx = IntPtr.Zero;
    private IntPtr _backend = IntPtr.Zero;
    private ulong _features;

    public string Name { get; private set; } = "(none)";

    // Rate belongs to the player's screen reader.
    public int? Rate { get => null; set { } }

    private PrismEngine(bool braille, Action<string> log)
    {
        _braille = braille;
        _log = log;
    }

    public static PrismEngine? Create(string nativeDirectory, bool braille, Action<string> log)
    {
        if (!PrismNative.Load(nativeDirectory, out var detail))
        {
            log($"Speech unavailable: {detail}");
            return null;
        }
        log($"Loaded native library: {detail}");

        var engine = new PrismEngine(braille, log);
        try
        {
            // prism_init(NULL) is valid and uses the global backend registry.
            engine._ctx = PrismNative.prism_init(IntPtr.Zero);
            if (engine._ctx == IntPtr.Zero)
            {
                log("Speech unavailable: prism_init returned NULL");
                return null;
            }

            if (!engine.CreateBackend())
            {
                log("Speech unavailable: no screen reader or TTS backend could be created. "
                    + "Start NVDA/JAWS (or make sure a SAPI voice is installed) and restart.");
                return null;
            }
        }
        catch (DllNotFoundException ex)
        {
            log($"Speech unavailable: prism.dll could not be loaded ({ex.Message})");
            return null;
        }
        catch (BadImageFormatException)
        {
            log("Speech unavailable: prism.dll is the wrong architecture. A 64-bit build is required.");
            return null;
        }
        return engine;
    }

    private bool CreateBackend()
    {
        // create_best returns the highest-priority backend that initializes,
        // ALREADY initialized - do not call prism_backend_initialize on it.
        _backend = PrismNative.prism_registry_create_best(_ctx);
        if (_backend == IntPtr.Zero) return false;

        Name = PrismNative.PtrToString(PrismNative.prism_backend_name(_backend));
        _features = PrismNative.prism_backend_get_features(_backend);
        _log($"Speech backend: {Name} (features 0x{_features:X})");
        return true;
    }

    public bool Speak(string text, bool interrupt, out string error)
    {
        error = "";
        if (_backend == IntPtr.Zero) return true;

        var payload = PrismNative.Utf8Z(text);
        var result = (_features & PrismNative.FeatureOutput) != 0
            ? PrismNative.prism_backend_output(_backend, payload, interrupt)
            : PrismNative.prism_backend_speak(_backend, payload, interrupt);

        if (result == PrismNative.PrismOk
            && _braille
            && (_features & PrismNative.FeatureOutput) == 0
            && (_features & PrismNative.FeatureBraille) != 0)
        {
            PrismNative.prism_backend_braille(_backend, payload);
        }

        if (result == PrismNative.PrismOk) return true;
        error = PrismNative.ErrorText(result);
        return false;
    }

    public void Stop()
    {
        if (_backend != IntPtr.Zero && (_features & PrismNative.FeatureStop) != 0)
        {
            try { PrismNative.prism_backend_stop(_backend); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Backend re-creation. This is what lets the mod come back to life when the player
    /// starts or restarts their screen reader after the game.
    /// </summary>
    public bool Recover()
    {
        if (_backend != IntPtr.Zero)
        {
            PrismNative.prism_backend_free(_backend);
            _backend = IntPtr.Zero;
        }
        return CreateBackend();
    }

    public void Dispose()
    {
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
