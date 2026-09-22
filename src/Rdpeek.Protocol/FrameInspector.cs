using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Dvc.Diag.Protocol;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Rdpeek.Protocol;

/// <summary>One decoded field of a frame's message, for the inspector's field tree.</summary>
public sealed record FrameField(string Name, string Value, IReadOnlyList<FrameField> Children);

/// <summary>
/// One tapped frame: framing metadata, the decoded Envelope (body case + a field tree), and any
/// anomalies found. This is what a "why is my message malformed" view renders per row.
/// </summary>
public sealed record FrameRecord(
    long Seq,
    DateTime TimestampUtc,
    string Direction,
    int SizeBytes,
    bool Decoded,
    string BodyCase,
    ulong RequestId,
    IReadOnlyList<FrameField> Fields,
    IReadOnlyList<string> Anomalies);

/// <summary>
/// A tolerant tap over a DVC channel's byte stream: it re-implements the <c>[4-byte LE len][Envelope]</c>
/// framing but, unlike <see cref="FrameDecoder"/>, never throws — a bad length prefix, an oversized or
/// truncated frame, or an undecodable payload is <em>reported</em> as an anomaly so a developer can
/// see exactly what went wrong on the wire. Feed it whatever bytes flow (per direction); it yields a
/// <see cref="FrameRecord"/> per complete frame plus one for a fatal framing error.
/// </summary>
public sealed class FrameInspector
{
    private readonly Dictionary<string, Buffer> _byDirection = new();
    private long _seq;

    private sealed class Buffer { public byte[] Data = Array.Empty<byte>(); public int Len; }

    /// <summary>Feed bytes seen in a direction ("in"/"out"); returns the frames completed by them.</summary>
    public IReadOnlyList<FrameRecord> Push(string direction, ReadOnlySpan<byte> data)
    {
        var buf = _byDirection.TryGetValue(direction, out var b) ? b : _byDirection[direction] = new Buffer();
        Append(buf, data);

        var records = new List<FrameRecord>();
        int pos = 0;
        while (buf.Len - pos >= 4)
        {
            uint declared = BinaryPrimitives.ReadUInt32LittleEndian(buf.Data.AsSpan(pos, 4));

            if (declared > Frame.MaxFrameSize)
            {
                // Corrupt/oversized length prefix — we can't trust the stream from here, so report and
                // resync by dropping what we have (a real tap would re-establish on the next channel read).
                records.Add(Bad(direction, (int)Math.Min(declared, int.MaxValue),
                    $"length prefix {declared} exceeds the {Frame.MaxFrameSize} byte cap — likely corruption or a lost frame boundary"));
                buf.Len = 0;
                return records;
            }

            if (buf.Len - pos - 4 < declared) break;   // frame not fully arrived yet
            pos += 4;
            var payload = buf.Data.AsSpan(pos, (int)declared);
            pos += (int)declared;
            records.Add(Decode(direction, payload));
        }

        // Keep the unconsumed tail.
        Compact(buf, pos);
        return records;
    }

    private FrameRecord Decode(string direction, ReadOnlySpan<byte> payload)
    {
        var anomalies = new List<string>();
        if (payload.Length == 0) anomalies.Add("zero-length frame (no Envelope)");

        Envelope? env = null;
        try { env = Envelope.Parser.ParseFrom(payload); }
        catch (Exception ex) { anomalies.Add($"decode failed: {ex.Message}"); }

        string bodyCase = env?.BodyCase.ToString() ?? "—";
        ulong requestId = env?.RequestId ?? 0;

        if (env is not null)
        {
            if (env.BodyCase == Envelope.BodyOneofCase.None && payload.Length > 0)
                anomalies.Add("no body set (empty oneof)");
            // NB: request_id is a correlation id the client allocates and the agent echoes — not a
            // per-stream sequence number. Under any concurrency (e.g. a windowed file pull) ids
            // legitimately arrive non-monotonically, so a passive tap cannot flag "out of order"
            // without false positives. We deliberately do not.
        }

        var fields = env is null ? Array.Empty<FrameField>() : ExtractFields(env, depth: 3);
        return new FrameRecord(++_seq, DateTime.UtcNow, direction, payload.Length + 4,
            env is not null, bodyCase, requestId, fields, anomalies);
    }

    private FrameRecord Bad(string direction, int size, string anomaly) =>
        new(++_seq, DateTime.UtcNow, direction, size, false, "—", 0,
            Array.Empty<FrameField>(), new[] { anomaly });

    // ── field tree via protobuf reflection ────────────────────────────────────

    private static IReadOnlyList<FrameField> ExtractFields(IMessage msg, int depth)
    {
        var result = new List<FrameField>();
        foreach (var f in msg.Descriptor.Fields.InFieldNumberOrder())
        {
            object? value;
            try { value = f.Accessor.GetValue(msg); }
            catch { continue; }
            if (IsUnset(f, value)) continue;

            if (value is IMessage sub && depth > 0)
                result.Add(new FrameField(f.Name, sub.Descriptor.Name, ExtractFields(sub, depth - 1)));
            else if (value is System.Collections.IList list && list.Count > 0)
                result.Add(new FrameField(f.Name, $"[{list.Count}]",
                    depth > 0 ? list.Cast<object>().Take(20).Select((e, i) =>
                        e is IMessage m ? new FrameField($"[{i}]", m.Descriptor.Name, ExtractFields(m, depth - 1))
                                        : new FrameField($"[{i}]", Stringify(e), Array.Empty<FrameField>())).ToList()
                    : Array.Empty<FrameField>()));
            else
                result.Add(new FrameField(f.Name, Stringify(value), Array.Empty<FrameField>()));
        }
        return result;
    }

    private static bool IsUnset(FieldDescriptor f, object? value) => value switch
    {
        null => true,
        string s => s.Length == 0,
        bool b => !b,
        ByteString bs => bs.Length == 0,
        System.Collections.IList { Count: 0 } => true,
        IMessage => false,
        _ => IsZeroNumber(value),
    };

    private static bool IsZeroNumber(object v) => v switch
    {
        int i => i == 0,
        uint u => u == 0,
        long l => l == 0,
        ulong ul => ul == 0,
        double d => d == 0,
        float fl => fl == 0,
        Enum e => Convert.ToInt64(e) == 0,
        _ => false,
    };

    private static string Stringify(object? v) => v switch
    {
        null => "",
        ByteString bs => $"{bs.Length} bytes",
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    // ── buffer plumbing ────────────────────────────────────────────────────────

    private static void Append(Buffer buf, ReadOnlySpan<byte> data)
    {
        if (buf.Len + data.Length > buf.Data.Length)
        {
            var grown = new byte[Math.Max(buf.Len + data.Length, buf.Data.Length == 0 ? 4096 : buf.Data.Length * 2)];
            Array.Copy(buf.Data, grown, buf.Len);
            buf.Data = grown;
        }
        data.CopyTo(buf.Data.AsSpan(buf.Len));
        buf.Len += data.Length;
    }

    private static void Compact(Buffer buf, int consumed)
    {
        if (consumed == 0) return;
        int remaining = buf.Len - consumed;
        if (remaining > 0) Array.Copy(buf.Data, consumed, buf.Data, 0, remaining);
        buf.Len = remaining;
    }
}
