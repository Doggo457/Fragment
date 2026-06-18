using System;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Fragment.Services.Encoding;

/// <summary>
/// Converts a captured BGRA texture to NV12 entirely on the GPU using the fixed-function D3D11 Video
/// Processor (near-zero CPU). The NV12 output stays on the GPU to be fed straight to the H.264 encoder.
/// </summary>
public sealed class VideoProcessorConverter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;

    public int Width { get; }
    public int Height { get; }

    public VideoProcessorConverter(GpuRecordingDevice gpu, int width, int height, int fps)
    {
        _device = gpu.Device;
        Width = width + (width & 1);    // NV12 needs even dimensions (2x2 chroma)
        Height = height + (height & 1);

        _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = gpu.Context.QueryInterface<ID3D11VideoContext>();

        var rate = new Rational((uint)(fps > 0 ? fps : 60), 1u);
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)Width,
            InputHeight = (uint)Height,
            OutputWidth = (uint)Width,
            OutputHeight = (uint)Height,
            InputFrameRate = rate,
            OutputFrameRate = rate,
            Usage = VideoUsage.PlaybackNormal,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(content);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        // Input: full-range RGB desktop. Output: BT.709 limited-range YCbCr (the standard for H.264 HD).
        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace
        {
            Usage = 0, RGB_Range = 0 /* full */, YCbCr_Matrix = 1 /* BT.709 */, YCbCr_xvYCC = 0, Nominal_Range = 2 /* 0-255 */,
        });
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace
        {
            Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1 /* BT.709 */, YCbCr_xvYCC = 0, Nominal_Range = 1 /* 16-235 */,
        });
    }

    /// <summary>
    /// Creates a BGRA texture sized to the converter, usable as the VideoProcessor input. Uses
    /// <see cref="BindFlags.RenderTarget"/>: a ShaderResource-only texture is rejected as a VP input
    /// view by some drivers (AMD), whereas a render-target-capable Default texture is accepted everywhere.
    /// </summary>
    public ID3D11Texture2D CreateInputTexture()
        => _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

    /// <summary>Creates an NV12 render-target texture sized to the output, usable as the Blt target and MF input.</summary>
    public ID3D11Texture2D CreateNv12Texture()
        => _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget, // required to create a VideoProcessorOutputView
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

    /// <summary>GPU BGRA→NV12 blit. Both textures live on the shared device; no CPU touches the pixels.</summary>
    public void Convert(ID3D11Texture2D bgraSource, ID3D11Texture2D nv12Dest)
    {
        using var input = _videoDevice.CreateVideoProcessorInputView(bgraSource, _enumerator,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });
        using var output = _videoDevice.CreateVideoProcessorOutputView(nv12Dest, _enumerator,
            new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });

        var stream = new VideoProcessorStream
        {
            Enable = true,
            OutputIndex = 0,
            InputFrameOrField = 0,
            InputSurface = input,
        };
        _videoContext.VideoProcessorBlt(_processor, output, 0, 1, new[] { stream });
    }

    public void Dispose()
    {
        try { _processor?.Dispose(); } catch { }
        try { _enumerator?.Dispose(); } catch { }
        try { _videoContext?.Dispose(); } catch { }
        try { _videoDevice?.Dispose(); } catch { }
    }
}
