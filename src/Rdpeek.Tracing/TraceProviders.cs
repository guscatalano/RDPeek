namespace Rdpeek.Tracing;

/// <summary>Which side of the RDP connection a trace is captured on.</summary>
public enum TraceSide
{
    /// <summary>The session host (the in-session agent) — server-side RDP/DVC providers.</summary>
    Server,
    /// <summary>The local machine running mstsc/msrdc — client-side RDP providers.</summary>
    Client,
}

/// <summary>One ETW provider to enable in a trace: a friendly name, its GUID, and (for
/// documentation only) the module it lives in.</summary>
public readonly record struct TraceProvider(string Name, Guid Id, string Module = "")
{
    public override string ToString() => Module.Length > 0 ? $"{Name} ({Module})" : Name;
}

/// <summary>
/// The provider sets an RDPeek trace captures, and the parser that turns a user-supplied
/// spec string into a concrete list. Kept dependency-free (no ETW types) so the selection
/// logic is unit-testable without a trace session.
///
/// The default sets mirror what the rest of the codebase already listens to:
///   * Server  — the RDP-server DVC provider used by <c>DvcTrafficWatcher</c> plus the
///                 broader RemoteDesktopServices server providers.
///   * Client  — the mstsc/msrdc providers used by <c>ClientDvcProbe</c>.
/// </summary>
public static class TraceProviders
{
    // ---- Server (session host) side -------------------------------------------------

    /// <summary>
    /// Server-side RDP providers. The first is the same GUID <c>DvcTrafficWatcher</c>
    /// enables (Microsoft-Windows-RemoteDesktopServices, the DVC write-flush source); the
    /// rest broaden the capture to the server RDP core so the .etl is useful for more than
    /// just per-channel byte counts.
    /// </summary>
    public static readonly IReadOnlyList<TraceProvider> ServerDefaults = new[]
    {
        new TraceProvider("Microsoft-Windows-RemoteDesktopServices",             new Guid("8375996d-5801-4fe9-b0ae-f5c428758960"), "rdpserverbase.dll"),
        new TraceProvider("Microsoft.Windows.RemoteDesktop.ServerBase",          new Guid("8375996d-5801-4fe9-b0ae-f5c428758960"), "rdpserverbase.dll"),
        new TraceProvider("Microsoft-Windows-TerminalServices-RemoteConnectionManager", new Guid("c76baa63-ae81-421c-b425-340b4b24157f"), "termsrv.dll"),
        new TraceProvider("Microsoft-Windows-TerminalServices-LocalSessionManager",     new Guid("5d896912-022d-40aa-a3a8-4fa5515c76d7"), "lsm.dll"),
    };

    // ---- Client (mstsc/msrdc) side --------------------------------------------------

    /// <summary>
    /// Client-side RDP providers — the exact set <c>Rdpeek.Doctor.ClientDvcProbe</c>
    /// enables for its real-time DVC probe. GUIDs were read out of the shipping binaries'
    /// TraceLogging metadata (several override the name hash, so they can't be re-derived).
    /// </summary>
    public static readonly IReadOnlyList<TraceProvider> ClientDefaults = new[]
    {
        new TraceProvider("Microsoft.Windows.RemoteDesktop.ClientCore",           new Guid("080656c2-c24f-4660-8f5a-ce83656b0e7c"), "mstscax.dll"),
        new TraceProvider("Microsoft.Windows.RemoteDesktop.Base",                 new Guid("5795aab9-b0e3-419e-b0ef-7aef943cffa8"), "rdpbase.dll"),
        new TraceProvider("Microsoft.Windows.RemoteDesktop.Legacy",               new Guid("3ef15adf-1300-44a1-b85c-2a83549f5b9e"), "RdpRelayTransport.dll"),
        new TraceProvider("Microsoft.Windows.RDP.NamedPipe",                      new Guid("eb6594d8-6fad-53f7-350e-f4e4c531f68c"), "mstscax.dll"),
        new TraceProvider("Microsoft.Windows.RemoteDesktopServices.RailPlugin",   new Guid("43471865-f3ee-5dcf-bf8b-193fcbbe0f37"), "mstscax.dll"),
        new TraceProvider("Microsoft-Windows-TerminalServices-ClientActiveXCore", new Guid("28aa95bb-d444-4719-a36f-40462168127e"), "mstscax.dll"),
    };

    /// <summary>Defaults for a side.</summary>
    public static IReadOnlyList<TraceProvider> DefaultsFor(TraceSide side) =>
        side == TraceSide.Server ? ServerDefaults : ClientDefaults;

    /// <summary>Every provider we know a friendly name for, across both sides. Used to
    /// resolve a name token in a spec.</summary>
    public static IReadOnlyDictionary<string, TraceProvider> KnownByName { get; } = BuildKnown();

    private static Dictionary<string, TraceProvider> BuildKnown()
    {
        var map = new Dictionary<string, TraceProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ServerDefaults) map[p.Name] = p;
        foreach (var p in ClientDefaults) map[p.Name] = p;
        return map;
    }

    /// <summary>
    /// Turn a user spec into a concrete provider list. The spec is a comma- or
    /// semicolon-separated list of tokens; each token is one of:
    ///   * empty spec              => the defaults for <paramref name="side"/>
    ///   * a known provider name   => resolved from <see cref="KnownByName"/>
    ///   * a GUID                   => enabled by GUID, named "(guid)"
    ///   * name=guid               => an explicit name/GUID pair
    /// Duplicate GUIDs are collapsed (first name wins). Unparseable tokens are collected in
    /// <paramref name="rejected"/> rather than throwing, so a typo degrades to a note.
    /// </summary>
    public static IReadOnlyList<TraceProvider> Parse(string? spec, TraceSide side, out IReadOnlyList<string> rejected)
    {
        var bad = new List<string>();
        rejected = bad;

        if (string.IsNullOrWhiteSpace(spec))
            return DefaultsFor(side);

        var result = new List<TraceProvider>();
        var seen = new HashSet<Guid>();

        foreach (var raw in spec.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseToken(raw, out var provider)) { bad.Add(raw); continue; }
            if (seen.Add(provider.Id)) result.Add(provider);
        }

        // A spec that named only garbage falls back to the defaults rather than tracing nothing.
        return result.Count > 0 ? result : DefaultsFor(side);
    }

    /// <summary>Convenience overload that discards the rejected list.</summary>
    public static IReadOnlyList<TraceProvider> Parse(string? spec, TraceSide side) => Parse(spec, side, out _);

    private static bool TryParseToken(string token, out TraceProvider provider)
    {
        provider = default;
        if (token.Length == 0) return false;

        int eq = token.IndexOf('=');
        if (eq > 0)
        {
            string name = token[..eq].Trim();
            string guidText = token[(eq + 1)..].Trim();
            if (name.Length > 0 && Guid.TryParse(guidText, out var g))
            {
                provider = new TraceProvider(name, g);
                return true;
            }
            return false;
        }

        if (KnownByName.TryGetValue(token, out var known))
        {
            provider = known;
            return true;
        }

        if (Guid.TryParse(token, out var guid))
        {
            provider = new TraceProvider("(guid)", guid);
            return true;
        }

        return false;
    }
}
