using System.Runtime.InteropServices;

namespace DiceyA11y.Mac;

/// <summary>P/Invoke surface of libDiceyMac.dylib (Mac/Native/DiceyMac.swift).</summary>
internal static unsafe class MacNative
{
    private const string Lib = "DiceyMac";

    [DllImport(Lib)] internal static extern int dm_speech_create();
    [DllImport(Lib)] internal static extern int dm_speech_speak(byte[] utf8z, [MarshalAs(UnmanagedType.I1)] bool interrupt);
    [DllImport(Lib)] internal static extern void dm_speech_stop();
    [DllImport(Lib)] internal static extern void dm_speech_set_rate(float fraction);
    [DllImport(Lib)] internal static extern int dm_speech_set_voice(byte[] utf8z);

    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool dm_accessibility_trusted([MarshalAs(UnmanagedType.I1)] bool prompt);

    /// <summary>callback(keycode, down, flags, autorepeat) returns nonzero to swallow the key.</summary>
    [DllImport(Lib)] internal static extern int dm_keytap_start(delegate* unmanaged<int, int, ulong, int, int> callback);
    [DllImport(Lib)] internal static extern void dm_keytap_stop();

    [DllImport(Lib)] internal static extern int dm_frontmost_pid();

    /// <summary>Main thread only. Runs until dm_main_loop_stop, or for that many seconds when positive.</summary>
    [DllImport(Lib)] internal static extern void dm_main_loop_run(double seconds);
    [DllImport(Lib)] internal static extern void dm_main_loop_stop();

    /// <summary>
    /// Blocks the main thread the way AVSpeech needs it blocked: servicing the main run loop.
    /// Without it only the first utterance of the session is ever heard.
    /// </summary>
    internal static void WaitOnMainLoop(ManualResetEventSlim done)
    {
        new Thread(() => { done.Wait(); dm_main_loop_stop(); }) { IsBackground = true, Name = "DiceyA11y exit wait" }.Start();
        dm_main_loop_run(0);
    }

    [DllImport("libc")] internal static extern uint getuid();
}
