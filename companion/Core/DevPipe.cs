using System.IO.Pipes;
using System.Text;

namespace DiceyA11y.Core;

/// <summary>
/// Developer aid, enabled with --dev. Windows discards synthetic keystrokes sent to a
/// window that does not have focus, so the keyboard path cannot be driven from a test
/// script. This pipe (\\.\pipe\DiceyA11yDev) accepts one command per line and feeds it to
/// the same code a real key press reaches:
///
///     key Up | Down | Left | Right | Confirm | Back | X | Y | ...    a navigation key tap
///     read focus | screen | context | help                            the F3 / F4 / F5 / F1 reads
///
/// The reply is what was (or would be) spoken, so a script can assert on it.
/// </summary>
public sealed class DevPipe
{
    private readonly Func<string, string> _handle;
    private readonly Action<string> _log;

    public DevPipe(Func<string, string> handle, Action<string> log)
    {
        _handle = handle;
        _log = log;
    }

    public void Start()
    {
        new Thread(Run) { IsBackground = true, Name = "DiceyA11y dev pipe" }.Start();
        _log("Developer pipe listening.");
    }

    private void Run()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream("DiceyA11yDev", PipeDirection.InOut, 1, PipeTransmissionMode.Byte);
                server.WaitForConnection();
                using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                while (reader.ReadLine() is { } line)
                {
                    string reply;
                    try { reply = _handle(line.Trim()); }
                    catch (Exception ex) { reply = "error: " + ex.Message; }
                    writer.WriteLine(reply.Replace('\n', ' '));
                }
            }
            catch (IOException) { }
            catch (Exception ex) { _log("Dev pipe: " + ex.Message); Thread.Sleep(500); }
        }
    }
}
