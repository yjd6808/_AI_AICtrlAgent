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

    public string Health => Prefix + "/global/health";

    public string Doc => Prefix + "/doc";

    public string Sessions => Prefix + "/session";

    public string SessionStatus => Prefix + "/session/status";

    public string Session(string sessionId) => Prefix + "/session/" + sessionId;

    public string Messages(string sessionId) => Session(sessionId) + "/message";

    public string Children(string sessionId) => Session(sessionId) + "/children";

    public string PromptAsync(string sessionId) => Session(sessionId) + "/prompt_async";

    public string Abort(string sessionId) => Session(sessionId) + "/abort";

    public string Permission(string sessionId, string permissionId) => Session(sessionId) + "/permissions/" + permissionId;

    public string Event => Prefix + "/event";

    public string Providers => Prefix + "/config/providers";
}
