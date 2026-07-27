using Rdpeek.Agent;
using Xunit;

namespace Agent.Tests;

/// <summary>
/// The ETW route recognises a channel write by scraping the rendered event text, so
/// these cases pin the shape it accepts — including the near-misses it must reject.
/// </summary>
public class DvcTrafficParserTests
{
    // Representative of what TraceEvent renders for the RDP server's write-flush event.
    private const string WriteFlush =
        "<Event MSec=\"1234.5678\" PID=\"980\" PName=\"svchost\" TID=\"4444\" " +
        "EventName=\"CServerBase::FiringWriteFlush\" ProviderName=\"Microsoft-Windows-RemoteDesktopServices\" " +
        "Name=\"Microsoft::Windows::RDS::Graphics\" Size=\"12,345\" Message=\"CServerBase firing write flush\"/>";

    [Fact]
    public void ParsesChannelAndSize()
    {
        Assert.True(DvcTrafficParser.TryParseWriteFlush(WriteFlush, out string channel, out long bytes));
        Assert.Equal("Microsoft::Windows::RDS::Graphics", channel);
        Assert.Equal(12345, bytes);
    }

    [Fact]
    public void ParsesSizeWithoutGroupSeparators()
    {
        string line = WriteFlush.Replace("Size=\"12,345\"", "Size=\"987\"");
        Assert.True(DvcTrafficParser.TryParseWriteFlush(line, out _, out long bytes));
        Assert.Equal(987, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    // Right provider, wrong event: only write flushes carry a per-channel size.
    [InlineData("<Event EventName=\"CServerBase::OnChannelOpen\" Name=\"chan\" Size=\"10\"/>")]
    // A flush from somewhere other than the server side.
    [InlineData("<Event EventName=\"CClientBase\" Name=\"chan\" Size=\"10\" Message=\"firing write flush\"/>")]
    public void RejectsEventsThatAreNotServerWriteFlushes(string? line)
        => Assert.False(DvcTrafficParser.TryParseWriteFlush(line, out _, out _));

    [Fact]
    public void RejectsTruncatedAttributes()
    {
        Assert.False(DvcTrafficParser.TryParseWriteFlush(
            "<Event EventName=\"CServerBase firing write flush\" Name=\"chan", out _, out _));

        Assert.False(DvcTrafficParser.TryParseWriteFlush(
            "<Event EventName=\"CServerBase firing write flush\" Name=\"chan\" Size=\"abc\"/>", out _, out _));
    }

    [Fact]
    public void RejectsMissingChannelName()
        => Assert.False(DvcTrafficParser.TryParseWriteFlush(
            "<Event EventName=\"CServerBase firing write flush\" Name=\"\" Size=\"10\"/>", out _, out _));

    [Fact]
    public void DoesNotMatchSizeInsideAnotherAttribute()
    {
        // "MaxSize=" must not be mistaken for the payload's own "Size=" attribute.
        string line = "<Event EventName=\"CServerBase firing write flush\" Name=\"chan\" MaxSize=\"999\"/>";
        Assert.False(DvcTrafficParser.TryParseWriteFlush(line, out _, out _));
    }
}

public class RateTrackerTests
{
    private const long Second = TimeSpan.TicksPerSecond;

    [Fact]
    public void FirstSampleHasNoRate()
        => Assert.Equal(0, new RateTracker().Sample("chan", 1000, Second));

    [Fact]
    public void DerivesBytesPerSecondFromTheDelta()
    {
        var tracker = new RateTracker();
        tracker.Sample("chan", 1000, 10 * Second);
        Assert.Equal(500, tracker.Sample("chan", 3000, 14 * Second));
    }

    [Fact]
    public void TracksEachKeySeparately()
    {
        var tracker = new RateTracker();
        tracker.Sample("a", 100, Second);
        tracker.Sample("b", 0, Second);
        Assert.Equal(100, tracker.Sample("a", 200, 2 * Second));
        Assert.Equal(0, tracker.Sample("b", 0, 2 * Second));
    }

    [Fact]
    public void ReportsZeroWhenACounterResets()
    {
        // A channel that closed and reopened restarts its totals; that must not read as
        // a huge negative (or wrap into a huge positive) rate.
        var tracker = new RateTracker();
        tracker.Sample("chan", 5000, Second);
        Assert.Equal(0, tracker.Sample("chan", 10, 2 * Second));
    }

    [Fact]
    public void RetainDropsChannelsThatWentAway()
    {
        var tracker = new RateTracker();
        tracker.Sample("gone", 1000, Second);
        tracker.Retain(new HashSet<string> { "still-here" });

        // "gone" was forgotten, so it reads as a first sample again.
        Assert.Equal(0, tracker.Sample("gone", 2000, 2 * Second));
    }
}
