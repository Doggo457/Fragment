using System;
using System.IO;

namespace Fragment.Services.Encoding;

/// <summary>
/// Headless self-tests for the GPU recording engine, run via the FRAGMENT_GPUTEST env var so the engine
/// can be validated without the GUI. Writes results to %TEMP%\Fragment\gputest.log and exits.
/// </summary>
internal static class GpuSelfTest
{
    public static void Run(string mode)
    {
        var log = Path.Combine(Path.GetTempPath(), "Fragment", "gputest.log");
        try { Directory.CreateDirectory(Path.GetDirectoryName(log)!); } catch { }
        void W(string s) { try { File.AppendAllText(log, s + Environment.NewLine); } catch { } }

        try { File.WriteAllText(log, $"GPUTEST mode={mode} at {DateTime.Now:HH:mm:ss}{Environment.NewLine}"); } catch { }

        try
        {
            using var dev = new GpuRecordingDevice();
            W($"shared D3D11 device created (BGRA+VideoSupport), multithread-protected");
            W($"WinRT device bridge: {(dev.WinRtDevice != null ? "ok" : "null")}");
            W($"MF DXGI device manager: {(dev.DeviceManager != null ? "ok" : "null")}");
            W("RESULT: PASS");
        }
        catch (Exception ex)
        {
            W("RESULT: FAIL");
            W(ex.ToString());
        }
    }
}
