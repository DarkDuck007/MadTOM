using System;

namespace MadTOM.Models;

public sealed class LogEntryModel
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string FormattedTime => Timestamp.ToString("HH:mm:ss.fff");
    public string Message { get; set; } = string.Empty;
    public string Source { get; set; } = "madtomd";
    public string Level { get; set; } = "INFO";
}

