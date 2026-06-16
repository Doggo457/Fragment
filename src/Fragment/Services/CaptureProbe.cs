using System;
using System.Diagnostics;
using Fragment.Utils;

namespace Fragment.Services;

/// <summary>
/// Runtime check for whether the GPU Desktop Duplication backend (ddagrab) can actually capture
/// right now. It can't see exclusive-fullscreen games or DRM-protected content, so when it can't,
/// callers transparently fall back to the gdigrab (GDI) path instead of recording a black screen.
/// </summary>
internal static class CaptureProbe
{
    /// <summary>
    /// Runs a tiny two-frame ddagrab capture and reports whether it succeeded. Exit code 0 means
    /// Desktop Duplication is available for the primary output at this instant; any non-zero exit
    /// (or a hang) means it isn't (game in exclusive fullscreen, no DDA support, protected output).
    /// Takes roughly 0.2s.
    /// </summary>
    public static bool IsDesktopDuplicationWorking(string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -loglevel error -f lavfi -i ddagrab=output_idx=0:framerate=30 -frames:v 2 -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            ChildProcessTracker.Track(p); // never outlive the app, even if it hangs
            p.BeginErrorReadLine();        // drain pipes so the child can't block
            p.BeginOutputReadLine();

            if (!p.WaitForExit(4000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return p.ExitCode == 0;
        }
        catch
        {
            return false; // any failure: use the safe gdigrab path
        }
    }
}
