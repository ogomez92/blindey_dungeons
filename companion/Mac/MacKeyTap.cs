using System.Diagnostics;
using System.Runtime.InteropServices;
using DiceyA11y.Input;
using DiceyA11y.Launcher;
using Keys = DiceyA11y.Input.HotkeyHook.Keys;

namespace DiceyA11y.Mac;

/// <summary>
/// The macOS keyboard source for <see cref="HotkeyHook"/>: a CGEventTap (in libDiceyMac)
/// that reports every key, translated here to the Windows virtual-key codes the bindings
/// are written in, so every binding is shared. A tap that can swallow keys needs the
/// Accessibility permission for the app running the mod.
/// </summary>
internal static unsafe class MacKeyTap
{
    private static HotkeyHook? _hook;
    private static Action<string> _log = _ => { };

    // CGEventFlags
    private const ulong Shift = 0x20000, Control = 0x40000, Option = 0x80000, Command = 0x100000;

    // kVK_* (Carbon Events.h) -> Windows VK. Only keys something binds need to be here.
    private static readonly Dictionary<int, int> ToVirtualKey = new()
    {
        [0x7E] = Keys.Up, [0x7D] = Keys.Down, [0x7B] = Keys.Left, [0x7C] = Keys.Right,
        [0x24] = Keys.Enter, [0x4C] = Keys.Enter, [0x31] = Keys.Space, [0x35] = Keys.Escape,
        [0x30] = Keys.Tab, [0x33] = Keys.Backspace,
        [0x2B] = Keys.Comma, [0x2F] = Keys.Period, [0x18] = Keys.Plus, [0x1B] = Keys.Minus,
        // kVK_Control, the Mac's one Control key, reported through flagsChanged. Right
        // Control (0x3E) is left out so it stops speech on neither platform.
        [0x3B] = Keys.LeftControl,
        [0x74] = Keys.PageUp, [0x79] = Keys.PageDown, [0x73] = Keys.Home, [0x77] = Keys.End,
        [0x7A] = Keys.F1, [0x78] = Keys.F2, [0x63] = Keys.F3, [0x76] = Keys.F4, [0x60] = Keys.F5,
        [0x61] = Keys.F6, [0x62] = Keys.F7, [0x64] = Keys.F8, [0x65] = Keys.F9, [0x6D] = Keys.F10,
        [0x67] = Keys.F11, [0x6F] = Keys.F12,
        [0x00] = 'A', [0x0B] = 'B', [0x08] = 'C', [0x02] = 'D', [0x0E] = 'E', [0x03] = 'F', [0x05] = 'G',
        [0x04] = 'H', [0x22] = 'I', [0x26] = 'J', [0x28] = 'K', [0x25] = 'L', [0x2E] = 'M', [0x2D] = 'N',
        [0x1F] = 'O', [0x23] = 'P', [0x0C] = 'Q', [0x0F] = 'R', [0x01] = 'S', [0x11] = 'T', [0x20] = 'U',
        [0x09] = 'V', [0x0D] = 'W', [0x07] = 'X', [0x10] = 'Y', [0x06] = 'Z',
    };

    /// <summary>
    /// Starts the tap. Without the Accessibility permission it cannot exist yet: then
    /// <paramref name="needsPermission"/> runs (to say so), macOS shows its prompt, and the
    /// tap starts by itself once the player allows it (<paramref name="ready"/> runs then).
    /// </summary>
    public static void Start(HotkeyHook hook, Action<string> log, Action needsPermission, Action ready)
    {
        _hook = hook;
        _log = log;
        try
        {
            if (TryStart()) return;
            MacNative.dm_accessibility_trusted(prompt: true);
            log("Hotkeys waiting for the Accessibility permission (System Settings > Privacy & Security > Accessibility).");
            new Thread(() =>
            {
                // Said again every half minute: nothing works until it is done, and the first
                // time is easily talked over by the game's own opening announcements.
                var nextReminder = DateTime.MinValue;
                while (!MacNative.dm_accessibility_trusted(prompt: false))
                {
                    if (DateTime.UtcNow >= nextReminder) { needsPermission(); nextReminder = DateTime.UtcNow.AddSeconds(30); }
                    Thread.Sleep(1500);
                }
                if (TryStart()) ready();
            }) { IsBackground = true, Name = "DiceyA11y permission wait" }.Start();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            log($"Hotkeys unavailable: libDiceyMac.dylib could not be loaded ({ex.Message})");
        }
    }

    private static bool TryStart()
    {
        if (!MacNative.dm_accessibility_trusted(prompt: false) || MacNative.dm_keytap_start(&OnKeyEvent) == 0) return false;
        _log("Hotkeys active (event tap).");
        return true;
    }

    public static void Stop()
    {
        try { MacNative.dm_keytap_stop(); } catch { }
    }

    /// <summary>True when the frontmost window belongs to the game.</summary>
    public static bool GameFocused(int gamePid)
    {
        var pid = MacNative.dm_frontmost_pid();
        if (pid == 0) return false;
        if (gamePid != 0) return pid == gamePid;
        try { return Process.GetProcessById(pid).ProcessName == GamePaths.ProcessName; }
        catch { return false; }
    }

    [UnmanagedCallersOnly]
    private static int OnKeyEvent(int keycode, int down, ulong flags, int autorepeat)
    {
        try
        {
            if (_hook == null || !ToVirtualKey.TryGetValue(keycode, out var vk)) return 0;
            // Command counts as Alt does on Windows: part of the chord, so Command+Q, Command+Tab
            // and friends never match a binding and always reach the system.
            return _hook.OnKey(vk, down != 0,
                () => ((flags & Shift) != 0, (flags & Control) != 0, (flags & (Option | Command)) != 0)) ? 1 : 0;
        }
        catch (Exception ex)
        {
            _log($"Key tap error: {ex.Message}");
            return 0;
        }
    }
}
