namespace AiCtrl.Core.Domain;

public enum ProviderKind
{
    OpenCode,
    ClaudeCode,
}

public enum AcRunState
{
    Unknown,
    Idle,
    Busy,
    Retrying,
    WaitingPermission,
    Errored,
    ForeignBusy,
}

public enum ErrorClass
{
    None,
    QuotaExceeded,
    RateLimited,
    AuthFailed,
    ModelUnavailable,
    Network,
    Aborted,
    ToolError,
    Unknown,
}

public enum SessionDisplayState
{
    Running,
    Retrying,
    WaitingPermission,
    Stalled,
    QuotaBlocked,
    Errored,
    ForeignBusy,
    Aborted,
    Idle,
    Unknown,
}

public enum InstanceMode
{
    Managed,
    SharedStore,
}

public enum FeatureSection
{
    Agents,
    Workspace,
    Etc,
}

public static class ObservedIpSources
{
    public const string Connection = "connection";

    public const string ClientReported = "client-reported";
}
