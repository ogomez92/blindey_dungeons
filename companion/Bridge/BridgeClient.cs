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

    // ------------------------------------------------------------------ frame cache
    //
    // A request costs one game frame, and a battle poll used to make more than twenty of them
    // one after another: a third of a second before anything could be spoken. The bridge
    // serves up to 64 requests per frame, though, so reads that are sent together cost one
    // frame together. A scope remembers every pure read made inside it; a learning scope (the
    // poll) also sends, up front and all at once, the reads the previous poll made.

    private static readonly string[] ReadCommands = { "eval", "batch", "pluck", "dump", "iter", "stagetext" };

    /// <summary>Calls known to have no side effects; anything else with a "(" is never cached or replayed.</summary>
    private static readonly string[] PureCalls =
    {
        ".translate(", ".translatearray(", ".getequipmenttemplate(", ".getstring(", ".getitem(", ".keys(", ".get(",
        ".isopen(", ".numepisodeswon(", ".toString(",
    };

    /// <summary>Diagnostics: the reads of the current scope that were not answered ahead of time.</summary>
    public List<string> Missed { get; } = new();

    private readonly object _scopeLock = new();
    private Dictionary<string, string>? _cache;
    private int _cacheThread;
    // One learned list per key: the poll's and a key press's scopes alternate, and a single
    // list made each wipe the other's (every poll after an arrow key then cost 600 ms).
    private readonly Dictionary<string, List<string>> _learned = new();
    private readonly HashSet<string> _asked = new();

    private static bool IsPureRead(string command, string argument)
    {
        if (Array.IndexOf(ReadCommands, command) < 0) return false;
        var rest = argument;
        foreach (var call in PureCalls) rest = rest.Replace(call, "");
        if (rest.Contains('(')) return false;
        // Handles expire; only #0 (the application object) is stable across polls.
        for (var i = rest.IndexOf('#'); i >= 0; i = rest.IndexOf('#', i + 1))
            if (i + 1 >= rest.Length || rest[i + 1] != '0' || (i + 2 < rest.Length && char.IsDigit(rest[i + 2]))) return false;
        return true;
    }

    private sealed class Scope : IDisposable
    {
        private readonly Action _close;
        public Scope(Action close) => _close = close;
        public void Dispose() => _close();
    }

    private static readonly IDisposable NoScope = new Scope(() => { });

    /// <summary>
    /// Until disposed, pure reads made on this thread are answered once and remembered.
    /// With <paramref name="learnKey"/> set, the reads made in the previous scope of the same
    /// key are sent first, together, so the code that follows finds them already answered.
    /// </summary>
    public IDisposable OpenScope(string? learnKey = null)
    {
        lock (_scopeLock)
        {
            if (_cache != null) return NoScope;   // nested, or another thread's: leave it alone
            _cache = new Dictionary<string, string>();
            _asked.Clear();
            Missed.Clear();
            _cacheThread = Environment.CurrentManagedThreadId;
        }
        if (learnKey != null)
        {
            List<string> known;
            lock (_scopeLock) known = _learned.TryGetValue(learnKey, out var list) ? list : new List<string>();
            Warm(known.Select(k => { var tab = k.IndexOf('\t'); return (k[..tab], k[(tab + 1)..]); }));
        }
        return new Scope(() =>
        {
            lock (_scopeLock)
            {
                if (learnKey != null) _learned[learnKey] = _asked.ToList();
                _cache = null;
            }
        });
    }

    /// <summary>Sends these reads together (one frame) so the calls that follow are already answered.</summary>
    public void Warm(IEnumerable<(string Command, string Argument)> reads)
    {
        var cache = _cache;
        if (cache == null || _cacheThread != Environment.CurrentManagedThreadId) return;
        var sent = new List<(string Key, Task<string> Reply)>();
        foreach (var (command, argument) in reads)
        {
            var key = command + "\t" + argument;
            if (cache.ContainsKey(key) || sent.Any(s => s.Key == key) || !IsPureRead(command, argument)) continue;
            sent.Add((key, Send(command, argument, null)));
        }
        // The game does not tick while loading: one wait for the lot, not one each.
        var deadline = DateTime.UtcNow.AddMilliseconds(3000);
        foreach (var (key, reply) in sent)
        {
            var left = deadline - DateTime.UtcNow;
            if (reply.Wait(left > TimeSpan.Zero ? left : TimeSpan.Zero)) cache[key] = reply.Result;
        }
    }

    public void WarmBatch(params string[] expressions) => Warm(new[] { ("batch", string.Join(Unit, expressions)) });

    /// <summary>Raw request. Returns "!..." on any failure; never throws.</summary>
    public string Request(string command, string argument = "", string? extra = null, int timeoutMs = 3000)
    {
        var cache = _cache;
        var cacheable = cache != null && extra == null && _cacheThread == Environment.CurrentManagedThreadId && IsPureRead(command, argument);
        if (cacheable) _asked.Add(command + "\t" + argument);
        if (cacheable && cache!.TryGetValue(command + "\t" + argument, out var known)) return known;
        // Something is about to change in the game: what was read before it no longer holds.
        if (!cacheable && cache != null && _cacheThread == Environment.CurrentManagedThreadId) cache.Clear();

        if (cache != null && _cacheThread == Environment.CurrentManagedThreadId) Missed.Add(command + " " + (argument.Length > 90 ? argument[..90] : argument));
        var task = Send(command, argument, extra);
        var reply = task.Wait(timeoutMs) ? task.Result : "!timeout";
        if (cacheable && !reply.StartsWith("!timeout", StringComparison.Ordinal)) cache![command + "\t" + argument] = reply;
        return reply;
    }

    private static readonly Task<string> NotConnected = Task.FromResult("!bridge not connected");

    /// <summary>Requests actually sent to the game; the difference across a poll is what it cost in frames at worst.</summary>
    public long Sent => Interlocked.Read(ref _nextId);

    private Task<string> Send(string command, string argument, string? extra)
    {
        var pipe = _pipe;
        if (!_connected || pipe == null) return NotConnected;

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
            return Task.FromResult("!bridge write failed");
        }

        // The game stops ticking while it is loading or minimised; a timeout is not fatal.
        // (A reply that never comes leaves its entry behind until the connection drops.)
        return tcs.Task;
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
