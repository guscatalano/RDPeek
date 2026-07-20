using System.Collections.Concurrent;
using Dvc.Diag.Protocol;

namespace Rdpeek.Protocol;

/// <summary>
/// Correlates request/response <see cref="Envelope"/>s over a transport and
/// dispatches unsolicited pushes. Transport-agnostic: the same router drives a DVC
/// channel or the local broker pipe — you supply the send delegate and feed it
/// decoded envelopes via <see cref="Handle"/>.
///
/// Invariant (see proto comments): one side (the client/viewer) allocates
/// <c>request_id</c>s via <see cref="RequestAsync"/>; the other side (the agent)
/// only <see cref="RespondAsync"/>s or <see cref="PushAsync"/>es. That keeps ids
/// unambiguous — a nonzero id we didn't allocate is a peer message, not a response.
/// </summary>
public sealed class EnvelopeRouter
{
    private readonly Func<Envelope, Task> _send;
    private long _nextId;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<Envelope>> _pending = new();

    /// <summary>
    /// Raised for envelopes that are not responses to our outstanding requests:
    /// unsolicited pushes (<c>request_id == 0</c>) and peer-initiated requests.
    /// </summary>
    public event Action<Envelope>? OnMessage;

    public EnvelopeRouter(Func<Envelope, Task> send)
        => _send = send ?? throw new ArgumentNullException(nameof(send));

    private static long NowTicks() => DateTime.UtcNow.Ticks;

    /// <summary>Send a request and await the correlated response.</summary>
    public async Task<Envelope> RequestAsync(Envelope request, CancellationToken ct = default)
    {
        ulong id = (ulong)Interlocked.Increment(ref _nextId);
        request.RequestId = id;
        request.UtcTicks = NowTicks();

        var tcs = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        using var reg = ct.CanBeCanceled
            ? ct.Register(() => { if (_pending.TryRemove(id, out var t)) t.TrySetCanceled(ct); })
            : default;

        try
        {
            await _send(request).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Send an unsolicited push (subscription payload, event). <c>request_id = 0</c>.</summary>
    public Task PushAsync(Envelope message)
    {
        message.RequestId = 0;
        message.UtcTicks = NowTicks();
        return _send(message);
    }

    /// <summary>Reply to a peer request, echoing its <paramref name="toRequestId"/>.</summary>
    public Task RespondAsync(Envelope response, ulong toRequestId)
    {
        response.RequestId = toRequestId;
        response.UtcTicks = NowTicks();
        return _send(response);
    }

    /// <summary>Feed a decoded incoming envelope; completes a pending request or raises <see cref="OnMessage"/>.</summary>
    public void Handle(Envelope incoming)
    {
        if (incoming.RequestId != 0 && _pending.TryRemove(incoming.RequestId, out var tcs))
        {
            tcs.TrySetResult(incoming);
            return;
        }

        OnMessage?.Invoke(incoming);
    }
}
