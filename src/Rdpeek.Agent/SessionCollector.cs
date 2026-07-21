using System.Runtime.InteropServices;
using Dvc.Diag.Protocol;
using static Rdpeek.Agent.NativeApi;

namespace Rdpeek.Agent;

/// <summary>All sessions on the host (console + RDP) via WTSEnumerateSessions.</summary>
internal static class SessionCollector
{
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int count);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTS_SESSION_INFOW
    {
        public uint SessionId;
        public IntPtr pWinStationName;
        public int State;
    }

    private static readonly string[] StateNames =
        { "Active", "Connected", "ConnectQuery", "Shadow", "Disconnected",
          "Idle", "Listen", "Reset", "Down", "Init" };

    public static SessionList Collect()
    {
        var list = new SessionList();
        if (!WTSEnumerateSessionsW(WTS_CURRENT_SERVER_HANDLE, 0, 1, out IntPtr pp, out int count))
            return list;

        try
        {
            int stride = Marshal.SizeOf<WTS_SESSION_INFOW>();
            for (int i = 0; i < count; i++)
            {
                var si = Marshal.PtrToStructure<WTS_SESSION_INFOW>(pp + i * stride);
                string user = QuerySessionString(si.SessionId, WTS_INFO_CLASS.WTSUserName);
                string domain = QuerySessionString(si.SessionId, WTS_INFO_CLASS.WTSDomainName);

                list.Sessions.Add(new SessionList.Types.Session
                {
                    SessionId = si.SessionId,
                    Station = Marshal.PtrToStringUni(si.pWinStationName) ?? "",
                    State = si.State >= 0 && si.State < StateNames.Length ? StateNames[si.State] : si.State.ToString(),
                    User = string.IsNullOrEmpty(user) ? "" : (string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}"),
                    ClientName = QuerySessionString(si.SessionId, WTS_INFO_CLASS.WTSClientName),
                });
            }
        }
        finally
        {
            WTSFreeMemory(pp);
        }
        return list;
    }
}
