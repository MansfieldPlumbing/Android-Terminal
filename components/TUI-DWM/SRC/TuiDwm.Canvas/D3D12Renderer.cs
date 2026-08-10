using System;
using System.Runtime.InteropServices;
using TuiDwm.Core;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace TuiDwm.Canvas;

// D3D12 cell buffer renderer.
//
// Each frame:
//   1. Upload Cell[] into a RGBA8 upload texture (MemoryMarshal.Cast — zero copy).
//   2. Transition + CopyTextureRegion into the GPU texture.
//   3. DrawInstanced(6, cols*rows, 0, 0) — VS generates quads from SV_InstanceID.
//   4. Present.
//
// No vertex buffers. No index buffers. One root signature. One PSO.
public sealed class D3D12Renderer : IDisposable
{
    private const int FrameCount    = 2;
    private const Format BackFmt    = Format.R8G8B8A8_UNorm;
    private const Format CellFmt    = Format.R8G8B8A8_UNorm;
    private const Format AtlasFmt   = Format.R8G8B8A8_UNorm;

    // ── D3D12 core ─────────────────────────────────────────────────────────
    private ID3D12Device5         _device       = null!;
    private ID3D12CommandQueue    _cmdQueue     = null!;
    private IDXGISwapChain3       _swapChain    = null!;
    private ID3D12DescriptorHeap  _rtvHeap      = null!;
    private ID3D12DescriptorHeap  _srvHeap      = null!;
    private ID3D12Resource[]      _backBuffers  = new ID3D12Resource[FrameCount];
    private ID3D12CommandAllocator[]    _cmdAllocs = new ID3D12CommandAllocator[FrameCount];
    private ID3D12GraphicsCommandList4  _cmdList  = null!;
    private ID3D12Fence           _fence        = null!;
    private ulong[]               _fenceValues  = new ulong[FrameCount];
    private System.Threading.EventWaitHandle _fenceEvent = null!;

    // ── Pipeline ───────────────────────────────────────────────────────────
    private ID3D12RootSignature   _rootSig      = null!;
    private ID3D12PipelineState   _pso          = null!;

    // ── Cell texture ───────────────────────────────────────────────────────
    private ID3D12Resource?       _cellGpu;       // HEAP_DEFAULT — GPU reads
    private ID3D12Resource?       _cellUpload;    // HEAP_UPLOAD  — CPU writes
    private int                   _cellTexCols;
    private int                   _cellTexRows;

    // ── Atlas texture ──────────────────────────────────────────────────────
    private ID3D12Resource?       _atlasGpu;
    private ID3D12Resource?       _atlasUpload;

    // ── Effect state ───────────────────────────────────────────────────────
    public float EffectMode = 0f;  // 0=plain 1=water 2=bloom 3=crt

    private float _time;
    private int   _clientW, _clientH;
    private uint  _frameIndex;
    private int   _rtvSize;

    // ── Init ───────────────────────────────────────────────────────────────
    public void Initialize(nint hwnd, int width, int height)
    {
        _clientW = width;
        _clientH = height;

#if DEBUG
        if (D3D12.D3D12GetDebugInterface(out ID3D12Debug? debug).Success)
            debug?.EnableDebugLayer();
#endif

        DXGI.CreateDXGIFactory2(false, out IDXGIFactory4? factory);

        // Use the first hardware adapter
        D3D12.D3D12CreateDevice(null, FeatureLevel.Level_11_0, out _device!);

        var cqDesc = new CommandQueueDescription(CommandListType.Direct);
        _device.CreateCommandQueue(cqDesc, out _cmdQueue!);

        var scDesc = new SwapChainDescription1
        {
            BufferCount = FrameCount,
            Width       = (uint)width,
            Height      = (uint)height,
            Format      = BackFmt,
            BufferUsage = Usage.RenderTargetOutput,
            SwapEffect  = SwapEffect.FlipDiscard,
            SampleDescription = new SampleDescription(1, 0),
        };
        factory!.CreateSwapChainForHwnd(_cmdQueue, hwnd, scDesc, null, null, out IDXGISwapChain1? sc1);
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
        _swapChain  = sc1!.QueryInterface<IDXGISwapChain3>();
        _frameIndex = _swapChain.CurrentBackBufferIndex;

        // RTVs
        _rtvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.RenderTargetView, FrameCount));
        _rtvSize = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

        // SRV heap: slot 0 = cell texture, slot 1 = atlas
        _srvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2,
            DescriptorHeapFlags.ShaderVisible));

        // Back buffers + command allocators
        for (int i = 0; i < FrameCount; i++)
        {
            _backBuffers[i] = _swapChain.GetBuffer<ID3D12Resource>(i);
            var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
            rtvHandle.Ptr += (nuint)(i * _rtvSize);
            _device.CreateRenderTargetView(_backBuffers[i], null, rtvHandle);
            _device.CreateCommandAllocator(CommandListType.Direct, out _cmdAllocs[i]!);
        }

        _device.CreateCommandList(0, CommandListType.Direct, _cmdAllocs[0], null, out _cmdList!);
        _cmdList.Close();

        _device.CreateFence(0, FenceFlags.None, out _fence!);
        _fenceEvent = new System.Threading.EventWaitHandle(false,
            System.Threading.EventResetMode.AutoReset);

        BuildPipeline();
    }

    // ── Build root signature + PSO ─────────────────────────────────────────
    private void BuildPipeline()
    {
        // Root signature:
        //   [0] CBV b0 — FrameConstants (32-bit root constants, 10 values)
        //   [1] Descriptor table — t0 (cell), t1 (atlas)
        var rootParams = new RootParameter1[2];

        // 68 DWORDs: 4 scalars (GridCols, GridRows, Time, EffectMode)
        //           + 16 × float4 palette = 64 floats → total 68
        rootParams[0] = new RootParameter1(
            new RootConstants(0, 0, 68),
            ShaderVisibility.All);

        var ranges = new DescriptorRange1[]
        {
            new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, 0),
        };
        rootParams[1] = new RootParameter1(
            new RootDescriptorTable1(ranges),
            ShaderVisibility.Pixel);

        var samplers = new StaticSamplerDescription[]
        {
            new StaticSamplerDescription(Filter.MinMagMipPoint,  TextureAddressMode.Clamp, TextureAddressMode.Clamp,
                TextureAddressMode.Clamp, 0, 0, ComparisonFunction.Always,
                StaticBorderColor.TransparentBlack, 0, float.MaxValue, 0, 0, ShaderVisibility.Pixel),
            new StaticSamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
                TextureAddressMode.Clamp, 0, 0, ComparisonFunction.Always,
                StaticBorderColor.TransparentBlack, 0, float.MaxValue, 1, 0, ShaderVisibility.Pixel),
        };

        var rsDesc = new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout,
                rootParams, samplers));
        _device.CreateRootSignature(0, rsDesc, out _rootSig!);

        // Compile HLSL
        string hlsl = System.IO.File.ReadAllText(
            System.IO.Path.Combine(AppContext.BaseDirectory, "Shaders", "Terminal.hlsl"));

        Compiler.Compile(hlsl, "VSMain", "vs_5_1", out Vortice.Dxc.IDxcBlob? vsBlob, out _, defines: null, includes: null, flags: ShaderFlags.None);
        Compiler.Compile(hlsl, "PSMain", "ps_5_1", out Vortice.Dxc.IDxcBlob? psBlob, out _, defines: null, includes: null, flags: ShaderFlags.None);

        var rtFormats = new Format[1] { BackFmt };
        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature        = _rootSig,
            VertexShader         = new ShaderBytecode(vsBlob!.GetBufferPointer(), vsBlob.GetBufferSize()),
            PixelShader          = new ShaderBytecode(psBlob!.GetBufferPointer(), psBlob.GetBufferSize()),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats  = rtFormats,
            RasterizerState      = RasterizerDescription.CullNone,
            BlendState           = BlendDescription.Opaque,
            DepthStencilState    = DepthStencilDescription.None,
            SampleDescription    = new SampleDescription(1, 0),
            SampleMask           = uint.MaxValue,
        };
        _device.CreateGraphicsPipelineState(psoDesc, out _pso!);
    }

    // ── Upload cell buffer as GPU texture ──────────────────────────────────
    private void EnsureCellTexture(int cols, int rows)
    {
        if (_cellGpu != null && _cellTexCols == cols && _cellTexRows == rows)
            return;

        _cellGpu?.Dispose();
        _cellUpload?.Dispose();
        _cellTexCols = cols;
        _cellTexRows = rows;

        var texDesc = ResourceDescription.Texture2D(CellFmt, (uint)cols, (uint)rows, 1, 1);
        var defaultProp = new HeapProperties(HeapType.Default);
        var uploadProp  = new HeapProperties(HeapType.Upload);

        _device.CreateCommittedResource(defaultProp, HeapFlags.None,
            texDesc, ResourceStates.CopyDest, null, out _cellGpu!);

        // Upload resource: row pitch must be 256-byte aligned
        ulong rowPitch   = AlignUp((ulong)(cols * 4), 256);
        ulong uploadSize = rowPitch * (ulong)rows;
        var   uploadDesc = ResourceDescription.Buffer(uploadSize);

        _device.CreateCommittedResource(uploadProp, HeapFlags.None,
            uploadDesc, ResourceStates.GenericRead, null, out _cellUpload!);

        // After first creation, create the SRV
        _device.CreateShaderResourceView(_cellGpu, new ShaderResourceViewDescription
        {
            Format                  = CellFmt,
            ViewDimension           = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D               = new Texture2DShaderResourceView { MipLevels = 1 },
        }, _srvHeap.GetCPUDescriptorHandleForHeapStart());
    }

    public void UploadCells(CellBuffer buf)
    {
        EnsureCellTexture(buf.Width, buf.Height);

        ulong rowPitch = AlignUp((ulong)(buf.Width * 4), 256);
        var raw = MemoryMarshal.Cast<Cell, byte>(buf.Cells.AsSpan());

        // Map upload buffer, copy row by row (pitch alignment)
        _cellUpload!.Map(0, null, out nint ptr);
        int srcRowBytes = buf.Width * 4;
        for (int row = 0; row < buf.Height; row++)
        {
            var src  = raw.Slice(row * srcRowBytes, srcRowBytes);
            var dst  = new Span<byte>((void*)(ptr + (nint)(row * rowPitch)), srcRowBytes);
            src.CopyTo(dst);
        }
        _cellUpload.Unmap(0, null);
    }

    // ── Per-frame render ───────────────────────────────────────────────────
    // UploadCells() must be called first (under BufferLock in caller).
    // Render() does the GPU work — safe to call after releasing the lock.
    public void Render(CellBuffer buf, float dt)
    {
        _time += dt;
        // Upload already done by caller under BufferLock — skip here.

        _cmdAllocs[_frameIndex].Reset();
        _cmdList.Reset(_cmdAllocs[_frameIndex], _pso);

        // Copy cell upload → GPU texture
        _cmdList.ResourceBarrierTransition(_cellGpu!, ResourceStates.PixelShaderResource, ResourceStates.CopyDest);

        ulong rowPitch = AlignUp((ulong)(buf.Width * 4), 256);
        var   src      = new TextureCopyLocation(_cellUpload!, new PlacedSubresourceFootprint
        {
            Offset    = 0,
            Footprint = new SubresourceFootprint(CellFmt, (uint)buf.Width, (uint)buf.Height, 1, (uint)rowPitch),
        });
        var dst = new TextureCopyLocation(_cellGpu!, 0);
        _cmdList.CopyTextureRegion(dst, 0, 0, 0, src, null);

        _cmdList.ResourceBarrierTransition(_cellGpu!, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);

        // Render
        var rtv = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        rtv.Ptr += (nuint)(_frameIndex * _rtvSize);

        _cmdList.ResourceBarrierTransition(_backBuffers[_frameIndex],
            ResourceStates.Present, ResourceStates.RenderTarget);

        _cmdList.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(0, 0, 0, 1));
        _cmdList.OMSetRenderTargets(1, rtv);
        _cmdList.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, _clientW, _clientH));
        _cmdList.RSSetScissorRect(0, 0, _clientW, _clientH);

        _cmdList.SetGraphicsRootSignature(_rootSig);
        _cmdList.SetDescriptorHeaps(_srvHeap);
        _cmdList.SetGraphicsRootDescriptorTable(1, _srvHeap.GetGPUDescriptorHandleForHeapStart());

        // 68 DWORDs: [0]=GridCols [1]=GridRows [2]=Time [3]=EffectMode [4..67]=Palette float4[16]
        Span<uint> constants = stackalloc uint[68];
        var cf = MemoryMarshal.Cast<uint, float>(constants);
        constants[0] = (uint)buf.Width;
        constants[1] = (uint)buf.Height;
        cf[2] = _time;
        cf[3] = EffectMode;
        FillDefaultPalette(cf.Slice(4)); // writes 64 floats into indices [4..67]

        _cmdList.SetGraphicsRoot32BitConstants(0, 68, ref constants[0], 0);

        _cmdList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _cmdList.DrawInstanced(6, (uint)(buf.Width * buf.Height), 0, 0);

        _cmdList.ResourceBarrierTransition(_backBuffers[_frameIndex],
            ResourceStates.RenderTarget, ResourceStates.Present);

        _cmdList.Close();
        _cmdQueue.ExecuteCommandList(_cmdList);
        _swapChain.Present(1, PresentFlags.None);

        // Advance fence
        ulong fenceVal = ++_fenceValues[_frameIndex];
        _cmdQueue.Signal(_fence, fenceVal);

        _frameIndex = _swapChain.CurrentBackBufferIndex;

        // Wait for the new frame's previous work if needed
        if (_fence.CompletedValue < _fenceValues[_frameIndex])
        {
            _fence.SetEventOnCompletion(_fenceValues[_frameIndex], _fenceEvent.SafeWaitHandle.DangerousGetHandle());
            _fenceEvent.WaitOne();
        }
    }

    public void Resize(int w, int h)
    {
        _clientW = w; _clientH = h;
        WaitIdle();
        for (int i = 0; i < FrameCount; i++) _backBuffers[i]?.Dispose();
        _swapChain.ResizeBuffers(FrameCount, (uint)w, (uint)h, BackFmt, SwapChainFlags.None);
        _frameIndex = _swapChain.CurrentBackBufferIndex;
        for (int i = 0; i < FrameCount; i++)
        {
            _backBuffers[i] = _swapChain.GetBuffer<ID3D12Resource>(i);
            var h2 = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
            h2.Ptr += (nuint)(i * _rtvSize);
            _device.CreateRenderTargetView(_backBuffers[i], null, h2);
        }
    }

    private void WaitIdle()
    {
        ulong v = ++_fenceValues[_frameIndex];
        _cmdQueue.Signal(_fence, v);
        _fence.SetEventOnCompletion(v, _fenceEvent.SafeWaitHandle.DangerousGetHandle());
        _fenceEvent.WaitOne();
    }

    private static void FillDefaultPalette(Span<float> p)
    {
        // 16 xterm base colors as RGBA floats (R,G,B,A per entry = 64 floats)
        ReadOnlySpan<uint> hex = stackalloc uint[16]
        {
            0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00,
            0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
            0x767676, 0xE74856, 0x16C60C, 0xF9F1A5,
            0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2,
        };
        for (int i = 0; i < 16; i++)
        {
            p[i * 4 + 0] = ((hex[i] >> 16) & 0xFF) / 255f;
            p[i * 4 + 1] = ((hex[i] >>  8) & 0xFF) / 255f;
            p[i * 4 + 2] = ((hex[i]       ) & 0xFF) / 255f;
            p[i * 4 + 3] = 1f;
        }
    }

    private static ulong AlignUp(ulong v, ulong align) => (v + align - 1) & ~(align - 1);

    public void Dispose()
    {
        WaitIdle();
        _cellGpu?.Dispose();     _cellUpload?.Dispose();
        _atlasGpu?.Dispose();    _atlasUpload?.Dispose();
        _pso?.Dispose();         _rootSig?.Dispose();
        _cmdList?.Dispose();
        for (int i = 0; i < FrameCount; i++) { _cmdAllocs[i]?.Dispose(); _backBuffers[i]?.Dispose(); }
        _fence?.Dispose();       _srvHeap?.Dispose(); _rtvHeap?.Dispose();
        _swapChain?.Dispose();   _cmdQueue?.Dispose(); _device?.Dispose();
        _fenceEvent?.Dispose();
    }
}
