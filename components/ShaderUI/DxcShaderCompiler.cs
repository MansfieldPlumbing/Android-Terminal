using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TuiDwm.Port;

/// <summary>
/// A lightweight, device-agnostic pure C# wrapper for the DirectX Shader Compiler (DXC).
/// Directly P/Invokes into dxcompiler (dxcompiler.dll / libdxcompiler.so) to compile HLSL
/// shaders into DXIL (for Direct3D12) or SPIR-V (for Vulkan on Android/Linux) without external dependencies.
/// </summary>
public static class DxcShaderCompiler
{
    private const string DxcLibraryName = "dxcompiler";

    // Standard DXC COM GUIDs
    private static readonly Guid CLSID_DxcCompiler = new("73E22D93-E6CE-47F3-B5BF-F0664F39C1B0");
    private static readonly Guid IID_IDxcCompiler3 = new("222C46C7-5D1E-410E-8B3B-D0C301362741");
    private static readonly Guid IID_IDxcResult = new("58346CDA-D492-402D-A05D-62F4017B7D9C");

    // DXC Enums and Constants
    private const uint DXC_OUT_OBJECT = 1;
    private const uint DXC_OUT_ERRORS = 2;
    private const uint DXC_CP_UTF8 = 65001;

    [StructLayout(LayoutKind.Sequential)]
    private struct DxcBuffer
    {
        public IntPtr Ptr;
        public nuint Size;
        public uint Encoding;
    }

    [DllImport(DxcLibraryName, CallingConvention = CallingConvention.StdCall, PreserveSig = false)]
    private static extern void DxcCreateInstance(
        [In] ref Guid rclsid,
        [In] ref Guid riid,
        [Out, MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    [ComImport]
    [Guid("222C46C7-5D1E-410E-8B3B-D0C301362741")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxcCompiler3
    {
        [PreserveSig]
        int Compile(
            ref DxcBuffer pSource,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] pArguments,
            uint argCount,
            IntPtr pIncludeHandler,
            ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppResult);
    }

    [ComImport]
    [Guid("58346CDA-D492-402D-A05D-62F4017B7D9C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxcResult
    {
        [PreserveSig]
        int GetStatus(out int pStatus);

        [PreserveSig]
        int GetResult(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppResult);

        [PreserveSig]
        bool HasOutput(uint dxcOutKind);

        [PreserveSig]
        int GetOutput(uint dxcOutKind, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppResult, out IntPtr ppOutputName);
    }

    [ComImport]
    [Guid("8BA5FB08-5195-40E2-AC58-0D989C3A0102")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxcBlob
    {
        IntPtr GetBufferPointer();
        nuint GetBufferSize();
    }

    [ComImport]
    [Guid("E5204DC7-D1C1-4D3C-BDFE-31EE5985A0E1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxcBlobEncoding : IDxcBlob
    {
        new IntPtr GetBufferPointer();
        new nuint GetBufferSize();
        [PreserveSig]
        int GetEncoding(out bool pKnown, out uint pCodePage);
    }

    /// <summary>
    /// Compiles HLSL source string to bytecode (DXIL or SPIR-V).
    /// </summary>
    /// <param name="source">HLSL Source Code</param>
    /// <param name="entryPoint">Shader Entrypoint Name (e.g., "vs_main" or "fs_main")</param>
    /// <param name="targetProfile">Target Profile (e.g., "vs_6_0", "ps_6_0", "cs_6_0")</param>
    /// <param name="compileToSpirV">True to compile to Vulkan SPIR-V, False for D3D12 DXIL</param>
    /// <param name="errorLog">Receives compile-time warning and error messages if compilation fails</param>
    /// <returns>Compiled byte array, or null if compilation failed</returns>
    public static byte[]? Compile(string source, string entryPoint, string targetProfile, bool compileToSpirV, out string errorLog)
    {
        errorLog = string.Empty;
        IntPtr sourcePtr = IntPtr.Zero;

        try
        {
            // 1. Instantiate the DXC Compiler
            DxcCreateInstance(ref CLSID_DxcCompiler, ref IID_IDxcCompiler3, out object compilerObj);
            var compiler = (IDxcCompiler3)compilerObj;

            // 2. Prepare the source buffer
            byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
            sourcePtr = Marshal.AllocHGlobal(sourceBytes.Length);
            Marshal.Copy(sourceBytes, 0, sourcePtr, sourceBytes.Length);

            var sourceBuffer = new DxcBuffer
            {
                Ptr = sourcePtr,
                Size = (nuint)sourceBytes.Length,
                Encoding = DXC_CP_UTF8
            };

            // 3. Define compiler arguments
            // Standard command-line-style arguments matching dxc.exe behavior
            var arguments = new System.Collections.Generic.List<string>
            {
                "-E", entryPoint,
                "-T", targetProfile,
                "-O3" // Maximum Optimization
            };

            if (compileToSpirV)
            {
                // Instruct DXC to output SPIR-V binary layout instead of DXIL
                arguments.Add("-spirv");
                arguments.Add("-fvk-use-dx-layout"); // Force HLSL-style layout packing for buffers
            }

            // 4. Run compilation pass
            Guid riidResult = IID_IDxcResult;
            int hr = compiler.Compile(
                ref sourceBuffer,
                arguments.ToArray(),
                (uint)arguments.Count,
                IntPtr.Zero,
                ref riidResult,
                out object resultObj);

            if (hr != 0)
            {
                errorLog = $"DXC compiler failed to execute with HRESULT: 0x{hr:X8}";
                return null;
            }

            var result = (IDxcResult)resultObj;
            result.GetStatus(out int status);

            // 5. Gather compiler warning/error diagnostics
            if (result.HasOutput(DXC_OUT_ERRORS))
            {
                Guid blobGuid = typeof(IDxcBlobEncoding).GUID;
                result.GetOutput(DXC_OUT_ERRORS, ref blobGuid, out object errBlobObj, out _);
                if (errBlobObj is IDxcBlobEncoding errBlob && errBlob.GetBufferSize() > 0)
                {
                    IntPtr errPtr = errBlob.GetBufferPointer();
                    byte[] errBytes = new byte[(int)errBlob.GetBufferSize()];
                    Marshal.Copy(errPtr, errBytes, 0, errBytes.Length);
                    errorLog = Encoding.UTF8.GetString(errBytes).TrimEnd('\0');
                }
            }

            // 6. Return bytecode on success
            if (status == 0 && result.HasOutput(DXC_OUT_OBJECT))
            {
                Guid blobGuid = typeof(IDxcBlob).GUID;
                result.GetOutput(DXC_OUT_OBJECT, ref blobGuid, out object objBlobObj, out _);
                if (objBlobObj is IDxcBlob objBlob)
                {
                    IntPtr objPtr = objBlob.GetBufferPointer();
                    byte[] compiledBytes = new byte[(int)objBlob.GetBufferSize()];
                    Marshal.Copy(objPtr, compiledBytes, 0, compiledBytes.Length);
                    return compiledBytes;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            errorLog = $"Exception during DXC compilation: {ex.Message}";
            return null;
        }
        finally
        {
            if (sourcePtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(sourcePtr);
            }
        }
    }
}
