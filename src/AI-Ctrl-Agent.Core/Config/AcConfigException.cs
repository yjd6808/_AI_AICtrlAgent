namespace AiCtrl.Core.Config;

public sealed class AcConfigException : Exception
{
    public AcConfigException(string filePath, int line, int column, string reason, Exception? innerException = null)
        : base($"{filePath}:{line}:{column} {reason}", innerException)
    {
        FilePath = filePath;
        Line = line;
        Column = column;
        Reason = reason;
    }

    public string FilePath { get; }

    public int Line { get; }

    public int Column { get; }

    public string Reason { get; }
}
