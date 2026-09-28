using Rdpeek.Client;
using Xunit;

namespace ClientLogic.Tests;

/// <summary>
/// Regression tests for agent↔window correlation — the logic that repeatedly broke for multi-connection,
/// gateway and Cloud PC setups ("no agent on all", "no agent on two", plugin correlation). Each test
/// encodes a scenario that shipped broken at some point.
/// </summary>
public class AgentCorrelationTests
{
    private static RdpWindow Win(int pid, string host, nint hwnd) => new(hwnd, pid, $"{host} - Remote Desktop", host);

    private static BrokerServer.AgentState Agent(int seq, string host, string status = "connected", int clientPid = 0) =>
        new() { Pid = 5000, Seq = seq, ClientPid = clientPid, Host = host, Status = status };

    [Fact]
    public void SingleWindow_SingleAgent_IsAssigned_EvenWithoutAnyMatch()
    {
        var windows = new[] { Win(pid: 100, host: "ABC", hwnd: 1) };          // title host ABC
        var states = new[] { Agent(seq: 1, host: "XYZ") };                    // agent reports XYZ, no pid
        var map = AgentCorrelation.Assign(windows, states);
        Assert.Same(states[0], map[(nint)1].State);                          // order-pair catches the lone pair
    }

    [Fact]
    public void TwoConnections_MatchedByClientPid_EvenWhenHostNamesDiffer()
    {
        // The gateway/Cloud PC case: window titles never match the agent host, but the plugin reported
        // the client pid, so each agent joins its own window exactly.
        var windows = new[] { Win(101, "ABC", 1), Win(202, "DEF", 2) };
        var states = new[]
        {
            Agent(seq: 1, host: "CPC-AAA", clientPid: 202),
            Agent(seq: 2, host: "CPC-BBB", clientPid: 101),
        };
        var map = AgentCorrelation.Assign(windows, states);
        Assert.Equal("CPC-BBB", map[(nint)1].State!.Host);   // window pid 101 -> agent with clientPid 101
        Assert.Equal("CPC-AAA", map[(nint)2].State!.Host);   // window pid 202 -> agent with clientPid 202
    }

    [Fact]
    public void TwoConnections_MatchedByHostName_CaseAndDomainInsensitive()
    {
        var windows = new[] { Win(1, "server01.corp.local", 1), Win(2, "SERVER02", 2) };
        var states = new[] { Agent(1, "SERVER01"), Agent(2, "server02.corp.local") };
        var map = AgentCorrelation.Assign(windows, states);
        Assert.Equal("SERVER01", map[(nint)1].State!.Host);
        Assert.Equal("server02.corp.local", map[(nint)2].State!.Host);
    }

    [Fact]
    public void TwoConnections_NoPidNoHostMatch_OrderPairsSoNeitherIsBlank()
    {
        // Old plugin (no client pid) through a gateway: names can't match, but both still get an agent
        // (order-pair) instead of both showing "no agent".
        var windows = new[] { Win(1, "ABC", 10), Win(2, "DEF", 20) };
        var states = new[] { Agent(1, "XYZ"), Agent(2, "UVW") };
        var map = AgentCorrelation.Assign(windows, states);
        Assert.NotNull(map[(nint)10].State);
        Assert.NotNull(map[(nint)20].State);
        Assert.NotSame(map[(nint)10].State, map[(nint)20].State);   // distinct agents, never doubled up
    }

    [Fact]
    public void SameHost_TwoConnections_DisambiguatedByClientPid()
    {
        // Two sessions to the SAME host — host match can't tell them apart, but client pid can.
        var windows = new[] { Win(101, "HOST", 1), Win(202, "HOST", 2) };
        var states = new[] { Agent(1, "HOST", clientPid: 101), Agent(2, "HOST", clientPid: 202) };
        var map = AgentCorrelation.Assign(windows, states);
        Assert.Equal(101, map[(nint)1].State!.ClientPid);
        Assert.Equal(202, map[(nint)2].State!.ClientPid);
    }

    [Fact]
    public void PluginsListeningButNoAgent_ShowsNoAgent()
    {
        var windows = new[] { Win(1, "ABC", 1) };
        var states = new[] { Agent(1, "", status: "listening") };   // client side up, no agent yet
        var map = AgentCorrelation.Assign(windows, states);
        Assert.Null(map[(nint)1].State);
        Assert.Contains("no agent", map[(nint)1].Text);
    }

    [Fact]
    public void ClientPidWins_OverAnAvailableHostMatch()
    {
        // Pass 0 (pid) must claim before pass 1 (host), so pid is authoritative.
        var windows = new[] { Win(101, "HOST", 1) };
        var states = new[]
        {
            Agent(seq: 1, host: "HOST", clientPid: 999),   // host matches but wrong pid
            Agent(seq: 2, host: "OTHER", clientPid: 101),  // pid matches this window
        };
        var map = AgentCorrelation.Assign(windows, states);
        Assert.Equal(101, map[(nint)1].State!.ClientPid);
        Assert.Equal("OTHER", map[(nint)1].State!.Host);
    }

    [Fact]
    public void HostsMatch_Normalizes()
    {
        Assert.True(AgentCorrelation.HostsMatch("SERVER01", "server01"));
        Assert.True(AgentCorrelation.HostsMatch("server01.corp.local", "SERVER01"));
        Assert.False(AgentCorrelation.HostsMatch("server01", "server02"));
        Assert.False(AgentCorrelation.HostsMatch("", "server01"));
    }
}
