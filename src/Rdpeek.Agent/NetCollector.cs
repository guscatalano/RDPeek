using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Dvc.Diag.Protocol;

namespace Rdpeek.Agent;

/// <summary>Active TCP connections + listening ports with owning process (IPv4).</summary>
internal static class NetCollector
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid;
    }

    private static readonly string[] StateNames =
        { "", "CLOSED", "LISTEN", "SYN-SENT", "SYN-RCVD", "ESTABLISHED", "FIN-WAIT1",
          "FIN-WAIT2", "CLOSE-WAIT", "CLOSING", "LAST-ACK", "TIME-WAIT", "DELETE-TCB" };

    public static NetConnList Collect()
    {
        var list = new NetConnList();

        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return list;

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return list;

            int count = Marshal.ReadInt32(buf);
            var names = ProcessNames();
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(buf + 4 + i * rowSize);
                string state = row.State < StateNames.Length ? StateNames[row.State] : row.State.ToString();
                bool listen = state == "LISTEN";
                list.Entries.Add(new NetConnList.Types.Entry
                {
                    Protocol = "TCP",
                    Local = $"{Addr(row.LocalAddr)}:{Port(row.LocalPort)}",
                    Remote = listen ? "" : $"{Addr(row.RemoteAddr)}:{Port(row.RemotePort)}",
                    State = state,
                    Pid = row.OwningPid,
                    Process = names.GetValueOrDefault(row.OwningPid, ""),
                });
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return list;
    }

    private static string Addr(uint netOrder) => new IPAddress(BitConverter.GetBytes(netOrder)).ToString();

    // Port is the low 16 bits in network byte order.
    private static int Port(uint p) => ((int)(p & 0xFF) << 8) | ((int)((p >> 8) & 0xFF));

    private static Dictionary<uint, string> ProcessNames()
    {
        var map = new Dictionary<uint, string>();
        foreach (var p in Process.GetProcesses())
        {
            try { map[(uint)p.Id] = p.ProcessName; } catch { /* exited */ }
        }
        return map;
    }
}
