using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Fragment.Services;

/// <summary>Applies the Windows dark title bar to windows that keep the OS chrome (settings/editor dialogs).</summary>
public static class NativeTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;       // Windows 10 1903+ / 11
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;   // Windows 10 1809

    public static void ApplyDarkTitleBar(Window window)
    {
        void Apply()
        {
            IntPtr h = new WindowInteropHelper(window).Handle;
            if (h == IntPtr.Zero) return;
            int on = 1;
            if (DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply();
        else window.SourceInitialized += (_, _) => Apply();
    }
}
