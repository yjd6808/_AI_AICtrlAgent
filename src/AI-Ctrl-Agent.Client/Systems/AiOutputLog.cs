using System.Globalization;
using System.Text;
using AiCtrl.Core.Domain;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Systems;

internal sealed class AiOutputLog
{
    private readonly ILogger _logger;
    private readonly MonitorOptions _options;

    public AiOutputLog(ILogger logger, MonitorOptions options)
    {
        _logger = logger;
        _options = options;
    }

    public void UserPrompt(SessionRef session, string messageId, string text) =>
        _logger.LogInformation(
            "{Header}\n{Body}",
            $"[user prompt] {{session={session.SessionId} msg={messageId}}}",
            Indent(text));

    public void AssistantText(SessionRef session, string messageId, ModelRef? model, string text)
    {
        var modelPart = model is null ? string.Empty : $" model={model.ProviderId}/{model.ModelId}";
        var (body, truncated, _) = Cap(text, 64 * 1024);
        _logger.LogInformation(
            "{Header}\n{Body}{Suffix}",
            $"[assistant text] {{session={session.SessionId} msg={messageId}{modelPart}}}",
            Indent(body),
            truncated ? "\n    (truncated)" : string.Empty);
    }

    public void Reasoning(SessionRef session, string messageId, string text)
    {
        if (!_options.LogReasoning)
        {
            return;
        }

        var (body, truncated, _) = Cap(text, 64 * 1024);
        _logger.LogInformation(
            "{Header}\n{Body}{Suffix}",
            $"[reasoning] {{session={session.SessionId} msg={messageId}}}",
            Indent(body),
            truncated ? "\n    (truncated)" : string.Empty);
    }

    public void ToolCompleted(SessionRef session, string partId, ToolPartView tool)
    {
        var duration = tool.StartedAt is { } started && tool.CompletedAt is { } completed
            ? FormatDuration(completed - started)
            : null;
        var durationPart = duration is null ? string.Empty : $" {duration}";
        var statusPart = string.IsNullOrEmpty(tool.Status) ? "completed" : tool.Status;
        var header = $"[tool {tool.Tool} {statusPart}{durationPart}] {{session={session.SessionId} part={partId}}}";

        var lines = new List<string> { header };

        if (!string.IsNullOrEmpty(tool.Input))
        {
            var input = Cap(tool.Input, _options.LogToolInputMaxKb * 1024);
            lines.Add($"    input: {input.Text}{(input.Truncated ? " (truncated)" : string.Empty)}");
        }

        if (!string.IsNullOrEmpty(tool.OutputPreview))
        {
            var output = Cap(tool.OutputPreview, _options.LogToolOutputMaxKb * 1024);
            var sizeKb = output.Bytes / 1024.0;
            var note = output.Truncated
                ? $"output({sizeKb.ToString("F1", CultureInfo.InvariantCulture)}KB, 앞 {_options.LogToolOutputMaxKb}KB까지 기록)"
                : $"output({sizeKb.ToString("F1", CultureInfo.InvariantCulture)}KB)";
            lines.Add($"    {note}: {Indent(output.Text)}");
        }

        _logger.LogInformation("{Message}", string.Join('\n', lines));
    }

    public void Subtask(SessionRef session, string partId, string? description) =>
        _logger.LogInformation(
            "{Header}{Description}",
            $"[subtask] {{session={session.SessionId} part={partId}}}",
            description is null ? string.Empty : " " + Cap(description, 2000).Text);

    public void Error(SessionRef session, ErrorInfo error)
    {
        var message = Cap(error.Message, 2000).Text;
        _logger.LogWarning(
            "{Header} {Message}",
            $"[error {error.Class}] {{session={session.SessionId}}}",
            message);
    }

    private static string Indent(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\n    ", StringComparison.Ordinal);

    private static string? FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds < 60
            ? duration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s"
            : duration.ToString(@"m\mss\s", CultureInfo.InvariantCulture);

    private static (string Text, bool Truncated, int Bytes) Cap(string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes <= maxBytes)
        {
            return (text, false, bytes);
        }

        var raw = Encoding.UTF8.GetBytes(text);
        var decoded = Encoding.UTF8.GetString(raw, 0, maxBytes);
        if (decoded.Length > 0 && decoded[^1] == '\uFFFD')
        {
            decoded = decoded[..^1];
        }

        return (decoded, true, bytes);
    }
}
