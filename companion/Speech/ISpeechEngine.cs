namespace DiceyA11y.Speech;

/// <summary>
/// One way of getting text out: Prism (screen readers, Windows) or AVSpeechSynthesizer
/// (macOS). <see cref="ScreenReaderOutput"/> owns the queueing and calls these from its
/// single speech thread only.
/// </summary>
internal interface ISpeechEngine : IDisposable
{
    string Name { get; }

    /// <summary>Speaks (and brailles, where that applies). False when the engine failed and should be recovered.</summary>
    bool Speak(string text, bool interrupt, out string error);

    void Stop();

    /// <summary>Re-creates the engine after a failure. False if it is still unavailable.</summary>
    bool Recover();

    /// <summary>Speech rate in percent, 0-100; null when the engine leaves rate to the screen reader.</summary>
    int? Rate { get; set; }
}
