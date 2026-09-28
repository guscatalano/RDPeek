using Rdpeek.Tracing;
using Xunit;

namespace Tracing.Tests;

/// <summary>
/// The provider selection is the fragile, user-facing part of tracing (a spec string in, a concrete
/// provider list out), so it is pinned here without an ETW session in the loop.
/// </summary>
public class TraceProvidersTests
{
    [Fact]
    public void EmptySpec_UsesServerDefaults()
    {
        var result = TraceProviders.Parse(null, TraceSide.Server, out var rejected);
        Assert.Equal(TraceProviders.ServerDefaults, result);
        Assert.Empty(rejected);
    }

    [Fact]
    public void EmptySpec_UsesClientDefaults()
    {
        var result = TraceProviders.Parse("   ", TraceSide.Client);
        Assert.Equal(TraceProviders.ClientDefaults, result);
    }

    [Fact]
    public void KnownName_ResolvesToProvider()
    {
        var result = TraceProviders.Parse("Microsoft.Windows.RemoteDesktop.ClientCore", TraceSide.Client, out var rejected);
        Assert.Single(result);
        Assert.Equal(new Guid("080656c2-c24f-4660-8f5a-ce83656b0e7c"), result[0].Id);
        Assert.Empty(rejected);
    }

    [Fact]
    public void KnownName_IsCaseInsensitive()
    {
        var result = TraceProviders.Parse("microsoft.windows.remotedesktop.clientcore", TraceSide.Client);
        Assert.Single(result);
        Assert.Equal(new Guid("080656c2-c24f-4660-8f5a-ce83656b0e7c"), result[0].Id);
    }

    [Fact]
    public void BareGuid_IsAccepted()
    {
        var g = Guid.NewGuid();
        var result = TraceProviders.Parse(g.ToString(), TraceSide.Server, out var rejected);
        Assert.Single(result);
        Assert.Equal(g, result[0].Id);
        Assert.Equal("(guid)", result[0].Name);
        Assert.Empty(rejected);
    }

    [Fact]
    public void NameEqualsGuid_PairIsAccepted()
    {
        var g = Guid.NewGuid();
        var result = TraceProviders.Parse($"MyProvider={g}", TraceSide.Server);
        Assert.Single(result);
        Assert.Equal("MyProvider", result[0].Name);
        Assert.Equal(g, result[0].Id);
    }

    [Fact]
    public void MultipleTokens_SplitOnCommaAndSemicolon()
    {
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var result = TraceProviders.Parse($"{g1} ; {g2}", TraceSide.Server);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.Id == g1);
        Assert.Contains(result, p => p.Id == g2);
    }

    [Fact]
    public void DuplicateGuid_IsCollapsed()
    {
        var g = Guid.NewGuid();
        var result = TraceProviders.Parse($"{g},{g}", TraceSide.Server);
        Assert.Single(result);
    }

    [Fact]
    public void OnlyGarbage_FallsBackToDefaults_AndReports()
    {
        var result = TraceProviders.Parse("not-a-guid, also nonsense", TraceSide.Server, out var rejected);
        Assert.Equal(TraceProviders.ServerDefaults, result);
        Assert.Equal(2, rejected.Count);
    }

    [Fact]
    public void MixedGoodAndBad_KeepsGoodReportsBad()
    {
        var g = Guid.NewGuid();
        var result = TraceProviders.Parse($"{g}, garbage", TraceSide.Server, out var rejected);
        Assert.Single(result);
        Assert.Equal(g, result[0].Id);
        Assert.Single(rejected);
        Assert.Equal("garbage", rejected[0]);
    }

    [Fact]
    public void ServerAndClientDefaults_AreNonEmptyAndDistinctSets()
    {
        Assert.NotEmpty(TraceProviders.ServerDefaults);
        Assert.NotEmpty(TraceProviders.ClientDefaults);
        Assert.Equal(TraceProviders.ServerDefaults, TraceProviders.DefaultsFor(TraceSide.Server));
        Assert.Equal(TraceProviders.ClientDefaults, TraceProviders.DefaultsFor(TraceSide.Client));
    }
}
