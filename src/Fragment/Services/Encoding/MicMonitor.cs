using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Fragment.Services.Encoding;

/// <summary>
/// Live microphone monitor for tuning the noise gate / suppression by ear. Captures the selected mic and
/// runs the SAME cleanup chain the recorder uses (mono → spectral suppression → noise gate), then plays
/// the processed result out the default render device so you can hear exactly what will be recorded.
///
/// Gate threshold, suppression strength and the on/off toggles can be changed live (the chain is not
/// rebuilt, so there are no clicks), and a post-suppression / pre-gate level meter shows where speech
/// sits relative to the gate threshold. Intended for headphones — speaker output can feed back into the
/// mic. Mic input feeds a thread-safe buffer (push); a separate playback thread pulls it through the DSP.
/// </summary>
public sealed class MicMonitor : IDisposable
{
    private WasapiCapture? _capture;
    private IWavePlayer? _out;
    private MMDevice? _renderDev;              // render endpoint backing _out; WasapiOut does NOT dispose it
    private BufferedWaveProvider? _buf;
    private SpectralNoiseSuppressor? _supp;
    private NoiseGateSampleProvider? _gate;
    private LevelMeterSampleProvider? _meter;
    private bool _disposed;

    public bool Active { get; private set; }

    /// <summary>Post-suppression, pre-gate peak level in dBFS — what the gate compares to its threshold.</summary>
    public float LevelDb => _meter?.LevelDb ?? -100f;

    public MicMonitor(string? micDeviceName, MicProcessing proc)
    {
        try
        {
            // Open the capture endpoint; the resolved MMDevice can be disposed once WasapiCapture has it.
            MMDevice? dev = ResolveMic(micDeviceName);
            try
            {
                try { _capture = dev != null ? new WasapiCapture(dev) : new WasapiCapture(); }
                catch { _capture = new WasapiCapture(); } // selected device unavailable → default mic
            }
            finally { try { dev?.Dispose(); } catch { } }

            _buf = new BufferedWaveProvider(_capture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,            // never block the capture thread
                BufferDuration = TimeSpan.FromSeconds(2),
                ReadFully = true,                          // return silence on underrun so playback never stalls
            };
            _capture.DataAvailable += (_, e) => _buf?.AddSamples(e.Buffer, 0, e.BytesRecorded);

            // Same order as the recorder: mono → spectral suppression → (meter tap) → noise gate.
            // Down-mix any channel count to mono so the suppressor's mono precondition always holds.
            ISampleProvider sp = _buf.ToSampleProvider();
            if (sp.WaveFormat.Channels != 1) sp = new DownmixToMonoSampleProvider(sp);
            _supp = new SpectralNoiseSuppressor(sp, proc.SuppressStrength) { Enabled = proc.SuppressEnabled };
            sp = _supp;
            _meter = new LevelMeterSampleProvider(sp);
            sp = _meter;
            _gate = new NoiseGateSampleProvider(sp, proc.GateThresholdDb) { Enabled = proc.GateEnabled };
            sp = _gate;

            _out = BuildOutput(sp);
        }
        catch
        {
            Dispose();   // release anything already acquired before the throw
            throw;
        }
    }

    /// <summary>Starts capture + playback. Returns false if the devices won't start (the caller disposes on failure).</summary>
    public bool Start()
    {
        if (_disposed) return false;
        try { _capture?.StartRecording(); _out?.Play(); }
        catch { return false; }
        Active = true;
        return true;
    }

    // ---- live tuning (called from the UI thread; processors read these atomically on the audio thread) ----
    public bool GateEnabled { set { if (_gate != null) _gate.Enabled = value; } }
    public int GateThresholdDb { set { if (_gate != null) _gate.ThresholdDb = value; } }
    public bool SuppressEnabled { set { if (_supp != null) _supp.Enabled = value; } }
    public int SuppressStrength { set { if (_supp != null) _supp.Strength = value / 100f; } }

    // Prefer low-latency WASAPI shared on the default render device (matching its mix format); fall back to
    // WaveOut (16-bit, accepted by any driver) if WASAPI init fails. The render MMDevice is kept in
    // _renderDev because WasapiOut uses it until its own Dispose and never disposes it itself.
    private IWavePlayer BuildOutput(ISampleProvider chain)
    {
        WasapiOut? wo = null;
        MMDevice? dev = null;
        try
        {
            using var en = new MMDeviceEnumerator();
            dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            WaveFormat fmt;
            using (var ac = dev.AudioClient) fmt = ac.MixFormat;
            var outp = Adapt(chain, fmt.SampleRate, fmt.Channels);
            wo = new WasapiOut(dev, AudioClientShareMode.Shared, false, 100);
            wo.Init(outp);
            _renderDev = dev;   // ownership transferred; disposed in Dispose after _out
            dev = null;
            return wo;
        }
        catch
        {
            try { wo?.Dispose(); } catch { }     // release the activated AudioClient before falling back
            try { dev?.Dispose(); } catch { }
            var fb = new WaveOutEvent { DesiredLatency = 140 };
            fb.Init(Adapt(chain, 48000, 2), convertTo16Bit: true);
            return fb;
        }
    }

    // Bring the mono/own-rate chain up to the output device's channel count and sample rate.
    private static ISampleProvider Adapt(ISampleProvider src, int rate, int channels)
    {
        if (src.WaveFormat.Channels == 1 && channels >= 2) src = new MonoToStereoSampleProvider(src);
        if (src.WaveFormat.SampleRate != rate) src = new WdlResamplingSampleProvider(src, rate);
        return src;
    }

    // Best-effort map of a mic name to a WASAPI capture device by friendly name (mirrors GpuAudioCapture).
    // Disposes every enumerated device it does not return; the returned device is handed to WasapiCapture.
    private static MMDevice? ResolveMic(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            using var en = new MMDeviceEnumerator();
            MMDevice? exact = null;
            MMDevice? best = null;
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                if (exact != null) { d.Dispose(); continue; }
                var fn = d.FriendlyName;
                if (string.Equals(fn, name, StringComparison.OrdinalIgnoreCase)) { exact = d; continue; }
                if (best == null &&
                    (fn.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                     name.Contains(fn, StringComparison.OrdinalIgnoreCase)))
                    best = d;
                else
                    d.Dispose();
            }
            if (exact != null) { best?.Dispose(); return exact; }
            return best;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Active = false;
        // Stop the output first (so it stops pulling the chain), then the capture, then release both — and
        // the render device LAST, since WasapiOut uses it until its own Dispose runs.
        try { _out?.Stop(); } catch { }
        try { _capture?.StopRecording(); } catch { }
        try { _out?.Dispose(); } catch { }
        try { _capture?.Dispose(); } catch { }
        try { _renderDev?.Dispose(); } catch { }
        _out = null; _capture = null; _renderDev = null; _buf = null; _supp = null; _gate = null; _meter = null;
    }
}
