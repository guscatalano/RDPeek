using Dvc.Diag.Protocol;

namespace Rdpeek.Agent;

/// <summary>
/// Canned host-inventory data for demos — a plausible remote server ("DEMO-SRV01") so the companion
/// shows realistic processes/network/sessions/services without exposing the machine actually running
/// the agent. The live parts (handshake, frames, file pull) are unaffected.
/// </summary>
internal sealed class FakeAgentData : IAgentData
{
    public SysInfoSnapshot SysInfo() => new()
    {
        HostName = "DEMO-SRV01",
        OsProductName = "Windows Server 2022 Datacenter",
        OsDisplayVer = "21H2",
        OsBuild = 20348,
        OsUbr = 2340,
        UptimeMs = 9L * 24 * 3600 * 1000 + 4 * 3600 * 1000,   // ~9.2 days
        CpuName = "Intel(R) Xeon(R) Gold 6338 CPU @ 2.00GHz",
        CpuLogical = 16,
        CpuPercent = 23.7,
        MemTotalBytes = 64UL * 1024 * 1024 * 1024,
        MemAvailBytes = 41UL * 1024 * 1024 * 1024,
        UserName = "CONTOSO\\svc-app",
        SessionId = 2,
        ClientName = "RDPEEK-DEMO",
        Protocol = "RDP",
    };

    public ProcessList Processes(bool allSessions)
    {
        var list = new ProcessList();
        void P(uint pid, uint sess, string img, string user, ulong ws) =>
            list.Processes.Add(new ProcessList.Types.Proc { Pid = pid, SessionId = sess, ImageName = img, UserName = user, WorkingSet = ws });
        P(4, 0, "System", "SYSTEM", 148_000);
        P(732, 0, "svchost.exe", "SYSTEM", 62_000_000);
        P(1120, 0, "sqlservr.exe", "CONTOSO\\svc-sql", 4_820_000_000);
        P(2044, 0, "w3wp.exe", "CONTOSO\\svc-app", 1_240_000_000);
        P(2200, 2, "explorer.exe", "CONTOSO\\svc-app", 96_000_000);
        P(3312, 2, "powershell.exe", "CONTOSO\\svc-app", 88_000_000);
        P(3760, 0, "MsMpEng.exe", "SYSTEM", 210_000_000);
        P(4880, 2, "rdpeek-agent.exe", "CONTOSO\\svc-app", 34_000_000);
        return list;
    }

    public NetConnList NetConn()
    {
        var list = new NetConnList();
        void N(string proto, string local, string remote, string state, string proc, uint pid) =>
            list.Entries.Add(new NetConnList.Types.Entry { Protocol = proto, Local = local, Remote = remote, State = state, Process = proc, Pid = pid });
        N("TCP", "0.0.0.0:3389", "0.0.0.0:0", "LISTEN", "svchost.exe", 732);
        N("TCP", "0.0.0.0:445", "0.0.0.0:0", "LISTEN", "System", 4);
        N("TCP", "0.0.0.0:1433", "0.0.0.0:0", "LISTEN", "sqlservr.exe", 1120);
        N("TCP", "0.0.0.0:80", "0.0.0.0:0", "LISTEN", "w3wp.exe", 2044);
        N("TCP", "10.0.0.12:3389", "10.0.0.1:52193", "ESTABLISHED", "svchost.exe", 732);
        N("TCP", "10.0.0.12:1433", "10.0.0.44:51120", "ESTABLISHED", "sqlservr.exe", 1120);
        N("TCP", "10.0.0.12:49712", "52.96.44.10:443", "ESTABLISHED", "w3wp.exe", 2044);
        return list;
    }

    public SessionList Sessions()
    {
        var list = new SessionList();
        void S(uint id, string station, string user, string state, string client) =>
            list.Sessions.Add(new SessionList.Types.Session { SessionId = id, Station = station, User = user, State = state, ClientName = client });
        S(0, "Services", "", "Disconnected", "");
        S(1, "Console", "", "Connected", "");
        S(2, "RDP-Tcp#3", "CONTOSO\\svc-app", "Active", "RDPEEK-DEMO");
        return list;
    }

    public ServiceList Services()
    {
        var list = new ServiceList();
        void S(string name, string status, string start, string display) =>
            list.Services.Add(new ServiceList.Types.Service { Name = name, Status = status, StartType = start, Display = display });
        S("MSSQLSERVER", "Running", "Auto", "SQL Server (MSSQLSERVER)");
        S("W3SVC", "Running", "Auto", "World Wide Web Publishing Service");
        S("TermService", "Running", "Auto", "Remote Desktop Services");
        S("WinDefend", "Running", "Auto", "Microsoft Defender Antivirus Service");
        S("Spooler", "Stopped", "Disabled", "Print Spooler");
        S("wuauserv", "Stopped", "Manual", "Windows Update");
        return list;
    }

    public PerfSnapshot Perf()
    {
        var snap = new PerfSnapshot();
        void C(string name, double value, string unit) =>
            snap.Counters.Add(new PerfSnapshot.Types.Counter { Name = name, Value = value, Unit = unit, Group = "host" });
        C("Processor Time", 23.7, "%");
        C("Available Memory", 41984, "MB");
        C("Disk Queue Length", 0.03, "");
        C("Processes", 148, "");
        return snap;
    }
}
