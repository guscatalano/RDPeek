using Dvc.Diag.Protocol;
using Rdpeek.Protocol;
using Xunit;

namespace Protocol.Tests;

public class EnvelopeRouterTests
{
    [Fact]
    public async Task RequestAsync_completes_on_matching_response()
    {
        EnvelopeRouter router = null!;
        // Simulate the peer: on send, immediately reply echoing the request id.
        router = new EnvelopeRouter(sent =>
        {
            router.Handle(new Envelope
            {
                RequestId = sent.RequestId,
                SysinfoSnapshot = new SysInfoSnapshot { HostName = "REMOTE01" },
            });
            return Task.CompletedTask;
        });

        var reply = await router.RequestAsync(
            new Envelope { SysinfoRequest = new SysInfoRequest() });

        Assert.Equal("REMOTE01", reply.SysinfoSnapshot.HostName);
    }

    [Fact]
    public async Task RequestAsync_assigns_increasing_nonzero_ids()
    {
        var seen = new List<ulong>();
        EnvelopeRouter router = null!;
        router = new EnvelopeRouter(sent =>
        {
            seen.Add(sent.RequestId);
            router.Handle(new Envelope { RequestId = sent.RequestId, Ping = new Ping() });
            return Task.CompletedTask;
        });

        await router.RequestAsync(new Envelope { Ping = new Ping() });
        await router.RequestAsync(new Envelope { Ping = new Ping() });

        Assert.Equal(2, seen.Count);
        Assert.All(seen, id => Assert.NotEqual(0ul, id));
        Assert.True(seen[1] > seen[0]);
    }

    [Fact]
    public void Push_has_zero_request_id_and_reaches_OnMessage()
    {
        Envelope? captured = null;
        var router = new EnvelopeRouter(sent =>
        {
            captured = sent;
            return Task.CompletedTask;
        });

        router.PushAsync(new Envelope { CounterSample = new CounterSample() });

        Assert.NotNull(captured);
        Assert.Equal(0ul, captured!.RequestId);
    }

    [Fact]
    public void Unsolicited_push_raises_OnMessage()
    {
        var router = new EnvelopeRouter(_ => Task.CompletedTask);
        Envelope? pushed = null;
        router.OnMessage += e => pushed = e;

        router.Handle(new Envelope { RequestId = 0, CounterSample = new CounterSample() });

        Assert.NotNull(pushed);
        Assert.Equal(Envelope.BodyOneofCase.CounterSample, pushed!.BodyCase);
    }

    [Fact]
    public void Unknown_response_id_is_treated_as_peer_message()
    {
        var router = new EnvelopeRouter(_ => Task.CompletedTask);
        Envelope? routed = null;
        router.OnMessage += e => routed = e;

        // Nonzero id we never allocated => peer-initiated, not a response.
        router.Handle(new Envelope { RequestId = 999, ProcessListRequest = new ProcessListRequest() });

        Assert.NotNull(routed);
        Assert.Equal(Envelope.BodyOneofCase.ProcessListRequest, routed!.BodyCase);
    }
}
