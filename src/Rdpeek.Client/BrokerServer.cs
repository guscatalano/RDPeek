using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Dvc.Diag.Protocol;
using Google.Protobuf;
using Microsoft.Win32.SafeHandles;
using Rdpeek.Client;

namespace Rdpeek.Client;

/// <summary>
/// Hosts the local broker pipe that plugin connections report to. Tracks per-connection
/// agent state (keyed by pid:seq) so the UI can show "agent detected / not detected"
/// live, without watching logs.
/// </summary>
public sealed class BrokerServer : IDisposable
{
    public sealed class AgentState
    {
        public int Pid;
        public int Seq;
        public string Status = "";
        public string Host = "";
        public SysInfoSnapshot? Sysinfo;
        public ProcessList? Procs;
        public NetConnList? Net;
        public SessionList? Sessions;
        public ServiceList? Services;
        public PerfSnapshot? Perf;
        public CounterSample? Counters;
        public ClientLink? Link;
        public SystemDetail? System;
    }

    private readonly ConcurrentDictionary<string, AgentState> _states = new();
    private readonly ConcurrentDictionary<int, StreamWriter> _pluginWriters = new();  // pid -> command sink
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Raised (on a background thread) whenever a plugin reports a change.</summary>
    public event Action? Changed;

    /// <summary>Raised (background thread) for a file-pull update from the plugin: kind is
    /// "pullprogress" or "pulldone", payload is the tab-separated detail.</summary>
    public event Action<string, string>? PullUpdate;

    /// <summary>Raised (background thread) for frame-inspector updates: kind is "framestats" or
    /// "frameanomaly", payload is the tab-separated detail.</summary>
    public event Action<string, string>? FrameUpdate;

    /// <summary>Raised (background thread) for a remote directory listing: kind is "filelist" (path +
    /// entries) or "filelisterror" (path + message).</summary>
    public event Action<string, string>? FileListUpdate;

    /// <summary>Raised (background thread) with the result of a latency probe.</summary>
    public event Action<string>? ProbeUpdate;

    public IReadOnlyList<AgentState> Snapshot() => _states.Values.ToList();

    /// <summary>Send a command line down to a connected plugin (companion → plugin). pid 0 broadcasts
    /// to all. Best-effort — returns false if no matching plugin is connected.</summary>
    public bool SendCommand(int pid, string line)
    {
        bool sent = false;
        foreach (var (p, w) in _pluginWriters)
            if (pid == 0 || p == pid)
                try { w.WriteLine(line); sent = true; } catch { }
        return sent;
    }

    public void Start() => _ = AcceptLoopAsync(_cts.Token);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var server = CreateServer();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                _ = HandleAsync(server);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server)
    {
        var writer = new StreamWriter(server) { AutoFlush = true };   // full-duplex: send commands down
        int connPid = 0;
        try
        {
            using var reader = new StreamReader(server);
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                var parsed = Broker.Parse(line);
                if (parsed is null) continue;

                var (kind, pid, seq, payload) = parsed.Value;
                var key = $"{pid}:{seq}";
                if (pid != 0) { connPid = pid; _pluginWriters[pid] = writer; }

                if (kind == "gone")
                {
                    _states.TryRemove(key, out _);
                    Changed?.Invoke();
                    continue;
                }

                if (kind is "pullprogress" or "pulldone")
                {
                    PullUpdate?.Invoke(kind, payload);
                    continue;
                }

                if (kind is "framestats" or "frame")
                {
                    FrameUpdate?.Invoke(kind, payload);
                    continue;
                }

                if (kind is "filelist" or "filelisterror")
                {
                    FileListUpdate?.Invoke(kind, payload);
                    continue;
                }

                if (kind == "probe")
                {
                    ProbeUpdate?.Invoke(payload);
                    continue;
                }

                var st = _states.GetOrAdd(key, _ => new AgentState { Pid = pid, Seq = seq });
                switch (kind)
                {
                    case "connected":
                    case "listening":
                        st.Status = kind;
                        if (!string.IsNullOrEmpty(payload)) st.Host = payload;
                        break;
                    case "sysinfo":
                        try
                        {
                            st.Sysinfo = SysInfoSnapshot.Parser.ParseJson(payload);
                            if (string.IsNullOrEmpty(st.Host)) st.Host = st.Sysinfo.HostName;
                        }
                        catch { }
                        break;
                    case "procs":
                        try { st.Procs = ProcessList.Parser.ParseJson(payload); } catch { }
                        break;
                    case "net":
                        try { st.Net = NetConnList.Parser.ParseJson(payload); } catch { }
                        break;
                    case "sessions":
                        try { st.Sessions = SessionList.Parser.ParseJson(payload); } catch { }
                        break;
                    case "services":
                        try { st.Services = ServiceList.Parser.ParseJson(payload); } catch { }
                        break;
                    case "perf":
                        try { st.Perf = PerfSnapshot.Parser.ParseJson(payload); } catch { }
                        break;
                    case "counters":
                        try { st.Counters = CounterSample.Parser.ParseJson(payload); } catch { }
                        break;
                    case "link":
                        try { st.Link = ClientLink.Parser.ParseJson(payload); } catch { }
                        break;
                    case "system":
                        try { st.System = SystemDetail.Parser.ParseJson(payload); } catch { }
                        break;
                }

                Changed?.Invoke();
            }
        }
        catch
        {
            // client closed / malformed — ignore
        }
        finally
        {
            if (connPid != 0) _pluginWriters.TryRemove(new KeyValuePair<int, StreamWriter>(connPid, writer));
            server.Dispose();
        }
    }

    /// <summary>
    /// Create the broker pipe and drop its mandatory integrity label to Low, so the
    /// plugin (medium integrity, launched by mstsc) can write to it even if the
    /// companion happens to run elevated. Best-effort — a plain pipe still works when
    /// the companion runs non-elevated (same integrity as the plugin).
    /// </summary>
    private static NamedPipeServerStream CreateServer()
    {
        var pipe = new NamedPipeServerStream(
            Broker.PipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try { ApplyLowIntegrityLabel(pipe.SafePipeHandle); } catch { /* best-effort */ }
        return pipe;
    }

    private const int SE_KERNEL_OBJECT = 6;
    private const int LABEL_SECURITY_INFORMATION = 0x10;
    private const uint SDDL_REVISION_1 = 1;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out IntPtr securityDescriptor, out uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorSacl(
        IntPtr securityDescriptor, out bool saclPresent, out IntPtr sacl, out bool saclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(
        SafePipeHandle handle, int objectType, int securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static void ApplyLowIntegrityLabel(SafePipeHandle handle)
    {
        // "S:(ML;;NW;;;LW)" = Low mandatory label, no-write-up. Setting a label at or
        // below the caller's own integrity needs no special privilege.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("S:(ML;;NW;;;LW)", SDDL_REVISION_1, out IntPtr sd, out _))
            return;
        try
        {
            if (GetSecurityDescriptorSacl(sd, out bool present, out IntPtr sacl, out _) && present)
                SetSecurityInfo(handle, SE_KERNEL_OBJECT, LABEL_SECURITY_INFORMATION,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
        }
        finally
        {
            LocalFree(sd);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
