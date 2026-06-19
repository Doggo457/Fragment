using System;
using System.Threading.Tasks;
using Fragment.Models;

namespace Fragment.Services;

/// <summary>
/// Common surface for an always-on instant-replay buffer so the view-model can drive either the
/// ffmpeg-segment <see cref="ReplayBufferService"/> or the in-process GPU encoded-sample ring the same way.
/// </summary>
public interface IReplayBuffer
{
    /// <summary>True while the buffer is actively capturing.</summary>
    bool IsRunning { get; }

    /// <summary>Raised if the buffer stops on its own (crash / device loss) without Stop() being called.</summary>
    event EventHandler? Stopped;

    /// <summary>Starts continuous buffering sized to retain at least <paramref name="bufferSeconds"/> seconds.</summary>
    void Start(RecordingProfile profile, int bufferSeconds);

    /// <summary>Stops buffering and releases all resources.</summary>
    void Stop();

    /// <summary>
    /// Writes the most recent <paramref name="seconds"/> of buffered footage to <paramref name="outputPath"/>.
    /// Returns the output path on success, or null if no footage is available.
    /// </summary>
    Task<string?> SaveClipAsync(int seconds, string outputPath);
}
