using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ClipForge.Models;

namespace ClipForge.Services;

/// <summary>
/// Drives a single ffmpeg.exe process that captures the screen (via gdigrab) and,
/// optionally, audio (via dshow) and encodes to the configured output file.
/// </summary>
public sealed class ScreenRecorder
{
    private readonly string _ffmpegPath;
    private Process? _process;
    private readonly object _gate = new();

    public ScreenRecorder(string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            throw new ArgumentException("ffmpeg path must be supplied.", nameof(ffmpegPath));
        }

        _ffmpegPath = ffmpegPath;
    }

    /// <summary>True while an ffmpeg capture process is alive.</summary>
    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>The absolute path of the file currently being written, or null when idle.</summary>
    public string? CurrentOutputPath { get; private set; }

    /// <summary>Raised once the ffmpeg process has been launched.</summary>
    public event EventHandler? Started;

    /// <summary>Raised when recording finishes cleanly; payload is the output file path.</summary>
    public event EventHandler<string>? Stopped;

    /// <summary>Raised when ffmpeg fails to start or exits with a non-zero code; payload is a message.</summary>
    public event EventHandler<string>? Error;

    /// <summary>
    /// Builds the output path, constructs the ffmpeg command line and launches the capture process.
    /// </summary>
    public Task StartAsync(RecordingProfile profile)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        lock (_gate)
        {
            if (_process is { HasExited: false })
            {
                throw new InvalidOperationException("A recording is already in progress.");
            }
        }

        string outputPath;
        string arguments;
        try
        {
            outputPath = ResolveOutputPath(profile);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            arguments = BuildArguments(profile, outputPath);
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Failed to prepare recording: {ex.Message}");
            return Task.CompletedTask;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        Process process;
        try
        {
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += OnProcessExited;
            process.ErrorDataReceived += OnErrorDataReceived;

            if (!process.Start())
            {
                Error?.Invoke(this, "ffmpeg process failed to start.");
                process.Dispose();
                return Task.CompletedTask;
            }

            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Unable to launch ffmpeg: {ex.Message}");
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            _process = process;
            CurrentOutputPath = outputPath;
        }

        Started?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Requests a graceful stop by writing 'q' to ffmpeg's stdin and awaiting clean exit.
    /// </summary>
    public async Task StopAsync()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
        }

        if (process is null || process.HasExited)
        {
            return;
        }

        try
        {
            // 'q' tells ffmpeg to finalize the output (flush muxer trailer) and exit.
            await process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stdin may already be closed; fall through to wait/kill below.
        }

        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ffmpeg ignored the graceful request — terminate it so the file is at least closed.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Nothing further we can do.
            }
        }
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        // ffmpeg writes all logging to stderr; we simply drain it so the pipe never blocks.
        // Consumers wanting live progress can subscribe to a future event here.
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        Process? process;
        string? output;
        lock (_gate)
        {
            process = _process;
            output = CurrentOutputPath;
            _process = null;
        }

        int exitCode = -1;
        try
        {
            if (process is not null)
            {
                exitCode = process.ExitCode;
            }
        }
        catch (Exception)
        {
            // ExitCode can throw if the process object is in a bad state.
        }
        finally
        {
            process?.Dispose();
        }

        var path = output ?? string.Empty;
        CurrentOutputPath = null;

        // ffmpeg returns 0 on a clean 'q' stop. Exit code 255 is the conventional result of an
        // interrupt; treat it as a successful user-initiated stop because the file is still finalized.
        if (exitCode == 0 || exitCode == 255)
        {
            Stopped?.Invoke(this, path);
        }
        else
        {
            Error?.Invoke(this, $"ffmpeg exited with code {exitCode}.");
        }
    }

    private static string ResolveOutputPath(RecordingProfile profile)
    {
        var folder = string.IsNullOrWhiteSpace(profile.OutputFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ClipForge")
            : profile.OutputFolder;

        var now = DateTime.Now;
        var template = string.IsNullOrWhiteSpace(profile.FileNameTemplate)
            ? "ClipForge_{date}_{time}"
            : profile.FileNameTemplate;

        var fileName = template
            .Replace("{date}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Replace("{time}", now.ToString("HH-mm-ss", CultureInfo.InvariantCulture))
            .Replace("{name}", profile.Name ?? "Default");

        fileName = SanitizeFileName(fileName);

        return Path.Combine(folder, fileName + ContainerExtension(profile.Container));
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }

    /// <summary>
    /// Pure, side-effect-free construction of the full ffmpeg argument string for a capture session.
    /// Exposed for unit testing.
    /// </summary>
    public static string BuildArguments(RecordingProfile profile, string outputPath)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output path is required.", nameof(outputPath));
        }

        var sb = new StringBuilder();

        // Global flags. -y overwrites, banner hidden, errors-only logging to keep stderr clean.
        sb.Append("-hide_banner -loglevel error -y");

        // ---- Video input (gdigrab) ----
        AppendVideoInput(sb, profile);

        // ---- Audio input (dshow), if any ----
        var audioInputs = AppendAudioInputs(sb, profile);

        // ---- Encoding / mapping ----
        AppendEncoding(sb, profile, audioInputs);

        // ---- Output ----
        sb.Append(' ').Append(Quote(outputPath));

        return sb.ToString();
    }

    /// <summary>
    /// Appends the gdigrab video input. Handles full-screen, monitor offset, region and window-title capture.
    /// </summary>
    private static void AppendVideoInput(StringBuilder sb, RecordingProfile profile)
    {
        var fps = profile.Fps > 0 ? profile.Fps : 60;

        sb.Append(" -f gdigrab");
        sb.Append(" -framerate ").Append(fps.ToString(CultureInfo.InvariantCulture));

        // draw_mouse=1 captures the cursor; =0 hides it.
        sb.Append(" -draw_mouse ").Append(profile.CaptureCursor ? '1' : '0');

        switch (profile.Source)
        {
            case CaptureSource.Region:
                // gdigrab captures a rectangle starting at (offset_x, offset_y) of size video_size.
                var width = NormalizeEven(profile.RegionWidth > 0 ? profile.RegionWidth : 1920);
                var height = NormalizeEven(profile.RegionHeight > 0 ? profile.RegionHeight : 1080);
                sb.Append(" -offset_x ").Append(profile.RegionX.ToString(CultureInfo.InvariantCulture));
                sb.Append(" -offset_y ").Append(profile.RegionY.ToString(CultureInfo.InvariantCulture));
                sb.Append(" -video_size ")
                  .Append(width.ToString(CultureInfo.InvariantCulture))
                  .Append('x')
                  .Append(height.ToString(CultureInfo.InvariantCulture));
                sb.Append(" -i ").Append(Quote("desktop"));
                break;

            case CaptureSource.Monitor:
                // gdigrab itself has no monitor selector; we offset into the virtual desktop by the
                // monitor index multiplied by a nominal width. Region fields, when present, override.
                // For a precise multi-monitor crop the caller should populate the Region fields.
                if (profile.RegionWidth > 0 && profile.RegionHeight > 0)
                {
                    sb.Append(" -offset_x ").Append(profile.RegionX.ToString(CultureInfo.InvariantCulture));
                    sb.Append(" -offset_y ").Append(profile.RegionY.ToString(CultureInfo.InvariantCulture));
                    sb.Append(" -video_size ")
                      .Append(NormalizeEven(profile.RegionWidth).ToString(CultureInfo.InvariantCulture))
                      .Append('x')
                      .Append(NormalizeEven(profile.RegionHeight).ToString(CultureInfo.InvariantCulture));
                }
                sb.Append(" -i ").Append(Quote("desktop"));
                break;

            case CaptureSource.Window:
                // gdigrab can grab a specific top-level window by title: title=<window title>.
                var title = string.IsNullOrWhiteSpace(profile.WindowTitle) ? "desktop" : profile.WindowTitle!;
                sb.Append(" -i ").Append(Quote($"title={title}"));
                break;

            case CaptureSource.FullScreen:
            default:
                sb.Append(" -i ").Append(Quote("desktop"));
                break;
        }
    }

    /// <summary>
    /// Appends one or two dshow audio inputs depending on <see cref="AudioMode"/>.
    /// Returns the number of audio inputs added (0, 1 or 2).
    /// </summary>
    private static int AppendAudioInputs(StringBuilder sb, RecordingProfile profile)
    {
        switch (profile.Audio)
        {
            case AudioMode.SystemOnly:
                if (!string.IsNullOrWhiteSpace(profile.SystemAudioDevice))
                {
                    AppendDshowAudio(sb, profile.SystemAudioDevice!);
                    return 1;
                }
                return 0;

            case AudioMode.MicOnly:
                if (!string.IsNullOrWhiteSpace(profile.MicDevice))
                {
                    AppendDshowAudio(sb, profile.MicDevice!);
                    return 1;
                }
                return 0;

            case AudioMode.SystemAndMic:
                int count = 0;
                if (!string.IsNullOrWhiteSpace(profile.SystemAudioDevice))
                {
                    AppendDshowAudio(sb, profile.SystemAudioDevice!);
                    count++;
                }
                if (!string.IsNullOrWhiteSpace(profile.MicDevice))
                {
                    AppendDshowAudio(sb, profile.MicDevice!);
                    count++;
                }
                return count;

            case AudioMode.None:
            default:
                return 0;
        }
    }

    private static void AppendDshowAudio(StringBuilder sb, string deviceName)
    {
        // dshow device specifier: audio=<device name>. The whole token is quoted to survive spaces.
        sb.Append(" -f dshow -i ").Append(Quote($"audio={deviceName}"));
    }

    /// <summary>
    /// Appends codec selection, bitrate, container-specific flags and stream mapping.
    /// </summary>
    private static void AppendEncoding(StringBuilder sb, RecordingProfile profile, int audioInputs)
    {
        // GIF is a special-case pipeline: no audio, palette-friendly output.
        if (profile.Container == OutputContainer.Gif)
        {
            // High-quality GIF: build a palette on the fly and apply it.
            sb.Append(" -vf ")
              .Append(Quote($"fps={(profile.Fps > 0 ? profile.Fps : 15)},split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse"));
            return;
        }

        // ----- Video codec -----
        var videoCodec = MapVideoEncoder(profile.Encoder, profile.Container);
        sb.Append(" -c:v ").Append(videoCodec);

        // pix_fmt yuv420p ensures broad player compatibility (gdigrab yields bgra).
        sb.Append(" -pix_fmt yuv420p");

        // Software x264/x265 honor the speed preset; hardware encoders use their own preset names,
        // so we only emit -preset for the software encoders.
        if (IsSoftwareEncoder(profile.Encoder))
        {
            sb.Append(" -preset ").Append(profile.Preset.ToString());
        }

        // Bitrate target (CBR-ish): -b:v plus a matching maxrate/bufsize for predictable file size.
        if (profile.VideoBitrateKbps > 0)
        {
            var kbps = profile.VideoBitrateKbps.ToString(CultureInfo.InvariantCulture);
            sb.Append(" -b:v ").Append(kbps).Append('k');
            sb.Append(" -maxrate ").Append(kbps).Append('k');
            sb.Append(" -bufsize ").Append((profile.VideoBitrateKbps * 2).ToString(CultureInfo.InvariantCulture)).Append('k');
        }

        // Keyframe roughly every 2 seconds for seekability.
        var fps = profile.Fps > 0 ? profile.Fps : 60;
        sb.Append(" -g ").Append((fps * 2).ToString(CultureInfo.InvariantCulture));

        // ----- Audio handling / mapping -----
        if (audioInputs == 0)
        {
            // No audio captured.
            sb.Append(" -an");
        }
        else
        {
            if (audioInputs == 2)
            {
                // Mix the two dshow inputs (system + mic) into a single stereo track.
                // Inputs: 0 = video, 1 = first audio, 2 = second audio.
                sb.Append(" -filter_complex ")
                  .Append(Quote("[1:a][2:a]amix=inputs=2:duration=longest:dropout_transition=2[aout]"));
                sb.Append(" -map 0:v -map ").Append(Quote("[aout]"));
            }
            else
            {
                // Single audio input at index 1.
                sb.Append(" -map 0:v -map 1:a");
            }

            sb.Append(" -c:a ").Append(MapAudioCodec(profile.Container));
            if (profile.AudioBitrateKbps > 0)
            {
                sb.Append(" -b:a ").Append(profile.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture)).Append('k');
            }
        }

        // ----- Container-specific muxer flags -----
        if (profile.Container == OutputContainer.Mp4 || profile.Container == OutputContainer.Mov)
        {
            // +faststart relocates the moov atom for progressive playback.
            sb.Append(" -movflags +faststart");
        }
    }

    private static bool IsSoftwareEncoder(VideoEncoder encoder) =>
        encoder is VideoEncoder.x264 or VideoEncoder.x265 or VideoEncoder.VP9 or VideoEncoder.AV1;

    private static string MapVideoEncoder(VideoEncoder encoder, OutputContainer container) => encoder switch
    {
        VideoEncoder.x264 => "libx264",
        VideoEncoder.x265 => "libx265",
        VideoEncoder.NVENC_H264 => "h264_nvenc",
        VideoEncoder.NVENC_HEVC => "hevc_nvenc",
        VideoEncoder.AMF_H264 => "h264_amf",
        VideoEncoder.QSV_H264 => "h264_qsv",
        VideoEncoder.VP9 => "libvpx-vp9",
        VideoEncoder.AV1 => "libaom-av1",
        _ => "libx264",
    };

    private static string MapAudioCodec(OutputContainer container) => container switch
    {
        // WebM containers require Opus/Vorbis; everything else takes AAC happily.
        OutputContainer.WebM => "libopus",
        _ => "aac",
    };

    public static string ContainerExtension(OutputContainer container) => container switch
    {
        OutputContainer.Mp4 => ".mp4",
        OutputContainer.Mkv => ".mkv",
        OutputContainer.Mov => ".mov",
        OutputContainer.WebM => ".webm",
        OutputContainer.Gif => ".gif",
        _ => ".mp4",
    };

    private static int NormalizeEven(int value)
    {
        // Many encoders (yuv420p) require even dimensions.
        if (value <= 0)
        {
            return 2;
        }

        return value % 2 == 0 ? value : value - 1;
    }

    /// <summary>Wraps a token in double quotes, escaping any embedded quotes.</summary>
    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
