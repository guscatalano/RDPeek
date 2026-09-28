using Rdpeek.Plugin;
using Xunit;

namespace Plugin.Tests;

/// <summary>
/// Regression tests for the shared plugin server's lifetime. The bug: any one connection's Terminated()
/// shut down the process, killing the plugin for every other open connection ("was working then the
/// client plugin disappeared"). The server must exit only when the LAST connection ends.
/// </summary>
public class PluginLifetimeTests
{
    [Fact]
    public void OneOfTwoConnectionsEnding_DoesNotShutDown()
    {
        int shutdowns = 0;
        var life = new PluginLifetime(() => shutdowns++);

        life.Activated();   // connection A
        life.Activated();   // connection B
        life.Released();    // A ends

        Assert.Equal(0, shutdowns);   // B still open — must NOT shut down
        Assert.Equal(1, life.Live);
    }

    [Fact]
    public void LastConnectionEnding_ShutsDownExactlyOnce()
    {
        int shutdowns = 0;
        var life = new PluginLifetime(() => shutdowns++);

        life.Activated();
        life.Activated();
        life.Released();
        life.Released();    // last one out

        Assert.Equal(1, shutdowns);
        Assert.Equal(0, life.Live);
    }

    [Fact]
    public void SingleConnection_ShutsDownWhenItEnds()
    {
        int shutdowns = 0;
        var life = new PluginLifetime(() => shutdowns++);

        life.Activated();
        life.Released();

        Assert.Equal(1, shutdowns);
    }
}
