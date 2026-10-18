namespace AiCtrl.Core.Api;

public sealed record ApiResponse<T>(T Data, ResponseMeta Meta);

public sealed record ResponseMeta(
    string Source,
    DateTimeOffset AsOf,
    bool MachineOnline,
    bool Stale,
    long LatencyMs,
    string TraceId);

public static class MetaSources
{
    public const string Live = "live";

    public const string Cache = "cache";
}
