using System.Security.Cryptography;
using Dvc.Diag.Protocol;
using Google.Protobuf;

namespace Rdpeek.Protocol;

/// <summary>Outcome of a client-side file pull.</summary>
public readonly record struct PullResult(bool Ok, long Bytes, FileClose.Types.Status Status, string? Message);

/// <summary>
/// Server side of a windowed file <b>pull</b> (agent → client). Streams a source stream as
/// <see cref="FileChunk"/>s, capping the in-flight (sent-but-unacked) bytes at a window so a large
/// transfer can't overrun the channel's send queue, then sends a <see cref="FileClose"/> carrying
/// the whole-file SHA-256. The caller replies <see cref="FileOpenResult"/> and pumps incoming
/// <see cref="FileAck"/>s in via <see cref="OnAck"/>. Transport-agnostic: it only needs a send
/// delegate (a router push).
/// </summary>
public sealed class FilePullSender : IDisposable
{
    private readonly Func<Envelope, Task> _send;
    private readonly ulong _transferId;
    private readonly Stream _source;
    private readonly int _chunkSize;
    private readonly long _windowBytes;
    private readonly SemaphoreSlim _ackSignal = new(0);
    private long _acked;

    public FilePullSender(Func<Envelope, Task> send, ulong transferId, Stream source,
        int chunkSize = 256 * 1024, int windowChunks = 8)
    {
        _send = send;
        _transferId = transferId;
        _source = source;
        _chunkSize = Math.Max(1, chunkSize);
        _windowBytes = (long)_chunkSize * Math.Max(1, windowChunks);
    }

    /// <summary>Feed an incoming FileAck (contiguous bytes the client has durably received).</summary>
    public void OnAck(ulong offset)
    {
        if ((long)offset > _acked) { _acked = (long)offset; _ackSignal.Release(); }
    }

    /// <summary>Stream the file to completion, then send FileClose. Never throws to the caller —
    /// a read failure is reported as a FAILED FileClose.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[_chunkSize];
        long offset = 0;
        try
        {
            long total = _source.CanSeek ? _source.Length : -1;
            while (true)
            {
                int n = await _source.ReadAsync(buf.AsMemory(0, _chunkSize), ct).ConfigureAwait(false);
                bool last = n == 0 || (total >= 0 && offset + n >= total);

                // Window: don't let sent-but-unacked bytes exceed the window.
                while (offset - _acked >= _windowBytes && !ct.IsCancellationRequested)
                    await _ackSignal.WaitAsync(ct).ConfigureAwait(false);

                if (n > 0)
                {
                    sha.AppendData(buf, 0, n);
                    await _send(new Envelope
                    {
                        FileChunk = new FileChunk
                        {
                            TransferId = _transferId,
                            Offset = (ulong)offset,
                            Data = ByteString.CopyFrom(buf, 0, n),
                            Last = last,
                        },
                    }).ConfigureAwait(false);
                    offset += n;
                }
                else if (offset == 0)
                {
                    // Empty file: one terminal zero-length chunk so the client sees `last`.
                    await _send(new Envelope
                    {
                        FileChunk = new FileChunk { TransferId = _transferId, Offset = 0, Data = ByteString.Empty, Last = true },
                    }).ConfigureAwait(false);
                }

                if (last) break;
            }

            await _send(new Envelope
            {
                FileClose = new FileClose
                {
                    TransferId = _transferId,
                    Status = FileClose.Types.Status.Complete,
                    Sha256 = ByteString.CopyFrom(sha.GetHashAndReset()),
                },
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SafeClose(FileClose.Types.Status.Aborted, "cancelled").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SafeClose(FileClose.Types.Status.Failed, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task SafeClose(FileClose.Types.Status status, string message)
    {
        try
        {
            await _send(new Envelope
            {
                FileClose = new FileClose { TransferId = _transferId, Status = status, Message = message },
            }).ConfigureAwait(false);
        }
        catch { /* channel gone */ }
    }

    public void Dispose() => _ackSignal.Dispose();
}

/// <summary>
/// Client side of a file pull. Sends <see cref="FileOpen"/>(PULL), writes incoming
/// <see cref="FileChunk"/>s to a destination stream (acking each so the sender's window advances),
/// and on <see cref="FileClose"/> verifies the whole-file SHA-256. Self-subscribes to the router
/// for the duration of the transfer, filtering by <c>transfer_id</c>.
/// </summary>
public sealed class FilePullReceiver
{
    private readonly EnvelopeRouter _router;
    private readonly ulong _transferId;
    private readonly Stream _dest;
    private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly TaskCompletionSource<PullResult> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _written;

    public FilePullReceiver(EnvelopeRouter router, ulong transferId, Stream dest)
    {
        _router = router;
        _transferId = transferId;
        _dest = dest;
    }

    /// <summary>Request the pull of <paramref name="path"/> and drive it to completion.</summary>
    public async Task<PullResult> RunAsync(string path, CancellationToken ct = default)
    {
        _router.OnMessage += OnMessage;
        using var reg = ct.Register(() => _done.TrySetResult(new PullResult(false, _written, FileClose.Types.Status.Aborted, "cancelled")));
        try
        {
            var reply = await _router.RequestAsync(new Envelope
            {
                FileOpen = new FileOpen { TransferId = _transferId, Direction = FileOpen.Types.Direction.Pull, Path = path },
            }, ct).ConfigureAwait(false);

            if (reply.BodyCase == Envelope.BodyOneofCase.Error)
                return new PullResult(false, 0, FileClose.Types.Status.Failed, reply.Error.Message);
            if (reply.BodyCase != Envelope.BodyOneofCase.FileOpenResult)
                return new PullResult(false, 0, FileClose.Types.Status.Failed, $"unexpected reply {reply.BodyCase}");

            return await _done.Task.ConfigureAwait(false);
        }
        finally
        {
            _router.OnMessage -= OnMessage;
        }
    }

    private void OnMessage(Envelope env)
    {
        switch (env.BodyCase)
        {
            case Envelope.BodyOneofCase.FileChunk when env.FileChunk.TransferId == _transferId:
                var span = env.FileChunk.Data.Span;
                if (span.Length > 0)
                {
                    _dest.Write(span);
                    _sha.AppendData(span);
                    _written += span.Length;
                }
                _ = _router.PushAsync(new Envelope { FileAck = new FileAck { TransferId = _transferId, Offset = (ulong)_written } });
                break;

            case Envelope.BodyOneofCase.FileClose when env.FileClose.TransferId == _transferId:
                var close = env.FileClose;
                if (close.Status != FileClose.Types.Status.Complete)
                {
                    _done.TrySetResult(new PullResult(false, _written, close.Status, close.Message));
                    break;
                }
                var actual = _sha.GetHashAndReset();
                bool ok = actual.AsSpan().SequenceEqual(close.Sha256.Span);
                _done.TrySetResult(new PullResult(ok, _written, close.Status,
                    ok ? null : "sha256 mismatch"));
                break;
        }
    }
}
