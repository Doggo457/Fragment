using System.Windows;
using System.Windows.Threading;

namespace Fragment;

/// <summary>
/// Application entry point. Wires up global exception handling so that an
/// unhandled error surfaces to the user rather than silently terminating
/// the process.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Headless GPU-engine self-test: run, write a log, and exit without showing the UI.
        var gpuTest = Environment.GetEnvironmentVariable("FRAGMENT_GPUTEST");
        if (!string.IsNullOrEmpty(gpuTest))
        {
            Fragment.Services.Encoding.GpuSelfTest.Run(gpuTest);
            Shutdown(0);
            return;
        }

        // Headless UI render (dev only): render a window to a PNG so the design can be iterated visually.
        var render = Environment.GetEnvironmentVariable("FRAGMENT_RENDER");
        if (!string.IsNullOrEmpty(render))
        {
            RenderToPng(render);
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        // Surface unhandled UI-thread exceptions instead of crashing silently.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    // Render a window's visual tree to a PNG without showing it (no display needed) for design iteration.
    private void RenderToPng(string which)
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Fragment", $"render_{which}.png");
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath)!);
            Window w = which switch
            {
                "settings" => new Views.SettingsWindow(new ViewModels.SettingsViewModel(Services.SettingsService.Load())),
                _ => new MainWindow(),
            };

            double width = w.Width > 0 ? w.Width : 460;
            double height = w.Height > 0 ? w.Height : 560;

            // Wrap the content so the window background brush is included in the bitmap.
            var content = w.Content as System.Windows.FrameworkElement;
            w.Content = null;
            var root = new System.Windows.Controls.Border
            {
                Background = w.Background,
                Width = width,
                Height = height,
                Child = content,
                DataContext = w.DataContext, // preserve bindings after reparenting
            };
            var sz = new System.Windows.Size(width, height);
            root.Measure(sz);
            root.Arrange(new System.Windows.Rect(sz));
            root.UpdateLayout();

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)width, (int)height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = System.IO.File.Create(outPath);
            enc.Save(fs);
        }
        catch (Exception ex)
        {
            try { System.IO.File.WriteAllText(outPath + ".err.txt", ex.ToString()); } catch { }
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}",
            "Fragment",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Keep the app alive; the user can decide whether to continue.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            MessageBox.Show(
                $"A fatal error occurred:\n\n{ex.Message}",
                "Fragment",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
