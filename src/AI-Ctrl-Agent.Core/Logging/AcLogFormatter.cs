using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Core.Logging;

public static class AcLogFormatter
{
    private static readonly string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";

    public static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "FTL",
        _ => "???",
    };

    public static string ComposeLine(
        DateTimeOffset timestamp,
        LogLevel level,
        int threadId,
        string category,
        string message,
        string scopeSuffix,
        Exception? exception)
    {
        var builder = new StringBuilder(256);
        builder.Append(timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        builder.Append(" [").Append(LevelTag(level)).Append(']');
        builder.Append(" [T").Append(threadId.ToString("D2", CultureInfo.InvariantCulture)).Append(']');
        builder.Append(" [").Append(category).Append("] ");
        builder.Append(message);

        if (!string.IsNullOrEmpty(scopeSuffix))
        {
            builder.Append(' ').Append(scopeSuffix);
        }

        if (exception is not null)
        {
            builder.Append('\n');
            builder.Append(Indent(exception.ToString(), 4));
        }

        return builder.ToString();
    }

    public static string Indent(string text, int spaces)
    {
        var prefix = new string(' ', spaces);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var builder = new StringBuilder(text.Length + (lines.Length * spaces));

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(prefix).Append(lines[i]);
        }

        return builder.ToString();
    }
}
