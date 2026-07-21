using System.Runtime.InteropServices;
using Dvc.Diag.Protocol;
using Microsoft.Win32;

namespace Rdpeek.Agent;

/// <summary>Win32 services (name, display, status, start type).</summary>
internal static class ServiceCollector
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusExW(
        IntPtr hSCManager, int infoLevel, int serviceType, int serviceState,
        IntPtr services, uint bufSize, out uint bytesNeeded, out uint servicesReturned,
        ref uint resumeHandle, string? groupName);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr h);

    private const uint SC_MANAGER_ACCESS = 0x0005; // CONNECT | ENUMERATE_SERVICE
    private const int SC_ENUM_PROCESS_INFO = 0;
    private const int SERVICE_WIN32 = 0x30;
    private const int SERVICE_STATE_ALL = 0x3;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode,
                    dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint, dwProcessId, dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUM_SERVICE_STATUS_PROCESS
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public SERVICE_STATUS_PROCESS ServiceStatusProcess;
    }

    private static readonly string[] StateNames =
        { "", "Stopped", "StartPending", "StopPending", "Running", "ContinuePending", "PausePending", "Paused" };

    public static ServiceList Collect()
    {
        var list = new ServiceList();

        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ACCESS);
        if (scm == IntPtr.Zero) return list;

        try
        {
            uint resume = 0;
            EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
                IntPtr.Zero, 0, out uint needed, out _, ref resume, null);
            if (needed == 0) return list;

            IntPtr buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                resume = 0;
                if (!EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
                        buf, needed, out needed, out uint returned, ref resume, null))
                    return list;

                int stride = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();
                for (int i = 0; i < returned; i++)
                {
                    var s = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(buf + i * stride);
                    string name = Marshal.PtrToStringUni(s.lpServiceName) ?? "";
                    uint st = s.ServiceStatusProcess.dwCurrentState;
                    list.Services.Add(new ServiceList.Types.Service
                    {
                        Name = name,
                        Display = Marshal.PtrToStringUni(s.lpDisplayName) ?? "",
                        Status = st < StateNames.Length ? StateNames[st] : st.ToString(),
                        StartType = StartType(name),
                    });
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
        return list;
    }

    private static string StartType(string serviceName)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (k?.GetValue("Start") is int start)
                return start switch { 0 => "Boot", 1 => "System", 2 => "Auto", 3 => "Manual", 4 => "Disabled", _ => start.ToString() };
        }
        catch { }
        return "";
    }
}
