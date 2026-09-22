using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Rdpeek.Plugin;

/// <summary>
/// Out-of-process COM server host: registers the class object with COM so the RDP
/// client can activate the plugin (LocalServer32), then waits until the client is
/// done (IWTSPlugin.Terminated) before shutting down.
///
/// MTA is used so the router's async continuations can call back on the channel from
/// thread-pool threads without cross-apartment marshaling.
/// </summary>
internal static class PluginHost
{
    // Must match tools/register.ps1 and InspectorPlugin's [Guid].
    public const string ClsidString = "7B6D1E44-9C1A-4C7E-9E2B-11A0C0FFEE01";

    public static readonly ManualResetEventSlim Shutdown = new(false);

    private const uint COINIT_MULTITHREADED = 0x0;
    private const uint CLSCTX_LOCAL_SERVER = 0x4;
    private const uint REGCLS_MULTIPLEUSE = 0x1;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        ref Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
        uint dwClsContext, uint flags, out uint lpdwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);

    public static int RunServer()
    {
        // Everything before this point — CreateProcess, host resolution, CLR startup, JIT —
        // is invisible to the log, and it is the part mstsc actually waits on during COM
        // activation. Measure it, so "the COM spin-up hangs" is a number and not a guess.
        double startupMs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        Logger.Log($"server starting (-Embedding) — {startupMs:F0}ms from process create to entry");
        CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);

        var clsid = new Guid(ClsidString);
        var factory = new PluginClassFactory();

        int hr = CoRegisterClassObject(ref clsid, factory, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out uint cookie);
        if (hr < 0)
        {
            Logger.Log($"CoRegisterClassObject failed 0x{hr:X8}");
            CoUninitialize();
            return hr;
        }

        Logger.Log("class object registered — awaiting client");
        Shutdown.Wait();

        CoRevokeClassObject(cookie);
        CoUninitialize();
        Logger.Log("server stopped");
        return 0;
    }
}
