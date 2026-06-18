using System;
using System.Runtime.InteropServices;
using System.Threading;
using Fragment.Services; // reuse WgcCapture's interop interfaces + MonitorFromPoint
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Fragment.Services.Encoding;

/// <summary>
/// Windows.Graphics.Capture that keeps each frame ON THE GPU. Instead of the CPU staging/copy the
/// ffmpeg path uses, FrameArrived does a GPU→GPU CopyResource of the captured BGRA surface into an
/// app-owned "latest" texture on the shared device, so the encoder can consume it with no readback.
/// </summary>
public sealed class GpuWgcCapture : IDisposable
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid ID3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private GraphicsCaptureItem? _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly object _gate = new();

    private ID3D11Texture2D? _latest; // app-owned BGRA copy of the most recent frame (on the GPU)
    private int _w, _h;
    private bool _hasFrame;
    private volatile bool _disposed;
    private int _disposeGuard;
    private long _arrivedCount;

    public long ArrivedCount => Interlocked.Read(ref _arrivedCount);

    public GpuWgcCapture(GpuRecordingDevice gpu, IntPtr hmon, bool captureCursor)
    {
        _device = gpu.Device;
        _context = gpu.Context;
        _winrtDevice = gpu.WinRtDevice;

        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid iid = GraphicsCaptureItemIid;
        IntPtr itemAbi = interop.CreateForMonitor(hmon, ref iid);
        _item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemAbi);
        Marshal.Release(itemAbi);

        var size = _item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        _session = _framePool.CreateCaptureSession(_item);

        try
        {
            if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 12) &&
                ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                _session.IsBorderRequired = false;
        }
        catch { /* Win10: border unavoidable */ }
        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
                _session.IsCursorCaptureEnabled = captureCursor;
        }
        catch { }

        _framePool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
    }

    public bool WaitForFirstFrame(int timeoutMs, out int width, out int height)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            lock (_gate) { if (_hasFrame) { width = _w; height = _h; return true; } }
            Thread.Sleep(8);
        }
        width = height = 0;
        return false;
    }

    /// <summary>
    /// Copies the latest captured frame into <paramref name="dest"/> (caller-owned BGRA texture on the
    /// same device) under the lock — a quick GPU→GPU copy that snapshots a tear-free frame for the
    /// converter/encoder to consume without blocking capture. Returns false before the first frame.
    /// </summary>
    public bool CopyLatestInto(ID3D11Texture2D dest)
    {
        lock (_gate)
        {
            if (!_hasFrame || _latest is null) return false;
            _context.CopyResource(dest, _latest);
            return true;
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;
            using var src = GetTexture(frame.Surface);
            var desc = src.Description;
            int w = (int)desc.Width, h = (int)desc.Height;

            lock (_gate)
            {
                if (_disposed) return;
                if (_latest is null || _w != w || _h != h)
                {
                    _latest?.Dispose();
                    _latest = _device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)w,
                        Height = (uint)h,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        // RenderTarget (not ShaderResource): lets this texture also serve directly as a
                        // VideoProcessor input view if needed — a ShaderResource-only texture is rejected.
                        BindFlags = BindFlags.RenderTarget,
                        CPUAccessFlags = CpuAccessFlags.None,
                        MiscFlags = ResourceOptionFlags.None,
                    });
                    _w = w; _h = h;
                }
                _context.CopyResource(_latest, src);
                _hasFrame = true;
            }
            Interlocked.Increment(ref _arrivedCount);
        }
        catch { /* races during teardown */ }
    }

    private ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid iid = ID3D11Texture2DIid;
        IntPtr tex = access.GetInterface(ref iid);
        return new ID3D11Texture2D(tex);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeGuard, 1) != 0) return;
        try { _framePool.FrameArrived -= OnFrameArrived; } catch { }
        lock (_gate) { _disposed = true; }

        try { _session?.Dispose(); } catch { }   // retires the Win10 border
        try { _framePool?.Dispose(); } catch { }
        lock (_gate) { try { _latest?.Dispose(); } catch { } _latest = null; }
        _item = null; // release the WGC item so its registration can be GC-finalized (border-restart fix)
    }
}
