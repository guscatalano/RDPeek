using Rdpeek.Client;

namespace Rdpeek.Bootstrap;

/// <summary>
/// Watches for the provisioned agent to come up, from two independent signals raced against each
/// other — whichever fires first wins (we only care that <em>one</em> of them saw it):
///
/// <list type="number">
/// <item><b>Broker</b> — hosts the local <c>rdpeek-broker</c> pipe (<see cref="BrokerServer"/>); the
/// RDPeek plugin (loaded into our hosted control via rdpeek-vc-shim.dll) reports <c>connected</c>
/// the moment the agent opens its DVC.</item>
/// <item><b>Plugin log</b> — tails <c>%TEMP%\rdpeek-plugin.log</c> for a fresh
/// <c>OnNewChannelConnection</c> / <c>agent capabilities</c> line (works even if no broker signal
/// arrives, e.g. the plugin couldn't reach the pipe).</item>
/// </list>
///
/// Only lines written after construction count, so a stale log from a previous run never
/// false-positives. Construct this <em>before</em> connecting so the pipe is already listening when
/// the plugin first reports.
/// </summary>
internal sealed class AgentWatch : IDisposable
{
    private readonly BrokerServer _broker = new();
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), "rdpeek-plugin.log");
    private readonly long _logBaseline;
    private readonly TaskCompletionSource<string> _hit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AgentWatch()
    {
        _logBaseline = FileLength(_logPath);
        _broker.Changed += OnBrokerChanged;
        _broker.Start();
    }

    private void OnBrokerChanged()
    {
        // "connected" = the agent opened dvc::diag::inspector; "listening" is only our own plugin
        // listener, so it doesn't count as the agent being up.
        foreach (var st in _broker.Snapshot())
            if (st.Status == "connected")
            {
                _hit.TrySetResult($"broker: agent DVC connected (plugin pid {st.Pid}" +
                    (string.IsNullOrEmpty(st.Host) ? "" : $", host {st.Host}") + ")");
                return;
            }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for either signal. Returns a short
    /// description of the winning signal, or null if neither fired in time.</summary>
    public async Task<string?> WaitAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (_hit.Task.IsCompleted) return _hit.Task.Result;
            if (ScanLog() is { } fromLog) return fromLog;

            var slice = TimeSpan.FromMilliseconds(400);
            var remaining = deadline - DateTime.UtcNow;
            if (remaining < slice) slice = remaining;
            if (slice <= TimeSpan.Zero) break;

            // Wake early if the broker signal lands; otherwise poll the log every ~400ms.
            var completed = await Task.WhenAny(_hit.Task, Task.Delay(slice, ct)).ConfigureAwait(false);
            if (completed == _hit.Task) return _hit.Task.Result;
        }
        return _hit.Task.IsCompleted ? _hit.Task.Result : ScanLog();
    }

    /// <summary>Reads only the bytes appended since construction and looks for the plugin's
    /// channel-connect markers.</summary>
    private string? ScanLog()
    {
        try
        {
            if (!File.Exists(_logPath)) return null;
            using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length <= _logBaseline) return null;
            fs.Seek(_logBaseline, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var appended = reader.ReadToEnd();
            if (appended.Contains("OnNewChannelConnection") || appended.Contains("agent capabilities:"))
                return "log: plugin saw OnNewChannelConnection";
        }
        catch { /* log locked / rotating — try again next tick */ }
        return null;
    }

    private static long FileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    public void Dispose() => _broker.Dispose();
}
