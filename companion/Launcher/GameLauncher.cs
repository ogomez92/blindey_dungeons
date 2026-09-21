using System.Diagnostics;
using System.Text;
using DiceyA11y.Core;

namespace DiceyA11y.Launcher;

/// <summary>
/// Starts Dicey Dungeons and reads its standard output.
///
/// This is the mod's only channel out of the game. Dicey Dungeons scripts run in a
/// sandboxed hscript interpreter with no file, socket or process access - but
/// trace() reaches the process's stdout, and a GUI subsystem executable still
/// writes to an inherited stdout handle. Redirecting it here is what makes the
/// whole design possible.
/// </summary>
public sealed class GameLauncher : IDisposable
{
    private readonly Config _config;
    private readonly Action<string> _log;
    private Process? _process;
    private StreamWriter? _rawLog;

    /// <summary>Every line the game printed, already trimmed of the trailing newline.</summary>
    public event Action<string>? LineReceived;

    public event Action<int>? Exited;

    public int ProcessId => _process?.Id ?? 0;
    public bool Running => _process is { HasExited: false };

    public GameLauncher(Config config, Action<string> log)
    {
        _config = config;
        _log = log;
    }

    public string ExecutablePath => Path.Combine(_config.GameDirectory, "diceydungeons.exe");

    public bool Validate(out string problem)
    {
        if (!Directory.Exists(_config.GameDirectory))
        {
            problem = $"Game folder not found: {_config.GameDirectory}. Fix GameDirectory in {Config.DefaultPath}.";
            return false;
        }
        if (!File.Exists(ExecutablePath))
        {
            problem = $"diceydungeons.exe not found in {_config.GameDirectory}.";
            return false;
        }

        // The Polymod script mod is only the fallback for a game folder without the bridge.
        // With the bridge installed nothing else is needed, so a missing mod is not an error.
        var bridged = File.Exists(Path.Combine(_config.GameDirectory, "lime_orig.dll"));
        if (!bridged && !ModFolders().All(Directory.Exists))
        {
            problem = "Neither the accessibility bridge nor the fallback script mod is installed in "
                      + _config.GameDirectory + ". Run the installer again, then try again.";
            return false;
        }

        problem = "";
        return true;
    }

    private IEnumerable<string> ModFolders() => _config.ModIds
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(id => Path.Combine(_config.GameDirectory, "mods", id));

    /// <summary>True when the mod was started after the game (from Steam, say): attach, do not launch.</summary>
    public bool AttachIfRunning()
    {
        var running = Process.GetProcessesByName("diceydungeons").FirstOrDefault();
        if (running == null) return false;
        _process = running;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Exited?.Invoke(0);
        _log($"Dicey Dungeons is already running (pid {running.Id}); attaching to it.");
        return true;
    }

    public void Start(string rawLogPath)
    {
        var args = new List<string>();
        // Only ask for mods that exist: the game stops with an error on an unknown mod id.
        var mods = _config.ModIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => Directory.Exists(Path.Combine(_config.GameDirectory, "mods", id))).ToList();
        if (mods.Count > 0) args.Add("mod=" + string.Join(",", mods));
        if (!string.IsNullOrWhiteSpace(_config.ExtraGameArgs))
            args.AddRange(_config.ExtraGameArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var info = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            WorkingDirectory = _config.GameDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);

        try { _rawLog = new StreamWriter(rawLogPath, append: false) { AutoFlush = true }; }
        catch (Exception ex) { _log($"Could not open raw game log: {ex.Message}"); }

        _log($"Launching: {ExecutablePath} {string.Join(' ', args)}");

        _process = new Process { StartInfo = info, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => Handle(e.Data);
        _process.ErrorDataReceived += (_, e) => Handle(e.Data);
        _process.Exited += (_, _) => Exited?.Invoke(_process?.ExitCode ?? 0);

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private void Handle(string? line)
    {
        if (line is null) return;
        try { _rawLog?.WriteLine(line); } catch { /* disk full etc. is not fatal */ }
        LineReceived?.Invoke(line);
    }

    public void WaitForExit() => _process?.WaitForExit();

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch { }
        _process?.Dispose();
        _rawLog?.Dispose();
    }
}
