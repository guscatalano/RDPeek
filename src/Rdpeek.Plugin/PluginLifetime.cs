using System;
using System.Threading;

namespace Rdpeek.Plugin;

/// <summary>
/// Ref-counts live plugin instances for the shared out-of-process server (one process serves every RDP
/// connection via REGCLS_MULTIPLEUSE). The server must exit only when the LAST connection ends — not the
/// first — or ending one session kills the plugin for every other open connection. Pure and dependency-
/// free so it's unit-testable; PluginHost owns one instance wired to Shutdown.
/// </summary>
internal sealed class PluginLifetime
{
    private int _live;
    private readonly Action _onLastReleased;

    public PluginLifetime(Action onLastReleased) => _onLastReleased = onLastReleased;

    /// <summary>Live instance count (for tests/diagnostics).</summary>
    public int Live => Volatile.Read(ref _live);

    /// <summary>A connection's plugin was initialized.</summary>
    public void Activated() => Interlocked.Increment(ref _live);

    /// <summary>A connection's plugin terminated. Fires the shutdown callback only when it was the last.</summary>
    public void Released()
    {
        if (Interlocked.Decrement(ref _live) <= 0) _onLastReleased();
    }
}
