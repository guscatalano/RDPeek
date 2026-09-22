using Dvc.Diag.Protocol;

namespace Rdpeek.Agent;

/// <summary>
/// Canned host-inventory data for demos — a plausible remote server ("DEMO-SRV01") so the companion
/// shows realistic processes/network/sessions/services without exposing the machine actually running
/// the agent. The live parts (handshake, frames, file pull) are unaffected.
/// </summary>
internal sealed class FakeAgentData : IAgentData
{
    private readonly string _host;
    private readonly Persona _p;

    /// <summary>
    /// <paramref name="host"/> becomes the reported HostName (so a viewer can correlate this agent to
    /// the RDP window that reached it), and also seeds a "persona" — a different OS/CPU/load — so that
    /// simulating several connections shows visibly distinct hosts, not the same server N times.
    /// </summary>
    public FakeAgentData(string? host = null)
    {
        _host = string.IsNullOrWhiteSpace(host) ? "DEMO-SRV01" : host;
        var lastNum = new string(_host.Split('.', '-', '_').LastOrDefault(s => s.Any(char.IsDigit))?
            .Where(char.IsDigit).ToArray() ?? Array.Empty<char>());
        int seed = int.TryParse(lastNum, out var v) ? v : _host.Sum(c => c);
        _p = Personas[Math.Abs(seed) % Personas.Length];
    }

    private sealed record Persona(
        string Os, string Edition, string DisplayVer, uint Build, uint Ubr,
        string Cpu, uint CpuLogical, double CpuPct, ulong MemGb, ulong MemFreeGb);

    private static readonly Persona[] Personas =
    {
        new("Windows Server 2022 Datacenter", "ServerDatacenter", "21H2", 20348, 2340,
            "Intel(R) Xeon(R) Gold 6338 CPU @ 2.00GHz", 16, 23.7, 64, 41),
        new("Windows Server 2019 Standard", "ServerStandard", "1809", 17763, 5830,
            "AMD EPYC 7402P 24-Core Processor", 24, 61.2, 128, 33),
        new("Windows 11 Enterprise", "Enterprise", "23H2", 22631, 4169,
            "Intel(R) Core(TM) i7-1370P", 20, 8.9, 32, 19),
    };

    public SysInfoSnapshot SysInfo() => new()
    {
        HostName = _host,
        OsProductName = _p.Os,
        OsDisplayVer = _p.DisplayVer,
        OsBuild = _p.Build,
        OsUbr = _p.Ubr,
        UptimeMs = 9L * 24 * 3600 * 1000 + 4 * 3600 * 1000,   // ~9.2 days
        CpuName = _p.Cpu,
        CpuLogical = _p.CpuLogical,
        CpuPercent = _p.CpuPct,
        MemTotalBytes = _p.MemGb * 1024 * 1024 * 1024,
        MemAvailBytes = _p.MemFreeGb * 1024 * 1024 * 1024,
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
        C("Processor Time", _p.CpuPct, "%");
        C("Available Memory", _p.MemFreeGb * 1024, "MB");
        C("Disk Queue Length", 0.03, "");
        C("Processes", 148, "");

        // RemoteFX Network + Graphics — the RDP link-quality counters, which really exist only on the
        // session host. Names match RemoteFxCollector; values jitter a little so the Link tab looks live.
        const string inst = "RDP-Tcp 2";
        double J(double v, double spread) => Math.Round(v * (1 + (_rng.NextDouble() - 0.5) * spread), 2);
        void L(string name, double value) =>
            snap.Counters.Add(new PerfSnapshot.Types.Counter { Name = name, Value = value, Group = "link", Instance = inst });
        void G(string name, double value) =>
            snap.Counters.Add(new PerfSnapshot.Types.Counter { Name = name, Value = value, Group = "graphics", Instance = inst });
        L("Current TCP RTT", J(28, 0.4));
        L("Base TCP RTT", 24);
        L("Current TCP Bandwidth", J(48_000_000, 0.1));
        L("Total Sent Rate", J(3_500_000, 0.3));
        L("Total Received Rate", J(180_000, 0.3));
        L("Loss Rate", J(0.1, 1.0));
        L("Retransmission Rate", J(0.2, 1.0));
        G("Frame Quality", J(92, 0.06));
        G("Average Encoding Time", J(6, 0.4));
        G("Input Frames/Second", J(30, 0.2));
        G("Output Frames/Second", J(28, 0.2));
        G("Frames Skipped/Second - Insufficient Network Resources", J(0.5, 1.0));
        G("Graphics Compression ratio", J(12, 0.2));
        return snap;
    }

    public SystemDetail SystemDetail()
    {
        var d = new SystemDetail
        {
            BuildLabEx = "20348.1.amd64fre.fe_release.210507-1500",
            BuildLab = "20348.fe_release.210507-1500",
            EditionId = "ServerDatacenter",
            DisplayVersion = "21H2",
            InstallDate = "2024-11-03",
            RegisteredOwner = "Contoso IT",
            BootTimeTicks = (DateTime.UtcNow - TimeSpan.FromMilliseconds(9L * 24 * 3600 * 1000 + 4 * 3600 * 1000)).Ticks,
            UptimeMs = 9L * 24 * 3600 * 1000 + 4 * 3600 * 1000,
        };
        void H(string id, string desc, string on) =>
            d.Hotfixes.Add(new SystemDetail.Types.Hotfix { HotfixId = id, Description = desc, InstalledOn = on });
        H("KB5031364", "Security Update", "9/12/2025");
        H("KB5030216", "Update", "8/14/2025");
        H("KB5028171", "Security Update", "7/10/2025");
        H("KB5027225", "Servicing Stack Update", "6/13/2025");

        d.Gpus.Add(new SystemDetail.Types.Gpu
        {
            Name = "Microsoft Hyper-V Video", DriverVersion = "10.0.20348.1", DriverDate = "2021-05-07",
            VramBytes = 8UL * 1024 * 1024, Status = "OK",
        });
        d.Gpus.Add(new SystemDetail.Types.Gpu
        {
            Name = "NVIDIA A16-4Q (vGPU)", DriverVersion = "537.13", DriverDate = "2025-08-22",
            VramBytes = 4UL * 1024 * 1024 * 1024, Status = "OK",
        });

        void P(string name, string cls, string status, string problem) =>
            d.Devices.Add(new SystemDetail.Types.PnpDevice { Name = name, DeviceClass = cls, Status = status, Problem = problem });
        P("Microsoft Hyper-V Network Adapter", "Net", "OK", "");
        P("NVIDIA A16-4Q", "Display", "OK", "");
        P("Generic PnP Monitor", "Monitor", "OK", "");
        P("Intel 82574L Gigabit Network", "Net", "Error", "CM_PROB 28: drivers not installed");
        P("Remote Desktop Device Redirector Bus", "System", "OK", "");
        return d;
    }

    // Live-looking per-channel traffic: bytes accumulate across polls, rates jitter ±15%.
    private long _tick;
    private readonly Random _rng = new();

    public CounterSample DvcCounters()
    {
        _tick++;
        var s = new CounterSample { Source = "perfmon", Note = "Per-channel traffic on the session host (demo data)." };
        void Ch(string name, ulong baseSent, ulong baseRecv, double sendRate, double recvRate, double rtt)
        {
            double j = 0.85 + _rng.NextDouble() * 0.30;
            s.Channels.Add(new CounterSample.Types.ChannelCounters
            {
                Name = name,
                BytesSent = baseSent + (ulong)(_tick * sendRate * 3),      // ~3s per poll
                BytesReceived = baseRecv + (ulong)(_tick * recvRate * 3),
                SendRateBps = sendRate * j,
                RecvRateBps = recvRate * j,
                RttMs = rtt > 0 ? rtt * j : 0,
            });
        }
        Ch("graphics", 120_000_000, 40_000, 3_500_000, 800, 0);
        Ch("input", 8_000, 900_000, 200, 42_000, 0);
        Ch("rdpdr", 3_000_000, 4_800_000, 90_000, 150_000, 1.2);
        Ch("cliprdr", 700_000, 350_000, 0, 0, 0.9);
        Ch("drdynvc", 90_000, 84_000, 300, 280, 0);
        Ch("dvc::diag::inspector", 200_000, 480_000, 2_000, 4_800, 2.1);
        return s;
    }
}
