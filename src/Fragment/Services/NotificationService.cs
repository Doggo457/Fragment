using System;
using System.Collections.Generic;
using System.Windows;
using Fragment.Views;

namespace Fragment.Services;

/// <summary>
/// Shows transient "toast" notifications at the top-left of the primary monitor
/// when a clip or recording is saved, so the user gets immediate feedback without
/// hunting through a folder. Toasts stack downward and are safe to raise from any
/// thread (work is marshalled onto the UI dispatcher).
/// </summary>
public static class NotificationService
{
    // Inset from the work-area corner and spacing between stacked toasts (DIPs).
    private const double Margin = 16;
    private const double Gap = 6;
    private const int MaxVisible = 4;

    private static readonly List<ToastWindow> Active = new();

    /// <summary>Announce that an instant-replay or trimmed clip was saved.</summary>
    public static void ShowClipSaved(string filePath) => Show("Clip saved", filePath);

    /// <summary>Announce that a finished recording was saved.</summary>
    public static void ShowRecordingSaved(string filePath) => Show("Recording saved", filePath);

    private static void Show(string title, string filePath)
    {
        var app = Application.Current;
        if (app == null) return;

        if (app.Dispatcher.CheckAccess())
        {
            ShowOnUiThread(title, filePath);
        }
        else
        {
            app.Dispatcher.BeginInvoke(new Action(() => ShowOnUiThread(title, filePath)));
        }
    }

    private static void ShowOnUiThread(string title, string filePath)
    {
        // Cap the stack: drop the oldest if we're already at the limit.
        while (Active.Count >= MaxVisible)
        {
            Active[0].BeginClose();
            Active.RemoveAt(0);
        }

        var toast = new ToastWindow(title, filePath);
        toast.Closed += (_, _) =>
        {
            Active.Remove(toast);
            Reflow();
        };

        Active.Add(toast);
        Reflow();
        toast.Show();
    }

    /// <summary>Re-stack the currently visible toasts from the top-left corner downward.</summary>
    private static void Reflow()
    {
        // SystemParameters.WorkArea is the primary monitor's work area in DIPs,
        // already excluding the taskbar — exactly what we want for a top-left toast.
        var wa = SystemParameters.WorkArea;

        // The window is larger than its visible card by ShadowMargin on every side
        // (transparent space reserved for the drop shadow), so offset the outer bounds
        // by -ShadowMargin to make the visible card sit at the intended inset, and
        // advance by the *visible* card height when stacking.
        double left = wa.Left + Margin - ToastWindow.ShadowMargin;
        double y = wa.Top + Margin - ToastWindow.ShadowMargin;

        foreach (var toast in Active)
        {
            toast.Left = left;
            toast.Top = y;
            y += toast.Height - (2 * ToastWindow.ShadowMargin) + Gap;
        }
    }
}
