using AiCtrl.Core.Commands;

namespace AiCtrl.Server.Persistence;

public sealed class CommandEntity
{
    public string Id { get; set; } = string.Empty;

    public string MachineId { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public CommandStatus Status { get; set; }

    public string IssuedBy { get; set; } = string.Empty;

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public int Attempt { get; set; }

    public string? ResultJson { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string TraceId { get; set; } = string.Empty;
}

public sealed class CommandStepEntity
{
    public long Id { get; set; }

    public string CommandId { get; set; } = string.Empty;

    public int Seq { get; set; }

    public string Step { get; set; } = string.Empty;

    public string? Message { get; set; }

    public DateTimeOffset At { get; set; }
}

public sealed class IdempotencyKeyEntity
{
    public string UserId { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string CommandId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class QueryCacheEntity
{
    public string MachineId { get; set; } = string.Empty;

    public string CacheKey { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public DateTimeOffset AsOf { get; set; }
}
