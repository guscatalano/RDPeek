using System.Globalization;

namespace Rdpeek.Agent;

/// <summary>
/// Extracts (channel, byte count) from one formatted RDP-server DVC trace event.
///
/// Ported from https://github.com/guscatalano/RDP_DVC_Watcher (ETWListener), which
/// established that the server's DVC provider emits a "firing write flush" event per
/// channel write, carrying the channel name and payload size.
///
/// Deliberately dependency-free (a string in, numbers out) so the fragile part of the
/// ETW port — the text scraping — is unit-testable without an ETW session.
/// </summary>
internal static class DvcTrafficParser
{
    // TraceEvent renders an event as XML-ish text: <Event ... Name="..." Size="1,234" ...>.
    // Matching with the leading space anchors on an attribute boundary, so "Size=" can't
    // match the tail of some other attribute (e.g. "MaxSize=").
    private const string NameAttr = " Name=\"";
    private const string SizeAttr = " Size=\"";

    /// <summary>
    /// True when <paramref name="eventText"/> is a server-side DVC write flush, in which
    /// case <paramref name="channel"/> and <paramref name="bytes"/> describe the write.
    /// </summary>
    public static bool TryParseWriteFlush(string? eventText, out string channel, out long bytes)
    {
        channel = "";
        bytes = 0;
        if (string.IsNullOrEmpty(eventText)) return false;

        // The provider covers both halves of the stack; only the server-side writes carry
        // the per-channel size we're counting.
        if (eventText.IndexOf("serverbase", StringComparison.OrdinalIgnoreCase) < 0) return false;
        if (eventText.IndexOf("firing write flush", StringComparison.OrdinalIgnoreCase) < 0) return false;

        if (!TryReadAttribute(eventText, NameAttr, out channel) || channel.Length == 0) return false;
        if (!TryReadAttribute(eventText, SizeAttr, out string sizeText)) return false;

        // Sizes are group-separated in the rendered text ("1,234"); strip separators and
        // parse invariantly so the agent's locale can't change the numbers.
        sizeText = sizeText.Replace(",", "").Replace(".", "").Trim();
        if (!long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes) || bytes < 0)
        {
            bytes = 0;
            return false;
        }

        return true;
    }

    private static bool TryReadAttribute(string text, string attr, out string value)
    {
        value = "";
        int start = text.IndexOf(attr, StringComparison.Ordinal);
        if (start < 0) return false;
        start += attr.Length;

        int end = text.IndexOf('"', start);
        if (end < 0) return false;

        value = text[start..end];
        return true;
    }
}
