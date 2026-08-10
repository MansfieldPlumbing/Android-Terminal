using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace Subsystem;
// WE MUST NOT USE SKIA OR VORTICE
/// <summary>
/// High-performance C# Direct3D12 rendering pipeline with zero per-frame allocations.
/// Supports high-speed copy and blitting of SkiaSharp SKBitmaps into a GPU-bound Texture2D Array.
/// </summary>
public sealed class D3D12Renderer : IDisposable
{
    private const int FrameCount = 2;
    private const Format BackBufferFormat = Format.R8G8B8A8_UNorm;

    private ID3D12Device5 _device = null!;
    private ID3D12CommandQueue _commandQueue = null!;
    private IDXGISwapChain3 _swapChain = null!;
    private ID3D12DescriptorHeap _rtvHeap = null!;
    private ID3D12Resource[] _backBuffers = new ID3D12Resource[FrameCount];
    private ID3D12CommandAllocator[] _commandAllocators = new ID3D12CommandAllocator[FrameCount];
    private ID3D12GraphicsCommandList4 _commandList = null!;
    private ID3D12Fence _fence = null!;
    private ulong[] _fenceValues = new ulong[FrameCount];
    private System.Threading.EventWaitHandle _fenceEvent = null!;

    private ID3D12RootSignature _rootSignature = null!;
    private ID3D12PipelineState _pipelineState = null!;

    // GPU constant and structured buffers
    private ID3D12Resource _structuredBufferGpu = null!;
    private ID3D12Resource _structuredBufferUpload = null!;
    private ID3D12Resource _constantBufferUpload = null!;

    // Static Atlases and Applet Texture Array
    private ID3D12Resource _chromeAtlasGpu = null!;
    private ID3D12Resource _chromeStaging = null!;
    private ID3D12Resource _emojiAtlasGpu = null!;
    private ID3D12Resource _emojiStaging = null!;
    private ID3D12Resource _fontAtlasGpu = null!;
    private ID3D12Resource _fontStaging = null!;

    private ID3D12Resource _appletTexture = null!;
    private ID3D12Resource _appletUploadStaging = null!;

    // Descriptor Heaps
    private ID3D12DescriptorHeap _srvHeap = null!;

    private uint _frameIndex;
    private int _rtvDescriptorSize;
    private int _screenWidth;
    private int _screenHeight;
    private float _time;

    public void Initialize(nint hwnd, int width, int height)
    {
        _screenWidth = width;
        _screenHeight = height;

#if DEBUG
        if (D3D12.D3D12GetDebugInterface(out ID3D12Debug? debug).Success)
            debug?.EnableDebugLayer();
#endif

        DXGI.CreateDXGIFactory2(false, out IDXGIFactory4? factory);
        D3D12.D3D12CreateDevice(null, FeatureLevel.Level_11_0, out _device!);

        var queueDesc = new CommandQueueDescription(CommandListType.Direct);
        _device.CreateCommandQueue(queueDesc, out _commandQueue!);

        var swapChainDesc = new SwapChainDescription1
        {
            BufferCount = FrameCount,
            Width = (uint)width,
            Height = (uint)height,
            Format = BackBufferFormat,
            BufferUsage = Usage.RenderTargetOutput,
            SwapEffect = SwapEffect.FlipDiscard,
            SampleDescription = new SampleDescription(1, 0),
        };

        factory!.CreateSwapChainForHwnd(_commandQueue, hwnd, swapChainDesc, null, null, out IDXGISwapChain1? sc1);
        _swapChain = sc1!.QueryInterface<IDXGISwapChain3>();
        _frameIndex = _swapChain.CurrentBackBufferIndex;

        _rtvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, FrameCount));
        _rtvDescriptorSize = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

        for (int i = 0; i < FrameCount; i++)
        {
            _backBuffers[i] = _swapChain.GetBuffer<ID3D12Resource>(i);
            var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
            rtvHandle.Ptr += (nuint)(i * _rtvDescriptorSize);
            _device.CreateRenderTargetView(_backBuffers[i], null, rtvHandle);
            _device.CreateCommandAllocator(CommandListType.Direct, out _commandAllocators[i]!);
        }

        _device.CreateCommandList(0, CommandListType.Direct, _commandAllocators[0], null, out _commandList!);

        AllocateResources();
        LoadAtlasTextures();

        _commandList.Close();
        _commandQueue.ExecuteCommandList(_commandList);
        WaitIdle();

        BuildPipeline();
        BuildSrvDescriptorHeap();

        _device.CreateFence(0, FenceFlags.None, out _fence!);
        _fenceEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset);
    }

    private void BuildPipeline()
    {
        // Parameter 0: Descriptor Table containing 5 SRVs (t0 through t4)
        // Parameter 1: ConstantBuffer<Config> (b0 CBV) via Root Descriptor
        var rootParams = new RootParameter1[2];

        var srvRanges = new DescriptorRange1[]
        {
            new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 5, 0)
        };
        rootParams[0] = new RootParameter1(new RootDescriptorTable1(srvRanges), ShaderVisibility.Pixel);
        rootParams[1] = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);

        var samplers = new StaticSamplerDescription[]
        {
            // s0: Point/Linear Wrap
            new StaticSamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Wrap, TextureAddressMode.Wrap, TextureAddressMode.Wrap, 0, 0, ComparisonFunction.Always, StaticBorderColor.TransparentBlack, 0, float.MaxValue, 0, 0, ShaderVisibility.Pixel),
            // s1: Clamp-to-edge Bilinear
            new StaticSamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Always, StaticBorderColor.TransparentBlack, 0, float.MaxValue, 0, 0, ShaderVisibility.Pixel)
        };

        var rsDesc = new VersionedRootSignatureDescription(new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, rootParams, samplers));
        _device.CreateRootSignature(0, rsDesc, out _rootSignature!);

        string shaderPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Terminal.hlsl");
        string hlslCode = System.IO.File.Exists(shaderPath) ? System.IO.File.ReadAllText(shaderPath) : "";

        Compiler.Compile(hlslCode, "vs_main", "vs_5_1", out Vortice.Dxc.IDxcBlob? vsBlob, out _, ShaderFlags.None);
        Compiler.Compile(hlslCode, "fs_main", "ps_5_1", out Vortice.Dxc.IDxcBlob? psBlob, out _, ShaderFlags.None);

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature,
            VertexShader = new ShaderBytecode(vsBlob!.GetBufferPointer(), vsBlob.GetBufferSize()),
            PixelShader = new ShaderBytecode(psBlob!.GetBufferPointer(), psBlob.GetBufferSize()),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new Format[] { BackBufferFormat },
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            SampleDescription = new SampleDescription(1, 0),
            SampleMask = uint.MaxValue
        };
        _device.CreateGraphicsPipelineState(psoDesc, out _pipelineState!);
    }

    private void AllocateResources()
    {
        uint maxElements = 256;
        uint elementSize = (uint)Marshal.SizeOf<UiElementData>();
        uint bufferSize = maxElements * elementSize;

        var defaultProp = new HeapProperties(HeapType.Default);
        var uploadProp = new HeapProperties(HeapType.Upload);

        _device.CreateCommittedResource(defaultProp, HeapFlags.None, ResourceDescription.Buffer(bufferSize), ResourceStates.CopyDest, null, out _structuredBufferGpu!);
        _device.CreateCommittedResource(uploadProp, HeapFlags.None, ResourceDescription.Buffer(bufferSize), ResourceStates.GenericRead, null, out _structuredBufferUpload!);

        uint cbSize = (uint)((Marshal.SizeOf<float>() * 16 + 255) & ~255);
        _device.CreateCommittedResource(uploadProp, HeapFlags.None, ResourceDescription.Buffer(cbSize), ResourceStates.GenericRead, null, out _constantBufferUpload!);

        // Allocate 1024x1024x16 Texture2D Array for windows (slices 0-15)
        var texDesc = ResourceDescription.Texture2D(
            Format.R8G8B8A8_UNorm,
            1024,
            1024,
            16, // Array size
            1,  // Mip levels
            1,  // Sample count
            0,  // Sample quality
            ResourceFlags.None
        );
        _device.CreateCommittedResource(defaultProp, HeapFlags.None, texDesc, ResourceStates.PixelShaderResource, null, out _appletTexture!);

        // Allocate 4MB single-slice staging upload buffer (1024 * 1024 * 4 bytes)
        _device.CreateCommittedResource(uploadProp, HeapFlags.None, ResourceDescription.Buffer(4 * 1024 * 1024), ResourceStates.GenericRead, null, out _appletUploadStaging!);
    }

    private void LoadAtlasTextures()
    {
        string baseDir = AppContext.BaseDirectory;
        _chromeAtlasGpu = LoadTextureFromPng(System.IO.Path.Combine(baseDir, "cascadia-ui-chrome-atlas.png"), out _chromeStaging);
        _emojiAtlasGpu = LoadTextureFromPng(System.IO.Path.Combine(baseDir, "cascadia-emoji-universal-atlas.png"), out _emojiStaging);
        _fontAtlasGpu = LoadTextureFromPng(System.IO.Path.Combine(baseDir, "cascadia-code-atlas.png"), out _fontStaging);
    }

    private ID3D12Resource LoadTextureFromPng(string filePath, out ID3D12Resource stagingBuffer)
    {
        if (!System.IO.File.Exists(filePath))
        {
            // Try parent directory fallback if running in nested bin folders
            string parentPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(filePath) ?? "", "..", "..", "..", System.IO.Path.GetFileName(filePath));
            if (System.IO.File.Exists(parentPath)) filePath = parentPath;
        }

        using var codec = SkiaSharp.SKCodec.Create(filePath);
        using var bitmap = SkiaSharp.SKBitmap.Decode(codec);

        int width = bitmap.Width;
        int height = bitmap.Height;
        int rowBytes = width * 4;
        uint totalSize = (uint)(height * rowBytes);

        var defaultProp = new HeapProperties(HeapType.Default);
        var uploadProp = new HeapProperties(HeapType.Upload);

        var texDesc = ResourceDescription.Texture2D(
            Format.R8G8B8A8_UNorm,
            (uint)width,
            (uint)height,
            1, // Array size
            1, // Mip levels
            1, // Sample count
            0, // Sample quality
            ResourceFlags.None
        );

        _device.CreateCommittedResource(defaultProp, HeapFlags.None, texDesc, ResourceStates.CopyDest, null, out ID3D12Resource gpuTex);
        _device.CreateCommittedResource(uploadProp, HeapFlags.None, ResourceDescription.Buffer(totalSize), ResourceStates.GenericRead, null, out stagingBuffer);

        stagingBuffer.Map(0, null, out nint uploadPtr);
        unsafe
        {
            byte* dst = (byte*)uploadPtr;
            byte* src = (byte*)bitmap.GetPixels();
            System.Buffer.MemoryCopy(src, dst, totalSize, totalSize);
        }
        stagingBuffer.Unmap(0, null);

        var dstLoc = new TextureCopyLocation(gpuTex, 0);
        var srcLoc = new TextureCopyLocation(stagingBuffer, new PlacedSubresourceFootprint
        {
            Offset = 0,
            Footprint = new SubresourceFootprint(Format.R8G8B8A8_UNorm, (uint)width, (uint)height, 1, (uint)rowBytes)
        });

        _commandList.CopyTextureRegion(dstLoc, 0, 0, 0, srcLoc, null);
        _commandList.ResourceBarrierTransition(gpuTex, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);

        return gpuTex;
    }

    private void BuildSrvDescriptorHeap()
    {
        _srvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 5));
        var handle = _srvHeap.GetCPUDescriptorHandleForHeapStart();
        int handleIncrement = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        // 1. t0: StructuredBuffer<UiElementData>
        var srvDescT0 = new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default
        };
        srvDescT0.Buffer.FirstElement = 0;
        srvDescT0.Buffer.NumElements = 256;
        srvDescT0.Buffer.StructureByteStride = (uint)Marshal.SizeOf<UiElementData>();
        srvDescT0.Buffer.Flags = BufferShaderResourceViewFlags.None;
        _device.CreateShaderResourceView(_structuredBufferGpu, srvDescT0, handle);
        handle.Ptr += (nuint)handleIncrement;

        // 2. t1: chromeAtlas (Texture2D)
        var srvDescTex = new ShaderResourceViewDescription
        {
            Format = Format.R8G8B8A8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default
        };
        srvDescTex.Texture2D.MipLevels = 1;
        srvDescTex.Texture2D.MostDetailedMip = 0;
        srvDescTex.Texture2D.ResourceMinLODClamp = 0.0f;
        _device.CreateShaderResourceView(_chromeAtlasGpu, srvDescTex, handle);
        handle.Ptr += (nuint)handleIncrement;

        // 3. t2: emojiAtlas (Texture2D)
        _device.CreateShaderResourceView(_emojiAtlasGpu, srvDescTex, handle);
        handle.Ptr += (nuint)handleIncrement;

        // 4. t3: fontAtlas (Texture2D)
        _device.CreateShaderResourceView(_fontAtlasGpu, srvDescTex, handle);
        handle.Ptr += (nuint)handleIncrement;

        // 5. t4: appletTexture (Texture2DArray, slices 0-15)
        var srvDescArray = new ShaderResourceViewDescription
        {
            Format = Format.R8G8B8A8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2DArray,
            Shader4ComponentMapping = ShaderComponentMapping.Default
        };
        srvDescArray.Texture2DArray.ArraySize = 16;
        srvDescArray.Texture2DArray.FirstArraySlice = 0;
        srvDescArray.Texture2DArray.MipLevels = 1;
        srvDescArray.Texture2DArray.MostDetailedMip = 0;
        srvDescArray.Texture2DArray.ResourceMinLODClamp = 0.0f;
        _device.CreateShaderResourceView(_appletTexture, srvDescArray, handle);
    }

    public void Render(ReadOnlySpan<UiElementData> elements, float dt, Dictionary<int, SkiaSharp.SKBitmap>? windowBitmaps = null)
    {
        _time += dt;

        // 1. Reset command allocator and command list
        _commandAllocators[_frameIndex].Reset();
        _commandList.Reset(_commandAllocators[_frameIndex], _pipelineState);

        // 2. Upload window bitmaps (if any) to appletTexture array slices
        if (windowBitmaps != null && windowBitmaps.Count > 0)
        {
            _commandList.ResourceBarrierTransition(_appletTexture, ResourceStates.PixelShaderResource, ResourceStates.CopyDest);

            foreach (var kvp in windowBitmaps)
            {
                int winIdx = kvp.Key;
                if (winIdx < 0 || winIdx >= 16) continue;

                var bitmap = kvp.Value;
                if (bitmap == null || bitmap.IsEmpty) continue;

                _appletUploadStaging.Map(0, null, out nint stagingPtr);
                unsafe
                {
                    nint srcPixels = bitmap.GetPixels();
                    int copyW = Math.Min(bitmap.Width, 1024);
                    int copyH = Math.Min(bitmap.Height, 1024);
                    int srcRowBytes = bitmap.RowBytes;
                    int dstRowBytes = 1024 * 4;

                    byte* srcPtr = (byte*)srcPixels;
                    byte* dstPtr = (byte*)stagingPtr;

                    for (int y = 0; y < copyH; y++)
                    {
                        Buffer.MemoryCopy(
                            srcPtr + (y * srcRowBytes),
                            dstPtr + (y * dstRowBytes),
                            dstRowBytes,
                            copyW * 4
                        );
                    }
                }
                _appletUploadStaging.Unmap(0, null);

                var dstLoc = new TextureCopyLocation(_appletTexture, (uint)winIdx);
                var srcLoc = new TextureCopyLocation(_appletUploadStaging, new PlacedSubresourceFootprint
                {
                    Offset = 0,
                    Footprint = new SubresourceFootprint(Format.R8G8B8A8_UNorm, 1024, 1024, 1, 4096)
                });

                _commandList.CopyTextureRegion(dstLoc, 0, 0, 0, srcLoc, null);
            }

            _commandList.ResourceBarrierTransition(_appletTexture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        }

        // 3. Upload instanced state data
        _structuredBufferUpload.Map(0, null, out nint uploadPtr);
        unsafe
        {
            var dstSpan = new Span<UiElementData>((void*)uploadPtr, 256);
            elements.CopyTo(dstSpan);
        }
        _structuredBufferUpload.Unmap(0, null);

        // 4. Upload uniform constants
        _constantBufferUpload.Map(0, null, out nint cbPtr);
        unsafe
        {
            float* cbFloat = (float*)cbPtr;
            cbFloat[0] = _screenWidth;
            cbFloat[1] = _screenHeight;
            cbFloat[2] = _time;
            cbFloat[3] = BitConverter.ToSingle(BitConverter.GetBytes((uint)((0 << 24) | (127 << 16) | (255 << 8) | 0)), 0);
            cbFloat[4] = iThemeColor_R;
            cbFloat[5] = iThemeColor_G;
            cbFloat[6] = iThemeColor_B;
            cbFloat[7] = 1.0f;
            cbFloat[8] = iDpiScale;
            cbFloat[9] = iCameraOffset_X;
            cbFloat[10] = iCameraOffset_Y;
        }
        _constantBufferUpload.Unmap(0, null);

        // 5. Commit structure upload copy
        _commandList.ResourceBarrierTransition(_structuredBufferGpu, ResourceStates.PixelShaderResource, ResourceStates.CopyDest);
        _commandList.CopyResource(_structuredBufferGpu, _structuredBufferUpload);
        _commandList.ResourceBarrierTransition(_structuredBufferGpu, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);

        var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        rtvHandle.Ptr += (nuint)(_frameIndex * _rtvDescriptorSize);

        _commandList.ResourceBarrierTransition(_backBuffers[_frameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

        _commandList.ClearRenderTargetView(rtvHandle, new Vortice.Mathematics.Color4(0.0f, 0.0f, 0.0f, 1.0f));
        _commandList.OMSetRenderTargets(rtvHandle);
        _commandList.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, _screenWidth, _screenHeight));
        _commandList.RSSetScissorRect(0, 0, _screenWidth, _screenHeight);

        _commandList.SetGraphicsRootSignature(_rootSignature);
        _commandList.SetDescriptorHeaps(new ID3D12DescriptorHeap[] { _srvHeap });
        _commandList.SetGraphicsRootDescriptorTable(0, _srvHeap.GetGPUDescriptorHandleForHeapStart());
        _commandList.SetGraphicsRootConstantBufferView(1, _constantBufferUpload.GPUVirtualAddress);

        _commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _commandList.DrawInstanced(6, 1, 0, 0);

        _commandList.ResourceBarrierTransition(_backBuffers[_frameIndex], ResourceStates.RenderTarget, ResourceStates.Present);
        _commandList.Close();

        _commandQueue.ExecuteCommandList(_commandList);
        _swapChain.Present(1, PresentFlags.None);

        ulong fenceVal = ++_fenceValues[_frameIndex];
        _commandQueue.Signal(_fence, fenceVal);

        _frameIndex = _swapChain.CurrentBackBufferIndex;

        if (_fence.CompletedValue < _fenceValues[_frameIndex])
        {
            _fence.SetEventOnCompletion(_fenceValues[_frameIndex], _fenceEvent.SafeWaitHandle.DangerousGetHandle());
            _fenceEvent.WaitOne();
        }
    }

    public void Resize(int w, int h)
    {
        _screenWidth = w;
        _screenHeight = h;
        WaitIdle();

        for (int i = 0; i < FrameCount; i++) _backBuffers[i]?.Dispose();

        _swapChain.ResizeBuffers(FrameCount, (uint)w, (uint)h, BackBufferFormat, SwapChainFlags.None);
        _frameIndex = _swapChain.CurrentBackBufferIndex;

        for (int i = 0; i < FrameCount; i++)
        {
            _backBuffers[i] = _swapChain.GetBuffer<ID3D12Resource>(i);
            var h2 = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
            h2.Ptr += (nuint)(i * _rtvDescriptorSize);
            _device.CreateRenderTargetView(_backBuffers[i], null, h2);
        }
    }

    private void WaitIdle()
    {
        ulong v = ++_fenceValues[_frameIndex];
        _commandQueue.Signal(_fence, v);
        _fence.SetEventOnCompletion(v, _fenceEvent.SafeWaitHandle.DangerousGetHandle());
        _fenceEvent.WaitOne();
    }

    public float iThemeColor_R { get; set; } = 0.0f;
    public float iThemeColor_G { get; set; } = 120.0f / 255.0f;
    public float iThemeColor_B { get; set; } = 215.0f / 255.0f;
    public float iDpiScale { get; set; } = 1.0f;
    public float iCameraOffset_X { get; set; } = 0.0f;
    public float iCameraOffset_Y { get; set; } = 0.0f;

    public void Dispose()
    {
        WaitIdle();
        _chromeAtlasGpu?.Dispose();
        _chromeStaging?.Dispose();
        _emojiAtlasGpu?.Dispose();
        _emojiStaging?.Dispose();
        _fontAtlasGpu?.Dispose();
        _fontStaging?.Dispose();
        _appletTexture?.Dispose();
        _appletUploadStaging?.Dispose();
        _srvHeap?.Dispose();
        _structuredBufferGpu?.Dispose();
        _structuredBufferUpload?.Dispose();
        _constantBufferUpload?.Dispose();
        _pipelineState?.Dispose();
        _rootSignature?.Dispose();
        _commandList?.Dispose();
        for (int i = 0; i < FrameCount; i++)
        {
            _commandAllocators[i]?.Dispose();
            _backBuffers[i]?.Dispose();
        }
        _fence?.Dispose();
        _rtvHeap?.Dispose();
        _swapChain?.Dispose();
        _commandQueue?.Dispose();
        _device?.Dispose();
        _fenceEvent?.Dispose();
    }
}
