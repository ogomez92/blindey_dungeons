using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DiceyA11y.Mac;

namespace DiceyA11y.Input;

/// <summary>
/// A low-level keyboard hook that gives the mod its own review hotkeys.
///
/// The game is a native SDL application, so there is no way to add key handling
/// from inside it. The companion therefore listens globally and, by default,
/// only acts while Dicey Dungeons is the foreground window - a background process
/// must never swallow keys the player meant for something else.
///
/// Handled keys are swallowed so the game never sees them.
/// </summary>
public sealed class HotkeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;   // Alt
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int VK_INSERT = 0x2D, VK_CAPITAL = 0x14;   // the screen reader's modifier keys

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    /// <summary>
    /// A key plus its full modifier state. Alt is part of the chord even though
    /// nothing binds it, so that Alt+F4 falls through to Windows instead of being
    /// swallowed as a bare F4.
    /// </summary>
    public readonly record struct Chord(int VirtualKey, bool Shift, bool Control, bool Alt = false)
    {
        public override string ToString()
        {
            var name = VirtualKey is >= 0x70 and <= 0x87 ? "F" + (VirtualKey - 0x6F) : ((char)VirtualKey).ToString();
            var prefix = (Control ? "Control " : "") + (Alt ? "Alt " : "") + (Shift ? "Shift " : "");
            return prefix + name;
        }
    }

    private readonly Dictionary<Chord, Action> _bindings = new();
    private readonly Dictionary<Chord, (Action Down, Action Up)> _heldBindings = new();
    private readonly Dictionary<int, Action> _heldDown = new();   // vk -> release action, while held
    private readonly Dictionary<int, Action> _observed = new();   // vk -> action; the key goes on to the game
    private readonly HashSet<int> _observedDown = new();
    private readonly BlockingQueue _ordered = new();
    private readonly Func<bool> _shouldHandle;
    private readonly Action<string> _log;

    private HookProc? _proc;          // kept alive: the hook holds a raw pointer to it
    private IntPtr _hook = IntPtr.Zero;
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _running;

    /// <summary>Process id of the game, used to scope the hotkeys to its window.</summary>
    public int GameProcessId { get; set; }

    public bool ScopeToGameWindow { get; set; } = true;

    public HotkeyHook(Action<string> log, Func<bool>? shouldHandle = null)
    {
        _log = log;
        _shouldHandle = shouldHandle ?? (() => true);
    }

    public void Bind(int virtualKey, Action handler, bool shift = false, bool control = false)
        => _bindings[new Chord(virtualKey, shift, control)] = handler;

    /// <summary>
    /// A key that stands in for a held button: <paramref name="down"/> runs once when it
    /// goes down (auto-repeat is ignored) and <paramref name="up"/> when it is released.
    /// Both run in order on one worker thread, so a quick tap can never arrive as up, down.
    /// </summary>
    public void BindHeld(int virtualKey, Action down, Action up, bool shift = false, bool control = false)
        => _heldBindings[new Chord(virtualKey, shift, control)] = (down, up);

    /// <summary>
    /// A key the mod reacts to without taking it: Left Control stops speech the way it does
    /// in a screen reader, and must still work as the Control of Control+D and Alt+Tab.
    /// Runs once per press, whatever the auto-repeat.
    /// </summary>
    public void BindObserved(int virtualKey, Action handler) => _observed[virtualKey] = handler;

    public IEnumerable<Chord> Bindings => _bindings.Keys;

    /// <summary>macOS: runs when the keyboard cannot be read until the player grants a permission.</summary>
    public Action NeedsPermission { get; set; } = () => { };

    /// <summary>macOS: runs when the keyboard starts working after that permission was granted.</summary>
    public Action Ready { get; set; } = () => { };

    public void Start()
    {
        if (OperatingSystem.IsMacOS())
        {
            MacKeyTap.Start(this, _log, NeedsPermission, Ready);
            return;
        }
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "DiceyA11y hotkeys" };
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _proc = HookCallback;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
        {
            _log($"Hotkeys unavailable: SetWindowsHookEx failed (win32 error {Marshal.GetLastWin32Error()})");
            return;
        }
        _log("Hotkeys active.");

        // A low-level hook only fires while its thread pumps messages.
        while (_running && GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var message = (int)wParam;
        var down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        if (!down && message is not (WM_KEYUP or WM_SYSKEYUP))
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var vk = (int)data.vkCode;
        if (vk is VK_INSERT or VK_CAPITAL) _readerKeyDown[vk == VK_INSERT ? 0 : 1] = down;
        // Alt, the Windows key and the screen reader's own key (Insert or Caps Lock, in NVDA, JAWS
        // and Narrator) count as Alt does: they make a chord nothing binds, so Win+Tab, Win+arrows,
        // NVDA+Up, NVDA+Tab and Alt+F4 reach Windows and the screen reader. Players found the mod
        // took the whole keyboard while the game had focus: Win+E even ended their turn.
        // Insert and Caps Lock are tracked here, not asked of Windows: the screen reader swallows
        // them in its own hook, after this one, so Windows never records them as held.
        var swallow = OnKey(vk, down,
            () => ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0,
                   (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0,
                   (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 || (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0
                   || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0 || _readerKeyDown[0] || _readerKeyDown[1]));
        return swallow ? 1 : CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Insert, Caps Lock: held right now, as this hook saw them go down and up.</summary>
    private readonly bool[] _readerKeyDown = new bool[2];

    /// <summary>
    /// One key event from either platform, as a Windows virtual-key code. True = swallow it.
    /// Must return quickly: it runs inside the system's keyboard hook or event tap.
    /// </summary>
    internal bool OnKey(int vk, bool down, Func<(bool Shift, bool Control, bool Alt)> modifiers)
    {
        if (!down)
        {
            // Released even if focus has moved away since: a button must never stay stuck.
            lock (_observedDown) _observedDown.Remove(vk);
            Action? release;
            lock (_heldDown) { _heldDown.Remove(vk, out release); }
            if (release == null) return false;
            _ordered.Enqueue(release, _log);
            return true;
        }

        try
        {
            lock (_heldDown)
            {
                if (_heldDown.ContainsKey(vk)) return true;   // auto-repeat of a held key
            }
            if (_observed.TryGetValue(vk, out var observe))
            {
                bool first;
                lock (_observedDown) first = _observedDown.Add(vk);
                if (first && (!ScopeToGameWindow || GameWindowFocused()) && _shouldHandle())
                    ThreadPool.QueueUserWorkItem(_ => { try { observe(); } catch (Exception ex) { _log($"Key observer failed: {ex.Message}"); } });
                return false;   // the key goes on to the game
            }
            var (shift, control, alt) = modifiers();
            var chord = new Chord(vk, shift, control, alt);

            if (_heldBindings.TryGetValue(chord, out var held)
                && (!ScopeToGameWindow || GameWindowFocused())
                && _shouldHandle())
            {
                lock (_heldDown) { _heldDown[vk] = held.Up; }
                _ordered.Enqueue(held.Down, _log);
                return true;
            }

            if (_bindings.TryGetValue(chord, out var handler)
                && (!ScopeToGameWindow || GameWindowFocused())
                && _shouldHandle())
            {
                // Run off the hook thread: a slow handler here would stall all input
                // system-wide, and the system would silently drop the hook.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { handler(); }
                    catch (Exception ex) { _log($"Hotkey handler failed: {ex.Message}"); }
                });
                return true; // swallow, so the game never sees it
            }
        }
        catch (Exception ex)
        {
            _log($"Hotkey hook error: {ex.Message}");
        }
        return false;
    }

    private bool GameWindowFocused()
    {
        if (OperatingSystem.IsMacOS()) return MacKeyTap.GameFocused(GameProcessId);
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        if (GameProcessId != 0)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == GameProcessId) return true;
        }

        // Fall back to the window class if the process id is not known yet.
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        return cls.ToString() == "SDL_app";
    }

    public void Dispose()
    {
        if (OperatingSystem.IsMacOS()) { MacKeyTap.Stop(); return; }
        _running = false;
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
    }

    /// <summary>Runs queued actions one at a time, in order, off the hook thread.</summary>
    private sealed class BlockingQueue
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(Action, Action<string>)> _items = new();

        public BlockingQueue()
        {
            new Thread(() =>
            {
                foreach (var (action, log) in _items.GetConsumingEnumerable())
                {
                    try { action(); }
                    catch (Exception ex) { log($"Key handler failed: {ex.Message}"); }
                }
            }) { IsBackground = true, Name = "DiceyA11y key actions" }.Start();
        }

        public void Enqueue(Action action, Action<string> log) => _items.Add((action, log));
    }

    /// <summary>Virtual-key codes used by the default bindings.</summary>
    public static class Keys
    {
        public const int F1 = 0x70, F2 = 0x71, F3 = 0x72, F4 = 0x73, F5 = 0x74, F6 = 0x75;
        public const int F7 = 0x76, F8 = 0x77, F9 = 0x78, F10 = 0x79, F12 = 0x7B;
        public const int Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28;
        public const int Enter = 0x0D, Escape = 0x1B, Space = 0x20, Tab = 0x09, Backspace = 0x08;
        public const int Comma = 0xBC, Period = 0xBE;   // VK_OEM_COMMA, VK_OEM_PERIOD
        public const int Plus = 0xBB, Minus = 0xBD;     // VK_OEM_PLUS (the =/+ key), VK_OEM_MINUS
        public const int F11 = 0x7A, PageUp = 0x21, PageDown = 0x22, Home = 0x24, End = 0x23;
        public const int LeftControl = 0xA2;   // VK_LCONTROL: a low-level hook reports the side
        public static int Letter(char c) => char.ToUpperInvariant(c);
    }

    /// <summary>True when a process with this id is still alive.</summary>
    public static bool ProcessAlive(int pid)
    {
        if (pid == 0) return false;
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }
}
