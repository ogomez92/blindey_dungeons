using DiceyA11y.Mac;

namespace DiceyA11y.Speech;

/// <summary>
/// Speech on macOS through AVSpeechSynthesizer. Unlike a screen reader it has no settings
/// of its own the player already tuned, so rate (and optionally the voice) is the mod's:
/// <c>SpeechRate</c> and <c>MacVoice</c> in the config, rate also on hotkeys.
/// </summary>
internal sealed class AvSpeechEngine : ISpeechEngine
{
    private readonly Action<string> _log;
    private int _rate;

    public string Name => "AVSpeech";

    private AvSpeechEngine(Action<string> log) => _log = log;

    public static AvSpeechEngine? Create(int ratePercent, string voice, Action<string> log)
    {
        try
        {
            if (MacNative.dm_speech_create() == 0) { log("Speech unavailable: AVSpeechSynthesizer could not be created"); return null; }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            log($"Speech unavailable: libDiceyMac.dylib could not be loaded ({ex.Message})");
            return null;
        }
        var engine = new AvSpeechEngine(log) { Rate = ratePercent };
        if (!string.IsNullOrWhiteSpace(voice) && MacNative.dm_speech_set_voice(PrismNative.Utf8Z(voice)) == 0)
            log($"Voice '{voice}' not found; using the system voice.");
        log($"Speech backend: AVSpeech, rate {engine._rate}%");
        return engine;
    }

    public int? Rate
    {
        get => _rate;
        set
        {
            _rate = Math.Clamp(value ?? 50, 0, 100);
            MacNative.dm_speech_set_rate(_rate / 100f);
        }
    }

    public bool Speak(string text, bool interrupt, out string error)
    {
        error = "";
        if (MacNative.dm_speech_speak(PrismNative.Utf8Z(text), interrupt) != 0) return true;
        error = "synthesizer not created";
        return false;
    }

    public void Stop() => MacNative.dm_speech_stop();

    public bool Recover() => MacNative.dm_speech_create() != 0;

    public void Dispose()
    {
        try { MacNative.dm_speech_stop(); } catch { }
    }
}
