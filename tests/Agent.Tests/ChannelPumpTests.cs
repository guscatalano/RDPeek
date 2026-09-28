using System;
using System.Collections.Generic;
using Rdpeek.Agent;
using Xunit;

namespace Agent.Tests;

/// <summary>
/// Regression tests for the agent's connect/serve/retry loop (<see cref="ChannelPump"/>). The agent is
/// a DVC *server* that runs in the RDP session; if the client plugin isn't loaded yet, opening the
/// channel fails — and the agent must keep retrying rather than exit, and must re-open after every
/// disconnect. This encodes that contract without the WTS API.
/// </summary>
public class ChannelPumpTests
{
    [Fact]
    public void With_no_client_plugin_it_keeps_retrying_and_never_serves_or_exits()
    {
        int opens = 0;
        int waits = 0;
        const int stopAfter = 6;

        ChannelPump.Run(
            tryOpen: () => { opens++; return 0; },                 // 0 == no client plugin listening
            serve: _ => throw new Xunit.Sdk.XunitException("serve must not run when the channel never opens"),
            stopped: () => opens >= stopAfter,                      // stop only so the test terminates
            waitBetween: () => waits++);

        Assert.True(opens >= stopAfter, "the agent must keep retrying to open the channel");
        Assert.Equal(opens, waits);                                // one backoff per failed open — it waited, it didn't spin or quit
    }

    [Fact]
    public void It_reopens_the_channel_after_every_disconnect()
    {
        var events = new List<string>();
        int serves = 0;

        ChannelPump.Run(
            tryOpen: () => { events.Add("open"); return 1; },       // channel opens each attempt
            serve: _ => { events.Add("serve"); serves++; },         // returns immediately == client detached
            stopped: () => serves >= 3,
            waitBetween: () => events.Add("wait"));

        Assert.Equal(3, serves);                                    // re-opened + re-served after each disconnect
        // Each cycle is open → serve; it never gives up after the first disconnect.
        Assert.Equal(new[] { "open", "serve", "wait", "open", "serve", "wait", "open", "serve" }, events);
    }

    [Fact]
    public void A_serve_error_is_treated_as_a_disconnect_not_a_crash()
    {
        int opens = 0;

        // serve throwing (a mid-session channel error) must not escape the loop — it re-opens.
        ChannelPump.Run(
            tryOpen: () => { opens++; return 1; },
            serve: _ => throw new InvalidOperationException("channel blew up mid-serve"),
            stopped: () => opens >= 3,
            waitBetween: () => { });

        Assert.True(opens >= 3, "a serve exception should loop back to re-open, not propagate out");
    }

    [Fact]
    public void Stop_requested_before_the_first_attempt_serves_nothing()
    {
        int opens = 0;
        ChannelPump.Run(
            tryOpen: () => { opens++; return 1; },
            serve: _ => throw new Xunit.Sdk.XunitException("should not serve once already stopped"),
            stopped: () => true,                                    // already stopping
            waitBetween: () => { });
        Assert.Equal(0, opens);
    }
}
