using System.Runtime.InteropServices;
using Dvc.Diag.Protocol;
using static Rdpeek.Agent.NativeApi;

namespace Rdpeek.Agent;

/// <summary>Enumerates processes in the current session (or all sessions) via WTS.</summary>
internal static class ProcessCollector
{
    public static ProcessList Collect(bool allSessions, uint currentSessionId)
    {
        var result = new ProcessList();

        uint level = 1; // WTS_PROCESS_INFO_EX
        uint target = allSessions ? WTS_ANY_SESSION : currentSessionId;

        if (!WTSEnumerateProcessesExW(WTS_CURRENT_SERVER_HANDLE, ref level, target, out IntPtr pInfo, out uint count))
            return result;

        try
        {
            int stride = Marshal.SizeOf<WTS_PROCESS_INFO_EXW>();
            for (uint i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_PROCESS_INFO_EXW>(pInfo + (int)(i * stride));
                result.Processes.Add(new ProcessList.Types.Proc
                {
                    Pid = info.ProcessId,
                    SessionId = info.SessionId,
                    ImageName = Marshal.PtrToStringUni(info.pProcessName) ?? "",
                    UserName = ResolveSid(info.pUserSid),
                    WorkingSet = info.WorkingSetSize,
                });
            }
        }
        finally
        {
            WTSFreeMemoryExW(WTSTypeProcessInfoLevel1, pInfo, count);
        }

        return result;
    }
}
