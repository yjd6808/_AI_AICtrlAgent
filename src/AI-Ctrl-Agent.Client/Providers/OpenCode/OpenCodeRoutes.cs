namespace AiCtrl.Client.Providers.OpenCode;

public sealed class OpenCodeRoutes
{
    public static OpenCodeRoutes V1 { get; } = new(string.Empty);

    public static OpenCodeRoutes V2 { get; } = new("/api");

    public OpenCodeRoutes(string prefix)
    {
        Prefix = prefix;
    }

    public string Prefix { get; }

    public bool IsV2 => Prefix.Length > 0;

    public string Health => IsV2 ? Prefix + "/info" : Prefix + "/global/health";

    public string Doc => IsV2 ? "/openapi.json" : Prefix + "/doc";

    public string Sessions => Prefix + "/session";

    public string SessionsForDirectory(string directory) =>
        IsV2 ? Sessions : Sessions + "?directory=" + Uri.EscapeDataString(directory);

    public string SessionStatus => IsV2 ? Prefix + "/session/active" : Prefix + "/session/status";

    public string Session(string sessionId) => Prefix + "/session/" + sessionId;

    public string Messages(string sessionId) => Session(sessionId) + "/message";

    public string MessagesPage(string sessionId, string? order, int limit, string? cursor = null)
    {
        var path = Messages(sessionId) + "?limit=" +
                   limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (order is not null)
        {
            path += "&order=" + Uri.EscapeDataString(order);
        }

        return cursor is null ? path : path + "&cursor=" + Uri.EscapeDataString(cursor);
    }

    public string Children(string sessionId) => Session(sessionId) + "/children";

    public string PromptAsync(string sessionId) => Session(sessionId) + (IsV2 ? "/prompt" : "/prompt_async");

    public string Abort(string sessionId) => Session(sessionId) + (IsV2 ? "/interrupt" : "/abort");

    public string Permission(string sessionId, string permissionId) =>
        IsV2
            ? Session(sessionId) + "/permission/" + permissionId + "/reply"
            : Session(sessionId) + "/permissions/" + permissionId;

    public string SessionModel(string sessionId) => Session(sessionId) + "/model";

    public string SessionAgent(string sessionId) => Session(sessionId) + "/agent";

    public string Event => Prefix + "/event";

    public string Providers => IsV2 ? Prefix + "/model" : Prefix + "/config/providers";
}
