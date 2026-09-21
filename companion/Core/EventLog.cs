namespace DiceyA11y.Core;

/// <summary>
/// Plain-text log written next to the executable and overwritten each launch.
///
/// Two separate files matter for support: everything the companion did
/// (diceya11y.log) and everything it actually said (diceya11y-spoken.log). A
/// report of "it read this wrong" is undiagnosable without the second one.
/// </summary>
public sealed class EventLog : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly object _gate = new();
    private readonly bool _echoToConsole;

    public EventLog(string path, bool echoToConsole)
    {
        _echoToConsole = echoToConsole;
        try
        {
            _writer = new StreamWriter(path, append: false) { AutoFlush = true };
        }
        catch
        {
            _writer = null; // a read-only install folder must not stop the mod
        }
    }

    public void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        lock (_gate)
        {
            try { _writer?.WriteLine(line); } catch { }
            if (_echoToConsole) Console.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate) { _writer?.Dispose(); }
    }
}
