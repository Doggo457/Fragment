using System;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using WinRT;
using Windows.Graphics.DirectX.Direct3D11;

namespace Fragment.Services.Encoding;

/// <summary>
/// The single D3D11 device + Media Foundation context shared by WGC capture, the BGRA→NV12 video
/// processor, and the hardware H.264 encoder — so a captured frame never leaves the GPU (no readback).
/// Created once per recording/buffer session; every stage borrows this device.
/// </summary>
public sealed class GpuRecordingDevice : IDisposable
{
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = true)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    // Media Foundation is process-wide; start it once and let process exit shut it down.
    private static int _mfStarted;

    private int _disposed;

    /// <summary>The shared D3D11 device (hardware, with BGRA + video support).</summary>
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }

    /// <summary>MF device manager that hands the SAME D3D device to the encoder MFT (no copy).</summary>
    public IMFDXGIDeviceManager DeviceManager { get; }

    /// <summary>WinRT bridge of the same device, for Windows.Graphics.Capture.</summary>
    public IDirect3DDevice WinRtDevice { get; }

    public GpuRecordingDevice()
    {
        // VideoSupport is required for the D3D11 VideoProcessor (BGRA→NV12) and the hardware encoder MFT;
        // BgraSupport for the WGC B8G8R8A8 surfaces.
        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        Device = device;
        Context = context;

        // The device/context is touched by WGC's free-threaded FrameArrived, the feeder, and the encoder.
        using (var mt = Device.QueryInterface<ID3D11Multithread>())
            mt.SetMultithreadProtected(true);

        // WinRT device bridge for WGC.
        using (var dxgi = Device.QueryInterface<Vortice.DXGI.IDXGIDevice>())
        {
            uint hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr abi);
            if (hr != 0) Marshal.ThrowExceptionForHR((int)hr);
            WinRtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(abi);
            Marshal.Release(abi);
        }

        // Media Foundation: start full MF once, then share this device with the encoder MFT.
        if (Interlocked.Exchange(ref _mfStarted, 1) == 0)
            MediaFactory.MFStartup(useLightVersion: false).CheckError();

        DeviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        DeviceManager.ResetDevice(Device).CheckError();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { DeviceManager?.Dispose(); } catch { }
        try { WinRtDevice?.Dispose(); } catch { }
        try { Context?.Dispose(); } catch { }
        try { Device?.Dispose(); } catch { }
    }
}
