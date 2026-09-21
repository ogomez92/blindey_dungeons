using System.Runtime.InteropServices;
using System.Text;

namespace DiceyA11y.Speech;

/// <summary>
/// Raw P/Invoke surface for prism.dll (Prism SDK 0.18.2).
///
/// Facts taken from prism.h, not guessed:
///  - calling convention is cdecl
///  - strings are null-terminated UTF-8, caller-owned, only need to outlive the call
///  - C bool is one byte, so it must be marshalled as UnmanagedType.I1
///  - prism_init takes a PrismConfig*; NULL is valid and uses the global registry
///  - prism_registry_create_best returns an already-initialized backend
///  - strings returned by the library are library-owned and must never be freed
///  - PrismError 0 == PRISM_OK
/// </summary>
internal static class PrismNative
{
    private const string Dll = "prism";

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    /// <summary>
    /// Pre-loads prism.dll by absolute path. The process working directory is the
    /// game folder, not ours, so relying on the default search order is not safe.
    /// </summary>
    internal static bool Load(string directory, out string detail)
    {
        var path = Path.Combine(directory, "prism.dll");
        if (!File.Exists(path))
        {
            detail = $"prism.dll not found at {path}";
            return false;
        }

        var handle = LoadLibraryW(path);
        if (handle == IntPtr.Zero)
        {
            detail = $"LoadLibrary failed for {path} (win32 error {Marshal.GetLastWin32Error()})";
            return false;
        }

        detail = path;
        return true;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr prism_init(IntPtr cfg);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void prism_shutdown(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr prism_registry_create_best(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void prism_backend_free(IntPtr backend);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr prism_backend_name(IntPtr backend);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong prism_backend_get_features(IntPtr backend);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int prism_backend_speak(IntPtr backend, byte[] utf8,
        [MarshalAs(UnmanagedType.I1)] bool interrupt);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int prism_backend_output(IntPtr backend, byte[] utf8,
        [MarshalAs(UnmanagedType.I1)] bool interrupt);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int prism_backend_braille(IntPtr backend, byte[] utf8);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int prism_backend_stop(IntPtr backend);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr prism_error_string(int error);

    // PrismBackendFeature bits we care about (prism.h).
    internal const ulong FeatureSpeak   = 1UL << 2;
    internal const ulong FeatureBraille = 1UL << 4;
    internal const ulong FeatureOutput  = 1UL << 5;
    internal const ulong FeatureStop    = 1UL << 7;

    internal const int PrismOk = 0;

    /// <summary>UTF-8 encodes with an explicit trailing NUL byte.</summary>
    internal static byte[] Utf8Z(string s)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
        return bytes; // the final byte is already zero
    }

    /// <summary>Reads a library-owned UTF-8 string. Never frees it.</summary>
    internal static string PtrToString(IntPtr p)
        => p == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUTF8(p) ?? string.Empty);

    internal static string ErrorText(int error)
        => PtrToString(prism_error_string(error)) is { Length: > 0 } s ? s : $"error {error}";
}
