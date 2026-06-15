using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Fragment.Views;

/// <summary>
/// A small borderless "toast" that pops up at the top-left of the primary monitor
/// when a clip or recording is saved. It fades/slides in, auto-dismisses after a
/// few seconds (pausing while hovered), and opens the file's folder when clicked.
/// Positioning and stacking are handled by <see cref="Services.NotificationService"/>.
/// </summary>
public partial class ToastWindow : Window
{
    /// <summary>
    /// Transparent inset (DIPs) between the window's outer bounds and the visible card,
    /// reserved for the drop shadow. Must match the <c>Root</c> border's Margin in XAML.
    /// <see cref="Services.NotificationService"/> uses it so the visible card lands at the
    /// intended corner inset and stacks tightly.
    /// </summary>
    public const double ShadowMargin = 12;

    private readonly string _filePath;
    private readonly DispatcherTimer _dismiss;
    private bool _closing;

    public ToastWindow(string title, string filePath)
    {
        InitializeComponent();

        _filePath = filePath ?? string.Empty;
        TitleText.Text = title;
        FileText.Text = string.IsNullOrEmpty(_filePath) ? string.Empty : Path.GetFileName(_filePath);
        ToolTip = _filePath;

        _dismiss = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        _dismiss.Tick += (_, _) => BeginClose();

        Loaded += (_, _) => _dismiss.Start();
        // Pause the countdown while the user is reading / about to click.
        MouseEnter += (_, _) => _dismiss.Stop();
        MouseLeave += (_, _) =>
        {
            if (_closing) return;
            _dismiss.Interval = TimeSpan.FromSeconds(2);
            _dismiss.Start();
        };
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        OpenLocation();
        BeginClose();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // Don't also treat the click as "open the folder".
        e.Handled = true;
        BeginClose();
    }

    /// <summary>Reveal the saved file in Explorer (selected), falling back to its folder.</summary>
    private void OpenLocation()
    {
        if (string.IsNullOrEmpty(_filePath)) return;

        try
        {
            if (File.Exists(_filePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_filePath}\"")
                {
                    UseShellExecute = true
                });
                return;
            }

            var folder = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")
                {
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Opening Explorer is best-effort; never let it crash the toast.
        }
    }

    /// <summary>Play the exit animation, then close.</summary>
    public void BeginClose()
    {
        if (_closing) return;
        _closing = true;
        _dismiss.Stop();

        var sb = new Storyboard();

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));

        var slide = new DoubleAnimation(-24, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(slide, Slide);
        Storyboard.SetTargetProperty(slide, new PropertyPath(TranslateTransform.XProperty));

        sb.Children.Add(fade);
        sb.Children.Add(slide);
        sb.Completed += (_, _) => Close();
        sb.Begin();
    }
}
