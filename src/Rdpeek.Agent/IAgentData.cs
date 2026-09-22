using Dvc.Diag.Protocol;

namespace Rdpeek.Agent;

/// <summary>
/// The source of the host-inventory snapshots the agent serves. <see cref="RealAgentData"/> reads
/// this machine via the collectors; <see cref="FakeAgentData"/> returns canned "remote server" data
/// for demos. Only the enumeration collectors are behind this seam — the DVC handshake, frames, and
/// file transfer stay genuinely live either way.
/// </summary>
internal interface IAgentData
{
    SysInfoSnapshot SysInfo();
    ProcessList Processes(bool allSessions);
    NetConnList NetConn();
    SessionList Sessions();
    ServiceList Services();
    PerfSnapshot Perf();
    CounterSample DvcCounters();
    SystemDetail SystemDetail();
    EventLogList EventLog(string logName, int max);
}

/// <summary>Real collectors — the shipped behaviour.</summary>
internal sealed class RealAgentData(uint sessionId) : IAgentData
{
    public SysInfoSnapshot SysInfo() => SysInfoCollector.Collect();
    public ProcessList Processes(bool allSessions) => ProcessCollector.Collect(allSessions, sessionId);
    public NetConnList NetConn() => NetCollector.Collect();
    public SessionList Sessions() => SessionCollector.Collect();
    public ServiceList Services() => ServiceCollector.Collect();
    public PerfSnapshot Perf() => PerfCollector.Collect();
    public CounterSample DvcCounters() => Agent.DvcCounters.Snapshot();
    public SystemDetail SystemDetail() => SystemDetailCollector.Collect();
    public EventLogList EventLog(string logName, int max) => EventLogCollector.Collect(logName, max);
}
