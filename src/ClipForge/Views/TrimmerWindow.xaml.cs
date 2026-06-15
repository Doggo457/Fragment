using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using ClipForge.Services;
using ClipForge.Utils;
using Microsoft.Win32;

namespace ClipForge.Views;

/// <summary>
/// Code-behind for the trimmer dialog. Hosts a <see cref="TrimmerViewModel"/>
/// that drives a <see cref="ClipTrimmer"/> to cut a sub-range out of a video.
/// </summary>
public partial class TrimmerWindow : Window
{
    public TrimmerWindow(ClipTrimmer trimmer, string? initialInputPath = null)
    {
        InitializeComponent();
        var vm = new TrimmerViewModel(trimmer, this) { InputPath = initialInputPath ?? string.Empty };
        DataContext = vm;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// View model behind <see cref="TrimmerWindow"/>. Validates the start/end fields,
/// derives an output path, and invokes <see cref="ClipTrimmer.TrimAsync"/>.
/// </summary>
internal sealed class TrimmerViewModel : INotifyPropertyChanged
{
    private readonly ClipTrimmer _trimmer;
    private readonly Window _owner;

    private string _inputPath = string.Empty;
    private string _startText = "00:00:00";
    private string _endText = "00:00:10";
    private bool _reEncode;
    private string _statusText = "Select an input file and a start/end range.";
    private bool _isBusy;

    public TrimmerViewModel(ClipTrimmer trimmer, Window owner)
    {
        _trimmer = trimmer ?? throw new ArgumentNullException(nameof(trimmer));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        BrowseInputCommand = new RelayCommand(_ => BrowseInput(), _ => !_isBusy);
        TrimCommand = new RelayCommand(async _ => await TrimAsync(), _ => CanTrim());
    }

    public RelayCommand BrowseInputCommand { get; }
    public RelayCommand TrimCommand { get; }

    public string InputPath
    {
        get => _inputPath;
        set { if (_inputPath != value) { _inputPath = value; OnPropertyChanged(); RaiseCommands(); } }
    }

    public string StartText
    {
        get => _startText;
        set { if (_startText != value) { _startText = value; OnPropertyChanged(); RaiseCommands(); } }
    }

    public string EndText
    {
        get => _endText;
        set { if (_endText != value) { _endText = value; OnPropertyChanged(); RaiseCommands(); } }
    }

    public bool ReEncode
    {
        get => _reEncode;
        set { if (_reEncode != value) { _reEncode = value; OnPropertyChanged(); } }
    }

    public string StatusText
    {
        get => _statusText;
        set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
    }

    private bool CanTrim()
    {
        if (_isBusy)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(InputPath) || !File.Exists(InputPath))
        {
            return false;
        }

        if (!TryParseTimeSpan(StartText, out var start) || !TryParseTimeSpan(EndText, out var end))
        {
            return false;
        }

        return end > start;
    }

    private void RaiseCommands()
    {
        BrowseInputCommand.RaiseCanExecuteChanged();
        TrimCommand.RaiseCanExecuteChanged();
    }

    private void BrowseInput()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a video to trim",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.flv|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(_owner) == true)
        {
            InputPath = dialog.FileName;
        }
    }

    private async Task TrimAsync()
    {
        if (!TryParseTimeSpan(StartText, out var start) || !TryParseTimeSpan(EndText, out var end))
        {
            StatusText = "Start/end must be in hh:mm:ss(.fff) format.";
            return;
        }

        if (end <= start)
        {
            StatusText = "End must be greater than start.";
            return;
        }

        var outputPath = BuildOutputPath(InputPath, _reEncode);

        try
        {
            SetBusy(true);
            StatusText = "Trimming…";
            var result = await _trimmer.TrimAsync(InputPath, start, end, outputPath, _reEncode);
            StatusText = $"Saved: {result}";
        }
        catch (Exception ex)
        {
            StatusText = $"Trim failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        RaiseCommands();
    }

    /// <summary>Derives an output path next to the input, suffixed with "_trim".</summary>
    private static string BuildOutputPath(string inputPath, bool reEncode)
    {
        var dir = Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory();
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);
        if (string.IsNullOrEmpty(ext))
        {
            ext = ".mp4";
        }

        var candidate = Path.Combine(dir, $"{name}_trim{ext}");
        var index = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{name}_trim_{index}{ext}");
            index++;
        }

        return candidate;
    }

    /// <summary>
    /// Parses "hh:mm:ss", "mm:ss", "ss", or a fractional-seconds variant into a
    /// <see cref="TimeSpan"/>. Returns false on malformed input.
    /// </summary>
    private static bool TryParseTimeSpan(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();

        // Plain seconds (possibly fractional), e.g. "12" or "12.5".
        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            && !text.Contains(':'))
        {
            value = TimeSpan.FromSeconds(seconds);
            return value >= TimeSpan.Zero;
        }

        var parts = text.Split(':');
        try
        {
            switch (parts.Length)
            {
                case 2:
                    {
                        // mm:ss(.fff)
                        var m = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                        var s = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                        value = TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s);
                        return value >= TimeSpan.Zero;
                    }
                case 3:
                    {
                        // hh:mm:ss(.fff)
                        var h = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                        var m = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                        var s = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                        value = TimeSpan.FromHours(h) + TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s);
                        return value >= TimeSpan.Zero;
                    }
                default:
                    return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
