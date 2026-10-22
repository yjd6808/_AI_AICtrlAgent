namespace AiCtrl.Core.Protocol;

public static class TransferDirections
{
    public const string Upload = "up";

    public const string Download = "down";
}

public static class TransferStatuses
{
    public const string Pending = "pending";

    public const string Uploading = "uploading";

    public const string Ready = "ready";

    public const string Downloading = "downloading";

    public const string Completed = "completed";

    public const string Failed = "failed";

    public const string Expired = "expired";
}

public static class TransferConflicts
{
    public const string Fail = "fail";

    public const string Overwrite = "overwrite";

    public const string Rename = "rename";
}

public sealed record UploadCreateRequest(
    string RelDir,
    string FileName,
    long Size,
    string? Sha256,
    string Conflict);

public sealed record DownloadCreateRequest(string RelPath, bool AsZip);

public sealed record TransferBeginRequest(long Size, string? Sha256);

public sealed record TransferCreatedView(string TransferId, int ChunkSize, DateTimeOffset ExpiresAt);

public sealed record TransferStatusView(
    string TransferId,
    string Direction,
    string MachineId,
    string WorkspaceId,
    string RelPath,
    long Size,
    string? Sha256,
    bool AsZip,
    string? Conflict,
    int ChunkSize,
    int ChunksTotal,
    IReadOnlyList<int> ReceivedChunks,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? Error);

public sealed record TransferCommandView(string TransferId, string CommandId, string Status, bool Pending);
