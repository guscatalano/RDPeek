using System.Collections.Concurrent;
using Dvc.Diag.Protocol;

namespace Rdpeek.Protocol;

/// <summary>Result of a file-source lookup: found, absent, or outside the allowed roots.</summary>
public enum FileSourceResult { Ok, NotFound, NotAllowed }

/// <summary>
/// Where a <see cref="FilePullService"/> reads files from — the security boundary. The agent's
/// implementation confines access to advertised roots and reads the real disk; a test's reads an
/// in-memory dictionary. Never let a path escape the allowed roots.
/// </summary>
public interface IFileSource
{
    FileSourceResult OpenRead(string path, out Stream? stream, out long size);
    FileSourceResult List(string path, out FileList? list);
}

/// <summary>
/// Agent-side file-<b>pull</b> dispatcher: wires an <see cref="EnvelopeRouter"/> to an
/// <see cref="IFileSource"/>. Answers <see cref="FileListRequest"/>, and for a
/// <see cref="FileOpen"/>(PULL) opens the file and streams it with a <see cref="FilePullSender"/>,
/// routing the client's <see cref="FileAck"/>s to advance the window. PUSH is rejected (this is a
/// pull-only, read-only posture) — a build that enables it wires a writer here.
/// </summary>
public sealed class FilePullService : IDisposable
{
    private readonly EnvelopeRouter _router;
    private readonly IFileSource _source;
    private readonly int _chunkSize;
    private readonly int _windowChunks;
    private readonly ConcurrentDictionary<ulong, FilePullSender> _active = new();

    public FilePullService(EnvelopeRouter router, IFileSource source,
        int chunkSize = 256 * 1024, int windowChunks = 8)
    {
        _router = router;
        _source = source;
        _chunkSize = chunkSize;
        _windowChunks = windowChunks;
        _router.OnMessage += OnMessage;
    }

    private void OnMessage(Envelope env)
    {
        switch (env.BodyCase)
        {
            case Envelope.BodyOneofCase.FileListRequest:
                HandleList(env);
                break;
            case Envelope.BodyOneofCase.FileOpen:
                HandleOpen(env);
                break;
            case Envelope.BodyOneofCase.FileAck when _active.TryGetValue(env.FileAck.TransferId, out var s):
                s.OnAck(env.FileAck.Offset);
                break;
            case Envelope.BodyOneofCase.FileClose when _active.TryRemove(env.FileClose.TransferId, out var aborted):
                aborted.Dispose();   // client aborted
                break;
        }
    }

    private void HandleList(Envelope env)
    {
        var status = _source.List(env.FileListRequest.Path, out var list);
        _ = status switch
        {
            FileSourceResult.Ok => _router.RespondAsync(new Envelope { FileList = list }, env.RequestId),
            FileSourceResult.NotAllowed => Err(env.RequestId, Error.Types.Code.PathNotAllowed, env.FileListRequest.Path),
            _ => Err(env.RequestId, Error.Types.Code.NotFound, env.FileListRequest.Path),
        };
    }

    private void HandleOpen(Envelope env)
    {
        var open = env.FileOpen;
        if (open.Direction != FileOpen.Types.Direction.Pull)
        {
            _ = Err(env.RequestId, Error.Types.Code.NotSupported, "push not enabled on this build");
            return;
        }

        var status = _source.OpenRead(open.Path, out var stream, out var size);
        if (status != FileSourceResult.Ok || stream is null)
        {
            var code = status == FileSourceResult.NotAllowed ? Error.Types.Code.PathNotAllowed : Error.Types.Code.NotFound;
            _ = Err(env.RequestId, code, open.Path);
            return;
        }

        _ = _router.RespondAsync(new Envelope
        {
            FileOpenResult = new FileOpenResult { TransferId = open.TransferId, TotalSize = (ulong)size },
        }, env.RequestId);

        var sender = new FilePullSender(_router.PushAsync, open.TransferId, stream, _chunkSize, _windowChunks);
        _active[open.TransferId] = sender;
        _ = Task.Run(async () =>
        {
            try { await sender.RunAsync().ConfigureAwait(false); }
            finally { _active.TryRemove(open.TransferId, out _); sender.Dispose(); stream.Dispose(); }
        });
    }

    private Task Err(ulong requestId, Error.Types.Code code, string message) =>
        _router.RespondAsync(new Envelope { Error = new Error { Code = code, Message = message } }, requestId);

    public void Dispose()
    {
        _router.OnMessage -= OnMessage;
        foreach (var s in _active.Values) s.Dispose();
        _active.Clear();
    }
}
