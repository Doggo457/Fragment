using System.Runtime.InteropServices;

namespace ClipForge.Utils;

/// <summary>
/// P/Invoke declarations for the Win32 global hotkey API and related constants.
/// </summary>
internal static class NativeMethods
{
    // ----- Modifier flags for RegisterHotKey (fsModifiers) -----
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    // ----- Window message raised when a registered hotkey fires -----
    public const int WM_HOTKEY = 0x0312;

    /// <summary>
    /// Registers a system-wide hotkey. Returns false if the combination is
    /// already taken by another application.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    /// <summary>
    /// Releases a previously registered hotkey identified by <paramref name="id"/>.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
