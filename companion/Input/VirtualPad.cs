using DiceyA11y.Bridge;

namespace DiceyA11y.Input;

/// <summary>Lime's GamepadButton numbering, which is what GamepadState.onButtonDown takes.</summary>
public enum PadButton
{
    A = 0, B = 1, X = 2, Y = 3, Back = 4, Guide = 5, Start = 6, LeftStick = 7, RightStick = 8,
    LeftShoulder = 9, RightShoulder = 10, Up = 11, Down = 12, Left = 13, Right = 14,
}

/// <summary>A key as the screen handlers see it, before it becomes a gamepad button.</summary>
public enum NavKey { Up, Down, Left, Right, Confirm, Back, X, Y, LeftShoulder, RightShoulder, Start, Select, Snap, PreviousItem, NextItem, Path }

/// <summary>The status shortcuts: Ctrl+D, Ctrl+H, Ctrl+E.</summary>
public enum StatusKind { Dice, Health, Enemies }

/// <summary>
/// Dicey Dungeons has no keyboard navigation at all: menus, the map and combat are driven
/// by mouse or gamepad only (measured: arrow-key events dispatched to the game do nothing).
/// It does have complete gamepad navigation, so the mod plugs in a gamepad of its own.
///
/// The game keeps one GamepadState per connected pad in GamepadManager.gamepads and folds
/// them into a global state every frame. A GamepadState built with no Lime gamepad behind
/// it and pushed onto that list is treated like any other pad; calling onButtonDown /
/// onButtonUp on it is exactly what Lime's own button events do. Everything the player
/// does therefore goes through the game's own navigation, focus rules and sounds.
/// </summary>
public sealed class VirtualPad
{
    private const string Pads = "{GamepadManager}.gamepads";

    private readonly BridgeClient _bridge;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private int _index = -1;

    public VirtualPad(BridgeClient bridge, Action<string> log)
    {
        _bridge = bridge;
        _log = log;
    }

    /// <summary>Forget the cached slot, e.g. after the bridge reconnects to a new game process.</summary>
    public void Reset() { lock (_lock) _index = -1; }

    /// <summary>
    /// Creates the pad ahead of the first key press. A press delivered in the same moment the
    /// pad is created is lost (seen: the first F11 after launch did nothing), and a blind
    /// player cannot tell a lost key from a broken mod.
    /// </summary>
    public void Ensure() { lock (_lock) { if (_index < 0) Locate(); } }

    public void Down(PadButton button) => Send($"onButtonDown({(int)button})");
    public void Up(PadButton button) => Send($"onButtonUp({(int)button})");

    public void Tap(PadButton button, int holdMs = 60)
    {
        Down(button);
        Thread.Sleep(holdMs);
        Up(button);
    }

    /// <summary>
    /// Pushes the left stick in a direction (unit vector, y down) for a moment, then centres
    /// it. Lime's axes: 0 = left stick x, 1 = left stick y.
    /// </summary>
    public void PushStick(double x, double y, int holdMs)
    {
        var ix = System.Globalization.CultureInfo.InvariantCulture;
        // Both axes must land on the same frame: with x alone set for a frame the game reads
        // a purely horizontal push and may walk to a different neighbour (seen happening).
        Send($"onAxisMove(0, {x.ToString("0.0000", ix)})", $"onAxisMove(1, {y.ToString("0.0000", ix)})");
        Thread.Sleep(holdMs);
        Send("onAxisMove(0, 0.0)", "onAxisMove(1, 0.0)");
    }

    /// <summary>Sets one analog axis (Lime numbering: 2 and 3 are the right stick's x and y).</summary>
    public void Axis(int axis, double value)
        => Send($"onAxisMove({axis}, {value.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture)})");

    private void Send(params string[] calls)
    {
        lock (_lock)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (_index < 0 && !Locate()) return;

                // Same frame: confirm the slot is still ours (pads with a real device behind
                // them have a non-null "gamepad"), then act on it.
                var batch = new List<string> { $"{Pads}[{_index}].gamepad" };
                batch.AddRange(calls.Select(c => $"{Pads}[{_index}].{c}"));
                var r = _bridge.Batch(batch);
                if (r[0].IsNull && r.Skip(1).All(v => !v.IsError)) return;
                _index = -1;   // the list changed under us (pad unplugged, game restarted)
            }
            _log("Virtual gamepad: could not deliver " + string.Join(", ", calls));
        }
    }

    private bool Locate()
    {
        var count = _bridge.Eval($"{Pads}.length");
        if (count.IsError) return false;
        var n = count.AsInt();
        for (var i = 0; i < n; i++)
        {
            if (_bridge.Eval($"{Pads}[{i}].gamepad").IsNull) { _index = i; return true; }
        }

        if (!_bridge.Call($"{Pads}.push({{Type}}.createInstance({{GamepadState}}, []))")) return false;
        _index = n;
        _log($"Virtual gamepad created in slot {n}.");
        return true;
    }
}
