using System.Runtime.InteropServices;

namespace Rdpeek.Doctor;

internal enum Bitness { Unknown, X86, X64, Arm64 }

/// <summary>P/Invoke + PE helpers for the registration smoke tests.</summary>
internal static class NativeMethods
{
    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    internal static extern int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    internal const uint CLSCTX_INPROC_SERVER = 0x1;
    internal const uint CLSCTX_LOCAL_SERVER = 0x4;
    internal const uint COINIT_MULTITHREADED = 0x0;

    internal static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    // IWTSPlugin — the interface the RDP client expects a DVC plugin to expose.
    internal static readonly Guid IID_IWTSPlugin = new("A1230201-1439-4E62-A414-190D0AC3D40E");

    /// <summary>Read a PE file's machine type without loading it.</summary>
    internal static Bitness ReadPeBitness(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt16() != 0x5A4D) return Bitness.Unknown;   // 'MZ'
            fs.Position = 0x3C;
            int peOffset = br.ReadInt32();
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x0000_4550) return Bitness.Unknown; // 'PE\0\0'
            ushort machine = br.ReadUInt16();
            return machine switch
            {
                0x014C => Bitness.X86,
                0x8664 => Bitness.X64,
                0xAA64 => Bitness.Arm64,
                _ => Bitness.Unknown,
            };
        }
        catch
        {
            return Bitness.Unknown;
        }
    }

    /// <summary>Try to activate a CLSID; return the HRESULT and whether it exposes IWTSPlugin.</summary>
    internal static (int hr, bool isPlugin) ProbeCom(Guid clsid)
    {
        Guid iidUnknown = IID_IUnknown;
        int hr = CoCreateInstance(
            ref clsid, IntPtr.Zero,
            CLSCTX_INPROC_SERVER | CLSCTX_LOCAL_SERVER,
            ref iidUnknown, out IntPtr pUnk);

        if (hr < 0 || pUnk == IntPtr.Zero) return (hr, false);

        bool isPlugin = false;
        Guid iidPlugin = IID_IWTSPlugin;
        if (Marshal.QueryInterface(pUnk, ref iidPlugin, out IntPtr pPlugin) == 0)
        {
            isPlugin = true;
            Marshal.Release(pPlugin);
        }

        Marshal.Release(pUnk);
        return (hr, isPlugin);
    }
}
