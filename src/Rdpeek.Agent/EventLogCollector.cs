using System.Diagnostics.Eventing.Reader;
using Dvc.Diag.Protocol;

namespace Rdpeek.Agent;

/// <summary>
/// The most recent entries from a named Windows Event Log (System, Application, …), newest first.
/// Read-only. Some logs (e.g. Security) need elevation; that failure is reported as a note rather
/// than throwing, so the viewer can say why it's empty.
/// </summary>
internal static class EventLogCollector
{
    public static EventLogList Collect(string logName, int max)
    {
        if (string.IsNullOrWhiteSpace(logName)) logName = "System";
        max = Math.Clamp(max <= 0 ? 100 : max, 1, 500);

        var list = new EventLogList { LogName = logName };
        try
        {
            var query = new EventLogQuery(logName, PathType.LogName) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            for (EventRecord? rec = reader.ReadEvent(); rec is not null && list.Entries.Count < max; rec = reader.ReadEvent())
            {
                using (rec)
                {
                    string message;
                    try { message = rec.FormatDescription() ?? ""; }
                    catch { message = ""; }
                    if (message.Length > 400) message = message[..400];
                    message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();

                    list.Entries.Add(new EventLogList.Types.Entry
                    {
                        Level = LevelName(rec),
                        Time = rec.TimeCreated?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                        Source = rec.ProviderName ?? "",
                        EventId = (uint)Math.Max(0, rec.Id),
                        Message = message,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            list.Note = ex.Message;
        }
        return list;
    }

    private static string LevelName(EventRecord rec)
    {
        try
        {
            if (!string.IsNullOrEmpty(rec.LevelDisplayName)) return rec.LevelDisplayName;
        }
        catch { /* provider metadata missing — fall back to the numeric level */ }
        return rec.Level switch
        {
            1 => "Critical",
            2 => "Error",
            3 => "Warning",
            4 => "Information",
            5 => "Verbose",
            _ => "Information",
        };
    }
}
