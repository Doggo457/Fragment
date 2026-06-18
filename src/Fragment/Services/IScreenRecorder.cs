using System;
using System.Threading.Tasks;
using Fragment.Models;

namespace Fragment.Services;

/// <summary>
/// Common surface for a direct-recording engine so the view-model can drive either the ffmpeg-based
/// <see cref="ScreenRecorder"/> or the in-process GPU <see cref="Encoding.GpuScreenRecorder"/> the same way.
/// </summary>
public interface IScreenRecorder
{
    /// <summary>True while a capture is active.</summary>
    bool IsRecording { get; }

    /// <summary>Absolute path of the file currently being written, or null when idle.</summary>
    string? CurrentOutputPath { get; }

    /// <summary>Raised once capture has started.</summary>
    event EventHandler? Started;

    /// <summary>Raised when recording finishes cleanly; payload is the output file path.</summary>
    event EventHandler<string>? Stopped;

    /// <summary>Raised on failure; payload is a message.</summary>
    event EventHandler<string>? Error;

    Task StartAsync(RecordingProfile profile);
    Task StopAsync();
}
