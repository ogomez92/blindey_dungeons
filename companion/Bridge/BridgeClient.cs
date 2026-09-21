using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace DiceyA11y.Bridge;

/// <summary>One on-screen text field, in the game's 3840x2160 canvas coordinates.</summary>
public readonly record struct StageText(int X, int Y, int Width, int Height, string Raw)
{
    public int CenterX => X + Width / 2;
    public int Bottom => Y + Height;
}

/// <summary>
/// Client for the in-process bridge (bridge/bridge.cpp). The bridge answers on the game's
/// frame tick, so a request costs about one frame; callers that need several values ask
/// for them in one <see cref="Batch"/> so they also see one consistent frame.
///
/// Thread safe. Requests from different threads are pipelined, replies are matched by id.
/// </summary>
public sealed class BridgeClient : IDisposable
{
    private const string PipeName = "DiceyA11yBridge";
    public const char UnitSeparator = '\x1f';
    private const char Unit = UnitSeparator;

    private readonly Action<string> _log;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<string>> _pending = new();
    private NamedPipeClientStream? _pipe;
    private Thread? _reader;
    private long _nextId;
    private volatile bool _connected;

    public bool Connected => _connected;
    public event Action? Disconnected;

    public BridgeClient(Action<string> log) => _log = log;

    public bool Connect(int timeoutMs)
    {
        Disconnect();
        try
        {
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(timeoutMs);
            _pipe = pipe;
            _connected = true;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "DiceyA11y bridge reader" };
            _reader.Start(pipe);
            return Request("ping") == "pong";
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ReadLoop(object? state)
    {
        var pipe = (NamedPipeClientStream)state!;
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                var tab = line.IndexOf('\t');
                if (tab <= 0 || !long.TryParse(line.AsSpan(0, tab), out var id)) continue;
                if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(line[(tab + 1)..]);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }

        if (ReferenceEquals(pipe, _pipe)) DropConnection();
    }

    private void DropConnection()
    {
        if (!_connected) return;
        _connected = false;
        foreach (var kv in _pending)
            if (_pending.TryRemove(kv.Key, out var tcs)) tcs.TrySetResult("!bridge disconnected");
        _log("Bridge connection lost.");
        Disconnected?.Invoke();
    }

    public void Disconnect()
    {
        _connected = false;
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    /// <summary>Raw request. Returns "!..." on any failure; never throws.</summary>
    public string Request(string command, string argument = "", string? extra = null, int timeoutMs = 3000)
    {
        var pipe = _pipe;
        if (!_connected || pipe == null) return "!bridge not connected";

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var line = extra == null ? $"{id}\t{command}\t{argument}\n" : $"{id}\t{command}\t{argument}\t{extra}\n";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            lock (_writeLock)
            {
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _pending.TryRemove(id, out _);
            DropConnection();
            return "!bridge write failed";
        }

        // The game stops ticking while it is loading or minimised; a timeout is not fatal.
        if (tcs.Task.Wait(timeoutMs)) return tcs.Task.Result;
        _pending.TryRemove(id, out _);
        return "!timeout";
    }

    public GameValue Eval(string expression) => new(Request("eval", expression));

    /// <summary>Evaluates every expression on the same game frame.</summary>
    public GameValue[] Batch(IReadOnlyList<string> expressions)
    {
        var reply = Request("batch", string.Join(Unit, expressions));
        var parts = reply.Split(Unit);
        var result = new GameValue[expressions.Count];
        var failed = parts.Length != expressions.Count;
        for (var i = 0; i < result.Length; i++)
            result[i] = failed ? new GameValue(reply.StartsWith('!') ? reply : "!batch mismatch") : new GameValue(parts[i]);
        return result;
    }

    public GameValue[] Batch(params string[] expressions) => Batch((IReadOnlyList<string>)expressions);

    /// <summary>Calls a game function for its side effect. Returns false if it failed.</summary>
    public bool Call(string expression)
    {
        var v = Eval(expression);
        if (v.IsError) _log($"Bridge call failed: {expression} -> {v.Error}");
        return !v.IsError;
    }

    public bool SetField(string target, string field, string valueExpression)
        => Request("set", string.Join(Unit, target, field, valueExpression)) == "ok";

    /// <summary>Field name and value of each field of an object, or index and value for an array.</summary>
    public List<(string Name, GameValue Value)> Dump(string expression)
    {
        var parts = Request("dump", expression).Split(Unit);
        var list = new List<(string, GameValue)>();
        for (var i = 1; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq > 0) list.Add((parts[i][..eq], new GameValue(parts[i][(eq + 1)..])));
        }
        return list;
    }

    /// <summary>
    /// For each element of a game array, the given paths evaluated against it, all on one
    /// frame. Null when the array itself could not be read.
    /// </summary>
    public List<GameValue[]>? Pluck(string arrayExpression, params string[] paths)
    {
        var reply = Request("pluck", arrayExpression + Unit + string.Join(Unit, paths));
        if (reply.StartsWith('!')) return null;
        var rows = new List<GameValue[]>();
        if (reply.Length == 0) return rows;
        foreach (var record in reply.Split('\x1e'))
        {
            var cells = record.Split(Unit);
            if (cells.Length != paths.Length) continue;
            rows.Add(cells.Select(c => new GameValue(c)).ToArray());
        }
        return rows;
    }

    /// <summary>Every visible text field on the game's stage, top to bottom then left to right.</summary>
    public List<StageText> StageTexts()
    {
        var reply = Request("stagetext", "{Screen}.stage");
        var list = new List<StageText>();
        if (reply.Length == 0 || reply[0] == '!') return list;
        foreach (var item in reply.Split(Unit))
        {
            var f = item.Split(',', 5);   // x,y,w,h,text - the text itself may contain commas
            if (f.Length != 5) continue;
            if (!int.TryParse(f[0], out var x) || !int.TryParse(f[1], out var y)
                || !int.TryParse(f[2], out var w) || !int.TryParse(f[3], out var h)) continue;
            list.Add(new StageText(x, y, w, h, GameValue.Unescape(f[4])));
        }
        return ReadingOrder(list);
    }

    /// <summary>
    /// Rows first, then left to right. Text on one visual row rarely shares an exact y, so
    /// rows are clustered by gap (a tolerance inside the comparer would not be transitive).
    /// </summary>
    private static List<StageText> ReadingOrder(List<StageText> items)
    {
        const int rowGap = 24;
        items.Sort((p, q) => p.Y.CompareTo(q.Y));
        var ordered = new List<StageText>(items.Count);
        var row = new List<StageText>();
        foreach (var item in items)
        {
            if (row.Count > 0 && item.Y - row[^1].Y > rowGap)
            {
                ordered.AddRange(row.OrderBy(t => t.X));
                row.Clear();
            }
            row.Add(item);
        }
        ordered.AddRange(row.OrderBy(t => t.X));
        return ordered;
    }

    public void Dispose() => Disconnect();
}
