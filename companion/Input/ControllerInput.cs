using DiceyA11y.Bridge;

namespace DiceyA11y.Input;

/// <summary>A control on the player's own controller, sticks and triggers split into directions.</summary>
public enum PadControl
{
    A, B, X, Y, Back, Start, LeftStickClick, RightStickClick, LeftShoulder, RightShoulder,
    DpadUp, DpadDown, DpadLeft, DpadRight, LeftTrigger, RightTrigger,
    LeftStickUp, LeftStickDown, LeftStickLeft, LeftStickRight,
    RightStickUp, RightStickDown, RightStickLeft, RightStickRight,
}

/// <summary>
/// The player's own controller, taken from the game so that it goes through the mod exactly as
/// the keyboard does. Left with the game, the controller drove the game's navigation directly:
/// the mod's card list, map cursor, Tab groups and review keys were keyboard-only, and since the
/// mod never saw a press, it never interrupted speech either (a player moving fast heard a queue
/// of every item passed).
///
/// How the controller is taken (measured with a controller faked through Lime's own
/// <c>Gamepad.__connect</c> and <c>onButtonDown.dispatch</c>, which is the path SDL's events take):
/// the game keeps one <c>GamepadState</c> per connected pad in <c>GamepadManager.gamepads</c>;
/// Lime's button and axis events write into that state through listeners its constructor added,
/// and every frame the game folds the list into <c>GamepadManager.global</c> and clears
/// <c>internal_justpressed</c>. Taken out of the list, the state still receives every event but
/// the game no longer sees it, and its <c>internal_justpressed</c> stays set until the mod clears
/// it, so a tap shorter than a frame is not lost. A bridge handle cannot be kept across requests,
/// so the state is parked in a typed slot nothing else writes: the mod's own virtual pad's
/// <c>rumble.inputgamepad</c> (a <c>GamepadState</c> field). On a clean exit the controller is
/// given back to the game.
/// </summary>
public sealed class ControllerInput : IDisposable
{
    private const string Pads = "{GamepadManager}.gamepads";
    private const int Buttons = 15;   // Lime GamepadButton A0 .. DpadRight14
    private const int Guide = 5;      // left to Steam and the system

    private static readonly PadControl?[] ButtonControl =
    {
        PadControl.A, PadControl.B, PadControl.X, PadControl.Y, PadControl.Back, null, PadControl.Start,
        PadControl.LeftStickClick, PadControl.RightStickClick, PadControl.LeftShoulder, PadControl.RightShoulder,
        PadControl.DpadUp, PadControl.DpadDown, PadControl.DpadLeft, PadControl.DpadRight,
    };

    private readonly BridgeClient _bridge;
    private readonly VirtualPad _pad;
    private readonly Action<string> _log;
    private readonly HashSet<PadControl> _down = new();
    private Thread? _thread;
    private volatile bool _running;
    private bool _taken;

    /// <summary>Runs on the reader thread, in the order the controls moved. Must not block for long.</summary>
    public event Action<PadControl, bool>? Changed;

    /// <summary>A controller was taken from the game (true) or went away (false).</summary>
    public event Action<bool>? Connection;

    /// <summary>When false the game keeps the controller and the mod never sees it.</summary>
    public bool Enabled { get; set; } = true;

    public bool HasController => _taken;

    public ControllerInput(BridgeClient bridge, VirtualPad pad, Action<string> log)
    {
        _bridge = bridge;
        _pad = pad;
        _log = log;
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "DiceyA11y controller" };
        _thread.Start();
    }

    private void Run()
    {
        while (_running)
        {
            try
            {
                if (!_bridge.Connected || !Enabled) { Drop(); Thread.Sleep(500); continue; }
                if (_pad.Slot is not { } slot) { Thread.Sleep(300); continue; }
                if (!_taken) { if (!TryTake(slot)) Thread.Sleep(300); continue; }
                Read(slot);
            }
            catch (Exception ex)
            {
                _log("Controller error: " + ex.Message);
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>Takes the first real controller in the game's list, if there is one.</summary>
    private bool TryTake(string slot)
    {
        // Already parked: the mod was closed without giving it back (killed, crashed) and has
        // started again. Out of the game's list nothing else would ever find it.
        var already = _bridge.Batch(slot + ".rumble.inputgamepad.gamepad", slot + ".rumble.inputgamepad.gamepad.connected");
        if (already[0].Kind == GameValueKind.Object && already[1].AsBool())
        {
            // Presses latched while nobody read it are stale: replayed, a Back + Start pressed
            // between two runs opened the pause menu (seen).
            ClearLatches(slot + ".rumble.inputgamepad");
            _taken = true;
            _log("Controller found where the mod left it.");
            Connection?.Invoke(true);
            return true;
        }

        var pads = _bridge.Pluck(Pads, "gamepad");
        if (pads == null) return false;
        var index = pads.FindIndex(p => p[0].Kind == GameValueKind.Object);
        if (index < 0) return false;

        // Park it first, then take it out of the list: never a moment where nothing holds it.
        if (!_bridge.SetField(slot + ".rumble", "inputgamepad", $"{Pads}[{index}]")) return false;
        var parked = _bridge.Eval(slot + ".rumble.inputgamepad.gamepad");
        if (parked.Kind != GameValueKind.Object) { Restore(slot); return false; }
        if (_bridge.Eval($"{Pads}.splice({index}, 1)").IsError) { Restore(slot); return false; }
        _pad.Reset();   // the list shrank: the mod's own pad may have moved down a slot
        _taken = true;
        var name = _bridge.Eval(slot + ".rumble.inputgamepad.gamepad.name").AsString();
        _log($"Controller taken from the game{(name.Length > 0 ? ": " + name : "")}.");
        Connection?.Invoke(true);
        return true;
    }

    private void ClearLatches(string state)
    {
        for (var b = 0; b < Buttons; b++)
            _bridge.Request("seti", string.Join("\u001f", state + ".internal_justpressed", b.ToString(), "false"));
    }

    private void Read(string slot)
    {
        var state = slot + ".rumble.inputgamepad";
        var reads = new List<string>(4 + Buttons * 2 + 6) { slot + ".gamepad", state + ".gamepad.connected" };
        for (var b = 0; b < Buttons; b++) reads.Add($"{state}.internal_pressed[{b}]");
        for (var b = 0; b < Buttons; b++) reads.Add($"{state}.internal_justpressed[{b}]");
        for (var a = 0; a < 6; a++) reads.Add($"{state}.internal_axis[{a}]");
        var v = _bridge.Batch(reads);

        // The mod's own pad is no longer where it was (the game restarted, the list changed).
        if (!v[0].IsNull) { _pad.Reset(); return; }
        if (v[1].IsError) return;   // the game is loading or not ticking
        if (!v[1].AsBool())
        {
            _log("Controller disconnected.");
            Drop();
            Restore(slot);
            Connection?.Invoke(false);
            return;
        }

        for (var b = 0; b < Buttons; b++)
        {
            if (b == Guide || ButtonControl[b] is not { } control) continue;
            var pressed = v[2 + b].AsBool();
            var just = v[2 + Buttons + b].AsBool();
            if (just)
            {
                _bridge.Request("seti", string.Join("\u001f", state + ".internal_justpressed", b.ToString(), "false"));
                if (_down.Contains(control)) Set(control, false);   // released and pressed again between two reads
                Set(control, true);
                if (!pressed) Set(control, false);                  // a tap shorter than a read
            }
            else Set(control, pressed);
        }

        var axis = new double[6];
        for (var a = 0; a < 6; a++) axis[a] = v[2 + Buttons * 2 + a].AsDouble();
        Stick(axis[0], axis[1], PadControl.LeftStickUp, PadControl.LeftStickDown, PadControl.LeftStickLeft, PadControl.LeftStickRight);
        Stick(axis[2], axis[3], PadControl.RightStickUp, PadControl.RightStickDown, PadControl.RightStickLeft, PadControl.RightStickRight);
        Trigger(axis[4], PadControl.LeftTrigger);
        Trigger(axis[5], PadControl.RightTrigger);
    }

    // A stick is four buttons, one at a time: the direction it leans furthest, pressed past 0.6
    // and released under 0.35, so resting near the threshold does not chatter.
    private void Stick(double x, double y, PadControl up, PadControl down, PadControl left, PadControl right)
    {
        PadControl? held = _down.Contains(up) ? up : _down.Contains(down) ? down : _down.Contains(left) ? left : _down.Contains(right) ? right : null;
        var horizontal = Math.Abs(x) >= Math.Abs(y);
        var strength = Math.Max(Math.Abs(x), Math.Abs(y));
        PadControl? want = strength < (held == null ? 0.6 : 0.35) ? null
            : horizontal ? (x < 0 ? left : right) : (y < 0 ? up : down);
        // While held, only a clear lean another way changes the direction.
        if (held != null && want != null && want != held && strength < 0.6) want = held;
        if (want == held) return;
        if (held != null) Set(held.Value, false);
        if (want != null) Set(want.Value, true);
    }

    private void Trigger(double value, PadControl control)
        => Set(control, _down.Contains(control) ? value > 0.25 : value > 0.5);

    private void Set(PadControl control, bool down)
    {
        if (down ? !_down.Add(control) : !_down.Remove(control)) return;
        Changed?.Invoke(control, down);
    }

    /// <summary>Releases whatever was held, so no button stays pressed in the mod.</summary>
    private void Drop()
    {
        foreach (var c in _down.ToList()) Set(c, false);
        _taken = false;
    }

    /// <summary>Puts the mod's own pad's rumble slot back the way the game made it.</summary>
    private void Restore(string slot) => _bridge.SetField(slot + ".rumble", "inputgamepad", slot);

    /// <summary>Gives the controller back to the game, so it keeps working after the mod closes.</summary>
    public void Dispose()
    {
        _running = false;
        _thread?.Join(1000);
        if (!_taken || !_bridge.Connected || _pad.Slot is not { } slot) return;
        try
        {
            if (_bridge.Eval(slot + ".rumble.inputgamepad.gamepad").Kind == GameValueKind.Object)
                _bridge.Call($"{Pads}.push({slot}.rumble.inputgamepad)");
            Restore(slot);
            _log("Controller given back to the game.");
        }
        catch { /* the game is closing */ }
    }
}
