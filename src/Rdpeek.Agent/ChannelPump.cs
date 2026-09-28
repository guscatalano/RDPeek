namespace Rdpeek.Agent;

/// <summary>
/// The agent's connect / serve / retry loop, factored out and kept dependency-free so its contract is
/// unit-testable without the WTS API: <b>keep retrying to open the channel until a client plugin is
/// listening, and re-open after every disconnect — never give up, never exit</b> (until asked to stop).
/// <see cref="ServeLoop"/> wires the real WtsChannel open/serve/close into it.
/// </summary>
internal static class ChannelPump
{
    /// <param name="tryOpen">Opens the diagnostics channel; returns a live handle, or 0 when no client
    /// plugin is listening yet (no RDP session, or RDPeek not loaded on the client).</param>
    /// <param name="serve">Serves a live handle until the channel closes (i.e. a disconnect).</param>
    /// <param name="stopped">Ends the loop (e.g. Ctrl+C).</param>
    /// <param name="waitBetween">Backoff between attempts (production: 1s).</param>
    public static void Run(Func<nint> tryOpen, Action<nint> serve, Func<bool> stopped, Action waitBetween)
    {
        while (!stopped())
        {
            nint h = tryOpen();
            if (h == 0)
            {
                // No client-side listener yet: wait and retry. This is the whole point — a server
                // whose client hasn't connected (or reconnected) waits, it does not fail.
                waitBetween();
                continue;
            }

            try { serve(h); }
            catch { /* a serve error is just a disconnect; fall through and re-open */ }

            if (!stopped()) waitBetween();
        }
    }
}
